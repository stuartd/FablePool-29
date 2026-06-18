# 02 — Object Pooling Strategy

## 1. Scope and philosophy

Pools serve lifetime class **L2**: objects with dynamic, message-driven lifetimes that don't fit
the epoch model — working orders, in-flight requests, timer entries, retry records. The design
principle is **pools are pre-sized, never grow, never shrink, never allocate after seal**, and
exhaustion is a *first-class designed behavior* (§6), not an afterthought.

We do **not** use `Microsoft.Extensions.ObjectPool` or `ArrayPool<T>.Shared` on the hot path:
both may allocate on miss (`ArrayPool.Shared.Rent` allocates a new array when a bucket is empty;
`DefaultObjectPool` allocates via its policy), neither gives generation-checked handles, and
neither provides exhaustion telemetry to our contract. `ArrayPool<T>` remains acceptable on cold
paths.

## 2. Pool taxonomy

| Pool kind | Element | Discipline | Used for |
|-----------|---------|-----------|----------|
| `StructPool<T>` | `unmanaged` struct in a slab | Single-threaded (one owner thread) | Per-thread state records (e.g., strategy's order intents) |
| `SharedStructPool<T>` | `unmanaged` struct in a slab | Multi-thread acquire/release, lock-free freelist | Objects whose acquire and release happen on different pinned threads (e.g., `OrderState`: acquired by Strategy, released by TX after fill/cancel-ack) |
| `BufferPool` | Fixed-size byte blocks in one slab | Single- or multi-threaded variants | Scratch encode buffers, snapshot staging |

All three are thin variations on one mechanism: a pre-allocated slab + a freelist of indices +
generation counters. There is deliberately no general-purpose `ClassPool<T>`: pooling reference
types reintroduces GC graph edges and accidental retention; if a design seems to need one, the
type gets converted to an unmanaged struct or moved off the hot path.

## 3. `StructPool<T>` specification (single-threaded)

```csharp
namespace Fp.HotPath.Pooling;

public sealed class StructPool<T> where T : unmanaged
{
    // Construction is pre-seal only (allocates the slab). Capacity is rounded up to a power of two.
    public StructPool(byte poolId, int capacity, string name);

    public int Capacity { get; }
    public int InUse { get; }              // exact; single-threaded
    public int HighWaterMark { get; }      // max InUse since start; exported to telemetry

    /// Acquire a free slot. O(1): pops the freelist head. Returns false when exhausted.
    /// The slot's memory is NOT zeroed on acquire (perf); callers must fully initialize
    /// (message/codec layer guarantees this; see doc 03 §6). ZeroOnRelease is a config option
    /// for pools holding sensitive data.
    public bool TryAcquire(out Handle<T> handle);

    /// Resolve to a borrow. Checked per build flavor (doc 01 §5.3).
    public ref T Get(Handle<T> handle);

    /// Return the slot. Bumps generation. Faults on double-release in checked builds.
    public void Release(Handle<T> handle);

    /// Pre-seal only: touch every page, optionally run an init function per slot.
    public void Prefault(Action<int>? initSlot = null);
}
```

### 3.1 Internal layout (normative)

- Slab: `T[] _slab = GC.AllocateArray<T>(capacity, pinned: true);`
- Generations: `uint[] _gen` (same length, also pinned at startup — pinning irrelevant here but
  keeps everything POH-resident and out of Gen0 statistics).
- Freelist: intrusive — a `int[] _nextFree` array plus `_freeHead`; index `-1` = end. We use a
  side array (not embedding next-pointers in `T`) so element layout stays exactly the domain
  layout and a released slot's memory can be poisoned in checked builds (`0xDD` fill) to surface
  use-after-release immediately.
- `Get` in Release-Fast: `return ref _slab[(int)h.Index & _mask];` — one AND, one bounds-elided
  array access (the mask makes the JIT drop the bounds check when `_mask` is `capacity-1` and
  the array length is the matching power of two; we verify codegen in the perf test suite).

### 3.2 Why slot memory is not zeroed on acquire

Zeroing is O(sizeof(T)) on the hot path. Instead: (a) message structs are fully written by their
constructor-equivalent `Init` methods, enforced by analyzer FP0010 ("all fields assigned in Init");
(b) checked builds poison on release, so any read-before-init reads `0xDDDD…` and trips field
validators. Pools holding partially-initialized-by-design records opt into `ZeroOnRelease`.

## 4. `SharedStructPool<T>` specification (multi-threaded)

Same surface as `StructPool<T>` with these differences:

- **Freelist is a Treiber stack on indices** with the generation embedded in the CAS word to
  defeat ABA: head word = `[32b generation-of-head | 32b index]`, advanced with
  `Interlocked.CompareExchange(ref long)`. Acquire/release are wait-free in the absence of
  contention and lock-free under it; worst case is a few CAS retries — bounded in practice
  because at most a handful of pinned threads share a pool.
- `InUse` is approximate (relaxed counter via `Interlocked.Increment/Decrement`); exact only at
  barriers.
- **Cache-line discipline:** the head word, the counters, and the slab live on separate cache
  lines (`[StructLayout]` padding on an internal `Pads` struct) to prevent false sharing between
  the acquiring and releasing threads.
- Per-thread **release buffers** are explicitly *not* used (a classic optimization): they add a
  reclamation delay that complicates exhaustion accounting. Revisit only if CAS contention shows
  up in the perf rig.

## 5. Sizing methodology (normative process, doc 09 cross-ref)

Every pool's capacity is a config value derived as follows and recorded in the deployment's
`memory-budget.toml`:

1. **Model the population.** For each pool, identify the count driver. Example, `OrderState`:
   `max working orders per symbol × symbols traded × safety factor for in-flight transitions`.
2. **Bound it from exchange/risk limits**, not from observed behavior: message-rate throttles,
   max-open-orders limits, and the risk layer's own caps give hard ceilings. If no hard ceiling
   exists, the risk layer MUST impose one — an unbounded population is a design error.
3. **Multiply by 2** (headroom factor; covers transient double-occupancy during handoffs, e.g.,
   order replaced while cancel in flight).
4. **Validate in soak:** `HighWaterMark / Capacity` MUST stay ≤ 0.5 in the worst recorded soak;
   the telemetry exporter publishes this ratio and the CI soak gate (doc 08 §7) fails above it.

Memory cost is reported at startup by `MemoryMap.Report()` and checked against the budget file —
a pool that silently grows in a config change gets caught at deploy review.

## 6. Exhaustion policy (first-class design)

`TryAcquire == false` is a *designed state* with a per-pool declared policy:

| Policy | Behavior | Appropriate for |
|--------|----------|-----------------|
| `RejectNew` | Caller declines the triggering action (e.g., strategy skips the new order; counter incremented) | Order/intent pools — the safe default: refusing to send a *new* order is always safe |
| `ShedOldest` | Caller frees a victim chosen by domain logic (e.g., cancel oldest passive order, reclaim its state) | Quote-heavy strategies that can always reduce exposure to make room |
| `Breach` | `HotPathContract.Breach(PoolExhausted)` → kill switch | Pools whose exhaustion implies the sizing model is broken (e.g., ack-tracking pool: exhaustion means the exchange owes us more acks than we believe possible) |

Policies are declared at construction (`PoolExhaustionPolicy` enum + optional victim callback set
pre-seal) so reviewers see the decision next to the sizing constant. Every exhaustion event
increments `pool.exhaustions` (telemetry) regardless of policy; **any nonzero value pages the
desk** — policy handling is a parachute, not a steady state.

## 7. Leak detection

A leak in this design = a handle acquired and never released; the pool drains over hours.
Detection layers:

1. **High-water-mark drift alarm.** Telemetry exports `InUse` and `HighWaterMark` per pool every
   second. A monotonic `InUse` floor rising over a window with flat business activity (open
   orders flat, but `OrderState.InUse` rising) triggers a warning. The alert rule ships with the
   telemetry package (doc 11).
2. **Epoch audit.** At each epoch barrier (all threads parked), pools can enumerate in-use slots
   (`AuditInUse(Action<uint index>)`, barrier-only API). Each domain registers a reconciler:
   e.g., every in-use `OrderState` must be reachable from the working-orders index. Orphans are
   logged with the slot's `LastAcquireSite` —
3. **Acquire-site tagging (checked builds only).** In `FP_CHECKED`, `TryAcquire` records a
   16-bit caller site id (source-generated table of call sites) into a side array, making epoch
   audit reports actionable without allocation.
4. **Soak gate.** The 24 h CI soak (doc 08 §7) requires every pool's `InUse` at end-of-soak
   barrier to equal its reconciled expected count.

There is deliberately **no GC-based safety net** (no finalizer that returns leaked slots): M-8
bans finalizers, and a silent self-heal would mask the bug the audit exists to catch.

## 8. `BufferPool` specification

Fixed-block byte pool for scratch buffers (encode staging, compression scratch, FIX string
assembly during migration phase):

```csharp
public sealed class BufferPool
{
    public BufferPool(byte poolId, int blockSize, int blockCount, string name,
                      BufferPoolConcurrency concurrency); // SingleThread | Shared

    public bool TryAcquire(out BufferLease lease);   // lease: ref struct, see below
    public void Release(in BufferHandle handle);
}

/// ref struct so a lease cannot be stored; expose handle explicitly for the
/// (rare, M-6-governed) case of passing buffer ownership through a ring.
public ref struct BufferLease
{
    public Span<byte> Span { get; }       // exactly blockSize bytes
    public BufferHandle Handle { get; }   // plain struct: (poolId, gen, index)
    public void Dispose();                // releases if not detached
    public BufferHandle Detach();         // caller takes over release duty (ownership transfer)
}
```

Block size is uniform per pool (no buddy/bucket scheme): heterogeneous needs get separate pools
(`BufferPool encode64k`, `BufferPool scratch4k`), keeping fragmentation impossible and accounting
exact.

## 9. What does NOT get pooled

- **Strings**: banned on the hot path entirely (doc 03 §7 defines `Symbol`, `ClOrdId` as inline
  fixed-size structs; doc 04 §6 covers text formatting into spans).
- **Arrays as messages**: messages are structs in rings, not pooled arrays.
- **Tasks/delegates**: no async, no delegate creation after seal (doc 07's threading model has no
  thread pool on the hot path; callbacks are registered pre-seal into fixed dispatch tables).
- **Order book nodes**: these are L1 (arena) — they live a whole session; pooling adds churn for
  no benefit (doc 06 §4 gives the book layout).

## 10. Failure-mode summary (full analysis in doc 09)

| Failure | Detection | Containment |
|---------|-----------|-------------|
| Exhaustion | `TryAcquire=false` + telemetry counter | Declared policy (§6) |
| Leak | HWM drift, epoch audit, soak gate | Epoch reset reclaims nothing for pools — must fix; breach if pool nears exhaustion |
| Use-after-release | Generation check (Debug/Checked); poison fill | Fault → breach in prod-checked; undefined in Release-Fast (hence canary policy, doc 01 §5.3) |
| Double release | Generation mismatch on second call | Same as above |
| Cross-pool handle confusion | PoolId check (Debug/Checked) | Same as above |
| False sharing on shared pool head | Perf rig regression (CAS latency histogram) | Padding audit; `dotnet-counters` cache-miss proxy metrics |
