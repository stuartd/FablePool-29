# Doc 11 — Consolidated API Reference

**Status:** Final
**Scope:** Public surface of the `FablePool.Hft` library family as specified in Docs 01–08. This document is the single normative reference for signatures, allocation guarantees, and threading contracts. Where prose here and an earlier doc disagree, this doc governs.

**Target framework:** .NET 8+ (`net8.0`). All APIs are `Span`-era; no APIs require unsafe code from callers, though several are implemented with unsafe internals.

**Conventions used below:**

- **Alloc:** allocation behavior. `None` = never allocates managed memory. `Warmup-only` = allocates only when called before `WarmupGate.Complete()`.
- **Threads:** the threading contract. `SPSC` = exactly one producer thread and one consumer thread; `Any` = thread-safe; `Owner` = must be called only by the owning thread established at construction/checkout (Doc 01 §1.6).
- `in`/`ref` parameter conventions follow Doc 04 §4.5: messages ≥ 16 bytes pass by `in`; mutation-in-place uses `ref` returns.

---

## 11.1 Namespace Map

| Namespace | Doc | Contents |
|---|---|---|
| `FablePool.Hft.Memory` | 01, 06 | Ownership primitives, arenas, native buffers |
| `FablePool.Hft.Pooling` | 02 | Object pools, handles, poolable contract |
| `FablePool.Hft.Messages` | 03 | Struct message types, symbol interning, price codec |
| `FablePool.Hft.Buffers` | 05 | Ring buffers (SPSC/MPSC), batch views, hot logger |
| `FablePool.Hft.Threading` | 07 | Pinned threads, affinity, spin policies, clock |
| `FablePool.Hft.Diagnostics` | 08 | Warmup gate, allocation sentinel, telemetry |

---

## 11.2 `FablePool.Hft.Memory`

### 11.2.1 `Arena` (Doc 06 §6.2)

Unmanaged bump allocator. Memory is reserved and committed (and optionally NUMA-bound and page-touched) at construction; individual allocations are pointer bumps; memory is reclaimed only by `Reset()` (scope discipline, Doc 01 §1.4) or `Dispose()`.

```csharp
public sealed class Arena : IDisposable
{
    public Arena(ArenaOptions options);

    public nuint Capacity { get; }            // bytes
    public nuint Used { get; }                // bytes; Threads: Owner (unsynchronized read elsewhere is advisory)

    public Span<T> Allocate<T>(int count) where T : unmanaged;       // Alloc: None. Throws ArenaExhaustedException.
    public bool TryAllocate<T>(int count, out Span<T> span) where T : unmanaged;  // Alloc: None.
    public ref T AllocateOne<T>() where T : unmanaged;               // Alloc: None.
    public NativeBuffer<T> AllocateBuffer<T>(int count) where T : unmanaged;      // long-lived handle form, §11.2.2

    public ArenaCheckpoint Checkpoint();      // Alloc: None
    public void ResetTo(ArenaCheckpoint cp);  // frees everything after cp; debug builds poison freed bytes (0xDE) — Doc 06 §6.7
    public void Reset();                      // ResetTo(start)
    public void Dispose();
}

public readonly record struct ArenaCheckpoint;   // opaque offset + generation

public sealed class ArenaOptions
{
    public required nuint CapacityBytes { get; init; }
    public int NumaNode { get; init; } = -1;          // -1 = first-touch
    public bool UseLargePages { get; init; } = false; // Doc 06 §6.6; falls back with telemetry event if OS denies
    public bool PrefaultPages { get; init; } = true;  // touch every page at construction
    public string Name { get; init; } = "arena";      // telemetry label
}
```

**Contract:** *Threads: Owner.* One arena per owning thread (or per pipeline stage); arenas are never shared across concurrently-running threads. Cross-thread handoff of arena-allocated data is forbidden — copy into a ring slot instead (Doc 01 §1.6, Doc 09 §9.3). `Span<T>`s returned by `Allocate` are invalidated by `ResetTo`/`Reset` crossing their allocation point; the generation embedded in `ArenaCheckpoint` makes stale `ResetTo` calls throw in debug builds.

