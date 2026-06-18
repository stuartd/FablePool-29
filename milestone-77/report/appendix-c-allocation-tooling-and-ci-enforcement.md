# Appendix C — Allocation Detection Tooling & CI Enforcement

> Companion to Section 07 (Allocation Source Catalog) and Section 08 (Mitigation
> Strategies). Section 07 lists *what* allocates; this appendix covers *how to find
> allocations you didn't know about* and — more importantly — *how to make sure they
> never come back*. Production experience is unanimous on this point: zero-allocation
> hot paths decay within months unless enforced by machines, not code review
> ([R14], [R19]).

---

## C.1 Detection: finding allocations

### C.1.1 Interactive profiling

| Tool | Strength | Notes |
|---|---|---|
| PerfView — *GC Heap Alloc Stacks* | Lowest overhead via ETW `AllocationTick` sampling (~every 100 KB by default); exact call stacks | The reference tool on Windows; free |
| dotnet-trace + `gc-verbose` profile | Cross-platform EventPipe equivalent of AllocationTick | `dotnet-trace collect -p <pid> --profile gc-verbose`; open trace in PerfView or Visual Studio |
| Visual Studio — .NET Object Allocation Tracking | Per-object capture, type/stack pivots, friendly UI | Higher overhead; use on dev workloads |
| JetBrains dotMemory / dotTrace | Allocation flame graphs, retained-size analysis | Commercial; excellent for "who keeps this alive" questions |
| dotnet-gcdump | Heap census by type at a point in time | Answers survivor questions (what's promoting), not allocation-site questions |
| dotnet-counters `System.Runtime` | Live `alloc-rate`, GC counts, `% time in GC` | First-look triage; near-zero overhead |

**Workflow that works:** run the replayed-market-data soak (Appendix A §A.6) under
PerfView/dotnet-trace, sort allocation stacks by total bytes, and walk the list
against the Section 07 catalog. In practice the top 20 stacks account for > 95% of
hot-path bytes, and most map directly to a catalog entry (LINQ operator, closure
display class, boxing call site, `string.Format`, async state machine box,
`List<T>.Grow`).

### C.1.2 What each catalog category looks like in a profiler

| Section 07 category | Telltale type names in allocation stacks |
|---|---|
| LINQ | `Enumerable+WhereSelectArrayIterator`, `Buffer<T>`, `OrderedEnumerable<T>` |
| Closures | `<>c__DisplayClass*`, `Func\`2`, `Action\`1` |
| Boxing | The *value type itself* appearing as a heap allocation (e.g. `System.Int32`, your struct name); `String.Concat(Object)` callers |
| String handling | `System.String`, `Char[]` under `StringBuilder`, `String.Split` → `String[]` |
| Async state machines | `AsyncStateMachineBox\`1`, `Task\`1`, `TaskCompletionSource`, timer queue items |
| Collection resizing | `T[]` allocated under `List\`1.Grow` / `Dictionary\`2.Resize` / `Queue\`1.SetCapacity` |
| Hidden BCL | `CancellationTokenSource`, `Timer`, `EventArgs`, enumerator boxes from `IEnumerable<T>` foreach over interfaces |

## C.2 Prevention: compile-time enforcement

### C.2.1 Roslyn analyzers

| Analyzer | What it flags | Recommendation |
|---|---|---|
| `ClrHeapAllocationAnalyzer` (a.k.a. ClrHeapAllocationsAnalyzer, NuGet `ClrHeapAllocationAnalyzer`) | Explicit `new`, boxing, closure capture, params-array, enumerator boxing — per call site | The workhorse. Scope it to hot-path projects only via `.editorconfig`; treat as **errors** there, disabled elsewhere |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` (`RS0030`) | Any API listed in `BannedSymbols.txt` | Ban LINQ (`System.Linq.Enumerable`), `string.Format`, `Convert.*`, `Task.Run`, `DateTime.Now`, `Activator.CreateInstance` … on hot-path projects |
| `Microsoft.CodeAnalysis.NetAnalyzers` (built into SDK) | CA1802/CA1805/CA1825 (`Array.Empty<T>()`), CA1827–CA1836 (perf rules), CA2012 (`ValueTask` misuse) | Raise the perf category to warning-as-error on hot-path projects |
| ErrorProne.NET (`ErrorProne.NET.CoreAnalyzers` / `.Structs`) | Hidden struct copies, `readonly` struct violations, `in`-parameter defensive copies | Complements allocation analyzers: struct *copies* aren't allocations but cost the same latency budget |

Example `BannedSymbols.txt` for a hot-path project:

```
T:System.Linq.Enumerable; LINQ allocates iterators/closures — use loops (Section 07.2)
M:System.String.Format(System.String,System.Object); boxes and allocates — use pooled ISpanFormattable formatting
M:System.String.Split(System.Char); allocates string[] and substrings — use ReadOnlySpan<char>.Split
M:System.Threading.Tasks.Task.Run(System.Action); thread-pool + allocation — hot path owns its threads
M:System.GC.Collect; induced GCs are a control-plane decision, never hot-path
P:System.DateTime.Now; TZ lookup + non-monotonic — use Stopwatch/hardware timestamps
M:System.Activator.CreateInstance(System.Type); reflection allocation
T:System.Text.RegularExpressions.Regex; backtracking + allocation — parse spans manually
```

These are advisory-by-default analyzers; the `.editorconfig` in the hot-path
directory must escalate them:

```ini
# src/HotPath/.editorconfig — applies only beneath this directory
[*.cs]
dotnet_diagnostic.RS0030.severity = error      # banned APIs
dotnet_diagnostic.HAA0101.severity = error     # params array allocation
dotnet_diagnostic.HAA0201.severity = error     # implicit string concat alloc
dotnet_diagnostic.HAA0301.severity = error     # closure capture
dotnet_diagnostic.HAA0302.severity = error     # display class allocation
dotnet_diagnostic.HAA0401.severity = error     # possible boxing (lambda)
dotnet_diagnostic.HAA0501.severity = error     # explicit new of ref type
dotnet_diagnostic.HAA0502.severity = error     # explicit new (generic)
dotnet_diagnostic.HAA0601.severity = error     # value type to object boxing
dotnet_diagnostic.HAA0602.severity = error     # delegate from struct method
```

> HAA rule IDs above follow the ClrHeapAllocationAnalyzer numbering as of its 3.x
> packages; verify IDs against the analyzer version you adopt — they have been
> renumbered once in the project's history.

### C.2.2 Limits of static analysis

Analyzers cannot see: allocations inside BCL calls (e.g. `Dictionary` resize),
JIT-dependent escape behavior, boxing through generic virtual dispatch resolved at
runtime, or allocations in third-party libraries. That residual is exactly what the
runtime test gate in C.3 exists to catch. Use both; neither is sufficient alone.

## C.3 Enforcement: the CI allocation-budget gate

The contract: *every hot-path operation allocates exactly its budgeted number of
bytes (usually zero) in steady state, asserted on every build.* The primitive is
`GC.GetAllocatedBytesForCurrentThread()`, which counts precisely and costs a TLS read.

```csharp
// AllocationAssert.cs — test-support library (xUnit shown; framework-agnostic core).
// Net8.0. The only subtlety is excluding one-time costs: JIT tier-up, statics,
// first-call caches. Hence: warmup iterations, then a measured burst.

using System;
using System.Runtime.CompilerServices;

public static class AllocationAssert
{
    /// <summary>
    /// Asserts that <paramref name="action"/> allocates at most
    /// <paramref name="budgetBytes"/> per invocation in steady state.
    /// </summary>
    public static void AtMost(long budgetBytes, Action action,
        int warmupIterations = 200, int measuredIterations = 1000,
        [CallerArgumentExpression(nameof(action))] string? expr = null)
    {
        // Warmup: drives tier-up, fills caches, initializes statics.
        for (int i = 0; i < warmupIterations; i++) action();

        // Settle: let any background tier-up recompilation finish.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        System.Threading.Thread.Sleep(50);
        for (int i = 0; i < warmupIterations; i++) action();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < measuredIterations; i++) action();
        long after = GC.GetAllocatedBytesForCurrentThread();

        long perCall = (after - before) / measuredIterations;
        if (perCall > budgetBytes)
            throw new AllocationBudgetExceededException(
                $"'{expr}' allocated {perCall} B/call " +
                $"(budget {budgetBytes} B/call, total {after - before} B " +
                $"over {measuredIterations} calls).");
    }

    public static void Zero(Action action,
        [CallerArgumentExpression(nameof(action))] string? expr = null)
        => AtMost(0, action, expr: expr);
}

