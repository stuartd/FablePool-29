# 00 — Overview: Goals, Constraints, and Architectural Summary

## 1. Problem statement

A C# trading system that allocates on its hot path is at the mercy of the garbage collector.
Milestone #1 measured the consequences on .NET 8:

- Gen0 collections under Server GC: typically 200 µs – 1.5 ms stop-the-world (STW) per pause,
  scaling with live-object survivorship and heap segment count.
- Gen1: 0.5 – 5 ms.
- Background Gen2: most phases concurrent, but two STW joins of 100 µs – 2 ms each, plus
  allocation stalls when allocating threads hit the budget mid-collection.
- `GC.TryStartNoGCRegion` defers but does not eliminate collections; exiting the region (or
  exhausting its budget) triggers a full blocking collection at an unpredictable moment.
- LOH (≥ 85,000 bytes) allocations fragment and force Gen2 work.

For a strategy whose decision-to-wire budget is **≤ 5 µs p99.9**, a single 200 µs pause is a
40× budget violation, and at realistic message rates (1–10 M msgs/s aggregated market data),
pauses are frequent enough to land on economically meaningful events with near certainty.

The conclusion: **the hot path must allocate zero managed bytes after warmup.** A GC that never
has new garbage to collect never needs to pause us. (Gen2/LOH residue from startup is collected
once during the warmup barrier, then never grows; the GC stays idle indefinitely.)

## 2. Goals

| ID | Goal | Acceptance criterion |
|----|------|---------------------|
| G1 | Zero managed allocations on hot path after warmup | EventPipe `GCAllocationTick` + `AllocationSampled` events: **0 events** attributable to hot-path threads during a 10-minute soak at full message rate (see doc 08) |
| G2 | Zero GC pauses affecting hot-path threads in steady state | `GCSuspendEEBegin` events: 0 during soak |
| G3 | Bounded, pre-declared memory footprint | All buffers/pools sized at startup from config; RSS flat (±1 page-cache noise) over 24 h soak |
| G4 | Deterministic tail latency | p99.9 wire-to-wire within budget on the latency rig; no outlier attributable to memory management |
| G5 | Mechanically enforced regression safety | CI gate fails any PR that introduces a hot-path allocation (doc 08) |
| G6 | Adoptable by existing codebases | Migration guide with phased plan; hot path isolatable behind a boundary so the rest of the system stays idiomatic C# (doc 10) |

## 3. Non-goals

- **N1:** Making the *entire* process allocation-free. Cold paths (config load, admin API, logging
  back-end, end-of-day jobs) run on non-pinned threads and may allocate freely; the GC may even run —
  Server GC suspends *all* managed threads, so we mitigate via goal G2's enforcement that cold-path
  allocation pressure stays low enough that steady-state collections simply do not occur, plus the
  escape hatch in §6.4.
- **N2:** Replacing the CLR GC or using a custom CLR build. We target stock .NET 8 so the design
  survives runtime upgrades.
- **N3:** Kernel-bypass networking design (OpenOnload/ef_vi/DPDK integration is a later milestone);
  this document assumes the receive path delivers bytes into our pre-allocated buffers and specifies
  the boundary contract it must honor.

## 4. The lifetime taxonomy (used by every other document)

Every byte of memory in the system belongs to exactly one **lifetime class**. The class determines
who allocates it, when, from where, and how it is reclaimed.

