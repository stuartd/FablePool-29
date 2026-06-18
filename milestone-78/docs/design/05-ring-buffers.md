# Design Doc 05 — Pre-Allocated Ring Buffers for Market Data and Order Flow

Status: Final draft for review
Depends on: 01-memory-ownership-model.md, 03-message-types.md, 04-span-memory-usage-rules.md
Audience: Core engine engineers

---

## 1. Purpose and requirements

All inter-thread communication on the hot path uses pre-allocated, bounded,
lock-free ring buffers. There are exactly four ring archetypes in the system:

| Ring | Producer(s) | Consumer(s) | Element | Capacity (default) |
|---|---|---|---|---|
| `MarketDataRing` | 1 (feed thread per venue) | 1..N (strategy threads) | `MarketDataEvent` struct, 64 B | 2^20 |
| `OrderCommandRing` | N (strategy threads) | 1 (order gateway thread) | `OrderCommand` struct, 64 B | 2^16 |
| `ExecReportRing` | 1 (gateway thread) | 1..N (strategy threads) | `ExecReport` struct, 64 B | 2^16 |
| `LogRing` | N (any hot thread) | 1 (cold logger thread) | `LogRecord` struct, 128 B | 2^18 |

Requirements:

- **Zero allocation** after construction. Storage is one contiguous slab.
- **Wait-free producers** on SPSC/SPMC rings; lock-free (bounded-retry CAS) on MPSC.
- **Bounded**: capacity is a power of two fixed at startup; behavior on full is an
  explicit per-ring policy (§6) — never blocking-with-allocation, never unbounded.
- **Cache-friendly**: sequence counters padded to cache lines; elements are
  cache-line-sized structs written in place (no object references in elements).
- **Mechanically sympathetic with NUMA**: slab allocated on the consumer's NUMA
  node for MD (consumer-bound workload), producer's node for order flow (rare,
  latency-critical writes). See Doc 07 §6.

Design lineage: LMAX Disruptor sequencing adapted to .NET structs + unmanaged
slabs; differences called out in §9.

---

## 2. Memory layout

A ring is a single unmanaged slab (from the `Rings` arena region, Doc 06 §3):

```
+-------------------------------------------------------------+
| Header (4 cache lines)                                       |
|   line 0: capacityMask, elementSize, flags     (immutable)   |
|   line 1: producerSequence (cursor)            (hot, prod)   |
|   line 2: consumerSequence(s) / gating array    (hot, cons)  |
|   line 3: cachedConsumerSeq (producer-local)   (hot, prod)   |
+-------------------------------------------------------------+
| Slot[0] | Slot[1] | ... | Slot[capacity-1]                   |
+-------------------------------------------------------------+
```

- Each `Slot` is `elementSize` bytes, where `elementSize % 64 == 0`
  (Doc 03 fixes hot messages at 64 B; logs at 128 B).
- Index math: `slotOffset = (sequence & capacityMask) * elementSize`.
- All counters are `long` sequences that increase monotonically forever
  (wrap is ignored: at 100M events/s a signed 64-bit sequence wraps after ~2,900
  years).
- Counters are accessed via `Volatile.Read`/`Volatile.Write` and
  `Interlocked.*`; each lives alone on a 64-byte line (we pad to 128 B on the
  producer cursor to defeat adjacent-line prefetcher sharing on Intel).

Per-slot **published flag**: instead of a separate availability array, each slot's
first 8 bytes are the slot's own sequence number, written *last* with release
semantics by the producer. A consumer reads slot-sequence with acquire semantics
and compares to the expected sequence; mismatch ⇒ not yet published. This makes
SPSC and MPSC consume paths identical and keeps the published bit on the same cache
line as the payload (one line transfer per event instead of two).

Consequence for Doc 03: every ring element struct begins with
`long Sequence` (reserved field, set by the ring, opaque to user code).

---

## 3. Variants

### 3.1 SPSC (`SpscRing<T>`)

Single producer, single consumer. No interlocked ops at all:

- Producer: read own cursor (plain), check against cached consumer sequence; if
  potentially full, `Volatile.Read` consumer sequence and re-cache; write payload;
  `Volatile.Write` slot sequence; advance own cursor (plain write — only the
  producer reads it for claiming, consumers gate on slot sequences).