**Alloc:** constructor allocates (warmup-only by convention; the `WarmupGate` sentinel will flag construction after warmup). All other members: None. `ArenaExhaustedException` is a pre-constructed singleton rethrow in release builds (Doc 09 §9.6) — prefer `TryAllocate` on hot paths.

### 11.2.2 `NativeBuffer<T>` (Doc 06 §6.3)

A long-lived, bounds-checked view over arena (or directly-allocated) unmanaged memory. Unlike `Span<T>`, it is a normal struct: storable in fields, usable across `await`-free method boundaries, convertible to `Span<T>` for access.

```csharp
public readonly struct NativeBuffer<T> where T : unmanaged
{
    public int Length { get; }
    public bool IsEmpty { get; }
    public ref T this[int index] { get; }                 // bounds-checked; Alloc: None
    public Span<T> AsSpan();                              // Alloc: None
    public Span<T> AsSpan(int start, int length);
    public NativeBuffer<T> Slice(int start, int length);  // Alloc: None
    public void Clear();                                  // zero-fill
}
```

**Threads:** follows its arena's owner unless handed off via the documented single-writer transfer pattern (Doc 01 §1.7: publish index over a ring, not the buffer itself). **Alloc:** None for all members.

### 11.2.3 Ownership annotations (Doc 01 §1.8)

Attributes consumed by the Roslyn analyzer (no runtime behavior):

```csharp
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor)]
public sealed class HotPathAttribute : Attribute;        // 0-alloc enforced; bans listed in Doc 08 §8.4

[AttributeUsage(AttributeTargets.Method)]
public sealed class WarmPathAttribute : Attribute;       // pooled/arena alloc only; not callable from [HotPath]

[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue)]
public sealed class TransfersOwnershipAttribute : Attribute;  // callee/caller now owns; analyzer tracks per Doc 01 §1.5

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class BorrowedAttribute : Attribute;       // must not be stored beyond the call
```

---

## 11.3 `FablePool.Hft.Pooling`

### 11.3.1 `IPoolable` (Doc 02 §2.3)

```csharp
public interface IPoolable
{
    void OnRent();    // called by pool on checkout, on the renting thread; must not allocate
    void OnReturn();  // called by pool on return; must reset ALL state to pristine — Doc 09 §9.4 (dirty-reuse) hinges on this
}
```

### 11.3.2 `ObjectPool<T>` and `PoolHandle<T>` (Doc 02 §2.4)

Fixed-capacity pool for stateful, identity-bearing objects (working orders, sessions). Capacity is final at construction; exhaustion is a `Try`-failure, never a grow (Doc 02 §2.5).

```csharp
public sealed class ObjectPool<T> where T : class, IPoolable
{
    public ObjectPool(int capacity, Func<T> factory, string name);
    // factory invoked exactly `capacity` times at construction (warmup). Never again.

    public int Capacity { get; }
    public int Available { get; }                 // approximate under concurrency; exact when Threads: Owner

    public bool TryRent(out PoolHandle<T> handle);            // Alloc: None. Threads: Any (lock-free freelist, Doc 02 §2.7)
    public void Return(ref PoolHandle<T> handle);             // Alloc: None. Threads: Any. Invalidates handle (sets default).
    public PoolTelemetry GetTelemetry();                      // copies counters into a struct; Alloc: None
}

public readonly struct PoolHandle<T> where T : class, IPoolable
{
    public bool IsValid { get; }                  // false if defaulted or stale generation
    public T Value { get; }                       // throws StaleHandleException on generation mismatch (use-after-return guard, Doc 02 §2.4.2)
    public bool TryGetValue(out T value);         // non-throwing form for hot paths
    public long Id { get; }                       // slot index + generation packed; loggable, ring-transmittable
}
```

**Threading contract:** `TryRent`/`Return` are thread-safe (MPMC freelist via tagged-index CAS). The *rented object itself* is **Owner**: exactly one thread uses it between rent and return; transferring ownership across threads is done by sending `handle.Id` over a ring and reconstructing via `pool.Resolve(id, out handle)`:

```csharp
public bool Resolve(long id, out PoolHandle<T> handle);   // generation-checked; Alloc: None; Threads: Any
```

