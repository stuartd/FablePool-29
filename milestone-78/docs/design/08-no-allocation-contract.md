# 08 — The "No Allocations After Warmup" Contract & Enforcement Strategy

Status: Design — Milestone 2
Depends on: 01 (ownership), 02 (pooling), 05 (ring buffers), 06 (arenas), 07 (threading)

---

## 1. Purpose

Every preceding document describes *how* to avoid allocations. This document defines the
**binding contract** that the system is allocation-free in steady state, and — critically —
the machinery that makes the contract *enforceable* rather than aspirational. A
zero-allocation architecture that is not continuously verified decays within weeks: one
innocent `string.Format` in a log call, one LINQ expression in a code review that nobody
flagged, one library upgrade that starts boxing internally, and the GC is back in the hot
path.

The contract has three legs:

1. **A precise definition** of what "no allocations" means, where it applies, and when it
   begins (the warmup boundary).
2. **Runtime enforcement** — in-process guards that detect violations in production and in
   soak tests, with type-level attribution via EventPipe.
3. **Build-time enforcement** — Roslyn analyzers, banned-API lists, and CI gates that fail
   pull requests which would violate the contract, *before* they reach a soak environment.

---

## 2. Contract Definition

### 2.1 Formal statement

> After the process signals `LifecyclePhase.SteadyState`, no thread registered as a
> **hot-path thread** shall cause any managed heap allocation (SOH, LOH, or POH), for the
> remainder of the trading session, except inside an explicitly registered
> **allocation amnesty scope** (§2.5).

Notes on scope:

- The contract applies **per thread**, not per process. Cold-path threads (admin RPC,
  EOD reporting, config reload) may allocate freely. This is what makes the contract
  achievable: we never try to make the *whole process* allocation-free, only the pinned
  hot-path threads defined in doc 07 (`PinnedThreadRegistry`).
- "Allocation" means any managed heap allocation, including:
  - `new` of a reference type, arrays included;
  - boxing (implicit or explicit), including boxing through interface dispatch on
    structs, `params object[]`, string interpolation, and enum-keyed dictionaries;
  - closure environment classes and delegate instances created by lambda capture;
  - async/iterator state machines that escape to the heap;
  - hidden runtime allocations: `string.Concat`, `Enum.ToString`, exception objects,
    `GCHandle`-backed allocations, params-array creation.
- Stack allocation (`stackalloc`), arena allocation (doc 06), pool *checkout* (doc 02),
  and ring-buffer claims (doc 05) are **not** allocations under this contract — they are
  the sanctioned alternatives.
- Native allocations (`NativeMemory.Alloc`) are forbidden in steady state by a *separate*
  clause (§2.4) because, while they don't trigger GC, they indicate an unbounded-memory
  bug. All native memory is reserved during `Init`.

### 2.2 Lifecycle phases

The process moves through an explicit, one-way state machine. Phase is held in a single
`static int` (volatile) and exposed via `Lifecycle.Phase`:

```
 ┌──────┐    ┌────────┐    ┌────────┐    ┌─────────────┐    ┌──────────┐
 │ Boot │───▶│  Init  │───▶│ Warmup │───▶│ SteadyState │───▶│ Teardown │
 └──────┘    └────────┘    └────────┘    └─────────────┘    └──────────┘
   CLR/host    allocate      JIT & GC      CONTRACT IN        contract
   startup,    everything:   settle:       FORCE: zero        released;
   config,     pools, ring   exercise      managed alloc      drain, free
   DI wiring   buffers,      every hot     on hot threads     native mem
               arenas,       path ≥ N
               threads       times
```

| Phase | Allocation policy (hot threads) | Typical duration |
|---|---|---|
| `Boot` | Unrestricted | seconds |
| `Init` | Unrestricted — this is when pools/arenas/buffers are sized and allocated | seconds–minutes |
| `Warmup` | Unrestricted but **measured** (baseline capture, §4.3) | 30 s – 5 min |
| `SteadyState` | **Zero** managed allocation; zero native allocation | the trading session |
| `Teardown` | Unrestricted | seconds |

Transition into `SteadyState` is gated by the **warmup completion criteria** (§3.3). The
transition is performed by the control thread; hot threads observe it via a volatile read
each loop iteration (one predictable branch, ~0 cost).

