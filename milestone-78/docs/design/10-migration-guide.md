# Doc 10 — Migration Guide: Retrofitting Existing C# Trading Codebases

**Status:** Final
**Audience:** Engineering leads migrating a live C# trading system to the allocation-free architecture (Docs 00–09)
**Companion docs:** Doc 08 (No-Allocation Contract) defines the end-state enforcement; Doc 09 (Failure-Mode Analysis) defines what to monitor during rollout.

---

## 10.1 Goals and Non-Goals

### Goals

1. Migrate the **hot path** (market-data ingest → strategy decision → order egress) of an existing C# trading system to the zero-allocation architecture **without a halt-and-rewrite**.
2. Keep the system **tradable at every intermediate step** — every phase ends in a deployable, observable, revertible state.
3. Establish the "no allocations after warmup" contract (Doc 08) incrementally, ratcheting the CI allocation gate down as each subsystem is converted.

### Non-Goals

- Converting cold-path code (EOD reporting, reconciliation, admin UI). It stays on the idiomatic GC-managed style permanently. The architecture explicitly permits this (Doc 01 §1.3: ownership domains).
- Eliminating the GC. The GC remains for startup, warmup, cold path, and crash handling. We eliminate *collections triggered by hot-path allocation*, not the runtime feature.

---

## 10.2 Phase 0 — Audit

You cannot ratchet what you cannot measure. Phase 0 produces three artifacts: an **allocation census**, a **hot-path inventory**, and a **dependency risk register**. Budget 1–2 weeks for a ~200 kLOC codebase.

### 10.2.1 Allocation census

Capture allocation behavior under realistic replayed market data (never synthetic uniform load — allocation profiles are burst-shaped).

**Tooling, in order of preference:**

| Tool | What it gives you | When |
|---|---|---|
| `dotnet-trace` with `--profile gc-verbose` (EventPipe) | Per-type allocation counts + stacks, low overhead, works in prod-like env | Primary census tool |
| `dotnet-counters monitor --counters System.Runtime` | `alloc-rate`, `gen-0-gc-count`, `% time in GC` — cheap continuous baseline | Always-on baseline before/during/after |
| PerfView `GC Heap Alloc Ignore Free` view | Sampled allocation stacks aggregated by call tree | Deep-dive on top offenders |
| `dotnet-gcdump` | Heap snapshot — finds *retained* graphs, identifies what pools must hold | Sizing pools and arenas |

**Census procedure:**

```bash
# 1. Baseline counters during a 30-minute replay of a high-volume session
dotnet-counters collect -p <pid> --counters System.Runtime --output baseline.csv

# 2. Allocation stacks during the same replay window
dotnet-trace collect -p <pid> --profile gc-verbose --duration 00:30:00 -o census.nettrace

# 3. Convert and aggregate
dotnet-trace convert census.nettrace --format Speedscope
```

Open `census.nettrace` in PerfView (`GC Heap Alloc Ignore Free (Coarse Sampling)` view). Export the top-100 allocation stacks by **bytes** and by **count** (small frequent allocations cause Gen0 pressure; large ones cause LOH fragmentation — both matter, differently).

**Deliverable: `audit/allocation-census.md`** — a table of every allocation site that fires on the hot path:

| Rank | Type | Count/min | Bytes/min | Allocating stack (top frame) | Category | Owner |
|---|---|---|---|---|---|---|
| 1 | `System.String` | 4.2 M | 410 MB | `FixParser.GetTag` | Parsing | feeds-team |
| 2 | `Order` (class) | 380 K | 61 MB | `OrderFactory.Create` | Domain model | oms-team |
| 3 | `Action<Fill>` closure | 380 K | 24 MB | `Strategy.OnFill` lambda | Eventing | strat-team |
| … | | | | | | |

**Category** drives which transformation pattern (§10.4) applies. The usual top offenders, in our experience and in the survey from Milestone 1:

1. **Strings** — FIX tag parsing, symbol lookups, log message formatting.
2. **Domain objects per message** — `new Quote()`, `new Order()` per tick.
3. **Closures and delegates** — lambdas capturing locals in event handlers and LINQ.
4. **Boxing** — `struct` into `object`/interface, `string.Format` args, non-generic collections, `Enum.ToString()`.
5. **Async state machines** — `async`/`await` on the hot path; `Task` allocation.
6. **Collection churn** — `List<T>` growth, `Dictionary` resizes, `ToArray()`/`ToList()`, iterator (`yield`) state machines.
7. **`params` arrays and varargs logging**.
8. **`DateTime`/`TimeSpan` formatting**, `Guid.NewGuid().ToString()`.

### 10.2.2 Hot-path inventory

Trace every code path from packet-in to order-out. Mark each method with one of three temperature classes (these become attributes enforced by the analyzer in Doc 08 §8.4):

- **`[HotPath]`** — executes per-message in steady state. Must become allocation-free. Target: the full ingest→decide→egress chain plus risk checks.
- **`[WarmPath]`** — executes occasionally during trading (instrument add, session re-login, parameter update). May allocate, but only via pools/arenas where it touches hot-path-owned data, and never on a pinned hot thread (Doc 07 §7.5).
- **Cold** (unattributed) — startup, EOD, admin. Unrestricted.

**Deliverable: `audit/hot-path-inventory.md`** — call-graph listing (generated with a Roslyn script walking from your `OnPacket`/`OnMarketData` entry points), each node annotated with its temperature and its census offenders.

### 10.2.3 Dependency risk register

Third-party libraries you call from the hot path are the migration's biggest schedule risk because you can't transform their internals.

For each hot-path dependency record: allocation behavior (from the census stacks attributed to its assemblies), whether an allocation-free API exists (`Span`-based overloads, `TryFormat`, pooled variants), and the mitigation:

| Dependency | Hot-path use | Allocates? | Mitigation |
|---|---|---|---|
| QuickFIX/n | FIX parse/build | Heavily (string-based) | **Replace** with in-house `Span<byte>` FIX codec (Doc 03 §3.6) — planned Phase 2 |
| Vendor feed SDK | UDP multicast decode | Per-message objects | **Wrap**: copy into struct messages at the boundary (anti-corruption layer, §10.3.2) |
| Serilog | Hot-path logging | Yes (message templates, boxing) | **Replace on hot path** with binary ring-buffer logger (Doc 05 §5.7); keep Serilog on cold path |
| protobuf-net | Internal IPC | Per-message | Replace with struct overlay messages (Doc 03) |

**Exit criteria for Phase 0:** census, inventory, and risk register reviewed; per-subsystem allocation budgets agreed (these seed the CI ratchet, §10.6.2); replay harness exists that can drive the system deterministically from captured pcaps.

---

## 10.3 Migration Strategy: Strangler Pattern Around the Hot Path

We do not rewrite in place. We stand up the new allocation-free components **alongside** the old ones, route traffic through them incrementally, and strangle the old path subsystem by subsystem. Two structural devices make this safe:

### 10.3.1 The seam: ring buffers as the strangler boundary

The pre-allocated ring buffers (Doc 05) are not just a performance feature — they are the **migration seam**. Each conversion phase moves one stage of the pipeline behind a ring buffer with a fixed struct message contract (Doc 03). Once a stage communicates only via struct messages over rings, its internals can be swapped (old allocating implementation ↔ new allocation-free implementation) behind the same buffer without the rest of the system noticing.

```
  Phase 1 state:

  [NEW ingest, alloc-free] → MarketDataRing → [adapter] → OLD strategy (allocating)
                                                            ↓
                                            OLD order path (allocating) → exchange

  Phase 3 state:

  [NEW ingest] → MarketDataRing → [NEW strategy, alloc-free] → OrderRing → [NEW egress] → exchange
                                                                 (old path retired)
```

### 10.3.2 The anti-corruption layer (ACL)

Wherever new code meets old code, insert an explicit translation adapter:

- **New → Old:** materialize an old-style heap object *from* a struct message. This adapter **allocates by design** and is therefore the *only* place in the new pipeline allowed to do so. It is `[WarmPath]`-attributed, runs on a non-pinned thread, and is deleted at the end of its phase. Keeping the allocation in one named, doomed class makes the census trivially attributable and prevents "temporary" allocations from leaking into new code.
- **Old → New:** copy old object fields into a struct message and publish to a ring. Zero allocation on the new side; the old side allocates as it always did.