**Alloc:** constructor warmup-only; all steady-state members None. `StaleHandleException` is fatal-by-design: it routes to the kill switch (Doc 09 §9.4) rather than being caught-and-continued.

### 11.3.3 `FixedCapacityMap<TKey, TValue>` (Doc 02 §2.6)

Open-addressing (linear-probe) hash map with all storage allocated at construction. No resize, no tombstone drift (backward-shift deletion).

```csharp
public sealed class FixedCapacityMap<TKey, TValue>
    where TKey : unmanaged, IEquatable<TKey>
    where TValue : struct
{
    public FixedCapacityMap(int capacity);        // rounds up to power of two; load factor cap 0.5 — Add fails beyond it
    public int Count { get; }
    public int Capacity { get; }

    public bool TryAdd(TKey key, in TValue value);          // Alloc: None
    public bool TryGetValue(TKey key, out TValue value);    // copy-out
    public ref TValue GetRef(TKey key);                     // ref into table; throws KeyNotFound (singleton) if absent
    public ref TValue GetOrAddRef(TKey key, out bool added);
    public bool Remove(TKey key);
    public void Clear();
    public Enumerator GetEnumerator();                      // struct enumerator; no interface, no boxing

    public struct Enumerator { public bool MoveNext(); public KeyValueRef Current { get; } }
    public readonly ref struct KeyValueRef { public TKey Key { get; } public ref TValue Value { get; } }
}
```

**Threads:** Owner (single-threaded by contract; this is a per-stage structure, not a shared one — sharing goes through rings). **Alloc:** constructor warmup-only; all else None. `ref` returns from `GetRef` are invalidated by any subsequent `Remove` (backward-shift moves entries) — analyzer rule HFT012 flags holding a ref across a mutation.

---

## 11.4 `FablePool.Hft.Messages`

### 11.4.1 Core message structs (Doc 03 §3.2–3.3)

All message types are `unmanaged`, blittable, `LayoutKind.Sequential`, explicitly sized, and versioned by a leading `MsgHeader`. Exact field layouts are normative in Doc 03; signatures of the shared header and the two highest-traffic messages:

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 16)]
public struct MsgHeader
{
    public ushort MsgType;        // MsgType enum
    public ushort SchemaVersion;
    public uint   PayloadSize;
    public long   TimestampNanos; // producer clock, Clock.Nanos()
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 64)]
public struct QuoteMsg { /* header + SymbolId, side prices/qtys as in Doc 03 §3.3.1 */ }

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 128)]
public struct OrderMsg { /* header + ClOrdId, SymbolId, Side, PriceTicks, Qty, Tif, Flags — Doc 03 §3.3.4 */ }
```

**Invariant (enforced by static asserts in `MessageLayoutTests` and a module initializer check):** `Unsafe.SizeOf<T>()` equals the declared `Size`, and size is a multiple of 8 (and 64 for ring-resident types, to keep slots cache-line aligned — Doc 05 §5.2).

### 11.4.2 `SymbolTable` (Doc 03 §3.5)

```csharp
public sealed class SymbolTable
{
    public SymbolTable(int capacity);
    public int Add(ReadOnlySpan<byte> symbol);              // Warmup-only (returns existing id if present)
    public int Lookup(ReadOnlySpan<byte> symbol);           // Alloc: None; returns -1 if unknown; Threads: Any after Freeze()
    public ReadOnlySpan<byte> Name(int symbolId);           // reverse lookup; Alloc: None
    public void Freeze();                                   // transitions to immutable read-mostly state; Add after Freeze throws
    public int Count { get; }
}
```

**Threads:** `Add` is single-threaded warmup; after `Freeze()`, `Lookup`/`Name` are wait-free and safe from any thread. Unknown live symbols are a warm-path event: feed handler emits `UnknownSymbol` telemetry and drops/parks the message (Doc 09 §9.5); intraday additions go through a coordinated re-freeze on the warm path.

### 11.4.3 `PriceCodec` (Doc 03 §3.4)

Fixed-point price arithmetic: prices are `long` tick counts against a per-instrument `TickTable` built at warmup.

```csharp
public static class PriceCodec
{
    public static long ToTicks(decimal price, in TickSpec spec);        // Warmup/cold only (decimal in signature)
    public static decimal ToDecimal(long ticks, in TickSpec spec);      // cold path / display only
    public static long ReadTicks(ReadOnlySpan<byte> ascii, in TickSpec spec);  // parse exchange ascii price; Alloc: None
    public static int WriteTicks(long ticks, in TickSpec spec, Span<byte> dest); // format for FIX; Alloc: None; returns bytes written
}

