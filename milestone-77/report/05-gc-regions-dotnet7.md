# Section 5 — GC Regions in .NET 7+ : Architecture and Latency Implications

> Part of the FablePool research survey *"Why .NET GC Causes Latency Spikes in HFT — and What to Do About It"*.
> Cross-references: §2 (generational mechanics), §3 (LOH/POH), §4 (GC modes & latency settings), §6 (measured pauses), §8 (mitigation matrices). Citation keys (`[REF-nn]`) resolve in §9.

---

## 5.1 Why the heap layout changed at all

From .NET Framework 1.0 through .NET 6, the CLR GC managed memory in **segments**: large, contiguous virtual-address ranges (typically 256 MB–4 GB on 64-bit Server GC, depending on heap count and era) carved up internally by generation. Each Server GC heap owned an *ephemeral segment* hosting gen0 and gen1 at its tip, plus chains of gen2 and LOH segments.

Starting in **.NET 7**, the default 64-bit CoreCLR GC replaced segments with **regions**: many small, uniformly sized blocks of address space (basic size **4 MB** by default, configurable; large/pinned-object regions use a larger multiple of the basic size) drawn from one big reserved range (~256 GB of reserve by default on 64-bit, also configurable). Regions were present behind a flag in late .NET 6 builds and became the default in .NET 7 [REF-11][REF-12]. .NET 7 also shipped the prior segments implementation as a standalone GC (`clrgc.dll` / `libclrgc.so`) selectable via `DOTNET_GCName`, as an escape hatch for regressions [REF-12].

The motivation, per the GC team [REF-11]:

1. **Decouple generation size from segment geometry.** With segments, gen0's effective size was entangled with the ephemeral segment layout; resizing a generation meant moving a boundary inside a contiguous block. With regions, a generation is simply *a set of regions*, and can grow or shrink by whole regions, non-contiguously.
2. **Finer-grained memory accounting and release.** A fully empty 4 MB region can be returned to a free pool (and eventually decommitted) immediately. Segments could only be released when an *entire* segment emptied, which on big heaps was rare. Regions therefore track the live set much more closely.
3. **Better cross-heap balancing under Server GC.** Free regions live on shared free lists; a hot heap can take regions a quiet heap released. With segments, per-heap memory was much stickier.
4. **A foundation for adaptive behavior** — most notably **DATAS** (Dynamic Adaptation To Application Sizes), the .NET 8+ feature that changes the Server GC heap count at runtime (§5.6). DATAS is essentially impossible to build on the segments design.

Nothing about the *collection algorithm family* changed: it is still a generational, mostly-compacting, stop-the-world (or background, for gen2) mark/plan/relocate/compact collector as described in §2. What changed is the *unit of memory management*, and that has several concrete latency consequences.

## 5.2 Mechanics: how regions work

### 5.2.1 Address space and region kinds

At startup the GC reserves one large contiguous range and partitions it logically. Regions come in (at least) three size classes [REF-11][REF-13]:

| Region kind | Default basic size | Hosts |
|---|---|---|
| Small | 4 MB | gen0, gen1, gen2 (SOH objects < 85,000 B) |
| Large | multiple of basic size (larger) | LOH objects |
| Huge | sized to the object | single objects bigger than a large region |

Key configuration knobs (environment-variable form; `COMPlus_` prefix also works):

