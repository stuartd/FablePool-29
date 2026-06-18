# 01 — Memory Ownership Model

## 1. Purpose

Allocation-free code is easy to write once and hard to keep correct, because C# gives you no
borrow checker. This document defines the ownership model that substitutes engineering discipline
plus mechanical checks for language enforcement: **every region of memory has exactly one owner at
every instant, ownership transfers are explicit, and views (spans) never outlive their owner.**

The model is deliberately small: four ownership roles, three transfer operations, and one handle
type family. Everything in docs 02–06 is an instance of this model.

## 2. Ownership roles

| Role | Holds | Responsibilities |
|------|-------|-----------------|
| **Allocator** | The slab/arena itself | Reserve memory at startup; never reclaim piecemeal; reclaim wholesale at epoch reset or shutdown |
| **Owner** | Exclusive write right to a region | Exactly one at a time; may write; must eventually `Release` (pooled), `Publish` (ring), or let the epoch reset reclaim (arena) |
| **Borrower** | Temporary read or read/write view (`Span<T>`, `ref`) | Must not store the view in any field, capture it, or hold it across a yield/await/release; lifetime strictly nested inside owner's |
| **Auditor** | Read-only access for telemetry from another thread | May read only fields documented as torn-read-safe (single word, or versioned via seqlock); never takes spans into mutable regions |

**Rule M-1:** A region has exactly one Owner. Concurrent writers are forbidden; cross-thread
handoff is only via the three transfer operations below.

**Rule M-2:** A Borrower's lifetime is lexically nested within the operation that produced the
borrow. In code-review terms: a `Span<T>` or `ref` obtained from owned memory may live in locals
and be passed *down* the call stack, never *up* (except as a return from a pure slicing helper
over a caller-supplied span) and never *sideways* into fields, statics, captured closures, or
other threads. (Full rules: doc 04.)

## 3. Transfer operations

There are exactly three ways ownership moves:

### 3.1 `Acquire` / `Release` (pools — L2)

```csharp
// Normative API shape (full spec in doc 02)
if (!pool.TryAcquire(out Handle<OrderState> h)) { /* exhaustion policy, doc 02 §6 */ }
ref OrderState s = ref pool.Get(h);   // borrow, nested in current scope
// ... mutate s ...
pool.Release(h);                       // ownership returns to pool; h is dead
```

- `TryAcquire` transfers ownership pool → caller.
- `Release` transfers caller → pool and **invalidates the handle** (generation bump, §5).
- Using a handle after `Release` is a *stale handle* fault, detected in checked builds (§5.3).

### 3.2 `Claim` / `Publish` // `Peek` / `Commit` (rings — producer/consumer sides)

```csharp
// Producer: claim a slot, write in place, publish.
if (ring.TryClaim(out RingSlot slot)) {
    slot.Span.WriteOrderNew(in order);  // borrow of slot memory
    ring.Publish(slot);                 // ownership → consumer side
}
// Consumer: peek the next published slot, read in place, commit.
while (ring.TryPeek(out RingSlot slot)) {
    Process(slot.Span);                 // borrow
    ring.Commit(slot);                  // ownership → producer side (slot reusable)
}
```

- Between `Claim` and `Publish`, the producer is Owner.
- Between `Publish` and `Commit`, the consumer is Owner.
- After `Commit`, the slot's memory may be rewritten at any time: **any span over it is dead.**
  This is the single most dangerous lifetime in the system and gets its own enforcement
  (doc 04 §5, doc 05 §7).

### 3.3 `Reset` (arenas — L1)

Arenas are bump allocators. Individual objects are never freed; the **epoch controller** is the
sole party allowed to call `arena.Reset()`, and only at an epoch boundary when all hot threads
have parked at the epoch barrier (doc 06 §5). Reset transfers everything in the arena back to the
Allocator at once.

**Rule M-3:** No other reclamation paths exist. There is no `Dispose` of individual hot-path
objects, no reference counting, no finalizers (finalizers are banned outright on hot-path types —
they resurrect objects and run on the finalizer thread, both unacceptable).

## 4. Why handles instead of object references

Pooled and arena objects are addressed by `Handle<T>` (a `readonly struct` wrapping a `ulong`),
not by C# references. Rationale:

1. **GC scan pressure.** A million live `OrderState` class instances referencing each other give
   the GC a million-node graph to trace if it ever runs. Structs-in-slabs addressed by handles
   present the GC with *one* object (the slab array) or *zero* (native arena).
2. **Cache density.** Slab storage is contiguous; handle = index means neighbors are prefetchable.
3. **Stale-use detection.** Generation counters (§5) catch use-after-release deterministically in
   checked builds — something raw references can't do without weak-reference overhead.
4. **Serializable identity.** A handle is a plain integer: it can cross a ring buffer, be logged,
   and be reconstructed, with no GC interaction.

Cost: an extra indirection through the slab base, and the loss of polymorphism (acceptable — hot
path types are sealed structs by design, doc 03).

## 5. `Handle<T>` specification

### 5.1 Layout

```csharp
namespace Fp.HotPath.Memory;

/// 64-bit handle: [ 8 bits poolId | 24 bits generation | 32 bits index ]
public readonly struct Handle<T> : IEquatable<Handle<T>> where T : struct
{
    private readonly ulong _bits;

    public const int IndexBits = 32, GenerationBits = 24, PoolIdBits = 8;

    public uint  Index      => (uint)_bits;
    public uint  Generation => (uint)((_bits >> 32) & 0xFF_FFFF);
    public byte  PoolId     => (byte)(_bits >> 56);
    public bool  IsNull     => _bits == 0;

    public static readonly Handle<T> Null = default;

    internal Handle(byte poolId, uint generation, uint index)
        => _bits = ((ulong)poolId << 56) | ((ulong)(generation & 0xFF_FFFF) << 32) | index;

    public bool Equals(Handle<T> other) => _bits == other._bits;
    public override bool Equals(object? obj) => obj is Handle<T> h && Equals(h); // cold-path only: boxes
    public override int GetHashCode() => _bits.GetHashCode();
    public static bool operator ==(Handle<T> a, Handle<T> b) => a._bits == b._bits;
    public static bool operator !=(Handle<T> a, Handle<T> b) => a._bits != b._bits;
}
```

- **Index** (32 bits): slot index within the pool/arena slab. 4 G slots per pool is ample.
- **Generation** (24 bits): incremented on every `Release` of the slot. Stale handles mismatch.
  24 bits wrap after 16.7 M reuses of a single slot; at 1 M reuses/sec of one slot (absurdly
  pathological) that is a ~16 s ABA window — accepted, since checked-build detection plus the
  rarity of same-slot hammering makes a wrapped false-match practically unobservable. Documented
  as residual risk in doc 09 §4.3.
- **PoolId** (8 bits): identifies which pool issued the handle, so a handle can be resolved by a
  central registry (`PoolRegistry.Resolve<T>(handle)`) and so cross-pool misuse is detectable.
- **Null handle** is all-zero: pool slot 0 of pool 0 is reserved/never issued so that `default`
  is always invalid.

### 5.2 Resolution

```csharp
public ref T Get(Handle<T> h);          // checked in Debug/Checked builds; unchecked in Release-Fast
public bool TryGet(Handle<T> h, out RefView<T> view); // always-checked variant for auditor threads
```

`Get` returns `ref T` into the slab — a Borrower view subject to Rule M-2. In particular,
**a `ref` from `Get` MUST NOT be held across `Release` of the same handle** or across any
operation that could reset the arena.

### 5.3 Build flavors

| Build | Handle checks | Use |
|-------|--------------|-----|
| `Debug` | Full: generation match, pool-id match, index bounds, owner-thread assert | Development |
| `Checked` (Release + `FP_CHECKED`) | Generation + bounds (branch, ~1–2 ns) | Soak rigs, UAT, canary prod |
| `Release-Fast` | Bounds via slab mask only (power-of-two slabs); generation check compiled out | Latency-critical prod |

The decision to run production in `Checked` vs `Release-Fast` is per-desk; the design recommends
`Checked` until a deployment has 90 soak-days without a single handle fault, then `Release-Fast`
with `Checked` canaries. Conditional compilation uses `[Conditional("FP_CHECKED")]` validation
methods so Release-Fast pays zero cost.

## 6. Ownership of the buffers themselves (L0)

The slabs, ring memory, and arena blocks are owned by a single composition root:
`HotPathRuntime` (doc 11). It:

1. Allocates all L0 memory during startup, before sealing — managed slabs are allocated on the
   **POH (Pinned Object Heap)** via `GC.AllocateArray<T>(length, pinned: true)` (so native code
   and `Span` interop never need pinning handles, and the buffers never move); native blocks via
   `NativeMemory.AlignedAlloc` (doc 06).
2. Registers every region with the `MemoryMap` (name, base, length, lifetime class) — used by
   diagnostics, the breach dumper (doc 09), and the CI footprint report.
3. Frees native memory only at process shutdown, after all pinned threads have joined.
   Managed slabs are simply released to the GC at shutdown (irrelevant — process is exiting).

**Rule M-4:** No component other than `HotPathRuntime` may call `NativeMemory.*` or
`GC.AllocateArray(pinned: true)`. Enforced by an analyzer rule (doc 08 §6) and code review.

## 7. Cross-thread ownership rules

- **Rule M-5:** Ownership transfer between threads happens **only** via ring `Publish`/`Commit`
  (which contain the necessary release/acquire fences — doc 05 §5) or via the epoch barrier.
  Handing a handle to another thread through any other channel (a field, a `ConcurrentQueue`,
  etc.) is forbidden on the hot path.
- **Rule M-6:** A handle traveling through a ring carries ownership of the referenced slot with
  it. The sender MUST NOT touch the slot after publishing the handle. Pattern: order-state
  handles flow Strategy → TX inside `OrderCmd` messages; TX becomes the owner of that
  `OrderState` until it publishes an ack event handing it back.
- **Rule M-7 (auditor reads):** Telemetry threads read shared counters via `Volatile.Read` on
  single-word fields, or via the `SeqLock<T>` utility (doc 11) for multi-word snapshots. They
  never resolve handles into mutable pooled state in Release builds.

## 8. Ownership state machines (textual diagrams)

### Pooled object (L2)

```
            TryAcquire                Release
  [InPool] ───────────► [Owned(thread T)] ───────────► [InPool, gen+1]
                            │      ▲
              publish handle│      │ack/return message
              via ring (M-6)▼      │
                       [Owned(thread U)]
```
Illegal transitions (checked builds fault, Release-Fast undefined → hence soak in Checked):
`Release` from non-owner; `Get` while InPool; double `Release`.

### Ring slot

```
        TryClaim          Publish          TryPeek..Commit
  [Free] ──────► [ProducerOwned] ──────► [Published] ──────► [Free]
```
The cycle is strictly unidirectional; `RingSlot` is a `ref struct` so it cannot be stored,
making "touch after Publish/Commit" syntactically hard (and semantically checked in Debug via
slot sequence validation).

### Arena object (L1)

```
       arena.Alloc<T>()                       epoch Reset (all threads parked)
  [Unallocated] ──────► [Owned, lives whole epoch] ──────► [Unallocated]
```

## 9. Interaction with the GC (what the collector sees)

After sealing, the managed heap reachable graph is: the `HotPathRuntime` root → a fixed set of
POH slab arrays + a fixed set of long-lived service objects created at startup. No new objects,
no graph mutation that creates new edges to new objects. Consequences:

- Gen0/Gen1 budgets are never consumed by hot threads → no collections triggered by them.
- If a cold-path collection ever occurs, hot-path slabs are POH/Gen2-resident and pre-pinned:
  they are not copied, and card-table noise is minimal because slab arrays of pure structs
  contain **no references** (Rule: pooled/arena structs MUST be unmanaged — no reference-type
  fields; `where T : unmanaged` is enforced at the pool/arena API level where possible, and by
  analyzer FP0007 for message types).
- Therefore even a worst-case accidental Gen2 has a small scan set — defense in depth, not an
  excuse to allocate.

## 10. Summary of rules

| Rule | Statement |
|------|-----------|
| O-1 | Hot path touches only L0–L3 memory; no managed allocation after seal |
| M-1 | Exactly one Owner per region at any instant |
| M-2 | Borrows are lexically nested; spans never stored or passed up/sideways |
| M-3 | Only three reclamation paths: pool Release, ring Commit, arena Reset |
| M-4 | Only `HotPathRuntime` allocates L0 memory |
| M-5 | Cross-thread transfer only via rings or epoch barrier |
| M-6 | A handle in a ring message carries ownership of its referent |
| M-7 | Auditors read via volatile single words or seqlocks only |
| M-8 | Pooled/arena element types are `unmanaged` structs (no reference fields, no finalizers) |