public sealed class AllocationBudgetExceededException : Exception
{
    public AllocationBudgetExceededException(string message) : base(message) { }
}
```

Usage in a test project:

```csharp
public class HotPathAllocationTests
{
    [Fact]
    public void DecodeMarketDataMessage_is_allocation_free()
    {
        var codec = TestFixtures.CreateCodec();
        var frame = TestFixtures.SampleItchAddOrderFrame();
        AllocationAssert.Zero(() => codec.Decode(frame.Span, out _));
    }

    [Fact]
    public void FormatOutboundOrder_is_allocation_free()
    {
        var writer = TestFixtures.CreatePooledOrderWriter();
        var order = TestFixtures.SampleOrder();
        AllocationAssert.Zero(() => writer.Write(order));
    }
}
```

Operational rules for the gate (learned the hard way in production shops):
1. **Run with tiering settled.** Either set `DOTNET_TieredCompilation=0` for the test
   process (via `runtimeconfig` of the test project) or rely on the double-warmup
   pattern above. Background tier-up *can* allocate on other threads but not on the
   measuring thread, which is why per-thread counting is used.
2. **Budgets are exact, not "small".** A budget of 0 means 0. The first "it's only
   24 bytes" exemption becomes 24 bytes × 2M messages/day.
3. **Measure on the same OS family as production** at least nightly; some BCL paths
   allocate differently per platform (e.g. globalization, sockets).
4. **Soak variant:** a nightly job runs the replay workload for ≥ 1 simulated session
   and asserts `GC.CollectionCount(0)` delta == 0 (or the explicitly budgeted count)
   across the session window — this catches slow leaks and rare-path allocations
   that per-operation tests miss.

## C.4 BenchmarkDotNet as a secondary check

BenchmarkDotNet's `[MemoryDiagnoser]` reports allocated B/op alongside timing and is
useful for *exploration* (comparing implementation candidates), but it is not the CI
gate: benchmarks are slow, and a `Gen0` column of `-` already tells you less than the
exact byte assertion of §C.3. Targeting BenchmarkDotNet `0.13`+:

```csharp
[MemoryDiagnoser]
public class CodecBenchmarks
{
    private MarketDataCodec _codec = null!;
    private ReadOnlyMemory<byte> _frame;

