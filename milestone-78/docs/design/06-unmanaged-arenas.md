# Design Doc 06 — Unmanaged Memory Arenas

Status: Final draft for review
Depends on: 01-memory-ownership-model.md, 04-span-memory-usage-rules.md, 05-ring-buffers.md
Audience: Core engine engineers; `FablePool.Memory` owners

---

## 1. Why unmanaged arenas

Even with zero steady-state allocation, *managed* pre-allocated memory still costs
us: large pinned arrays fragment the POH/LOH story, every reference field drags GC
mark phases over our data, and we cannot control NUMA placement or page size of
managed allocations. The hot path therefore keeps its bulk data — ring slabs, book
state, pooled buffers, scratch space — in **unmanaged arenas**: large slabs
obtained from the OS at startup, carved by bump allocation, never freed until
shutdown (or reset wholesale at safe points).

What stays managed: the small, fixed object graph of engine classes (rings,
sessions, strategies — a few thousand objects, all gen2-resident after warmup),
and everything off the hot path. The GC has almost nothing to scan that we own.

## 2. Arena taxonomy

| Arena | Lifetime | Reset? | Typical size | Pages |
|---|---|---|---|---|
| `StaticArena` | process | never | 1–8 GiB | huge/large pages, NUMA-bound |
| `FrameArena` (per engine thread) | one processing frame | every frame | 1–16 MiB | normal, NUMA-local |
| `SessionArena` (per venue session) | session login→logout | on session reset | 64–256 MiB | normal, NUMA-local |

- **StaticArena** holds ring slabs, order books, pool slabs, symbol tables.
  Allocation from it is *startup-phase only* (enforced: `Alloc` fail-fasts after
  `LifecyclePhase.Trading` begins).
- **FrameArena** is per-thread scratch: temporary decode buffers, candidate-order
  scratch, sort workspace. Reset (pointer rewind) at frame end — O(1), no
  destructor walk, because **only unmanaged types may live in arenas**.
- **SessionArena** holds per-session resend buffers and recovery state; reset on
  disconnect before re-login (a non-hot event).

## 3. Region layout inside `StaticArena`

Startup carves named regions so monitoring and core dumps are navigable:

```
StaticArena (e.g. 4 GiB, node 0)
├── Region "Rings"        — all ring slabs (Doc 05)
├── Region "Books"        — order book arrays per symbol (fixed depth ladders)
├── Region "Pools"        — BufferPool slabs (Doc 02)
├── Region "Symbols"      — symbol/instrument static tables
└── Region "Guard"        — trailing guard page(s)
```

Each region begins on a page boundary; debug builds insert a PROT_NONE/PAGE_NOACCESS
guard page between regions so cross-region overruns SIGSEGV/AV immediately instead
of corrupting neighbors.

## 4. API specification

```csharp
namespace FablePool.Memory;

public sealed unsafe class Arena : IDisposable
{
    public static Arena Create(in ArenaOptions options);

    public string Name { get; }
    public nuint  Capacity { get; }
    public nuint  Used { get; }          // monitoring
    public ulong  Epoch { get; }         // increments on Reset (Doc 04 §3.2)
    public int    NumaNode { get; }

    /// Bump-allocate raw bytes with alignment (power of two, default 64).
    /// Throws ArenaExhaustedException — startup/config error, fail-fast.
    public void*  Alloc(nuint bytes, nuint alignment = 64);

    /// Typed allocation helpers; T constrained to unmanaged.
    public ref T          Alloc<T>() where T : unmanaged;
    public Span<T>        AllocSpan<T>(int count) where T : unmanaged;
    public ArenaSpan<T>   AllocTracked<T>(int count) where T : unmanaged; // debug epoch checks

    /// Rewind to empty. O(1). Increments Epoch. Debug builds memset 0xCD.
    public void Reset();

    /// Pre-fault every page (write one byte per page). Warmup only.
    public void TouchAllPages();

    public void Dispose();   // shutdown only; munmap/VirtualFree
}

public readonly struct ArenaOptions
{
    public required string Name { get; init; }
    public required nuint  CapacityBytes { get; init; }
    public int  NumaNode { get; init; }            // -1 = no binding
    public bool UseLargePages { get; init; }       // 2 MiB pages where available
    public bool LockInMemory { get; init; }        // mlock / VirtualLock
    public bool GuardPages { get; init; }          // debug overrun detection
}

/// A named sub-range of an arena; same Alloc API, allocations bounded to region.
public sealed unsafe class ArenaRegion
{
    public static ArenaRegion Carve(Arena arena, string name, nuint bytes);
    public void* Alloc(nuint bytes, nuint alignment = 64);
    public Span<T> AllocSpan<T>(int count) where T : unmanaged;
}
```