public readonly struct TickSpec { public long TickNumerator { get; init; } public long TickDenominator { get; init; } public byte Decimals { get; init; } }
```

### 11.4.4 `FixFieldReader` / `FixFieldWriter` (Doc 03 §3.6, Doc 04 §4.3)

```csharp
public ref struct FixFieldReader
{
    public FixFieldReader(ReadOnlySpan<byte> message);
    public bool TryNext(out int tag, out ReadOnlySpan<byte> value);   // Alloc: None; value aliases input — Borrowed
    public bool TrySeek(int tag, out ReadOnlySpan<byte> value);       // forward scan from current position
    public int Position { get; }
}

public ref struct FixFieldWriter
{
    public FixFieldWriter(Span<byte> destination);
    public void WriteTag(int tag, ReadOnlySpan<byte> value);
    public void WriteTag(int tag, long value);                        // integer fast path, no formatting alloc
    public void WriteTagTicks(int tag, long ticks, in TickSpec spec);
    public int Finish(ReadOnlySpan<byte> beginString);  // back-fills BodyLength(9) + CheckSum(10); returns total length
}
```

**Threads:** `ref struct` — inherently stack-confined to one thread, cannot be captured (this is the Doc 04 escape-prevention mechanism). **Alloc:** None. Buffer overrun in `FixFieldWriter` throws (egress buffers are sized at warmup to max-message + margin; overrun is an invariant violation per Doc 09 §9.6).

---

## 11.5 `FablePool.Hft.Buffers`

### 11.5.1 `SpscRing<T>` (Doc 05 §5.3)

Single-producer single-consumer ring of inline struct slots. Power-of-two capacity; slots padded to 64-byte multiples; head/tail sequence counters on isolated cache lines (false-sharing rule, Doc 05 §5.2.2).

```csharp
public sealed class SpscRing<T> where T : unmanaged
{
    public SpscRing(int capacityPow2, RingOptions options);

    // Producer side — Threads: the single producer only
    public bool TryClaim(out RingSlot<T> slot);     // Alloc: None. False when full (RingFullPolicy decides next step)
    public ref T Claim();                            // throws RingFullException (singleton) — prefer TryClaim on hot path
    public void Publish();                           // release-fence; makes the claimed slot visible
    public bool TryWrite(in T value);                // claim+copy+publish convenience

    // Consumer side — Threads: the single consumer only
    public bool TryRead(out T value);                // copy-out; Alloc: None
    public bool TryPeek(out RingReadSlot<T> slot);   // zero-copy read view; consumer must call Advance()
    public void Advance();
    public int ReadBatch(Span<T> destination);       // drains up to destination.Length; returns count

    public RingTelemetry GetTelemetry();             // depth, high-watermark, full-count, sequence — Alloc: None; Threads: Any (advisory)
}

public readonly ref struct RingSlot<T> where T : unmanaged { public ref T Value { get; } }
public readonly ref struct RingReadSlot<T> where T : unmanaged { public ref readonly T Value { get; } }

