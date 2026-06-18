# Appendix B — GC Configuration Reference for Low-Latency .NET (8+)

> Companion to Section 04 (GC Modes & Latency Settings). Section 04 explains the
> mechanisms and tradeoffs; this appendix is the operational reference: every knob
> relevant to an HFT deployment, its exact syntax, default, and our recommendation
> with rationale. Settings are stated for **.NET 8** unless noted; where behavior
> differs in 7/9, it is flagged.
>
> **Verification caveat:** GC configuration names are stable but their *interactions*
> change between runtime releases. Treat the recommendation column as a starting
> point and validate with the Appendix A harness on your exact runtime version.

---

## B.1 The three configuration channels and their precedence

| Channel | Syntax example | When applied | Notes |
|---|---|---|---|
| `runtimeconfig.json` (or `runtimeconfig.template.json` / MSBuild properties) | `"configProperties": { "System.GC.Server": true }` | Process start | Preferred: versioned with the app |
| Environment variables | `DOTNET_gcServer=1` | Process start | Override runtimeconfig; useful for ops experiments. Legacy `COMPlus_` prefix still honored |
| Runtime API | `GCSettings.LatencyMode = ...` | Any time | Only latency mode, LOH compaction mode, and NoGC regions are runtime-switchable |

Boolean env vars use `1`/`0`; several numeric env vars are parsed as **hex** (a
classic operational trap — `DOTNET_GCgen0size=100000` is 1 MiB, not 100,000 bytes).
The `System.GC.*` runtimeconfig values are decimal. Prefer runtimeconfig for this
reason alone.

## B.2 Core mode selection

| Setting | runtimeconfig key | Env var | Default | HFT recommendation |
|---|---|---|---|---|
| Server GC | `System.GC.Server` | `DOTNET_gcServer` | `false` (true for ASP.NET templates) | **`true`.** Per-core heaps, parallel collection, dedicated GC threads (Section 04 §4.2) |
| Concurrent / background GC | `System.GC.Concurrent` | `DOTNET_gcConcurrent` | `true` | **`true`.** Turns Gen2 STW into two short suspension windows (Section 04 §4.4). Disable only for the process-separated control plane where throughput matters more |
| Retain VM | `System.GC.RetainVM` | `DOTNET_GCRetainVM` | `false` | **`true`.** Keeps decommit/recommit churn (and associated page-fault jitter) off the session; memory stays mapped |

## B.3 Heap count and CPU placement (Server GC)

| Setting | runtimeconfig key | Default | HFT recommendation |
|---|---|---|---|
| Heap count | `System.GC.HeapCount` | one per available core | Set to the number of **managed allocating threads**, not machine cores. On a 32-core box running 4 managed hot threads, 4–6 heaps collect far faster than 32 sparse ones |
| Affinitize mask | `System.GC.HeapAffinitizeMask` (hex bitmask) | unset | Place GC heaps/threads on the cores your managed threads own; keep them **off** the kernel-bypass NIC polling cores |
| Affinitize ranges | `System.GC.HeapAffinitizeRanges` (e.g. `"1-4,9"`; on Windows may include CPU groups) | unset | Use instead of mask on > 64-core machines |
| Disable affinity | `System.GC.NoAffinitize` | `false` | Leave `false`; floating GC threads are a jitter source |
| Hard limit | `System.GC.HeapHardLimit` (bytes) / `HeapHardLimitPercent` | container-aware default (75% of limit in containers) | Set explicitly to your sized budget. **Interaction:** the NoGC-region maximum budget is derived from heap size; an over-tight hard limit silently shrinks your usable NoGC window |
| Per-heap-type limits | `System.GC.HeapHardLimitSOH` / `LOH` / `POH` (and `...Percent`) | unset | Use only when fencing a misbehaving LOH; otherwise leave unset |

## B.4 Generation sizing, regions, and DATAS

| Setting | Key / env var | Default | HFT recommendation |
|---|---|---|---|
| Gen0 budget | `DOTNET_GCgen0size` (hex bytes) | dynamic (CPU-cache derived) | Increasing Gen0 size reduces Gen0 GC *frequency* at the cost of slightly longer marks and worse cache locality. For an allocation-free hot path it is irrelevant; for Tier-1-only deployments, a larger Gen0 (e.g. 256 MiB/heap) can push Gen0 GCs out of the session entirely. Measure both |
| Regions vs segments | regions default in .NET 7+ (x64/arm64); fallback to segments via `DOTNET_GCName=clrgc.dll` in some servicing bands | regions | Stay on **regions** (Section 05); only fall back to reproduce a suspected regions-specific regression, and report it upstream |
| DATAS (dynamic heap adaptation) | `System.GC.DynamicAdaptationMode` / `DOTNET_GCDynamicAdaptationMode` (0/1) | off in .NET 8, **on by default in .NET 9** for Server GC | **`0` (off).** DATAS trades steady-state determinism for footprint by resizing heap count mid-run — the opposite of what a latency-bound session wants. This is the single most important new knob to check when moving 8 → 9 |
| Conserve memory | `System.GC.ConserveMemory` (0–9) | 0 | Leave 0. It biases toward compaction/decommit — footprint over latency |
| LOH threshold | `System.GC.LOHThreshold` (bytes, ≥ 85000) | 85000 | Raising it moves "slightly large" arrays into SOH where they compact in ephemeral GCs. Useful targeted fix if profiling shows a band of e.g. 90–120 KB allocations; do not raise wholesale |
| Large pages | `System.GC.HeapHardLimit` + `DOTNET_GCLargePages=1` | off | Eliminates TLB-miss and page-fault jitter on big heaps. Requires OS privilege (`SeLockMemoryPrivilege` / hugepage pool) and a hard limit set; commit-all-up-front semantics — test startup behavior |
| High memory percent | `System.GC.HighMemoryPercent` | 90 | Lower only on shared hosts; on dedicated trading hosts leave default so the GC does not become eager near your normal occupancy |

