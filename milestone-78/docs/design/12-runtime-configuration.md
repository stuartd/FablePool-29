# 12 — Runtime & GC Configuration Reference

> Companion to [08 — No-Allocation Contract](08-no-allocation-contract.md) and
> [07 — Threading & Core Pinning](07-threading-and-core-pinning.md).
> This document specifies the exact runtime configuration the architecture
> assumes, why each knob is set the way it is, and which combinations are
> explicitly **not** supported.

The allocation-free architecture removes the GC from the hot path by
construction, but the runtime still runs a GC (for warmup, cold paths, and
control-plane code) and a JIT (which can recompile in the background). Both
must be configured so that their residual activity cannot perturb pinned
hot-path threads.

---

## 12.1 GC mode selection

| Configuration | Recommended value | Rationale |
|---|---|---|
| `System.GC.Server` | `false` (Workstation GC) | With a zero-allocation steady state, GC throughput is irrelevant; what matters is that GC machinery never competes for the isolated cores. Server GC creates one GC thread + one dedicated heap per logical core and (by default) hard-affinitizes those threads across the machine — including the cores we isolated for hot-path threads. Workstation GC uses the triggering thread plus minimal helper threads and keeps a far smaller footprint. |
| `System.GC.Concurrent` | `false` | Background (concurrent) GC keeps a standing background thread that wakes unpredictably. Since gen2 collections only happen during warmup/cold operations under the contract, a blocking gen2 at a *chosen* time (warmup boundary, scheduled maintenance window) is strictly preferable to an unscheduled background thread. |
| `System.GC.RetainVM` | `true` | Prevents the GC from returning segments to the OS and re-faulting them later; keeps the committed/touched page set stable after warmup, which matters once pages are pre-touched (see arenas, doc 06). |
| `System.GC.HeapHardLimit` | Set explicitly (e.g. `0x40000000` = 1 GiB) | Converts "slow leak eventually destabilises the host" into "leak hits a hard wall and trips alarms" — a deliberately fail-fast posture consistent with FMEA item F-7 in doc 09. Size it at 4–8× the measured post-warmup heap. |
| `System.GC.HeapAffinitizeMask` / `System.GC.NoAffinitize` | If Server GC is ever required: set `HeapAffinitizeMask` (or `HeapAffinitizeRanges`) to **exclude every isolated hot-path core**; alternatively `NoAffinitize=true`. | Defence in depth: even though we recommend Workstation GC, any deployment that opts into Server GC for cold-path-heavy services **must** keep GC threads off the pinned cores. This is a hard requirement, reviewed in deployment checklists. |

**Unsupported combination:** Server GC with default affinity on a machine with
isolated cores. The deployment validation script must reject it.

### Reference `runtimeconfig.template.json`

```json
{
  "configProperties": {
    "System.GC.Server": false,
    "System.GC.Concurrent": false,
    "System.GC.RetainVM": true,
    "System.GC.HeapHardLimit": 1073741824,
    "System.Runtime.TieredCompilation": false,
    "System.Runtime.TieredPGO": false
  }
}
```

Environment-variable equivalents (useful for ops overrides; env vars take
precedence over `runtimeconfig.json`):

| Env var | Meaning |
|---|---|
| `DOTNET_gcServer=0` | Workstation GC |
| `DOTNET_gcConcurrent=0` | Disable background GC |
| `DOTNET_GCRetainVM=1` | Retain segments |
| `DOTNET_GCHeapHardLimit=0x40000000` | Hard heap limit (hex) |
| `DOTNET_TieredCompilation=0` | Disable tiering (see §12.3) |
| `DOTNET_TieredPGO=0` | Disable dynamic PGO instrumentation |

---

## 12.2 Latency modes and No-GC regions

### `GCSettings.LatencyMode`

The warmup sequence (doc 08, §"Warmup protocol") ends with:

```csharp
GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
GC.WaitForPendingFinalizers();
GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
ContractEventSource.Log.WarmupComplete();
```

`SustainedLowLatency` suppresses *unsolicited* blocking gen2 collections for
the remainder of the session. Under the contract there should be nothing to
collect anyway; the latency mode is a belt-and-braces measure against cold-path
allocations triggering a compacting gen2 at a bad time.

### Why we do **not** rely on `TryStartNoGCRegion`

`GC.TryStartNoGCRegion(budget)` is sometimes proposed as the "fix" for GC in
HFT. We evaluated and rejected it as a primary mechanism:

1. **Finite budget.** The region aborts (with a GC) the moment cumulative
   allocation exceeds the budget — exactly the failure you cannot tolerate,
   delivered at an unpredictable time. It converts a measurable, gateable
   property ("we do not allocate") into a runtime gamble ("we hope we don't
   allocate more than X before the session ends").
2. **Fragile lifecycle.** `EndNoGCRegion` throws if a GC already happened;
   exceptions inside the region can leave the process in an awkward state.
3. **It masks bugs.** Allocations inside a no-GC region don't show up as GC
   pauses, so an allocation regression survives until the budget bursts —
   typically in production. Our gate (doc 08 + `tools/AllocationGate`) makes
   the regression fail CI instead.

**Permitted use:** wrapping *short, bounded* critical sections during partial
migrations (migration guide, phase 2), where the legacy code still allocates
and you need a temporary pause shield around e.g. an auction window. Always
pair with allocation telemetry and treat as scaffolding to be removed.

---

## 12.3 JIT configuration: tiering, PGO, and warmup interaction

Tiered compilation is a **post-warmup allocation source**: the runtime counts
invocations at Tier 0, then queues background recompilation to Tier 1, and the
background JIT allocates (method tables, IR, code heaps). It can fire minutes
into a session if a code path's call count crosses the threshold late.

Policy:

| Knob | Value | Rationale |
|---|---|---|
| `TieredCompilation` | `false` for the hot-path process | Methods compile directly at full optimisation; no background recompilation, no late JIT allocations, deterministic code quality from the first call. Startup is slower — irrelevant, because warmup already takes seconds by design. |
| `TieredPGO` | `false` | Dynamic PGO inserts instrumentation at Tier 0 and allocates during reoptimisation. With tiering off it is moot, but set explicitly so an SDK default change cannot re-enable it. |
| `ReadyToRun` | Optional, `true` for large deployments | R2R precompiled code reduces warmup *time*; the runtime may still re-JIT R2R code for better quality, which is another reason tiering stays off. |
| OSR (`DOTNET_TC_OnStackReplacement`) | Follows tiering | With tiering disabled, on-stack replacement is inactive; long-running loops are compiled optimised from the start. |

**NativeAOT** is attractive long-term (no JIT at all, smaller runtime), and the
architecture is compatible with it (no reflection-emit, no runtime codegen in
the hot path). It is *not* required for this milestone; we treat it as a future
hardening option because (a) some diagnostic tooling differs under AOT and
(b) third-party exchange SDKs in scope still assume CoreCLR. The design's
warmup protocol explicitly touches every hot-path method precisely so the
JIT-vs-AOT decision does not change the steady-state contract.

**Interaction with the allocation gate:** in CI, gated processes run with
tiering disabled (the sample project sets `<TieredCompilation>false</TieredCompilation>`).
If a production service must run with tiering on, the gate's warmup window must
exceed the tiering quiescence period, and any residual JIT allocation types may
be baselined only with a linked justification (contract doc, §"Baseline
governance").

---

## 12.4 Pinned Object Heap and large-object placement

* Buffers shared with native I/O (NIC userspace stacks, `Socket` receive
  buffers used with `SocketAsyncEventArgs`) are allocated **once at startup**
  on the **Pinned Object Heap** via
  `GC.AllocateArray<byte>(length, pinned: true)` (or
  `GC.AllocateUninitializedArray` where zeroing is wasteful). POH allocation
  removes the classic pinned-buffer fragmentation problem and avoids
  long-lived `GCHandle` pins on the normal heap.
* Arrays ≥ 85,000 bytes land on the LOH regardless; pre-allocate them during
  warmup only. The LOH is never compacted under our configuration
  (`GCSettings.LargeObjectHeapCompactionMode` is left at `Default`), which is
  safe because LOH contents are fixed after warmup.
* Anything whose lifetime is the whole session and whose address must be
  stable for native interop goes to the **unmanaged arenas** (doc 06) instead;
  POH is reserved for buffers that must remain *managed* arrays for API
  compatibility.

---

## 12.5 OS-level configuration (Linux production profile)

These complement the core-pinning design in doc 07 and are required for the
latency numbers in doc 13 to be reproducible:

| Setting | Value | Why |
|---|---|---|
| `isolcpus=` + `nohz_full=` + `rcu_nocbs=` kernel args | The hot-path core set | Removes scheduler ticks and RCU callbacks from isolated cores. |
| CPU governor | `performance`; C-states limited to C1 (`processor.max_cstate=1`, `intel_idle.max_cstate=1`) | Deep C-state exit latency (tens of µs) dwarfs our budget. |
| Turbo / frequency | Fixed frequency preferred for reproducibility; turbo acceptable in production if thermal headroom is monitored | Frequency transitions add jitter and confound benchmarking. |
| SMT | Disabled on hot-path cores (offline siblings) | Sibling contention adds unpredictable tens-of-ns stalls. |
| Transparent Huge Pages | `madvise` (not `always`) | `always` can stall on compaction; arenas request huge pages explicitly via `madvise(MADV_HUGEPAGE)` where beneficial. |
| Memory locking | `mlockall(MCL_CURRENT \| MCL_FUTURE)` at startup via P/Invoke (after arenas commit), with `memlock` ulimit raised | Eliminates major faults on hot memory. Pre-touching (doc 06) handles minor faults; mlock handles reclaim. |
| Swap | Disabled on trading hosts | A swapped page is a session-ending latency event. |
| IRQ affinity | All NIC IRQs steered away from isolated cores (or kernel-bypass stack in use) | Interrupt handlers on a pinned core are a direct jitter injection. |
| Clock source | `tsc` (verify `constant_tsc nonstop_tsc` CPU flags) | `Stopwatch` resolves to RDTSC-backed clock; HPET fallback is dramatically slower. |

A deployment **validation script** must assert all of the above (plus §12.1's
GC settings, read back via `GCSettings`/`AppContext`) at process start and
refuse to enter warmup on mismatch. Failing fast at startup is cheap; finding
out at 14:30 in the session is not.

---

## 12.6 Configuration matrix summary

| Profile | GC | Tiering | Use |
|---|---|---|---|
| **Production hot path** | Workstation, non-concurrent, RetainVM, hard limit | Off (or R2R + off) | Trading engine process |
| **CI gate** | Same as production | Off | `AllocationGate` runs — must match production to be meaningful |
| **Cold-path services** (risk reporting, UI gateways) | Server GC permitted, affinitized away from isolated cores | Default | Not subject to the contract |
| **Developer inner loop** | Defaults | Default | Fast iteration; contract checked in CI, not on dev boxes |

The CI profile **must** equal the production profile for GC/JIT settings —
gating a configuration you don't ship verifies nothing. The validation script
and the CI job share one canonical settings file to keep them from drifting.