| Class | Name | Allocated | Freed | Backing store | Examples |
|-------|------|-----------|-------|---------------|----------|
| L0 | **Static** | Process start / config load | Process exit | Managed heap (pre-warmup) or unmanaged | Symbol table, venue config, pinned buffers themselves |
| L1 | **Epoch** | Start of a trading epoch (session/day) | Epoch reset | Unmanaged arena (doc 06) | Per-session order book nodes, per-day stats |
| L2 | **Pooled** | Warmup (pool fill) | Returned to pool, never freed | Object pool over pre-allocated slabs (doc 02) | Order state records, timer entries |
| L3 | **Transient-frame** | Never (it's a view) | Scope exit | `Span<T>` over L0/L1/L2/ring memory (doc 04) | Decoded message views, scratch slices |
| L4 | **Cold** | Any time | GC | Managed heap | Everything off the hot path |

**Rule O-1 (the cardinal rule):** Hot-path code MUST only touch L0–L3 memory. Any `new` of a
reference type, any boxing, any closure capture, any LINQ, any `string` creation, any `params`
array, any iterator/async state machine on a hot-path thread after warmup is a contract violation
caught by the enforcement pipeline (doc 08).

## 5. Architectural summary

```
                 ┌──────────────────────────────────────────────────────────┐
                 │                       PROCESS                            │
                 │                                                          │
  NIC RX ──────► │ [RX thread, core 2] ──► MD Ring (SPSC) ──► [Strategy,    │
  (market data)  │   decode-in-place        L0 ring            core 3]      │
                 │                                              │           │
                 │                                              ▼           │
                 │                          Order Ring (SPSC) ◄─┘           │
                 │                              │                           │
                 │ [TX thread, core 4] ◄────────┘                           │
  NIC TX ◄────── │   encode-in-place                                        │
  (orders)       │                                                          │
                 │ [Control plane, cores 0-1, unpinned]                     │
                 │   config, admin, logging drain, telemetry, GC threads    │
                 └──────────────────────────────────────────────────────────┘
```

- **Data flows through pre-allocated ring buffers** (doc 05); messages are **structs decoded
  in place** over ring memory (docs 03, 04) — bytes are never copied onto the managed heap.
- **Mutable long-lived state** (order books, working orders) lives in **pools** (doc 02) and
  **arenas** (doc 06), addressed by **typed handles** (doc 01) rather than object references,
  which keeps the GC's scan set small and the data cache-dense.
- **One thread per pipeline stage, pinned to an isolated core** (doc 07). No locks on the hot
  path; cross-stage communication is exclusively via rings.
- **Warmup barrier**: at startup the process loads config, JITs/pre-compiles all hot methods,
  fills pools, touches every page, runs a forced full GC, then raises the
  `HotPathContract.Sealed` flag. After that flag, the allocation contract is in force (doc 08).

## 6. Cross-cutting decisions (binding on all documents)

### 6.1 Runtime configuration (normative)

`runtimeconfig.json` / environment for the trading process:

```json
{
  "configProperties": {
    "System.GC.Server": true,
    "System.GC.Concurrent": true,
    "System.GC.RetainVM": true,
    "System.GC.HeapHardLimit": "0x40000000",
    "System.Runtime.TieredCompilation": false,
    "System.Runtime.TieredPGO": false,
    "System.Runtime.ReadyToRun": true
  }
}
```

Rationale:
- **Server GC stays on** even though we intend never to trigger it: if a cold-path bug ever forces
  a collection, Server GC's parallel collection minimizes the damage.
- **Tiered compilation OFF** for the trading process: tiering causes recompilation pauses and
  OSR transitions mid-session; we accept slower startup and pay it inside the warmup barrier.
  (Alternative considered: keep tiering on and rely on warmup to reach Tier-1 — rejected because
  Tier-1 promotion is count-based and a rarely-hit branch can promote *after* sealing, and the
  background JIT thread competes for cores.)
- **Heap hard limit (1 GiB shown; size per deployment)** converts "slow leak" into "fast OOM
  during soak testing" — leaks must be found in test, not production.
- **ReadyToRun** images reduce JIT work in warmup; NativeAOT is evaluated in doc 10 §7 as an
  optional hardening step, not a requirement.

### 6.2 Latency mode

After the warmup barrier, hot-path operation does **not** rely on `GCLatencyMode` or
`TryStartNoGCRegion` — both were rejected as primary mechanisms (survey, milestone #1): the former
only biases heuristics, the latter detonates a blocking GC on exit/overflow. We set
`GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency` as defense-in-depth only.

### 6.3 Exceptions

Throwing allocates (the exception object, the stack trace). Hot-path code MUST NOT throw on any
expected path. All fallible hot-path APIs return status codes (`bool` try-pattern or a
`HotResult` enum). Exceptions remain for **invariant violations only** — situations where the
correct behavior is to stop trading (doc 09 §2 defines the kill-switch path, which is allowed to
allocate because the session is already over).

### 6.4 Escape hatch

A `HotPathContract.Breach(BreachReason)` API exists for the moment something goes irrecoverably
wrong (pool exhausted beyond policy, ring overrun beyond policy). It: (1) trips the kill switch
(cancel-on-disconnect / mass cancel), (2) unsets the sealed flag so diagnostics may allocate,
(3) dumps state. Designed in doc 09.

### 6.5 Naming conventions

- `Fp.HotPath.Memory` — arenas, slabs, handles.
- `Fp.HotPath.Pooling` — pools.
- `Fp.HotPath.Rings` — ring buffers.
- `Fp.HotPath.Messages` — struct message types and codecs.
- `Fp.HotPath.Threading` — pinning, spin-wait policy.
- `Fp.HotPath.Contract` — warmup barrier, sealing, breach, enforcement hooks.

Types whose instances are legal on the hot path are suffix-free; helpers usable only pre-seal are
suffixed `Builder`/`Loader` (e.g., `SymbolTableLoader` allocates; `SymbolTable` does not).

## 7. Glossary

| Term | Definition |
|------|-----------|
| **Hot path** | Code executed between packet arrival and order wire-out, plus timers feeding it; runs on pinned threads |
| **Warmup barrier** | The startup phase boundary after which the no-allocation contract is in force |
| **Sealed** | State of the process after the warmup barrier |
| **Handle** | A 64-bit value-type reference into a pool/arena (index + generation), replacing object references |
| **Slab** | A single large pre-allocated array or native block subdivided by a pool/arena |
| **Epoch** | A bounded trading period (typically one session) at whose end L1 memory is reset wholesale |
| **STW** | Stop-the-world: all managed threads suspended by the runtime |
| **SPSC/MPSC** | Single/multi-producer, single-consumer queue discipline |