### 4.1 Constraints enforced at runtime

- `T : unmanaged` is compile-time; additionally `AllocSpan<T>` asserts
  `RuntimeHelpers.IsReferenceOrContainsReferences<T>() == false` in debug as a
  belt-and-braces check against future constraint erosion via generics.
- `StaticArena.Alloc` after warmup ⇒ `FailFast` (debug) / `ArenaPhaseViolation`
  telemetry + reject (release, configurable; default fail-fast — see Doc 10 §4.2).
- Alignment: default 64 (cache line); ring slabs request page alignment.

## 5. OS integration

Acquisition (in `FablePool.Memory.Native`, the single P/Invoke surface):

- **Linux**: `mmap(MAP_PRIVATE|MAP_ANONYMOUS)`; `madvise(MADV_HUGEPAGE)` when
  `UseLargePages` (THP) or explicit `MAP_HUGETLB` when hugetlbfs pool configured;
  `mbind`/`numa_tonode_memory` (via libnuma if present, else
  `set_mempolicy` + first-touch by a thread pinned to the target node);
  `mlock` when `LockInMemory`.
- **Windows**: `VirtualAlloc(MEM_RESERVE|MEM_COMMIT)`; `MEM_LARGE_PAGES` with
  `SeLockMemoryPrivilege` when configured; `VirtualAllocExNuma` for node binding;
  `VirtualLock` when configured.
- Fallback chain: large pages → normal pages with a warning; NUMA bind → first-touch
  placement with a warning. Failures to *allocate at all* are startup-fatal.

First-touch discipline: even when explicit binding is available we still
`TouchAllPages()` from a thread pinned to the consuming core during warmup, which
both faults pages and validates placement (`move_pages` query in debug on Linux,
logged to startup report).

## 6. Interaction with the GC

- Arena memory is invisible to the GC: no roots, no scanning, no pinning needed.
- **Rule R-06-01: no managed references stored in arena memory, ever.** Enforced by
  the `unmanaged` constraint; references would be untracked and break the GC.
- Conversely, managed objects may hold *pointers/handles* into arenas freely
  (e.g. ring classes hold their slab pointer).
- `GC.AddMemoryPressure` is **not** called for arenas: we don't want the GC
  reacting to memory it can't reclaim; arena sizing is accounted for in deployment
  capacity planning instead.

## 7. Debug hardening

Debug/soak builds enable:

1. **Guard pages** between regions and after each arena (overrun ⇒ immediate AV
   with exact faulting address).
2. **Epoch-checked spans** (`ArenaSpan<T>`, Doc 04 §3.2) — use-after-reset traps.
3. **Poison on reset**: `Reset()` fills with `0xCD`; reading poisoned floats/prices
   produces conspicuous values caught by sanity asserts.
4. **Allocation logging**: every startup-phase `Alloc` recorded
   (name, size, caller) into the startup report for capacity review.

Release builds compile all four to nothing.

## 8. Failure modes (summary; full analysis in Doc 10)

| Failure | Detection | Response |
|---|---|---|
| Arena exhausted at startup | `Alloc` throws | Fatal: config undersized; refuse to start |
| FrameArena exhausted in trading | `Alloc` throws | Fail-fast thread → engine halt + cancel-all (a frame needing more than configured scratch is a logic bug) |
| NUMA bind unavailable | startup probe | Warn + first-touch fallback; startup report flags it |
| Large pages unavailable | startup probe | Warn + normal pages; latency budget note in report |
| Use-after-reset | debug epoch check / poison | Fail-fast in debug; unreachable in release if soak passes |

## 9. Test plan

- Alignment/exhaustion/reset unit tests including `nuint` overflow edges.
- Multi-threaded FrameArena tests asserting per-thread isolation (arenas are
  single-threaded by ownership; debug asserts owning thread id).
- NUMA placement integration test (Linux, `move_pages` verification) — runs in the
  perf lab, not CI.
- Guard-page tests: deliberate overrun in debug must AV and be caught by the
  crash-handler test harness.
- Epoch tests: span captured before `Reset()` must throw on `Get()` in debug.