Rule: **adapters never share references across the boundary.** Always copy. Aliasing a pooled struct's backing memory into old code that may retain it is the #1 cause of the corruption failures catalogued in Doc 09 §9.3.

---

## 10.4 Code Transformation Catalog (Before → After)

These are the mechanical patterns engineers apply during each phase. Each maps a census category (§10.2.1) to its target construct from Docs 01–08.

### T1 — Per-message class → pooled/inline struct message (Doc 03)

**Before:**

```csharp
public class Quote
{
    public string Symbol { get; set; }
    public decimal Bid { get; set; }
    public decimal Ask { get; set; }
    public DateTime Timestamp { get; set; }
}

void OnPacket(byte[] data)
{
    var q = new Quote
    {
        Symbol    = ParseSymbol(data),        // string alloc
        Bid       = ParseDecimal(data, 16),
        Ask       = ParseDecimal(data, 24),
        Timestamp = DateTime.UtcNow
    };
    _bus.Publish(q);                          // boxed/queued reference
}
```

**After:**

```csharp
// Doc 03 §3.2: fixed-layout, blittable, cache-line aware
[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 64)]
public struct QuoteMsg
{
    public long  TimestampNanos;   // monotonic, from Clock.Nanos() — no DateTime
    public int   SymbolId;         // interned int id (Doc 03 §3.5) — no string
    public long  BidPriceTicks;    // fixed-point ticks — no decimal
    public long  AskPriceTicks;
    public int   BidQty;
    public int   AskQty;
    public uint  SequenceNumber;
}

void OnPacket(ReadOnlySpan<byte> data)
{
    ref QuoteMsg q = ref _mdRing.Claim();     // Doc 05 §5.3: write in place, zero alloc
    q.TimestampNanos = Clock.Nanos();
    q.SymbolId       = _symbols.Lookup(data.Slice(0, 8));  // Span lookup, no string
    q.BidPriceTicks  = PriceCodec.ReadTicks(data.Slice(16));
    q.AskPriceTicks  = PriceCodec.ReadTicks(data.Slice(24));
    _mdRing.Publish();
}
```

Notes: `decimal` → fixed-point `long` ticks per Doc 03 §3.4 (decimal is 16 bytes, non-blittable in practice for our codecs, and slow); `string Symbol` → interned `int` id via the pre-built symbol table (Doc 03 §3.5, populated at warmup); `DateTime` → monotonic nanos.

### T2 — String parsing → `Span<byte>` parsing (Doc 04)

**Before:**

```csharp
string msg = Encoding.ASCII.GetString(buffer);          // alloc
string[] fields = msg.Split('\u0001');                  // alloc x N
foreach (var f in fields)
{
    var kv = f.Split('=');                              // alloc x 2N
    if (kv[0] == "55") symbol = kv[1];                  // alloc
}
```

**After:**

```csharp
ReadOnlySpan<byte> msg = buffer.AsSpan(0, length);
var reader = new FixFieldReader(msg);                   // ref struct, stack-only (Doc 04 §4.3)
while (reader.TryNext(out int tag, out ReadOnlySpan<byte> value))
{
    if (tag == 55)
        symbolId = _symbols.Lookup(value);              // hash over bytes, no string
}
```

Companion rules from Doc 04 that apply during this transform: parsers are `ref struct`s so they cannot escape to the heap; `value` spans alias the network buffer and must not outlive the message scope (ownership rule, Doc 01 §1.4); any field that must outlive the scope is *copied* into a struct message or arena.

### T3 — Closures/events → ring subscription or struct callbacks

**Before:**

```csharp
_orderManager.OnFill += fill => _strategy.HandleFill(fill, _book, currentRegime); // closure alloc per subscription, delegate invoke per fill, captured locals
```

**After (preferred — ring):**

```csharp
// Fills are published as FillMsg structs onto OrderEventRing (Doc 05 §5.5).
// Strategy thread consumes in its pinned loop (Doc 07 §7.3):
while (_running)
{
    while (_orderEvents.TryRead(out FillMsg fill))
        _strategy.HandleFill(in fill);
    _mdRing.DrainInto(_strategy);
    Thread.SpinWait(spinIterations);
}
```

