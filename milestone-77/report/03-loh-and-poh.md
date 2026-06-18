# 3. The Large Object Heap and the Pinned Object Heap

The LOH is where medium-sized buffer mistakes become gen 2 latency problems. This section covers its exact rules, its fragmentation behavior, compaction options, and the .NET 5+ Pinned Object Heap.

## 3.1 What goes on the LOH

- Any object whose size is **≥ 85,000 bytes** is allocated on the LOH. The threshold is bytes of the *object*, including header and (for arrays) element storage: `byte[85000 - overhead]` is the practical max small-object byte array (`byte[84988]` on 64-bit; verify per runtime with `GC.GetGeneration`).
- The threshold is configurable **upward** via `GCLOHThreshold` (`runtimeconfig.json`: `System.GC.LOHThreshold`), up to large values; raising it moves big allocations into the ephemeral path — occasionally useful, usually just relocates the pain (large gen 0 copies).
- Historical special case: on .NET Framework x86, `double[]` of ≥ 1,000 elements (~8 KB) went to the LOH for alignment reasons. Gone on 64-bit .NET (Core); noted because old advice still circulates.
- LOH allocation is **not** bump-pointer: it takes a free-list search (best-fit among free blocks) plus memory zeroing proportional to size. A 1 MB LOH allocation costs tens of microseconds in zeroing alone — *on the allocating thread, synchronously*. Large allocations are a direct hot-path latency cost even before any GC happens.

## 3.2 Collection semantics

- The LOH is collected **only during gen 2 collections**. There is no "LOH-only" GC. Dead LOH objects are unreachable but their memory is unrecoverable until the next gen 2 GC.
- Allocating on the LOH **counts toward gen 2 GC triggering**: sustained LOH allocation drives gen 2 frequency directly. A system that allocates a 256 KB snapshot buffer per market-data burst is scheduling its own gen 2 collections.
- During gen 2 GC, the LOH is **swept by default**: dead objects become free-list entries; live objects do not move.

## 3.3 Fragmentation: the slow-motion failure

Because the LOH sweeps, it fragments. The canonical death spiral, repeatedly observed in long-running services:

1. The app allocates large buffers of *varying* sizes (serialization buffers, growing `List<T>` backing arrays crossing 85 KB, `MemoryStream` internals).
2. Frees leave holes; new allocations need slightly different sizes; best-fit splits holes into smaller, less useful fragments.
3. The LOH's committed size grows (fragmentation can reach 50–80% of LOH size), gen 2 GCs get more frequent (memory pressure) and longer (more to sweep), and in container-limited deployments the process can hit OOM with most of its heap technically free.

For HFT the relevant symptom is not OOM but the **gradual lengthening of gen 2 pauses over a trading day** — a system that passed latency tests at 9:30 fails them at 15:55. Fragmentation metrics (`GC.GetGCMemoryInfo().FragmentedBytes`, ETW `GCHeapStats`) must be part of the monitoring baseline (§6.2).

## 3.4 Compacting the LOH on demand

Since .NET Framework 4.5.1 / all .NET Core:

```csharp
GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
GC.Collect(); // the next blocking gen 2 GC compacts the LOH, then the flag resets
```

- Compacting a fragmented multi-GB LOH can take **hundreds of milliseconds to seconds** — this is a maintenance operation, never a trading-hours operation. Production pattern: run it at session boundaries (post-close, pre-open) as part of a scheduled "heap hygiene" step, together with `GC.Collect(2, GCCollectionMode.Aggressive)` (.NET 7+, which also compacts and returns memory).
- Under `GCHeapHardLimit` (container limits), the runtime will compact the LOH automatically when nearing the limit — meaning **a memory-constrained deployment can experience surprise LOH-compaction pauses**. Size containers so this never engages during trading hours, or pre-empt it at scheduled times.

## 3.5 LOH design rules for trading systems

1. **Never allocate ≥85 KB in steady state.** All large buffers are allocated at startup or rented from `ArrayPool<T>.Shared` (whose large buckets are pooled precisely to avoid LOH churn).
2. **Beware accidental LOH crossings**: `List<T>` doubling (a `List<long>` crosses at ~10,600 elements → backing array ≥85 KB), `Dictionary` resizes, `MemoryStream.ToArray()`, `string` of ≥ ~42,500 chars (concatenated FIX logs!), `StringBuilder.ToString()` on big builders, LINQ `ToArray()` on large sequences.
3. **Uniform sizes if you must allocate large**: power-of-two pooled buffer sizes make free-list reuse nearly perfect and fragmentation negligible.
4. **Monitor** `FragmentedBytes` and LOH size; alert on intraday growth trends, not absolute values.

## 3.6 The Pinned Object Heap (.NET 5+)

Pinning is endemic to trading I/O: every overlapped socket receive, RDMA registration, and native interop call needs buffers that don't move. Pre-.NET 5, pinned buffers lived in the ephemeral/normal heap and **blocked compaction around them** — pinned young objects were a notorious source of gen 0 fragmentation and degenerate heap layouts.

The POH fixes this structurally:

```csharp
byte[] buf = GC.AllocateArray<byte>(64 * 1024, pinned: true);
// or uninitialized, skipping zeroing:
byte[] buf2 = GC.AllocateUninitializedArray<byte>(64 * 1024, pinned: true);
```

- POH objects **never move** (no `fixed`/`GCHandle` needed to take stable pointers — `Unsafe`/`MemoryMarshal` address-taking is safe for their lifetime) and never obstruct compaction of other heaps.
- The POH is collected with gen 2, swept like the LOH; the same fragmentation logic applies, so the same rule applies: **allocate POH buffers once at startup, uniform sizes, pool them, never churn them.**
- Only arrays of blittable-ish element types (no references) can be pinned-allocated.
- This is the project's blessed mechanism for I/O buffer pools, replacing both per-call `fixed` pinning and `GCHandleType.Pinned` on ordinary arrays.

## 3.7 Section summary

| Concern | Pre-mitigation behavior | Rule |
|---|---|---|
| Large temporaries | LOH churn → gen 2 frequency + sweep time | startup allocation + `ArrayPool` |
| Varying large sizes | fragmentation → intraday pause growth | uniform/pow2 pooled sizes |
| Fragmented LOH | growing heap, longer gen 2 | scheduled `CompactOnce` off-hours |
| Pinned I/O buffers | compaction obstruction, gen 0 fragmentation | POH (`AllocateArray(pinned: true)`) at startup |
| Container limits | surprise auto-compaction near limit | headroom + scheduled hygiene |