## B.5 Runtime-switchable controls

```csharp
using System.Runtime;

// 1) Session latency mode — set after warmup, before market open.
GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
// Background GC stays enabled; full blocking GCs are avoided except under
// memory pressure or explicit induced GC. See Section 04 §4.5 for what this
// does and does NOT promise.

// 2) Scheduled LOH compaction — maintenance window ONLY (e.g. post-close):
GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
// The mode auto-resets to Default after the compacting GC.

// 3) NoGC region around a known critical window:
const long NoGcBudget = 256L * 1024 * 1024; // must fit derived runtime limits
if (GC.TryStartNoGCRegion(NoGcBudget, disallowFullBlockingGC: false))
{
    try
    {
        RunCriticalWindow(); // MUST allocate < NoGcBudget across all heaps
    }
    finally
    {
        // EndNoGCRegion throws if a GC already ended the region (budget
        // exhausted with disallowFullBlockingGC:false induces a collection).
        if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
            GC.EndNoGCRegion();
    }
}
else
{
    // Entry can fail (budget too large for heap configuration, or region
    // already active). This branch must be a tested, alarmed code path.
    EnterDegradedModeAndAlert();
}
```

Operational rules for NoGC regions (from Section 04 §4.6 and production reports
[R14], [R19]):
1. Size the budget from soak-measured worst-case allocation in the window, ×2.
2. With `disallowFullBlockingGC: true`, exhaustion throws inside your hot path
   instead of inducing a GC — pick the failure mode you can actually handle.
3. The budget must fit within ephemeral segment/region capacity as derived from heap
   count and hard limits; entry failure on a config change is how this usually
   surfaces. Boot-time validation (Section 10 §10.5) should attempt a dry-run region.

## B.6 Adjacent (non-GC) runtime settings that affect GC-visible jitter

| Setting | Why it matters here |
|---|---|
| `<PublishReadyToRun>true</PublishReadyToRun>` | Removes most startup JIT; combined with warmup, prevents tier-up recompilation spikes mid-session being misattributed to GC |
| `DOTNET_TieredPGO=0`, `DOTNET_TC_QuickJitForLoops=0`, or `DOTNET_TieredCompilation=0` | Determinism vs peak codegen quality; see Appendix A §A.5 for measurement policy |
| `<ServerGarbageCollection>` / `<ConcurrentGarbageCollection>` MSBuild props | Compile-time equivalents of B.2 keys; pick one channel and stick to it |
| `<TieredCompilation>` / `<InvariantGlobalization>true</>` | Invariant globalization trims ICU allocations/loads at startup; harmless for typical trading payloads (verify if you format non-ASCII) |
| `System.Runtime.TieredCompilation.BackgroundWorkerTimeoutMs` | Tier-up worker scheduling; relevant only if tiering is left on |

## B.7 Reference profile (starting point, to be validated per §A)

`runtimeconfig.template.json` for a hot-path trading process on .NET 8, 6 managed
threads pinned to cores 2–7, 16 GiB heap budget:

```json
{
  "configProperties": {
    "System.GC.Server": true,
    "System.GC.Concurrent": true,
    "System.GC.RetainVM": true,
    "System.GC.HeapCount": 6,
    "System.GC.HeapAffinitizeRanges": "2-7",
    "System.GC.HeapHardLimit": 17179869184,
    "System.GC.DynamicAdaptationMode": 0
  }
}
```

Plus, in code at startup (after warmup): `GCSettings.LatencyMode =
GCLatencyMode.SustainedLowLatency;` — and the boot-time validator from Section 10
§10.5 asserting every one of these took effect (`GCSettings.IsServerGC`,
`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`, heap count via
`GC.GetConfigurationVariables()` on .NET 8+).

> `GC.GetConfigurationVariables()` (returns the GC's view of its own configuration)
> was added in .NET 8 — the cleanest way to assert configuration actually applied;
> verify availability on your target runtime.

---

*End of Appendix B.*
