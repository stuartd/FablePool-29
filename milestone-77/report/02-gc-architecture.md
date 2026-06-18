# 2. .NET GC Architecture and the Mechanics of Pauses

This section explains, at the level of the runtime's actual implementation (`src/coreclr/gc/gc.cpp` and friends in the dotnet/runtime repository), why the CLR GC pauses threads, what work happens inside a pause, and which parameters of your program determine pause length. Everything later in the report — modes, regions, mitigations — is a variation on the machinery described here.

## 2.1 The collector in one diagram

The CLR GC is:

- **Tracing**: liveness is computed by reachability from roots, not by reference counting.
- **Generational**: the heap is partitioned into gen 0, gen 1, gen 2, plus the Large Object Heap (LOH, logically "gen 3") and, since .NET 5, the Pinned Object Heap (POH, logically "gen 4"). Young generations are collected frequently and cheaply; old generations rarely and expensively.
- **Mostly compacting**: ephemeral collections usually compact (slide survivors together); gen 2 and LOH may sweep (build free lists) instead, decided per-GC by a fragmentation heuristic.
- **Stop-the-world, with an optional concurrent gen 2 mark** ("background GC", §4.3). There is no concurrent ephemeral collection and no concurrent compaction.

```
            ┌────────────────────────── managed heap ──────────────────────────┐
            │  gen0 (nursery)  │  gen1  │        gen2        │  LOH  │  POH    │
 allocation ─► bump pointer     promote ──► promote ──►        ≥85,000B  pinned
            │  per-thread       on survival   on survival     direct    direct │
            │  alloc contexts                                                  │
            └──────────────────────────────────────────────────────────────────┘
```

## 2.2 Allocation: why `new` is fast and why that's the trap

Small-object allocation is a *bump-pointer* operation inside a per-thread **allocation context** — a thread-local window (default quantum 8 KB, `alloc_quantum`) carved out of gen 0. The fast path is roughly:

```
alloc_ptr += size;
if (alloc_ptr <= alloc_limit) return object;   // ~ a handful of instructions
else slow path: refill context, possibly trigger GC
```

Three consequences matter for HFT:

1. **Allocation is so cheap (≈1–4 ns) that profilers and intuition under-weight it.** The cost of an allocation is not paid at the `new`; it is paid later, amortized, as GC work — marking, copying, and the pause that hosts them. This deferred-cost structure is why "the profiler says `new Order()` is cheap" is the most common false comfort in managed low-latency work.
2. **The gen 0 budget is the GC trigger.** When cumulative gen 0 allocation exhausts the gen 0 budget (dynamically tuned; initial size is roughly tied to L2/L3 cache size per heap — on the order of 256 KB–several MB per heap for Workstation, larger for Server; overridable via `GCgen0size`/`DOTNET_GCgen0size`), the next allocation triggers a gen 0 GC. **Allocation rate therefore directly sets GC frequency**: at 100 MB/s of allocation and a 4 MB effective budget, you get ~25 gen 0 GCs per second.
3. **Every thread shares the consequences.** Any thread's allocation can trigger the GC that suspends all threads (§2.7).

Large objects (≥ 85,000 bytes) bypass this path entirely and are allocated directly on the LOH with a free-list search and zeroing cost proportional to size (§3).

## 2.3 Generational hypothesis and promotion

The design bet: most objects die young. Ephemeral GCs collect only gen 0 (and gen 1 for a gen 1 GC); survivors are **promoted** (physically relocated, in compacting collections) to the next generation. Promotion is the expensive part of a young collection — pause time for ephemeral GC is dominated by **bytes surviving**, not bytes allocated:

> **Pause(gen0) ≈ c₁·(survivors copied) + c₂·(roots scanned) + c₃·(cards scanned) + suspension overhead**

This is the single most important formula in the report. It explains the two canonical failure modes:

- **High allocation rate, low survival** (e.g., a per-message temporary object): frequent but individually short gen 0 pauses. Bad, bounded.
- **Mid-life objects** ("gen 1/gen 2 crud": objects that survive a few collections then die — caches with TTLs, queued messages, pending orders held for seconds): survive gen 0, get copied, get promoted, force gen 1 and eventually gen 2 collections, and are dead by the time gen 2 runs. This is the *worst* pattern for the generational design and is endemic to trading systems, whose natural object lifetimes (order lifetime, quote validity window, session caches) sit exactly in the mid-life band.

## 2.4 Roots, the mark phase, and what makes marking long

A collection begins by computing the root set:

- **Stack roots**: every managed thread's stack is walked using GC info emitted by the JIT, identifying slots/registers holding references at the suspension point. Cost scales with **thread count × stack depth**. Deeply recursive code and hundreds of threads directly lengthen every pause.
- **Statics and handle tables**: strong/weak/pinned GC handles, including those created by interop and by the runtime itself.
- **Finalization queue** entries.
- For ephemeral GCs, **cross-generation references** found via the card table (§2.5).

Marking then traverses the object graph from the roots, restricted to the condemned generations. For gen 2 (full) GCs the entire live graph is traversed — this is why **full-GC pause scales with live set size, not heap size**: a 10 GB heap with 200 MB live marks quickly; a 2 GB heap with 1.8 GB live in a pointer-rich graph (deep order books as object trees, per-instrument dictionaries of class instances) marks slowly. Pointer-dense data structures are a marking tax; struct-of-arrays layouts are largely invisible to the marker.

## 2.5 Card tables and write barriers: the cost the mutator pays

For an ephemeral GC to be correct without scanning gen 2, the runtime must know about **old→young references** created since the last GC. Every reference store into a heap object goes through a **write barrier** (JIT-inserted, e.g. `JIT_WriteBarrier`): in addition to the store, it marks a **card** — a byte in a side table covering (on 64-bit) a 2 KB heap range (plus "card bundles" as a second level on large heaps).

Implications:

- **Mutator-side cost**: reference writes are a few instructions more expensive than plain writes, and card marking dirties cache lines shared across threads writing to nearby objects (a real, measurable false-sharing effect in pointer-heavy multi-threaded code). Struct fields and arrays of structs containing no references skip barriers entirely — another structural argument for data-oriented design on the hot path.
- **GC-side cost**: during an ephemeral GC, all dirtied cards are scanned and the covered ranges of gen 2 are walked to find the actual cross-references. A workload that constantly mutates references in old objects (e.g., updating `Order.LastQuote` reference fields on long-lived order objects) inflates *gen 0* pause times — old-object reference churn is charged to young-collection pauses.

## 2.6 Plan, relocate, compact, sweep

After marking, the GC decides per generation whether to **compact** (slide survivors, update every reference to moved objects) or **sweep** (thread dead space into free lists). Ephemeral generations almost always compact. Gen 2 compacts only when fragmentation heuristics demand it; LOH never compacts unless asked (§3.4).

- **Compaction cost** ≈ bytes moved + references fixed up. **Pinned objects** (`fixed`, `GCHandleType.Pinned`, pinned async I/O buffers) cannot move; they fracture the compaction plan into segments, leave fragmentation behind, and on pre-regions runtimes could effectively poison an ephemeral segment. Pinning young objects is the classic self-inflicted wound (sockets pinning per-call buffers); mitigations are pre-pinned pools and the POH (§3.6).
- **Sweep cost** ≈ proportional to fragmentation/free-list construction, much cheaper than compaction but leaves the heap larger and allocation slower (free-list fit instead of pointer bump for gen 2/LOH allocation).

## 2.7 Thread suspension: the irreducible pause

The suspension machinery is where "GC pause" actually lives, and it is paid even when GC work is tiny:

1. The triggering thread signals a suspension request (`SuspendEE`).
2. Every managed thread must reach a **GC-safe point**. Threads in managed code are typically *hijacked*: fully-interruptible code is interrupted at precise points using GC info; partially-interruptible code is redirected at the next call/return via return-address hijacking. Threads already in native code (P/Invoke) are left running but fenced — they will block on return to managed code.
3. After the last thread parks, GC work runs; then threads are resumed.

Properties that matter:

- **Suspension latency is set by the slowest thread to reach a safe point.** Long-running loops without calls are compiled with GC polls or fully-interruptible info, but tight `Span`-processing loops can still add tens of microseconds of time-to-suspend. With many threads, the *rendezvous* itself (cross-thread signaling, scheduler wakeups) is commonly 20–100 µs before any GC work begins — this is the floor under every pause. **[measured — §6.3]**
- **Non-allocating threads pay anyway.** This is the structural reason thread-level isolation fails and process-level isolation (§8) is the real boundary.
- **OS interference compounds it**: if a suspended thread's core is taken by another process, or the GC thread itself gets descheduled, the pause inherits scheduler latency. Production deployments isolate cores (CPU sets/affinity, `isolcpus`-equivalents) for exactly this reason (§6.7).

## 2.8 Finalization and other pause amplifiers

- **Finalizers**: objects with finalizers that die are not freed at GC; they are *promoted* and queued for the finalizer thread, doubling their lifetime cost and adding root-scanning work. Trading code should treat finalizers as banned on hot types; `SafeHandle` for OS resources, deterministic `Dispose` everywhere else.
- **GC handles**: every `GCHandle`, dependent handle (`ConditionalWeakTable`), and interop handle is a root to scan. Libraries that allocate handles per-operation inflate every pause.
- **`GC.AddMemoryPressure` / loader heaps / assemblies**: extra triggers and roots, mostly relevant at startup; pin them down during warm-up, not steady state.
- **Stack depth & thread count**, per §2.4: both are root-scan multipliers paid on *every* collection.

## 2.9 Why "just make a pauseless GC" is not on the table

For completeness, the survey evaluated whether the CLR could adopt a pauseless/concurrent-compacting design (in the family of Azul C4, ZGC, Shenandoah). Conclusions:

- Those collectors rely on **read barriers** (load-time checks/forwarding on every reference read), trading steady-state mutator throughput (typically cited 5–15%) and significant runtime complexity for sub-millisecond max pauses. The CLR has no read barrier infrastructure; retrofitting one is a multi-year runtime project, repeatedly discussed and declined in dotnet/runtime design discussions (refs in §10).
- Even ZGC-class collectors have *allocation stall* modes under allocation rates that outrun concurrent collection — at HFT allocation discipline levels, the difference between "ZGC" and "CLR + zero allocation" largely evaporates, while the read-barrier tax does not.
- Therefore the project's premise stands: on .NET, the path is **avoidance + isolation + bounded windows**, not waiting for a runtime that doesn't pause.

## 2.10 Section summary — the pause-length model

For planning, model pause time as:

| Driver | Affects | You control it by |
|---|---|---|
| Suspension rendezvous (threads, scheduler) | every GC, ~20–100 µs floor | fewer managed threads, core isolation, process isolation |
| Stack/root scan (threads × depth, handles) | every GC | shallow stacks on hot threads, few handles |
| Card scan (old-object reference churn) | ephemeral GCs | immutable/struct-based old data, no reference rewrites in steady state |
| Survivor copy (mid-life objects) | gen 0/1, drives promotion | pools, pre-allocation, arena lifetimes |
| Live-set mark (pointer density × live bytes) | gen 2 | struct-of-arrays, fewer long-lived object graphs |
| Compaction (bytes moved, pins) | any compacting GC | POH/pre-pinned pools, stable long-lived data |
| LOH sweep/fragmentation | gen 2 | no large temporaries; `ArrayPool`; §3 |

Every mitigation in §8 maps onto one or more rows of this table; every allocation source in §7 is an input to one of these drivers.