### 2.3 The warmup obligation

Warmup is not optional and not "run traffic for a while." It is a deterministic procedure
with completion criteria, because two CLR mechanisms allocate or jitter *lazily*:

1. **Tiered compilation / dynamic PGO.** Methods start at Tier 0, get instrumented, and
   are recompiled at Tier 1 in the background. Recompilation itself doesn't allocate on
   our threads, but Tier-0 code is slow and OSR transitions cause latency noise; worse,
   some BCL paths take allocating slow paths until caches warm (e.g., reflection caches,
   `Utf8Formatter` lookup tables, culture data).
2. **Lazy statics and caches.** First-touch of static constructors, `Encoding` objects,
   `TimeZoneInfo`, generic instantiations over new type arguments — all allocate on first
   use only.

Therefore warmup **must execute every hot-path code route**, with representative data, at
least `WarmupIterations` times (default 200,000 per route — enough to cross tiering
thresholds with margin even with dynamic PGO enabled). Doc 10 §6 covers building the
warmup driver from recorded market data.

### 2.4 Native-memory clause

All `Arena`, ring-buffer, and pool backing memory is reserved and committed during `Init`
(doc 06 §4 — pre-touch policy). In `SteadyState`:

- `NativeMemory.Alloc/Realloc/Free` is forbidden on hot threads (banned-API enforced, §6.2).
- Arena `Reset()` is permitted (it is pointer arithmetic, not allocation).
- Arena/pool **growth** is forbidden — exhaustion is a fault, handled per the failure-mode
  policy in doc 09 §4, never by allocating more.

### 2.5 Allocation amnesty scopes

Reality clause: some unavoidable events allocate — a `SocketException` on disconnect, a
fatal-path log message. Rather than pretend these don't exist, they must be wrapped:

```csharp
using (AllocationGuard.Amnesty(AmnestyReason.SessionDisconnect))
{
    // exceptional, latency-irrelevant code; allocations recorded but not faulted
}
```

Rules:
- An amnesty scope **must** carry a reason code from a closed enum (auditable).
- Amnesty scopes are counted and exported as metrics; a scope entered more than
  `MaxAmnestyPerSession` times (default 10) trips the same alarm as a contract violation,
  because "exceptional" code running frequently means the design is wrong.
- Amnesty does not silence EventPipe capture — allocations inside amnesty are still
  attributed and reported, just not faulted.

---

## 3. Runtime Enforcement — In-Process Guards

Defense in depth: three independent detectors with different precision/cost/attribution
trade-offs. All three run simultaneously; they answer different questions.

| Detector | Precision | Attribution | Cost on hot thread | Question answered |
|---|---|---|---|---|
| Per-thread byte counter (§3.1) | Exact (byte-accurate) | None (count only) | ~2 ns per check | *Did this thread allocate at all?* |
| GC collection sentinel (§3.2) | Coarse | None | Zero (control thread polls) | *Did a GC happen at all?* |
| EventPipe `GCAllocationTick` (§4) | Sampled (~100 KB granularity) | **Type name + size + thread** | Zero on hot thread (out-of-band) | *What allocated, and from where?* |

### 3.1 Per-thread allocation counter — the primary tripwire

`GC.GetAllocatedBytesForCurrentThread()` reads the thread's allocation-context counter.
It is exact (includes every managed allocation the thread performed), costs a few
nanoseconds, and requires no events or sessions. Each hot thread checks it once per loop
iteration:

```csharp
namespace ZeroAlloc.Enforcement;

/// <summary>
/// Per-thread allocation tripwire. One instance per hot thread, created during Init,
/// armed at the SteadyState transition. Zero-allocation itself: holds only longs.
/// </summary>
public struct AllocationGuard      // mutable struct, lives in the thread's loop frame
{
    private long _baseline;        // bytes allocated by this thread at arming time
    private long _amnestyBytes;    // bytes excused inside amnesty scopes
    private int  _armed;           // 0 = warmup, 1 = enforcing

    public void Arm()
    {
        _baseline = GC.GetAllocatedBytesForCurrentThread();
        _armed = 1;
    }

    /// <summary>Called once per event-loop iteration. Branch-predictable; ~2 ns.</summary>
    public void Check()
    {
        if (_armed == 0) return;
        long now = GC.GetAllocatedBytesForCurrentThread();
        long leaked = now - _baseline - _amnestyBytes;
        if (leaked > 0)
            ContractViolation.Raise(leaked);   // see §3.3 — does NOT throw on hot path
    }

    // Amnesty bookkeeping: scope records the counter on entry/exit and credits the delta.
    internal void Credit(long bytes) => _amnestyBytes += bytes;
}
```