- Consumer: `Volatile.Read` slot sequence at expected index; if published, read
  payload (plain), then `Volatile.Write` its consumer sequence (frees the slot).

Cost per op: ~1 cache-line transfer + 1–2 fences' worth of ordering on x86
(`Volatile` is free for ordering on x86/x64 stores/loads; on ARM64 it emits
`stlr`/`ldar`). Measured target: < 30 ns/op cross-core same-socket, < 100 ns
cross-socket.

### 3.2 SPMC broadcast (`BroadcastRing<T>`) — market data fan-out

One feed thread publishes; N strategy threads each read **every** event
(broadcast, not work-stealing). Each consumer owns an independent sequence in the
gating array (one cache line each, max 16 consumers per ring by config). The
producer's full-check gates on `min(consumerSequences)` — computed lazily only when
the cached minimum is exhausted, so the scan is off the common path.

A slow consumer therefore backpressures the feed. Policy for MD rings is
**drop-oldest-with-conflation is forbidden at ring level** (it destroys book
deltas); instead, a lagging consumer is *declared lapped* (§6.2) and must resync
from a snapshot. The producer never blocks.

### 3.3 MPSC (`MpscRing<T>`) — order commands, logs

Multiple producers claim slots with a single `Interlocked.Increment` on the
producer cursor (fetch-add, not CAS-loop — wait-free claim), write payload,
publish slot sequence with release. The single consumer consumes in sequence
order; a claimed-but-unpublished slot ahead of published ones simply stalls the
consumer for the nanoseconds until the slow producer's store lands (producers are
pinned and never preempted mid-publish in practice; the pathological
preempted-producer case is bounded by the consumer's spin policy and surfaced as a
`RingStallWarning` if it exceeds 50 µs — Doc 10 §6.3).

Full handling on claim: fetch-add can overrun capacity. The producer detects
overrun (`claimed - min(consumer) >= capacity`) and executes the ring's full
policy *without* unclaiming (the slot is published as a `Tombstone` message the
consumer skips). This keeps the claim wait-free at the cost of one wasted slot per
rejected publish — acceptable because order-command rejection is already an
emergency state (§6.3).

---

## 4. API specification