    [GlobalSetup]
    public void Setup()
    {
        _codec = TestFixtures.CreateCodec();
        _frame = TestFixtures.SampleItchAddOrderFrame();
    }

    [Benchmark]
    public int Decode() => _codec.Decode(_frame.Span, out _);
}
```

Acceptance rule: any candidate showing nonzero "Allocated" loses, regardless of mean
time, unless the allocation is explicitly budgeted and pooled-amortized.

## C.5 Runtime tripwires for production

Static analysis and CI cannot prove the deployed binary on deployed data allocates
nothing — so production carries cheap tripwires, sampled from a non-critical thread:

```csharp
// GcTripwire.cs — alarm if any GC occurs during session hours.
public sealed class GcTripwire
{
    private int _g0, _g1, _g2;

    public void ArmAtSessionOpen()
    {
        _g0 = GC.CollectionCount(0);
        _g1 = GC.CollectionCount(1);
        _g2 = GC.CollectionCount(2);
    }

    /// <returns>null if quiet; otherwise a description for the alerting pipeline.</returns>
    public string? Check()
    {
        int g0 = GC.CollectionCount(0) - _g0;
        int g1 = GC.CollectionCount(1) - _g1;
        int g2 = GC.CollectionCount(2) - _g2;
        return (g0 | g1 | g2) == 0
            ? null
            : $"GC during session: gen0+={g0} gen1+={g1} gen2+={g2}";
    }
}
```

Shops that run this tripwire report that it converts "mysterious 2 ms outlier at
14:31" investigations from days into minutes: either the tripwire fired (GC — find
the allocation with §C.1 on the replay) or it didn't (environment — Appendix A §A.5).

## C.6 Tooling/dependency summary

For the future milestones that adopt this appendix, the involved packages and the
versions this text targets (use caret/floating constraints, verify on resolution):

| Package | Targeted version line | Role |
|---|---|---|
| `ClrHeapAllocationAnalyzer` | 3.x | per-call-site allocation diagnostics |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | 3.3.x | API bans via BannedSymbols.txt |
| `ErrorProne.NET.CoreAnalyzers` | 0.x (pre-1.0; pin minor) | struct-copy diagnostics |
| `BenchmarkDotNet` | 0.13+ | exploratory perf/alloc comparison |
| `xunit` + `xunit.runner.visualstudio` | 2.x | host for the allocation gate |
| dotnet-trace / dotnet-counters / dotnet-gcdump | 8.x global tools | detection workflows |

---

*End of Appendix C.*