**After (when a direct callback is unavoidable):** a static-lambda or interface on a pre-allocated singleton — `static` lambdas (C# 9+) are compile-time guaranteed capture-free; the Doc 08 analyzer bans non-static lambdas in `[HotPath]` methods.

### T4 — `async`/`await` → pinned-thread event loops (Doc 07)

**Before:**

```csharp
async Task RunAsync()
{
    while (true)
    {
        var msg = await _channel.Reader.ReadAsync();   // Task + continuation alloc, thread-pool hop, scheduling jitter
        await ProcessAsync(msg);
    }
}
```

**After:**

```csharp
// Doc 07 §7.2: dedicated pinned thread, busy-spin with bounded backoff
var t = PinnedThread.Start(core: 4, name: "strategy", ThreadPriority.Highest, () =>
{
    var spinner = new BoundedSpinPolicy(maxSpins: 4096);   // struct
    while (Volatile.Read(ref _running))
    {
        if (_mdRing.TryRead(out QuoteMsg q)) { Process(in q); spinner.Reset(); }
        else spinner.Spin();
    }
});
```

`async` remains permitted on warm/cold paths and in network *session management* (logon, heartbeats); the per-message data path never awaits.

### T5 — Collection churn → pre-sized, open-addressed, struct-keyed collections

**Before:**

```csharp
var ordersForSymbol = _orders.Values.Where(o => o.Symbol == s).ToList();  // LINQ enumerator + closure + List alloc
_pendingBySymbol = new Dictionary<string, List<Order>>();                  // string keys (hash = alloc-adjacent, compare = slow), resize churn
```

**After:**

```csharp
// Doc 02 §2.6: fixed-capacity, open-addressing map with int keys, allocated once at warmup
private readonly FixedCapacityMap<int, OrderSlotList> _pendingBySymbol
    = new(capacity: 16_384);   // sized from audit data ×4 headroom (Doc 06 §6.4 sizing rule)

// Iteration without LINQ:
var slots = _pendingBySymbol.GetRef(symbolId);
for (int i = 0; i < slots.Count; i++)
    Process(ref slots.GetRef(i));
```

Mechanical sub-rules: no LINQ in `[HotPath]` (analyzer-enforced); no `foreach` over interfaces (boxes the enumerator) — `foreach` over arrays/`Span<T>`/concrete `List<T>` is fine; collections are sized at warmup from audit p99.9 cardinality × 4 and **never grow** — overflow is a `RingFullPolicy`-style fault, not a resize (Doc 09 §9.2).

### T6 — Boxing elimination

| Before | After |
|---|---|
| `object key = orderId;` / non-generic `Hashtable` | Generic `FixedCapacityMap<long, …>` |
| `string.Format("...", structVal)` | Binary logger (T7) or `ISpanFormattable.TryFormat` into a pooled buffer |
| `enum.ToString()` | Pre-built `string[]` indexed by enum value (warmup-built, cold-path only) |
| `EqualityComparer<T>.Default` on non-generic constraint | Constrain `where T : IEquatable<T>` — devirtualizes and unboxes |
| Interface dispatch on struct (`(IFoo)myStruct`) | Generic method `Process<T>(in T msg) where T : struct, IFoo` |

### T7 — Logging → binary ring logger (Doc 05 §5.7)

**Before:** `_log.Info($"Sent order {orderId} px={price} qty={qty}");` — string interpolation + boxing per call.

**After:**

```csharp
_hotLog.Write(LogEvent.OrderSent, orderId, priceTicks, qty);
// Writes a 32-byte fixed record into the SPSC log ring; a cold-path
// drainer thread formats to text/Serilog off the hot core.
```

### T8 — Exceptions → result codes on the hot path

Exceptions don't allocate until thrown, but a thrown exception on the hot path allocates the exception + stack trace and costs ~microseconds. Per Doc 09 §9.6: expected failures (`RingFull`, `UnknownSymbol`, `RiskReject`) return enum result codes; exceptions are reserved for invariant violations that route to the kill switch.

```csharp
// Before:  void Send(Order o) { if (!risk.Check(o)) throw new RiskException(...); }
// After:
SendResult TrySend(in OrderMsg o)
{
    var risk = _risk.Check(in o);
    if (risk != RiskResult.Pass) { _hotLog.Write(LogEvent.RiskReject, o.ClOrdId, (int)risk); return SendResult.RiskRejected; }
    ...
}
```

### T9 — Long-lived mutable domain state → arena/pool residency (Docs 01, 02, 06)

Order books, position state, and working orders move from GC-heap object graphs into pool slots (`ObjectPool<T>` for class-based stateful objects, Doc 02) or arena-resident structs (`Arena`/`NativeBuffer<T>`, Doc 06). The decision rule from Doc 01 §1.5:

- Per-message, scope-bounded → stack / `ref struct` / ring slot.
- Per-order lifetime (seconds–hours) → pool slot with handle (`PoolHandle<T>`, generation-checked against use-after-return, Doc 02 §2.4).
- Per-session lifetime (order book arrays, symbol tables) → unmanaged arena, allocated once at warmup, never freed intra-day (Doc 06 §6.2).

---

## 10.5 Phasing Plan

Each phase ends deployable. Indicative durations are for one team of 4–6 on a ~200 kLOC system; scale accordingly.

### Phase 1 — Foundations + market-data ingest (4–6 weeks)

- Build/import the core library: rings (Doc 05), pools (Doc 02), arenas (Doc 06), pinned threads (Doc 07), struct messages (Doc 03), hot logger.
- Stand up CI infrastructure: `[HotPath]` analyzer, EventPipe allocation harness, ratchet file (§10.6.2) — initially gating only the new library's own tests at **zero**.
- Convert feed handlers: packet → `Span` parse (T2) → struct message (T1) → `MarketDataRing`. ACL adapter materializes old `Quote` objects for the legacy strategy.
- **Verify:** ingest stage shows zero post-warmup allocation in the harness; legacy strategy output is bit-identical on replay (golden-output test).

### Phase 2 — Order egress + FIX codec (4–6 weeks)

- Convert order construction and FIX encoding to `Span<byte>` writers into pre-allocated send buffers; orders flow as `OrderMsg` structs over `OrderRing`.
- ACL on the *inbound* side: legacy strategy's `Order` objects are copied into `OrderMsg` at the boundary.
- Replace hot-path exceptions with result codes (T8) through the egress chain.
- **Verify:** outbound FIX byte-identical to legacy on replay (codec golden tests); egress stage at zero allocation; wire-to-wire p99.9 measured before/after.

### Phase 3 — Strategy core (6–10 weeks; the long pole)

- Move order books and position state into arenas/pools (T9). Convert strategy event handling to pinned-loop ring consumption (T3, T4). Remove LINQ/closures/boxing (T5, T6). Hot logging (T7).
- Migrate **one strategy at a time**, lowest-risk first. Run old and new strategy implementations in **shadow mode** simultaneously: both consume the live `MarketDataRing`; new strategy's orders go to a comparison sink, not the exchange; divergence beyond tolerance pages the team.
- Delete the Phase 1/2 ACL adapters as each strategy cuts over.
- **Verify per strategy:** ≥5 trading days of shadow with zero unexplained decision divergence; zero post-warmup allocation on its pinned threads; latency budget met.

### Phase 4 — Risk checks, hardening, full contract (3–4 weeks)

- Pre-trade risk converted (it sits inline on egress, so it inherits the zero budget).
- Core pinning rollout to production hosts (isolcpus/affinity config per Doc 07 §7.6), NUMA placement of arenas (Doc 06 §6.5).
- Ratchet the CI allocation gate to **0 bytes post-warmup for the entire hot path**; enable the production EventPipe sentinel (Doc 08 §8.6) in page-on-alloc mode.
- Chaos drills from Doc 09: ring-full injection, pool-exhaustion injection, slow-consumer kill-switch test.

### Phase 5 — Decommission (1–2 weeks)

- Delete legacy hot-path code, dead ACLs, and the old allocating dependencies from the hot-path assemblies (so the analyzer's assembly-level bans (Doc 08 §8.4.3) can be enabled: no `System.Linq`, no Serilog reference, etc., in `*.HotPath.csproj`).

---

## 10.6 Rollout and Verification

### 10.6.1 Environments and gates per phase

1. **Replay rig** (deterministic pcaps): golden-output equivalence + allocation harness. Gate: zero divergence, allocation budget met.
2. **Shadow production**: live data, no live orders, full comparison. Gate: ≥5 clean days.
3. **Canary**: live orders, capped notional/order-rate on a small symbol set, old path on hot standby behind a feature flag (single atomic flag read on the egress thread — no allocation). Gate: ≥3 clean days, latency SLO met, zero sentinel alarms.
4. **Full cutover**, then legacy deletion only after one further week.

### 10.6.2 The CI ratchet

`allocation-budgets.json` in the repo root, consumed by the EventPipe CI harness (Doc 08 §8.5):

```json
{
  "warmupMarkerEvent": "FablePool.Hft/WarmupComplete",
  "budgets": [
    { "scope": "FablePool.Hft.Buffers.*",   "postWarmupBytes": 0 },
    { "scope": "Feeds.Handlers.*",          "postWarmupBytes": 0 },
    { "scope": "Oms.Egress.*",              "postWarmupBytes": 0 },
    { "scope": "Strategies.Alpha1.*",       "postWarmupBytes": 0 },
    { "scope": "Strategies.Legacy.*",       "postWarmupBytes": 50000000 }
  ]
}
```

Rules: budgets only ever decrease (the harness fails the build if a PR raises one without a `BUDGET-INCREASE` approval label); every phase exit moves its subsystem's budget to 0; the legacy scope's budget shrinks as strategies migrate, documenting progress on the public dashboard.

### 10.6.3 Production monitoring during rollout

From Doc 09's failure catalog, the rollout-specific watch list:

| Signal | Source | Alarm condition |
|---|---|---|
| Post-warmup alloc events on pinned threads | EventPipe sentinel (Doc 08 §8.6) | any |
| Gen0 GC count delta after warmup | `System.Runtime` counters | > 0/hour on hot process |
| Ring high-watermark | ring telemetry struct (Doc 05 §5.8) | > 50% capacity |
| Pool outstanding-handle high-watermark | pool telemetry | > 75% capacity |
| Generation-check failures (use-after-return) | `PoolHandle` fault counter | any → kill switch |
| Shadow decision divergence | comparison sink | beyond per-strategy tolerance |
| Wire-to-wire p99.9 | timestamping capture | > budget |

### 10.6.4 Rollback

Every phase keeps its predecessor warm: feature-flag at the ring producer/consumer seam routes traffic back to the legacy implementation within one config push. Rollback drill is rehearsed in the canary stage before each cutover. Arena/pool state is *not* shared with the legacy path (copy-only ACL), so rollback never requires state migration — the legacy path rebuilds its own state from the recovery feed exactly as it does on a normal restart.

---

## 10.7 Team Playbook and Common Pitfalls

- **One transformation per PR.** A PR applies one catalog pattern (T1–T9) to one census cluster. Reviewers check against the catalog, not from first principles.
- **Never "while we're in here."** Behavior changes and allocation changes ship separately; the golden-output replay test is only meaningful if behavior is held constant.
- **Don't pool what should be a struct.** Teams over-apply `ObjectPool<T>` to per-message data; the decision rule is Doc 01 §1.5 (T9 above). Pools are for *stateful, identity-bearing, long-lived* objects only.
- **Watch the ACL leak.** Grep for the ACL class names weekly; any new caller outside the designated seam is a regression.
- **Warmup must touch everything.** Each migrated subsystem registers a warmup routine (Doc 08 §8.3): pre-JIT via exercising every hot method on synthetic messages, fault every page of every arena, fill/drain every ring once, populate symbol tables. A subsystem that allocates on its *first* real message after warmup fails the contract just as hard as one that allocates on every message.
- **Tiered compilation:** hot-path assemblies set `<TieredCompilation>false</TieredCompilation>` or rely on warmup iteration counts > tier-up thresholds; verify with `dotnet-counters` `methods-jitted` flat-lining post-warmup (Doc 08 §8.3.2).

---

## 10.8 Worked End-to-End Example

To anchor the catalog, the repository includes (Phase 1 deliverable) `samples/migration/` containing a miniature legacy pipeline (`before/`: string-FIX parse → class Quote → event-driven strategy → string-FIX order) and its migrated form (`after/`), with the replay harness demonstrating golden-output equivalence and the EventPipe harness demonstrating 0 bytes allocated across 10 M messages post-warmup. Engineers use this pair as the reference diff when applying T1–T9 to production code.