**Violation policy** (`ContractViolation.Raise`) is configurable per environment:

| Environment | Policy |
|---|---|
| CI / soak | `FailFast` — write violation record to pre-allocated diagnostic buffer, `Environment.FailFast`. The trace artifact (§4) attributes the type. |
| UAT | `Quarantine` — raise alarm, keep trading, re-baseline so each violation is reported once. |
| Production | `AlarmOnce` — alarm via pre-allocated ring-buffer message to the telemetry thread; never crash a live trading session over an allocation. |

`Raise` itself must not allocate: the violation record (thread id, leaked bytes,
timestamp) is written into a pre-allocated `SpscRingBuffer<ViolationRecord>` consumed by
the telemetry thread.

### 3.2 GC collection sentinel

The control thread polls every 100 ms:

```csharp
int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
```

Any increase after `SteadyState` is a contract violation *somewhere* in the process —
possibly a cold thread allocating so heavily it threatens hot threads via GC suspension
(even cold-thread allocation triggers stop-the-world phases that suspend hot threads;
non-concurrent gen-0 collections suspend everything). Policy: cold threads have a
*budget* (default: no gen-2 ever; gen-0 rate < 1/min), enforced as warnings, because the
process-wide design goal (docs 02/06) is that even cold paths are mostly pooled.

As belt-and-braces, sessions may optionally run inside
`GC.TryStartNoGCRegion(totalSize)` sized to the cold-path budget, with
`GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency` as the fallback if the
region cannot be established. The no-GC region converts "a GC happened" into a hard,
unambiguous signal: the region ends, the sentinel sees it, the alarm fires.

### 3.3 Warmup completion criteria (arming the guards)

The control thread arms `SteadyState` only when **all** of:

1. Every registered warmup route reports ≥ `WarmupIterations` executions.
2. JIT activity has quiesced: no `MethodJitting` events (observed via the same EventPipe
   session, CLR JIT keyword) for `JitQuietWindow` (default 10 s).
3. A forced full compaction has run: `GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true)`
   followed by `GC.WaitForPendingFinalizers()` and a second collect — so the heap entering
   the session is minimal and compacted, and any finalizer-driven allocation is flushed.
4. Each hot thread has performed one full loop iteration *after* the collect with its
   `AllocationGuard` in rehearsal mode (checks but logs rather than faults) and reported
   zero bytes — a dry run of the contract before it becomes binding.

---

## 4. EventPipe Allocation Tracking — Attribution

The per-thread counter says *that* you allocated; it cannot say *what*. Attribution comes
from the runtime's allocation events over EventPipe, captured **out-of-process** (CI,
soak) or by a **sidecar in-process session** (production), so the hot threads pay nothing.

### 4.1 The event: `GCAllocationTick`

Provider `Microsoft-Windows-DotNETRuntime`, keyword `GC` (`0x1`), level `Verbose`.
The runtime emits `GCAllocationTick` approximately once per **100 KB** allocated (per
heap), with payload including `TypeName`, `AllocationAmount`/`AllocationAmount64`,
`AllocationKind` (Small/Large/Pinned), and `HeapIndex`; the event metadata carries the OS
thread id, letting us filter to registered hot threads.

Implications of the 100 KB sampling granularity:

- A *sustained* leak (the realistic failure: a per-message allocation) crosses 100 KB
  within milliseconds at trading message rates and is attributed almost immediately.
- A *single tiny* allocation may not produce a tick. That is why §3.1 (byte-exact
  counter) is the tripwire and EventPipe is the *attributor*: counter fires → CI harness
  replays the workload under a full-verbosity trace to capture the type.