```csharp
namespace FablePool.Rings;

/// Element contract: unmanaged, first field must be `long Sequence`,
/// size a multiple of 64 and <= 1024. Verified at ring construction via
/// RingElement.Validate<T>() (reflection at startup only).
public interface IRingElement { /* marker; layout verified structurally */ }

public sealed unsafe class SpscRing<T> where T : unmanaged, IRingElement
{
    public static SpscRing<T> Create(Arena arena, int capacityPow2, RingFullPolicy policy, string name);

    public int  Capacity { get; }
    public long ProducerSequence { get; }   // monitoring only
    public long ConsumerSequence { get; }   // monitoring only

    // --- Producer side (single thread) ---

    /// Claims the next slot and returns a ref to write into.
    /// Returns false per FullPolicy (Reject) or spins (SpinWait) — never blocks the OS thread.
    public bool TryClaim(out RingSlot<T> slot);

    /// Publishes a previously claimed slot. Release-fence semantics.
    public void Publish(in RingSlot<T> slot);

    /// Convenience: claim+copy+publish for small elements.
    public bool TryWrite(in T element);

    // --- Consumer side (single thread) ---

    /// Non-blocking: returns ref readonly view of next event, or false.
    public bool TryRead(out ReadOnlyRingSlot<T> slot);

    /// Marks the slot consumed; its memory may be overwritten afterwards.
    public void Release(in ReadOnlyRingSlot<T> slot);

    /// Batch consume: invokes handler for up to maxBatch published events,
    /// releasing as it goes. Handler is a struct functor — no delegate alloc.
    public int Drain<THandler>(ref THandler handler, int maxBatch)
        where THandler : struct, IRingHandler<T>;
}

public interface IRingHandler<T> where T : unmanaged, IRingElement
{
    /// Return false to stop draining early.
    bool OnEvent(ref readonly T element, long sequence, bool endOfBatch);
}

public readonly ref struct RingSlot<T> where T : unmanaged, IRingElement
{
    public ref T Value { get; }          // write target
    public long Sequence { get; }
}

public readonly ref struct ReadOnlyRingSlot<T> where T : unmanaged, IRingElement
{
    public ref readonly T Value { get; }
    public long Sequence { get; }
}

public enum RingFullPolicy : byte
{
    SpinUntilFree,   // producer busy-spins (MD feed ring: never expected to trigger)
    Reject,          // TryClaim returns false (order ring: caller escalates)
}

public sealed unsafe class BroadcastRing<T> where T : unmanaged, IRingElement
{
    public static BroadcastRing<T> Create(Arena arena, int capacityPow2, int maxConsumers, string name);

    public bool TryWrite(in T element);            // producer; SpinUntilFree gating on min consumer
    public RingReader<T> AddReader(string consumerName);  // startup only; throws after Seal()
    public void Seal();                             // called at end of warmup; AddReader now fatal
}

/// Per-consumer cursor over a BroadcastRing. NOT thread-safe; owned by one thread.
public sealed class RingReader<T> where T : unmanaged, IRingElement
{
    public bool TryRead(out ReadOnlyRingSlot<T> slot);
    public void Release(in ReadOnlyRingSlot<T> slot);
    public int  Drain<THandler>(ref THandler handler, int maxBatch) where THandler : struct, IRingHandler<T>;

    /// True if the producer overwrote unread slots; reader must snapshot-resync.
    public bool Lapped { get; }
    public long Lag { get; }   // producerSeq - consumerSeq, monitoring
}

public sealed unsafe class MpscRing<T> where T : unmanaged, IRingElement
{
    public static MpscRing<T> Create(Arena arena, int capacityPow2, string name);
    public bool TryWrite(in T element);   // wait-free claim; Reject-on-full via tombstone
    public int  Drain<THandler>(ref THandler handler, int maxBatch) where THandler : struct, IRingHandler<T>;
}
```

Notes:

- `RingSlot<T>`/`ReadOnlyRingSlot<T>` are `ref struct`s ⇒ cannot escape the frame
  (Doc 04 R-04-01 holds by construction).
- `Drain` with a struct-functor `THandler` is the standard consumer loop; it
  monomorphizes per handler type — no virtual dispatch, no delegate, no closure.
- `Create` is **startup-phase only**; calling after `LifecyclePhase.Trading`
  (Doc 08 §2) trips the no-alloc contract and fail-fasts in debug.

---

## 5. Producer/consumer protocols (normative pseudocode)

SPSC publish:

```
claim:
  seq  = prodCursor                      // plain read, own field
  if seq - cachedMinCons >= capacity:
      cachedMinCons = Volatile.Read(consSeq)
      if seq - cachedMinCons >= capacity: -> full policy
  slot = &slab[(seq & mask) * elemSize]
publish:
  write payload fields (plain stores)
  Volatile.Write(slot->Sequence, seq)    // release: payload visible before seq
  prodCursor = seq + 1                   // plain store
```

SPSC consume:

```
  seq  = consSeq                          // own field
  slot = &slab[(seq & mask) * elemSize]
  if Volatile.Read(slot->Sequence) != seq: -> empty
  read payload (plain loads; acquire on seq orders them)
  Volatile.Write(consSeq, seq + 1)        // frees slot for producer
```

MPSC claim replaces the first line with
`seq = Interlocked.Increment(ref prodCursor) - 1` and adds the overrun/tombstone
check. Broadcast consume is identical to SPSC consume per reader; the producer's
full-check takes `min` over the gating array.

Memory-model justification: slot-sequence store/load pairs form release/acquire
edges; payload races are impossible because a consumer only reads payload after
observing the matching sequence, and the producer only rewrites a slot after all
gating consumer sequences pass it. On x86 this compiles to plain `mov`s (TSO); on
ARM64 to `stlr`/`ldar`. We do not use `Thread.MemoryBarrier()` anywhere in ring
code.

---

## 6. Full-ring and lapping policies