public sealed class RingOptions
{
    public string Name { get; init; } = "ring";
    public RingFullPolicy FullPolicy { get; init; } = RingFullPolicy.ReturnFalse;  // Doc 09 §9.2: ReturnFalse | DropOldest | KillSwitch
    public Arena? BackingArena { get; init; }        // null = pinned managed array; set = arena-resident slots (Doc 06 §6.3)
}
```

**Memory-ordering contract:** `Publish` is a release store of the producer sequence; `TryRead`/`TryPeek` acquire-load it. Slot contents written before `Publish` are visible to the consumer after a successful read — this is the cross-thread handoff primitive that replaces all shared mutable state (Doc 01 §1.7, Doc 07 §7.4).

**Alloc:** constructor warmup-only; all steady-state members None.

### 11.5.2 `MpscRing<T>` (Doc 05 §5.4)

Multi-producer single-consumer variant (order events from N gateway threads into one strategy thread). Same consumer API as `SpscRing<T>`; producer side:

```csharp
public bool TryWrite(in T value);   // CAS-claims a sequence, copies, publishes with per-slot availability flag; Alloc: None; Threads: Any producer
```

No zero-copy `Claim` on the MPSC producer side (a stalled producer holding an unpublished middle slot would head-of-line-block the consumer — Doc 05 §5.4.2 rationale); producers always copy-in via `TryWrite`.

### 11.5.3 `HotLogger` (Doc 05 §5.7)

```csharp
public sealed class HotLogger
{
    public HotLogger(SpscRing<LogRecord> ring, ushort sourceId);
    public void Write(LogEvent evt);                                   // Alloc: None; drops + increments drop-counter when ring full (logging never blocks trading)
    public void Write(LogEvent evt, long a0);
    public void Write(LogEvent evt, long a0, long a1);
    public void Write(LogEvent evt, long a0, long a1, long a2);
    public long DroppedCount { get; }
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 64)]
public struct LogRecord { /* TimestampNanos, SourceId, Event, A0..A2 */ }

public sealed class LogDrainer   // cold-path side; Threads: dedicated non-pinned thread
{
    public LogDrainer(SpscRing<LogRecord> ring, ILogSink sink);   // sink formats to text/Serilog/file
    public void RunUntilStopped(CancellationToken ct);
}
```

**Threads:** `HotLogger.Write` is Owner (one logger instance per hot thread; each has its own SPSC ring). The drainer is the single consumer.

---

## 11.6 `FablePool.Hft.Threading`

### 11.6.1 `PinnedThread` (Doc 07 §7.2)

```csharp
public sealed class PinnedThread
{
    public static PinnedThread Start(int core, string name, ThreadPriority priority, Action body);
    public static PinnedThread Start(in PinnedThreadOptions options, Action body);

    public int Core { get; }
    public string Name { get; }
    public bool IsRunning { get; }
    public void Join(TimeSpan timeout);
}

public readonly struct PinnedThreadOptions
{
    public int Core { get; init; }
    public string Name { get; init; }
    public ThreadPriority Priority { get; init; }
    public bool DisallowGcTransitions { get; init; }  // debug: assert no GC-coop transitions in body steady state
}
```

**Behavior:** creates a foreground `Thread`, sets OS affinity to the single `core` (Linux: `sched_setaffinity` via P/Invoke; Windows: `SetThreadAffinityMask`), names it for `perf`/ETW, sets priority, then runs `body`. `body` is invoked once; it owns its loop. Per Doc 07 §7.6, production hosts isolate these cores (`isolcpus`/`nohz_full` or Windows CPU sets); `PinnedThread` validates at start that the target core exists and logs (cold path) whether it is isolated.

**Alloc:** `Start` allocates (warmup-only by contract — all pinned threads start before `WarmupGate.Complete()`). The running body must be allocation-free; the sentinel (§11.7.2) watches exactly these thread IDs.

### 11.6.2 `BoundedSpinPolicy` (Doc 07 §7.3)

```csharp
public struct BoundedSpinPolicy
{
    public BoundedSpinPolicy(int maxSpinsBeforeYield);
    public void Spin();    // pause-instruction spins, escalating; after maxSpins, Thread.Yield() once, then resumes spinning
    public void Reset();   // call after useful work
}
```

**Alloc:** None. Mutable struct — store in a local of the thread loop, never in a shared field.

### 11.6.3 `Clock` (Doc 07 §7.7)

```csharp
public static class Clock
{
    public static long Nanos();          // monotonic nanoseconds (Stopwatch/rdtsc-calibrated); Alloc: None; Threads: Any
    public static long WallNanosUtc();   // wall-clock ns since Unix epoch, leap-aware via warmup calibration; Alloc: None
    public static void Calibrate();      // Warmup-only; pins calibration to current TSC/Stopwatch state
}
```

---

## 11.7 `FablePool.Hft.Diagnostics`

### 11.7.1 `WarmupGate` (Doc 08 §8.3)

```csharp
public static class WarmupGate
{
    public static bool IsWarm { get; }
    public static void RegisterWarmup(string name, Action warmupRoutine);  // cold path; routines run in registration order
    public static void RunAll();                 // executes routines, then JIT-settle check (methods-jitted flatline), then Complete()
    public static void Complete();               // emits the "FablePool.Hft/WarmupComplete" EventSource marker consumed by CI harness + sentinel
}
```

### 11.7.2 `AllocationSentinel` (Doc 08 §8.6)

In-process EventPipe listener that arms after `WarmupComplete` and fires on any GC allocation-tick event whose thread ID belongs to a registered pinned thread.

```csharp
public sealed class AllocationSentinel : IDisposable
{
    public AllocationSentinel(SentinelOptions options);
    public void WatchThread(int managedThreadId, string name);  // call from each pinned thread at startup
    public void Arm();                                          // typically wired to WarmupGate.Complete
    public long ViolationCount { get; }
    public event Action<AllocationViolation>? OnViolation;      // raised on sentinel's own (cold) thread