- .NET 9+ adds a configurable `AllocationSampled` event (provider keyword
  `0x80000000000`, Poisson-sampled, default mean 100 KB, tunable down) that yields
  per-sample type + stack with bounded overhead; the harness uses it when available and
  falls back to `GCAllocationTick` otherwise. **Maintainers should verify the keyword
  value and event name against the runtime version in use** — this surface is newer than
  `GCAllocationTick`.

### 4.2 Out-of-process capture (CI and soak) — `dotnet-trace`

```bash
dotnet-trace collect \
  --process-id $PID \
  --providers "Microsoft-Windows-DotNETRuntime:0x1:5" \
  --buffersize 512 \
  --output steadystate.nettrace \
  --duration 00:10:00
```

`0x1` = GC keyword, `5` = Verbose (required for allocation ticks). The resulting
`.nettrace` is parsed by the CI analyzer (§5.3) using the
`Microsoft.Diagnostics.Tracing.TraceEvent` library:

```csharp
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

using var source = new EventPipeEventSource("steadystate.nettrace");
var perType = new Dictionary<string, long>();
source.Clr.GCAllocationTick += (GCAllocationTickTraceData e) =>
{
    if (e.TimeStamp < steadyStateMark) return;            // ignore warmup
    if (!hotThreadIds.Contains(e.ThreadID)) return;        // hot threads only
    perType.TryGetValue(e.TypeName, out long n);
    perType[e.TypeName] = n + e.AllocationAmount64;
};
source.Process();
// any entry in perType => gate failure, with type names in the report
```

For call-stack attribution (which line allocated), the harness adds a second provider
spec capturing stacks: in practice, run the failing workload once more under
`dotnet-trace collect --profile gc-verbose`, open in PerfView/Visual Studio, and the
allocation stacks identify the call site. The CI report includes the exact reproduction
command.

### 4.3 In-process sidecar (production)

Production hosts often forbid attaching diagnostic tools. The sidecar is a *cold*,
unpinned, low-priority thread inside the process running an `EventListener`:

```csharp
internal sealed class AllocationAttributor : EventListener
{
    // Pre-allocated, bounded storage; the listener thread MAY allocate (it is a cold
    // thread) but is written pool-friendly to avoid disturbing the GC sentinel budget.
    private readonly ViolationSink _sink;          // ring buffer to telemetry

    protected override void OnEventSourceCreated(EventSource src)
    {
        if (src.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(src, EventLevel.Verbose, (EventKeywords)0x1 /* GC */);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName != "GCAllocationTick") return;
        if (Lifecycle.Phase != LifecyclePhase.SteadyState) return;
        if (!PinnedThreadRegistry.IsHotOsThread(e.OSThreadId)) return;

        // Payload order per ClrEtwAll manifest: AllocationAmount, AllocationKind,
        // ClrInstanceID, AllocationAmount64, TypeID, TypeName, HeapIndex, Address...
        // Resolve by name for robustness across runtime versions:
        int typeIdx = e.PayloadNames!.IndexOf("TypeName");
        int amtIdx  = e.PayloadNames!.IndexOf("AllocationAmount64");
        _sink.Report(e.OSThreadId,
                     (string)e.Payload![typeIdx]!,
                     (long)e.Payload![amtIdx]!);
    }
}
```

Caveats (documented because they bite):

- The `EventListener` callback runs on dispatcher threads and itself allocates (payload
  arrays, strings). That is acceptable: it is a cold thread, registered as such, and
  its allocations are excluded from the cold-thread budget by thread id.
- Enabling the GC keyword at Verbose adds the runtime's event-writing cost to allocating
  threads. Hot threads don't allocate in steady state, so they pay **nothing**; the cost
  lands only on the (allowed-to-allocate) cold threads and on the violator — exactly
  where we want the evidence.
- `EventWrittenEventArgs.OSThreadId` and name-based payload lookup are used instead of
  positional indices to survive payload-schema additions across runtime versions.

### 4.4 What EventPipe gives us per environment

| Environment | Mechanism | Output |
|---|---|---|
| Dev inner loop | `dotnet-counters monitor` (alloc rate, GC counts) + unit gates | immediate feedback |
| CI PR gate | Harness + `dotnet-trace` + TraceEvent analyzer | pass/fail + per-type table + repro command |
| Nightly soak | 8-hour run, rotating 10-min `.nettrace` captures | trend report; catches slow leaks & rare paths |
| Production | In-process sidecar `EventListener` | alarm with type name within ~1 s of violation |