### 6.1 Market data ring full (producer side)

Should be unreachable if capacity is sized to ≥ 2 s of peak feed rate (2^20 × 64 B
= 64 MiB ≈ 10M events at typical peaks — tune per venue). If reached, producer
spins with `X86Base.Pause()` (`SpinWait` without yielding) and increments
`md_ring_full_spins` (telemetry). Sustained spins > 1 ms trigger the
`FeedBackpressure` alarm (Doc 10 §6.1).

### 6.2 Lapped broadcast consumer

A reader whose lag exceeds capacity sets `Lapped = true` permanently until it
calls `ResyncTo(long sequence)` after rebuilding state from the snapshot service.
While lapped, `TryRead` returns false. The strategy must flatten or freeze per its
risk config (Doc 10 §5.2). The producer is *never* slowed by a lapped reader
(its sequence is removed from the gating set on lap detection).

### 6.3 Order command ring full

`TryWrite` returns false / publishes tombstone. The strategy thread treats this as
**order-path degraded**: it raises `OrderPathSaturated`, stops generating new
orders, and (config-dependent) fires the cancel-all via the dedicated emergency
SPSC ring (`EmergencyRing`, capacity 2^8, reserved exclusively for cancel-all and
kill-switch commands so saturation of the normal path can never block risk-off).

### 6.4 Log ring full

Drop-newest with a dropped-record counter folded into the next successful record.
Logging must never backpressure trading.

---

## 7. Sizing, construction, and warmup

- Capacities and consumer counts come from `RingTopologyConfig` (immutable,
  loaded at startup, checked into the repo per deployment).
- All rings are created during `LifecyclePhase.Init`, touch-faulted (every page
  written) during `LifecyclePhase.Warmup` so no soft page faults occur in trading,
  and `Seal()`ed at warmup end.
- Each ring registers with `RingRegistry` for monitoring: lag, throughput,
  full-spins, tombstones — sampled by the cold telemetry thread at 100 ms.

## 8. The log ring (`LogRecord`)

`LogRecord` (128 B): `Sequence:long`, `TimestampTsc:long`, `ThreadId:ushort`,
`EventId:ushort`, `Level:byte`, `ArgCount:byte`, padding, then 96 B of binary
args (`long`/`PriceTicks`/`InlineString8` slots). Format strings live in a
startup-registered table keyed by `EventId`; the cold logger thread renders text
off the hot path. Hot-path call shape:

```csharp
Log.Write(LogEvents.OrderSent, order.ClOrdId.Raw, (long)order.Price.Ticks, order.Qty.Raw);
```

`Log.Write` overloads exist for 0–6 `long` args; all args are converted by the
caller to `long` bit-patterns (no boxing, no `params`).

## 9. Differences vs. LMAX Disruptor

- Slot-embedded sequence instead of availability buffer (one line per event).
- Struct elements in unmanaged slab instead of pre-allocated object graph — no GC
  card-table or write-barrier traffic on publish (no reference stores at all).
- No `WaitStrategy` abstraction: consumers are pinned spinning threads (Doc 07);
  the only wait strategy is busy-spin with `Pause`, with a config-gated
  spin-then-`Thread.Yield` fallback for non-prod environments.
- No multicast dependency graphs (Disruptor "diamond"): our topology is fixed at
  the four archetypes; cross-stage dependencies are expressed as explicit rings.

## 10. Test plan

1. **Linearizability/loss tests**: producer writes sequence-stamped elements;
   consumer asserts gapless, in-order delivery — 1e9 ops per variant in CI soak.
2. **Stress with thread-pair placement**: same core (SMT siblings), same socket,
   cross-socket — assert ordering invariants hold and record latency histograms.
3. **Full-policy tests**: force-full each ring and assert documented policy.
4. **Lap tests**: stall a broadcast reader; assert lap detection, producer
   non-blocking, resync protocol.
5. **Allocation tests**: zero bytes allocated across 1e8 ops post-warmup (Doc 08
   harness).
6. **False-sharing regression**: perf test asserting ≥ target ops/s; guards
   against accidental padding removal (layout also asserted via
   `Marshal.OffsetOf` checks in unit tests).