    public readonly struct AllocationViolation
    {
        public int ThreadId { get; init; }
        public string ThreadName { get; init; }
        public string TypeName { get; init; }      // allocated type from the GC event
        public long Bytes { get; init; }
    }
}

public sealed class SentinelOptions
{
    public SentinelMode Mode { get; init; } = SentinelMode.AlertOnly;  // AlertOnly | KillSwitch (Doc 09 §9.7)
    public bool CaptureStacks { get; init; } = false;                  // stacks cost overhead; on in canary, off in steady prod
}
```

**Threads:** the sentinel runs its `EventListener` on a dedicated non-pinned thread; `WatchThread` is Any; violations never interrupt the hot thread itself — escalation is via the kill-switch flag the egress thread already polls.

### 11.7.3 Telemetry structs

All components expose copy-out telemetry structs (no live shared references):

```csharp
public readonly struct RingTelemetry  { public int Depth, HighWatermark, Capacity; public long FullCount, PublishedTotal; }
public readonly struct PoolTelemetry  { public int Outstanding, HighWatermark, Capacity; public long RentTotal, StaleHandleFaults; }
public readonly struct ArenaTelemetry { public nuint Used, HighWatermark, Capacity; public long ResetCount; }
```

Snapshot reads are tear-tolerant by design (each field is independently `Volatile.Read`); they are monitoring data, not synchronization (Doc 05 §5.8).

---

## 11.8 Cross-Cutting Contracts Summary

| Rule | Applies to | Reference |
|---|---|---|
| Zero managed allocation after `WarmupGate.Complete()` | every member marked **Alloc: None** above; enforced by analyzer (`[HotPath]`), CI EventPipe harness, prod sentinel | Doc 08 |
| Single ownership: one thread owns any mutable datum; transfer only via ring publish or pool `Resolve(id)` | arenas, pooled objects, maps, hot loggers | Doc 01 |
| `Try*` for expected failure, singleton exceptions for invariant violations, exceptions route to kill switch | rings, pools, codecs, writers | Doc 09 §9.6 |
| No `Try*` retry loops without `BoundedSpinPolicy` | all ring producers/consumers | Doc 07 §7.3 |
| `ref struct` views (`RingSlot`, `FixFieldReader`, `KeyValueRef`) must not outlive their scope; refs invalidated by container mutation | rings, maps, readers | Doc 04 |
| Fixed capacity everywhere; sizing = audit p99.9 × 4; overflow is a policy event, never a grow | rings, pools, maps, arenas | Docs 02, 05, 06 |
| Construction/registration is warmup-only | all constructors, `SymbolTable.Add`, `PinnedThread.Start`, `Calibrate` | Doc 08 §8.3 |

## 11.9 Versioning and Stability

The API surface above is **v1.0-frozen** for implementation milestone #3. Additive changes (new message types, new telemetry fields) bump `SchemaVersion` in `MsgHeader` and minor library version; any change to a signature, threading contract, or allocation guarantee in this document requires a design-doc revision PR touching both Doc 11 and the originating doc, reviewed by the architecture owners listed in Doc 00 §0.5.