---

## 5. CI Gates

Three gates, run in order of cost. A PR must pass all three to merge into the hot-path
projects (cold-path projects are exempt by directory).

### 5.1 Gate A — Static analysis (seconds, every build)

Compile-time prevention via Roslyn:

1. **Banned APIs** — `Microsoft.CodeAnalysis.BannedApiAnalyzers` with a
   `BannedSymbols.txt` checked into the hot-path projects:

   ```
   T:System.Linq.Enumerable; LINQ allocates enumerators/closures — use indexed loops
   M:System.String.Format(System.String,System.Object); boxes & allocates — use Utf8Formatter into pooled buffers
   M:System.String.Concat(System.String,System.String); allocates — use pooled Utf8 builders
   T:System.Collections.Generic.List`1; growth allocates — use FixedList<T> (doc 03 §7)
   T:System.Collections.Generic.Dictionary`2; resizing/boxing hazards — use FixedDictionary<TKey,TValue>
   M:System.GC.Collect; forbidden outside Lifecycle transitions
   M:System.Runtime.InteropServices.NativeMemory.Alloc(System.UIntPtr); Init-phase only — use Arena
   T:System.Threading.Tasks.Task; async state machines escape to heap — use the event-loop model (doc 07)
   M:System.Enum.ToString; reflection + allocation — use precomputed name tables
   T:System.Text.StringBuilder; allocates — use Utf8Writer over pooled buffers
   ```

   (Full list maintained in `eng/BannedSymbols.txt`; the excerpt shows the shape.
   Init/Teardown code that legitimately needs these lives in separate projects or uses
   `#pragma warning disable RS0030` with a mandatory justification comment, grep-audited.)

2. **Heap-allocation analyzer** — an allocation-detection analyzer (e.g., the
   `ClrHeapAllocationAnalyzer` family / `ErrorProne.NET` analyzers) configured as
   **error** severity in hot-path projects for: explicit `new` of reference types,
   boxing, closure captures, delegate allocations, params-array creation, and
   value-type-to-interface conversions. *Maintainers should pin whichever analyzer
   package the org standardizes on; the design requirement is the rule set, not the
   specific package.*

3. **In-house analyzers** (specified in doc 11 §9): enforce ownership annotations from
   doc 01 (`[Borrowed]`, `[Owns]`, `[PoolReturned]`), forbid `ref struct` escape via
   captured spans, and require `readonly` on message structs (doc 03).

`.editorconfig` scoping keeps all of this **error** in `src/HotPath/**` and **suggestion**
elsewhere, so the cold path stays productive.

### 5.2 Gate B — Micro-gates via BenchmarkDotNet (minutes, every PR)

Each hot-path component ships allocation benchmarks with `[MemoryDiagnoser]`. The gate
asserts the `Allocated` column is **0 B** per op (BDN measures via
`GC.GetTotalAllocatedBytes`, byte-exact):

```csharp
[MemoryDiagnoser]
public class OrderPathBenchmarks
{
    private OrderGateway _gw = null!;
    private MarketDataReplay _replay = null!;

    [GlobalSetup]
    public void Setup()
    {
        _gw = TestHost.BuildGateway();      // Init-phase allocation is fine here
        _replay = MarketDataReplay.Load("nasdaq-sample.bin");
        TestHost.Warmup(_gw, _replay);      // drives the real warmup procedure
    }

    [Benchmark]
    public void TickToOrder() => _gw.ProcessOne(_replay.Next());
}
```

A small runner parses BDN's JSON exporter output and fails the build if any benchmark in
the `HotPath` category reports `Allocated > 0`. This catches per-operation allocations
with **exact** attribution to the benchmarked component, far faster than a soak run.

### 5.3 Gate C — Integration gate with EventPipe (10–20 min, every PR to main)

The full-system gate, run by `eng/ci/allocation-gate.sh`:

1. Launch the trading host in **CI mode** (`ZEROALLOC_POLICY=FailFast`) against the
   exchange simulator with recorded market data (3 replay profiles: quiet, normal,
   stressed — the stressed profile includes opening-auction bursts and feed gaps,
   exercising recovery paths).
