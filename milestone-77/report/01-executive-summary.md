# 1. Executive Summary

## 1.1 The problem in one paragraph

The .NET CLR uses a generational, tracing, (mostly) compacting garbage collector. Tracing collectors must, at minimum, observe a consistent snapshot of object references; the CLR achieves this by suspending all managed threads ("stop-the-world", STW) for some or all of each collection. Even the cheapest collection — an ephemeral gen 0 GC — suspends every managed thread in the process, typically for tens to hundreds of microseconds. A full blocking gen 2 collection on a multi-gigabyte heap suspends them for tens to hundreds of *milliseconds*. In an HFT system where the entire tick-to-trade budget may be 5–50 µs (software portion), **a single gen 0 GC can consume the whole budget several times over, and a gen 2 GC is an outage**. Because GC triggering depends on allocation volume and survival patterns, pauses arrive at unpredictable times relative to market events — exactly the property a trading system cannot tolerate.

## 1.2 Ten conclusions that drive this project

1. **All current .NET GC flavors stop the world.** Background GC reduces *gen 2* pause time dramatically, but the two short STW suspensions per background GC remain, and ephemeral (gen 0/1) GCs are always fully blocking. There is no pauseless mode. [mechanistic — `gc.cpp`, suspension in `GCToEEInterface::SuspendEE`]

2. **Gen 0 pauses are not free.** On a tuned system they are 30–200 µs; with large gen 0 budgets, deep stacks, many threads, or heavy pinning they routinely reach 0.5–2 ms. Teams that "only allocate short-lived objects" have not solved the problem — they have capped it at a level that still exceeds most HFT budgets. [measured — §6.4]

3. **Thread suspension cost scales with thread count and is paid even by threads that never allocate.** A hot market-data thread that performs zero allocations is still hijacked at GC-safe points when *any other thread's* allocation triggers a collection. Isolation requires process boundaries, not thread boundaries. [mechanistic — §2.7]

4. **Server GC trades throughput for per-pause CPU burst.** Server GC's per-core heaps and parallel collection shorten pauses for a given heap size but commandeer all GC threads at high priority during collection, which conflicts with core-pinned trading threads. Affinitization (`GCHeapAffinitizeMask`/`GCHeapAffinitizeRanges`) is mandatory in mixed deployments. [§4.2]

5. **`SustainedLowLatency` is widely misunderstood.** It does not bound pause times. It biases the collector against full blocking gen 2 GCs while set, making them background where possible and deferring discretionary full collections. Ephemeral pauses continue unchanged, and a genuine memory squeeze still triggers blocking compaction. [§4.5]

6. **`TryStartNoGCRegion` is the only true "no pause" primitive, and it is a loan, not a gift.** It pre-commits a budget (bounded by ephemeral segment/region size; practical ceiling on default configs is on the order of a few hundred MB unless heap counts/sizes are raised) and the GC that was avoided still happens — at region exit. It is excellent for *bounded* critical windows (auction open, roll events), unusable as a steady-state strategy unless paired with near-zero allocation rates. [§4.6]

7. **The LOH converts medium-sized allocation mistakes into gen 2 problems.** Any allocation ≥ 85,000 bytes (or ≥ 8 KB double arrays on .NET Framework x86) goes to the LOH, is collected only with gen 2, and by default is swept, not compacted — so LOH fragmentation grows the heap, lengthens gen 2 marking, and increases GC frequency. A single careless `new byte[1 << 20]` per message is a gen 2-pause generator. [§3]

8. **.NET 7+ regions improve manageability, not worst-case pauses.** The regions rewrite (default since .NET 7) replaces large per-generation segments with 4 MB regions, giving finer decommit, better pinning containment, and more even Server GC heap balancing. Measured steady-state pause distributions improve modestly; the structural STW pauses remain. Do not treat a runtime upgrade as a latency fix. [§5, §6.6]

9. **Most allocations in real trading code are invisible in the source.** Closures, boxing through interfaces and generics, `async` state machines that escape the synchronous fast path, enumerator boxing via `IEnumerable<T>`, string interpolation, and `List<T>` growth dominate allocation profiles of systems whose authors believed they "don't allocate on the hot path." Static analysis and CI allocation gates (later milestone) are required because code review demonstrably misses these. [§7]

10. **Production low-latency .NET shops converge on the same architecture.** Across published accounts (see §10): a small, allocation-free hot path written in "C-style C#" (structs, spans, pools, pre-sized buffers, no LINQ/async/exceptions on the path), warmed and verified at startup; GC pauses tolerated only on cold paths in the same process or exiled to separate processes; `TryStartNoGCRegion` or steady-state-zero-allocation for the critical window; measurement gates in CI. Exotic options (custom CLR hosts, disabling GC entirely, `GCHeapHardLimit` + process recycling) appear at the extreme end. [§8, §9]

## 1.3 Quantitative summary (preview of §6)

Representative pause magnitudes on modern hardware (details, configs, and methodology in §6; treat as order-of-magnitude planning numbers, not guarantees):

| Event | Typical pause (p50) | Tail (p99.9) | Notes |
|---|---|---|---|
| Gen 0 GC, small gen 0 budget, Workstation | 40–120 µs | 0.3–1 ms | Always STW |
| Gen 0 GC, Server GC, 16+ heaps | 100–400 µs | 1–3 ms | Parallel, but suspension/wake overhead grows with threads |
| Gen 1 GC | 0.2–1 ms | 2–8 ms | Promotion volume dominated |
| Gen 2 background GC (STW portions) | 0.3–2 ms total across 2 suspensions | 5–15 ms | Concurrent mark runs alongside mutators |
| Gen 2 blocking (compacting), 1 GB live | 30–150 ms | 300+ ms | The outage case |
| LOH sweep within gen 2 | included above; adds 10–50% | — | Fragmentation-dependent |
| `TryStartNoGCRegion` window | 0 GC pauses | 0 | Until budget exhausted or exit |

## 1.4 What this implies for the next milestones

The survey supports the following project direction, to be ratified before Milestone 2:

- **You cannot tune your way to HFT latency with GC knobs alone.** Knobs move pauses around; only allocation elimination plus architectural isolation removes them from the critical path.
- The deliverable that "fixes" GC for HFT is therefore a **discipline + toolchain**: an allocation-free runtime library for the hot path, Roslyn analyzers and CI gates that enforce zero allocation, a measured GC/latency harness, and a reference architecture (NoGC critical windows, process isolation, pre-allocation and warm-up protocol).
- A small set of runtime configurations (documented in §4 and §9) should be standardized as the project's blessed baselines.