| Knob | Effect | HFT relevance |
|---|---|---|
| `DOTNET_GCRegionSize` | Basic region size (power of two) | Larger regions ≈ coarser accounting, fewer region transitions; rarely worth touching |
| `DOTNET_GCRegionRange` | Total reserved VA range | Only matters in constrained-VA environments |
| `DOTNET_GCName=clrgc.dll` | Revert to the segments GC (the standalone GC ships the *previous* major version's GC in .NET 7/8) | A/B testing if a regions-era regression is suspected |
| `DOTNET_GCConserveMemory` | Biases the GC to keep less free/committed memory | Generally **avoid** in HFT — trades latency for footprint |

> ⚠ Verify exact knob names/semantics against the runtime version you deploy; these are runtime-internal config switches and are documented primarily in `dotnet/runtime` sources and team blog posts [REF-11][REF-13], not in stable public docs.

### 5.2.2 Generations as region sets

Each logical generation on each heap is a linked list of regions. Allocation in gen0 proceeds bump-pointer within the current region; when it fills, the heap pulls another region from the free list (or commits a new one). After a GC, regions can be:

- **Promoted in place** — the whole region is relabeled to the next generation (cheap; no copying);
- **Compacted out of** — survivors are copied to target regions, the source region returns to the free list;
- **Swept in place** — free space within the region is threaded onto free lists (gen2/LOH style).

This per-region promote-vs-compact decision is new flexibility relative to segments, where promotion meant moving the generation boundary within a fixed block.

### 5.2.3 Free-region pools and decommit cadence

Emptied regions go to free lists (global, and effectively shared across Server GC heaps). The GC retains a budgeted number of free regions *committed* — so the next allocation burst doesn't pay page-fault/commit cost — and **decommits the excess gradually** over subsequent GCs rather than all at once [REF-11].

**Latency implication:** memory given back to the OS must be re-committed (and on first touch, soft-page-faulted) when the heap grows again. A trading process with a bursty open/close-of-session allocation profile can see regions decommitted during quiet periods and then pay commit + zeroing + page-fault costs exactly when the market re-opens. Mitigations:

- Pre-touch / warm the heap during startup to the steady-state size (allocate-and-drop a sized workload, then `GC.Collect()` once before go-live — consistent with the warm-up guidance in §8.5).
- On Windows, large pages (`DOTNET_GCLargePages=1` with `GCHeapHardLimit`) make commit coarse and pinned-resident — see §4.6.
- Keep allocation rate near zero on the hot path (§7, §8) so the steady-state region set simply never shrinks or grows.

## 5.3 Pinning behavior: the biggest practical win for HFT code

Under segments, a pinned object sitting in gen0 was poison: the GC could not move it, so it either had to **demote** surrounding survivors or leave holes that fragmented the ephemeral segment, and a long-lived pin near the segment tip could effectively wedge gen0's geometry. Network-heavy code — which is to say *all* HFT code — pins constantly: every overlapped socket receive into a non-POH `byte[]`, every `GCHandle.Alloc(..., GCHandleType.Pinned)` for interop with exchange-gateway native libraries.

Under regions, pinning is handled at **region granularity** [REF-11][REF-12]:

- A region containing pinned survivors can be *promoted in place* (relabeled gen1/gen2) without copying, while the rest of gen0's regions are reclaimed normally. The pin no longer distorts gen0's shape; gen0 simply gets fresh regions from the free list.
- Fragmentation caused by pins is confined to the pinned regions rather than smeared across a contiguous ephemeral segment.

**Measured effect** (see §6.5 for data and methodology): workloads with heavy transient pinning show materially lower gen0/gen1 pause variance on .NET 7+ regions than on .NET 6 segments — the gen0 *p99.9* tail tightens because the "pin forced an awkward compaction/demotion" pathology largely disappears. This does **not** remove the standing recommendation to put long-lived I/O buffers on the **POH** (`GC.AllocateArray<byte>(n, pinned: true)`, §3.4) — POH buffers are invisible to ephemeral collections entirely, which is strictly better — but it makes the *unavoidable* residual pinning (third-party libraries, runtime-internal pins) far less hazardous.

## 5.4 Latency-relevant behavioral changes, itemized

| Behavior | Segments (≤ .NET 6) | Regions (.NET 7+) | HFT latency impact |
|---|---|---|---|
| Gen0 geometry under pinning | Demotion/fragmentation; gen0 shape distorted | Pinned regions promoted in place; gen0 unaffected | ↓ gen0/gen1 pause variance (tail) |
| Memory release to OS | Whole-segment granularity; rare | Per-region, gradual decommit | Footprint ↓, but re-commit cost on bursts — warm the heap |
| Server GC heap balancing | Per-heap segments, sticky | Shared free-region pool | Fewer pathological "one hot heap" elongated GCs |
| Generation resizing | Boundary moves within segment | Add/remove whole regions | Gen0 budget honored more precisely → more *predictable* gen0 cadence |
| Cost model of a GC | Per-segment walks | Per-region bookkeeping (more, smaller units) | Slight constant-factor overhead; in practice noise vs. mark/copy cost |
| Card table / write barrier | Unchanged in essence | Unchanged in essence | Neutral |
| Promote-in-place option | Limited | First-class, per region | Some gen1 GCs get cheaper (relabel instead of copy) |

Two caveats observed in the wild during the .NET 7 cycle [REF-12][REF-14]:

1. **Early-regression risk.** A handful of workloads saw more gen2 collections or higher committed memory on early .NET 7 servicing releases as tuning heuristics were rebalanced for regions; most were fixed in 7.0.x servicing and in .NET 8. If you are qualifying a runtime upgrade, A/B against `DOTNET_GCName=clrgc.dll` (same runtime, segments GC) to isolate "regions" from "everything else in the new runtime."
2. **Committed-memory accounting differs.** Dashboards keyed to `Process.WorkingSet64` or container RSS will read differently after the upgrade because decommit timing changed. Recalibrate memory alerts; do not "fix" them with `GCConserveMemory` on latency-critical boxes.

## 5.5 Interaction with the settings from §4

- **Server GC + regions** is the assumed HFT baseline. Heap count still defaults to logical-core count (cap with `DOTNET_GCHeapCount` to match your isolated-core budget, §4.3); regions make a *reduced* heap count safer because memory rebalances via the shared free pool.
- **`GCHeapHardLimit` / containers**: regions respect hard limits with finer granularity; the failure mode at the limit is still aggressive gen2/compaction churn, so size limits with ≥ 2–3× live-set headroom (§4.6).
- **`SustainedLowLatency` / `LowLatency`** semantics (§4.5) are unchanged by regions.
- **Background GC** (§4.4) is unchanged in structure: BGC's two short STW phases and concurrent mark/sweep operate over region sets instead of segments; pause-relevant behavior is equivalent.

## 5.6 DATAS (.NET 8/9): adaptive heap counts — and why HFT should turn it off

**DATAS** (Dynamic Adaptation To Application Sizes) lets Server GC start with few heaps and grow/shrink the heap count at runtime based on throughput and footprint signals. It is opt-in on .NET 8 (`DOTNET_GCDynamicAdaptationMode=1`) and **enabled by default on .NET 9** for Server GC [REF-13].

DATAS is excellent for elastic services (many idle ASP.NET pods). For an HFT process it is the wrong trade:

- **Heap-count changes are a structural event**: they involve a GC and rebalancing of regions/allocation contexts across the new heap set — i.e., induced work at a time *the runtime* chooses, not you.
- DATAS biases toward **smaller gen0 budgets** when scaling down, which raises gen0 frequency (§6.3's frequency math) precisely when you wanted a fixed, characterized cadence.
- Determinism is the whole point of an HFT GC configuration. Every adaptive degree of freedom is a new axis of run-to-run variance you must re-qualify.

**Recommendation (feeds decision matrix M-2 in §8):** on .NET 9+, explicitly set `DOTNET_GCDynamicAdaptationMode=0` (or pin heap count via `DOTNET_GCHeapCount`, which disables DATAS), and record this in the deployment manifest so a runtime upgrade can't silently re-enable it.

## 5.7 Section conclusions

1. Regions change **memory management granularity**, not the collection algorithm; gen2 blocking-pause math from §2/§6 is unaffected.
2. The standout HFT win is **pinning containment**: tighter gen0/gen1 tails for socket-heavy code, and a safety net under residual non-POH pins.
3. The standout HFT risk is **adaptivity**: gradual decommit (warm the heap; keep hot-path allocation at zero) and DATAS (disable it).
4. Keep `DOTNET_GCName=clrgc.dll` in the qualification toolkit as a one-variable A/B lever when chasing upgrade regressions.
5. Net assessment: **.NET 7+ regions are a modest but real tail-latency improvement for HFT-shaped workloads**, provided adaptive features are pinned down. They do not change the strategic conclusion of this survey — pauses are bounded by what you allocate and what survives, so §7/§8 disciplines remain the primary tool.

---
*Citations: [REF-11] Maoni Stephens, "Put a DPAD on that GC!" / regions design notes; [REF-12] .NET 7 GC regions announcement & clrgc fallback; [REF-13] DATAS documentation and .NET 8/9 GC configuration docs; [REF-14] dotnet/runtime GC source and issue tracker. Full annotations in §9.*