2. Wait for the host to log `SteadyState` (it performs the §3.3 procedure itself).
3. Attach `dotnet-trace` with the GC-verbose provider (command in §4.2) for the full run.
4. Drive 10 minutes of replay per profile.
5. **Pass criteria:**
   - process exited 0 (no `FailFast` from an `AllocationGuard`);
   - `GC.CollectionCount` deltas across `SteadyState` = 0 for all generations
     (exported by the host on shutdown);
   - TraceEvent analysis of the `.nettrace` shows **zero** `GCAllocationTick` events
     attributed to hot thread ids after the steady-state marker event (the host emits a
     custom `ZeroAlloc/SteadyStateEntered` EventSource event so the analyzer can find
     the boundary in the same trace);
   - amnesty-scope count within budget.
6. On failure, the job publishes: the per-type allocation table, the violating thread
   names, the `.nettrace` artifact, and the one-line repro command.

Pipeline sketch (GitHub Actions; Azure DevOps equivalent in `eng/ci/`):

```yaml
allocation-gate:
  runs-on: [self-hosted, hft-bench]      # isolated, core-pinnable runner (doc 07 §8)
  steps:
    - uses: actions/checkout@v4
    - run: dotnet build -c Release
    - run: dotnet test -c Release --filter Category=HotPathUnit
    - run: dotnet run -c Release --project bench/HotPath.Benchmarks -- --filter '*' --exporters json
    - run: eng/ci/check-bdn-zero-alloc.sh bench/BenchmarkDotNet.Artifacts/results
    - run: eng/ci/allocation-gate.sh --profiles quiet,normal,stressed --duration 600
    - uses: actions/upload-artifact@v4
      if: failure()
      with: { name: nettrace, path: artifacts/*.nettrace }
```

### 5.4 Nightly soak gate

Identical to Gate C but 8 hours, with live-like jittered replay, plus:
- memory ceiling assertion (working set flat ±2% after hour 1 — catches native leaks the
  managed gates can't see);
- p99.9/p99.99 latency regression check against the stored baseline;
- amnesty-reason histogram diffed against the previous night.

---

## 6. Configuration Baseline

Runtime configuration that the contract assumes (checked at startup by
`Lifecycle.ValidateRuntimeConfig()`, which fails Boot if violated):

```xml
<!-- HotPathHost.csproj -->
<PropertyGroup>
  <ServerGarbageCollection>false</ServerGarbageCollection>      <!-- one small heap; SLL mode -->
  <ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>
  <TieredPgo>true</TieredPgo>                                   <!-- best steady-state codegen -->
  <TieredCompilation>true</TieredCompilation>                    <!-- warmup procedure handles tiering -->
  <InvariantGlobalization>true</InvariantGlobalization>          <!-- avoids ICU lazy allocs -->
  <UseSystemResourceKeys>true</UseSystemResourceKeys>            <!-- avoids resource-string allocs in exception paths -->
  <AutoreleasePoolSupport>false</AutoreleasePoolSupport>
</PropertyGroup>
```

Rationale notes: with a genuinely allocation-free hot path the *choice* of GC barely
matters in steady state — the configuration above optimizes the failure case (if a GC
does happen, workstation non-concurrent gen-0 on a tiny heap is microseconds) and the
warmup case. Teams preferring `TieredCompilation=false` (or R2R + composite images) for
deterministic first-call latency may do so; the warmup criteria in §3.3 make either
choice safe.

---

## 7. Summary of Responsibilities

| Actor | Obligation |
|---|---|
| Hot-path developer | Code passes Gates A–C; allocating code wrapped in amnesty with reason, or moved to cold thread |
| Component owner | Ships BDN zero-alloc benchmarks (Gate B) for every public hot-path API |
| Platform team | Maintains banned list, analyzers, gate scripts, warmup driver, replay profiles |
| Control thread (runtime) | Executes warmup criteria, arms guards, runs GC sentinel, hosts EventPipe sidecar |
| Hot thread (runtime) | One `AllocationGuard.Check()` per loop iteration |
| Ops | Treats production violation alarms as sev-2: trade on, fix before next session |
