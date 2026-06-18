# AllocationGate

`AllocationGate` is the enforcement tool for the **"no allocations after warmup"
contract** specified in [`docs/design/08-no-allocation-contract.md`](../../docs/design/08-no-allocation-contract.md).
It is a small, dependency-light .NET 8 console tool that:

1. **Launches or attaches** to a target .NET process.
2. Opens an **EventPipe** session subscribed to the CLR GC provider
   (`Microsoft-Windows-DotNETRuntime`, GC keyword, Verbose level) and to the
   application's contract `EventSource`.
3. Waits for the application's **warmup marker** event
   (`FablePool-Contract` / `WarmupComplete` by default, with a wall-clock
   timeout fallback).
4. Counts **`GCAllocationTick`** events during a configurable measurement
   window, attributing them to managed type names and OS thread IDs.
5. **Fails (exit code 1)** if non-baselined ticks exceed the budget
   (default budget: **zero**), making it directly usable as a CI gate.

## Quick start

```bash
# Build
dotnet build tools/AllocationGate/AllocationGate.csproj -c Release
dotnet build tools/SampleHotPath/SampleHotPath.csproj -c Release

# Launch the sample hot path under the gate
dotnet tools/AllocationGate/bin/Release/net8.0/AllocationGate.dll \
    --marker-provider FablePool-Contract \
    --marker-event WarmupComplete \
    --warmup-seconds 30 \
    --measure-seconds 10 \
    --max-ticks 0 \
    --json allocation-report.json \
    -- dotnet tools/SampleHotPath/bin/Release/net8.0/SampleHotPath.dll --duration 60
```

Attach to an already-running process instead:

```bash
dotnet AllocationGate.dll --pid 12345 --warmup-seconds 5 --measure-seconds 60
```

## Integrating your own application

Emit the warmup marker from your engine once — and only once — when warmup is
complete and the steady-state contract begins:

```csharp
[EventSource(Name = "FablePool-Contract")]
sealed class ContractEventSource : EventSource
{
    public static readonly ContractEventSource Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void WarmupComplete() => WriteEvent(1);
}

// ... after JIT warmup, pool pre-fill, final GC.Collect(), latency-mode switch:
ContractEventSource.Log.WarmupComplete();
```

If you cannot add the marker (e.g. gating a third-party binary), use
`--warmup-seconds` alone; the gate opens the window on timeout and notes
`trigger: warmup-timeout` in the report.

## Baseline files

A baseline file lists managed type names whose allocations are tolerated
inside the window — use it sparingly and only for documented cold-path types
(see the contract document for the approval process). Format: one
fully-qualified type name per line, `#` comments allowed.

```
# Known, accepted cold-path allocations (ticket FP-1234):
System.Threading.TimerQueueTimer
```

## Semantics, precision, and caveats

* `GCAllocationTick` is **sampled**: the runtime raises it roughly once per
  ~100 KB of allocation per type. A zero-tick window therefore guarantees the
  process allocated **less than the sampling threshold per type** during the
  window — not literally zero bytes. Consequences:
  * A single accidental `string` or boxing allocation per message **will**
    cross the threshold quickly at HFT message rates and be caught.
  * A *very* slow drip (bytes per minute) needs a long window. Run the gate
    with a short window on every PR, and a **long soak window (hours)**
    nightly — see `docs/design/13-benchmark-validation-methodology.md`.
* .NET 9+ exposes a per-object sampled allocation event (`AllocationSampled`);
  when the project baselines on .NET 9, the gate should additionally subscribe
  to it for finer attribution. This tool deliberately targets the
  long-stable `GCAllocationTick` so it runs on all supported runtimes today.
* The CLR's **tiered compilation** background work can allocate after startup.
  For deterministic gating, run measured processes with
  `DOTNET_TieredCompilation=0` (or `<TieredCompilation>false</TieredCompilation>`),
  as the sample does, or baseline the JIT's types explicitly. See
  `docs/design/12-runtime-configuration.md`.
* Pre-warmup ticks are reported for information but never gated — warmup is
  explicitly allowed (and expected) to allocate.

## Exit codes

| Code | Meaning |
|------|---------|
| 0 | Window completed; non-baselined ticks ≤ budget. **PASS** |
| 1 | Ticks over budget, or the window never opened/completed (target died). **FAIL** |
| 2 | Usage error or tool-level failure (couldn't attach, bad arguments). |

## CI usage

See [`.github/workflows/allocation-gate.yml`](../../.github/workflows/allocation-gate.yml)
for a complete working pipeline that builds the sample hot path, runs it under
the gate with a zero-tick budget, and uploads the JSON report as an artifact.
Wire the same job against your own engine's replay harness as described in the
migration guide (`docs/design/10-migration-guide.md`).
