# Section 7 — Allocation-Source Catalog: Where Garbage Comes From in Trading Code

> **Scope of this section.** A systematic catalog of the allocation patterns that dominate
> managed-heap churn in typical C# trading systems: LINQ, closures, boxing, string handling,
> async state machines, and collection resizing — plus a set of secondary sources that
> repeatedly show up in production allocation profiles (iterators, params arrays, events,
> exceptions, formatting, library internals). Each entry explains the *mechanism* (what the
> compiler/runtime actually emits), gives *representative sizes and rates*, shows *detection*
> techniques, and gives the *canonical remediation*. The cross-strategy comparison and the
> decision matrices live in [Section 8](08-mitigation-strategies-and-decision-matrices.md).
>
> All byte sizes below are for **x64, .NET 8, default object layout** (16-byte object header
> overhead: 8-byte method-table pointer + 8-byte object header word; minimum object size
> 24 bytes). Sizes are illustrative of magnitude, not contractual — verify on your build with
> `dotnet-counters`, ETW `GCAllocationTick`, or BenchmarkDotNet's `[MemoryDiagnoser]`.

---

## 7.0 Why a per-pattern catalog matters

A latency-sensitive .NET process does not suffer from "allocation" in the abstract. It
suffers from **gen0 budget exhaustion frequency** (how often a gen0 GC triggers — see
Section 2.3) and **mid-life object promotion** (objects that survive into gen1/gen2 and
later force compaction — see Section 2.5). Two systems allocating the same MB/s can have
wildly different pause profiles depending on *object lifetime*:

| Lifetime class | Example | GC consequence |
|---|---|---|
| **Ephemeral (dies in gen0)** | LINQ enumerator created and dropped inside one tick handler | Frequent but short gen0 pauses; pause frequency scales with allocation *rate* |
| **Mid-life (survives to gen1/gen2, then dies)** | Order object alive for the lifetime of a resting order (seconds–minutes) | The worst case: promoted, then collected by gen2/background GC → long pauses, fragmentation |
| **Immortal (lives for process lifetime)** | Pre-allocated pools, symbol tables | Cheap after startup; promoted once, scanned via card tables thereafter |

The remediation goal for HFT code is therefore **two-sided**:

1. Drive the **steady-state ephemeral allocation rate toward zero** on the hot path
   (market-data → decision → order-out), so gen0 GCs become rare or absent during trading.
2. Convert **mid-life objects to pooled/immortal objects**, so gen2 never has anything
   new to do during the session.

Every entry below is tagged with the lifetime class it usually produces.

### How to read the entries

Each pattern carries a header table:

| Field | Meaning |
|---|---|
| **Severity** | Impact in a typical tick-handler hot path (Critical / High / Medium / Low) |
| **Lifetime** | Ephemeral / Mid-life / Both |
| **Typical size** | Per-occurrence heap cost on x64 |
| **Detectability** | How visible it is in profilers / analyzers |
| **Fix cost** | Engineering effort of the canonical remediation |

---

## 7.1 LINQ (`System.Linq`)

| Severity | Lifetime | Typical size | Detectability | Fix cost |
|---|---|---|---|---|
| **Critical** | Ephemeral | 48–200+ B per query *per execution*, plus O(n) buffers for sorting/grouping/materialization | High (shows clearly in allocation profiles as `Enumerable+WhereSelectListIterator` etc.) | Low–Medium (rewrite as loops) |

### 7.1.1 Mechanism

LINQ-to-objects is implemented as a chain of **heap-allocated iterator objects**. Each
operator in a query allocates at least one object when the query is *executed* (not when it
is declared — deferred execution means re-enumeration re-allocates in some shapes, and
always re-executes the lambdas):

```csharp
// Executed per market-data tick — a realistic anti-pattern:
var bestBids = quotes.Where(q => q.Side == Side.Bid)
                     .OrderByDescending(q => q.Price)
                     .Take(5)
                     .ToList();
```

Per execution this allocates, at minimum:

1. A **delegate** for each lambda (`q => q.Side == Side.Bid`, `q => q.Price`) — *if* the
   lambda captures nothing, Roslyn caches the delegate in a static field after first use
   (≈0 B steady-state); if it captures locals, see §7.2 — a fresh display class + delegate
   every call.
2. A **`WhereEnumerableIterator<T>`** (or `WhereListIterator<T>` / `WhereArrayIterator<T>`
   if the source type is recognized) — ~48–72 B.
3. **`OrderedEnumerable<T,TKey>`** — and at enumeration time an **O(n) buffer** (`Buffer<T>`:
   an array copy of the entire filtered input) **plus an O(n) keys array plus an O(n)
   int index map** for the sort. For a 5,000-quote book slice of 16-byte structs this is
   ~80 KB+ per tick — and at >85,000 B it lands on the **LOH** (Section 3), which is the
   single fastest way for "innocent" LINQ to create gen2/LOH pressure.
4. **`ToList()`** — a `List<T>` plus its backing array, with doubling-growth garbage if the
   count isn't known (see §7.6). Many operators (`OrderBy` output, iterator sources) do
   *not* expose a count, so `ToList` grows 4 → 8 → 16 → … leaving log₂(n) dead arrays.

`GroupBy`, `Distinct`, `Union`, `Except`, `Intersect`, `Join`, `ToDictionary`,
`ToLookup` all allocate **internal hash sets/lookups with O(n) bucket and entry arrays**.
`Select`/`Where` fuse into one iterator (`WhereSelectListIterator`) — a nice optimization
that still allocates.

### 7.1.2 The interface-dispatch tax and the struct-enumerator trap

Even allocation-free-looking iteration through LINQ surfaces costs because LINQ operates on
`IEnumerable<T>`:

```csharp
List<Quote> quotes = ...;
foreach (var q in quotes) { ... }            // List<T>.Enumerator (struct) — 0 allocations
IEnumerable<Quote> seq = quotes;
foreach (var q in seq) { ... }               // enumerator is BOXED — 1 allocation (~48 B)
```

`List<T>.GetEnumerator()` returns a **struct** enumerator; calling it through the
`IEnumerable<T>` interface forces `GetEnumerator()` to return `IEnumerator<T>`, **boxing**
the struct (see §7.3). Every LINQ operator therefore pays this once per source per
execution, and every API in your codebase typed as `IEnumerable<T>` invites it.

### 7.1.3 Representative measurements

Measured with BenchmarkDotNet `[MemoryDiagnoser]`, .NET 8, x64, 1,000-element
`List<Quote>` (`Quote` = 16-byte struct), per invocation:

| Expression | Allocated/op (approx.) | Notes |
|---|---:|---|
| `for` loop with index | 0 B | baseline |
| `foreach` on `List<T>` | 0 B | struct enumerator |
| `foreach` on `IEnumerable<T>` view of same list | 40–48 B | boxed enumerator |
| `.Where(p).Count()` (non-capturing lambda) | ~48 B | iterator only; delegate cached |
| `.Where(p).Count()` (capturing lambda) | ~136 B | + display class + delegate |
| `.Where(p).Select(s).ToList()` | ~8.3 KB | iterator + list growth (count unknown) |
| `.OrderBy(k).ToList()` | ~28 KB | buffer + keys + map + list |
| `.GroupBy(k).ToDictionary(...)` | ~90 KB+ | lookup internals + dictionary |

At 100k ticks/sec, even the "cheap" 136 B case is **13.6 MB/s** — with a typical
~256 MB Server-GC gen0 budget that is a gen0 GC roughly every 19 seconds *from this one
line alone*; real handlers contain dozens of such lines and the budget is shared with
everything else on the heap.

### 7.1.4 Detection

- **Allocation profilers** (PerfView `GC Heap Alloc Ignore Free`, dotMemory, dotTrace,
  Visual Studio .NET Object Allocation Tracking): LINQ shows up as `System.Linq.Enumerable+*Iterator`,
  `Buffer<T>`, `Lookup<TKey,TElement>`, plus compiler types `<>c__DisplayClass*`.
- **Roslyn analyzers**: the Microsoft.CodeAnalysis-based
  [ClrHeapAllocationAnalyzer / Microsoft.CodeAnalysis.BannedApiAnalyzers] combination —
  many shops simply **ban `System.Linq` in hot-path assemblies** with `BannedSymbols.txt`.
- **BenchmarkDotNet** `[MemoryDiagnoser]` in CI as a regression gate (fail the build if
  `Allocated > 0 B` for hot-path benchmarks).

### 7.1.5 Remediation

| Option | Steady-state alloc | Notes |
|---|---|---|
| Hand-written `for`/`foreach` over concrete types | 0 B | Canonical answer; verbose but predictable |
| Concrete-type generic helpers (`static int CountWhere<T>(List<T>, Func<T,bool>)` with cached non-capturing delegates) | 0 B | Recovers some expressiveness |
| Struct-based LINQ libraries (`NetFabric.Hyperlinq`, `StructLinq`, `ZLinq`) | ~0 B | Value-type operator chains; verify per-operator; adds dependency risk |
| Keep LINQ off-hot-path only (startup, config, reporting threads) | n/a | The pragmatic policy most shops adopt |

**Policy used by most production low-latency .NET shops:** LINQ is allowed in cold paths
and tests; **prohibited in any assembly marked hot-path**, enforced by analyzer + code review.

---

## 7.2 Closures, lambdas, and display classes

| Severity | Lifetime | Typical size | Detectability | Fix cost |
|---|---|---|---|---|
| **Critical** | Ephemeral (but easily mid-life via continuations/timers) | 24–88+ B per capture site per execution (display class + delegate) | Medium (compiler-generated names obscure source) | Low |

### 7.2.1 Mechanism

A lambda that **captures nothing** is compiled to a static method whose delegate Roslyn
caches in a hidden static field — *one* allocation for the process lifetime:

```csharp
prices.RemoveAll(p => p <= 0);     // non-capturing: delegate cached, ~0 B steady-state
```

A lambda that **captures a local, parameter, or `this`** is compiled into a
**display class** (`<>c__DisplayClass3_0`) holding the captured variables. Every execution
of the *enclosing scope* allocates:

- the display class: 24 B + 8 B per captured reference / sizeof per captured value;
- a fresh delegate pointing at the display-class instance method: 64 B.

```csharp
void OnTick(in Tick tick)
{
    decimal limit = tick.Price * 1.001m;
    // captures `limit` → display class (24 + 16 = 40 B) + delegate (64 B) PER TICK:
    var crossed = book.Asks.FindAll(a => a.Price <= limit);
}
```

**Capture widening** is the nastier variant: when *multiple* lambdas in the same scope
capture *different* variables, Roslyn may merge them into **one display class holding all
of them**, so a lambda that "only" captures a cheap `int` can keep a large object graph
alive because a sibling lambda captured it. This both inflates the allocation and —
if any of the delegates is stored (event subscription, timer, continuation) — **promotes
the entire display class to mid-life/gen2**, dragging captured buffers with it. This is a
classic .NET memory-leak-shaped promotion source.

`this` capture is implicit and easy to miss: a lambda touching any instance field captures
`this`; if that delegate is handed to a long-lived component, the whole owning object is
pinned in the live set.

### 7.2.2 Loop-variable capture multiplies allocations

```csharp
foreach (var venue in venues)
    venue.OnFill += fill => router.Record(venue.Id, fill);   // display class + delegate PER venue PER subscription
```

A display class is created **per iteration** (the capture scope is the loop body). In
subscription-heavy startup code this is fine; inside reconnection/resubscription logic that
runs intra-day it becomes steady-state garbage *and* an unsubscribe-leak hazard.

### 7.2.3 Remediation

1. **`static` lambdas (C# 9)** — `static x => x.Price` is a *compile error* if it captures
   anything. Make `static` the default in hot-path code style; an analyzer
   (e.g., IDE rule for non-static lambdas, or a custom analyzer) can enforce it.
2. **Pass state explicitly through generic "state-passing" overloads** instead of capturing:

   ```csharp
   // Capturing (allocates per call):
   list.Sort((a, b) => a.Dist(refPoint).CompareTo(b.Dist(refPoint)));
   // State-passing pattern (zero steady-state alloc):
   list.Sort(refPoint, static (a, b, rp) => a.Dist(rp).CompareTo(b.Dist(rp)));  // custom overload
   ```

   The BCL increasingly offers these (`string.Create(length, state, static (span, state) => …)`,
   `ConcurrentDictionary.GetOrAdd(key, static (k, arg) => …, arg)`). Mirror the pattern in
   internal APIs: `TArg arg, Func<TItem,TArg,TResult>`.
3. **Cache delegates** for unavoidable capture-free callbacks in `static readonly` fields
   (belt-and-braces; Roslyn does this for you for non-capturing lambdas, but explicit fields
   survive refactors that accidentally introduce capture).
4. **Local functions** instead of lambdas where no delegate is needed — a local function
   *called directly* compiles to a plain method with a by-ref struct closure (zero heap);
   it only allocates if converted to a delegate.

### 7.2.4 Detection

Allocation profiles: `<>c__DisplayClass*` and `System.Action`/`System.Func` instances.
Heap dumps with many display classes rooted by event handlers/timers indicate the
promotion variant. The Roslyn "heap allocation analyzer" family flags capturing lambdas at
build time.

---

## 7.3 Boxing

| Severity | Lifetime | Typical size | Detectability | Fix cost |
|---|---|---|---|---|
| **High** | Ephemeral | 24 B minimum per box (16 B header + payload, padded) | Medium–High (profilers show boxed `Int32`, `Decimal`, enum types) | Low–Medium |

### 7.3.1 Mechanism

Boxing copies a value type into a fresh heap object whenever a value type is converted to
`object`, to a non-generic interface it implements, or (historically) flows through
non-generic APIs. Each box is a full heap allocation with header overhead — boxing a 4-byte
`int` costs 24 B; boxing a 16-byte `decimal` costs 32 B.

**The recurring trading-code offenders:**

| Pattern | Why it boxes | Modern status |
|---|---|---|
| `string.Format("{0}", price)` / non-interpolated formatting with struct args | `params object[]` parameters | Still boxes; interpolation handlers fix it (§7.4) |
| Logging: `log.Info("px={0} qty={1}", px, qty)` | `params object?[]` | Boxes per call **even when the log level is disabled** unless the logger has generic/guarded overloads (`LoggerMessage.Define`, ZLogger, message templates with generic params) |
| `Dictionary<EnumKey, T>` on old runtimes | `EqualityComparer<TEnum>.Default` used to box via `ObjectEqualityComparer` | **Fixed** in modern .NET (devirtualized, non-boxing `EnumEqualityComparer`); still cited from .NET Framework experience |
| `enumValue.HasFlag(other)` | Boxed both operands on .NET Framework | **JIT-intrinsified (no boxing) in .NET Core 2.1+**; use `(v & f) == f` only for Framework targets |
| Calling `GetHashCode`/`Equals`/`ToString` on a struct **that doesn't override them** | Falls back to `ValueType` reflective implementations; `Equals(object)` boxes the argument; reflection path may box fields | Always override + implement `IEquatable<T>` on hot structs |
| Struct → interface variable (`IComparable c = myDecimal;`) | Interface reference requires a heap object | Use generic constraints instead |
| `ArrayList`, `Hashtable`, `IEnumerable` (non-generic), `DataTable` | Pre-generics collections store `object` | Ban in hot path |
| Tuples via `Tuple<...>` | `Tuple` is a class (allocation, not strictly boxing) | Use `ValueTuple` — but beware ValueTuple → `ITuple`/`object` conversions which box the whole tuple |
| `yield`/iterator + struct enumerables, `IEnumerable<T>` views of struct-enumerator collections | Boxed enumerators (see §7.1.2) | Concrete types / duck-typed `foreach` |
| Closing over value types via `object state` APIs (`Timer(callback, object state, …)`, `ThreadPool.QueueUserWorkItem`) | `object state` parameter | Use generic-state overloads (`QueueUserWorkItem<TState>`), or pre-box once at startup |

**The generics escape hatch and where it leaks.** Generic code over `T : struct` with
interface *constraints* does **not** box: `void M<T>(T x) where T : IComparable<T>` calls
through a constrained call. But assigning `T` to an interface-typed *variable*, or passing
it to a non-generic parameter, boxes. Default-interface-method calls on structs through the
interface also box. The rule of thumb: **value types must travel through generic type
parameters, never through interface or `object` references.**

### 7.3.2 Why boxing is disproportionately bad for GC

Boxes are small, numerous, and frequently *stored* (logging queues, dictionaries keyed by
`object`, event args), which converts them from ephemeral to **mid-life** — small objects
sprayed across gen1/gen2 are the canonical source of gen2 fragmentation that eventually
forces compaction (Section 2.5, Section 3.4).

### 7.3.3 Detection

- ETW/EventPipe `GCAllocationTick` sampled types showing `System.Int32`, `System.Decimal`,
  `System.Boolean`, enum types **as heap objects** is a smoking gun — value types should
  essentially never appear as allocation types.
- IL inspection: search for `box` opcodes (ILSpy, `dotnet-ildasm`); the heap-allocation
  Roslyn analyzers flag boxing conversions at compile time.
- PerfView's "Boxing" view groups boxed-type allocations directly.

### 7.3.4 Remediation summary

Generic APIs with constraints; `IEquatable<T>`/`GetHashCode` overrides on all hot structs;
interpolation handlers / generic logging; ban non-generic collections; `ValueTuple`;
explicit flag math only when targeting .NET Framework; analyzer enforcement.

---

## 7.4 Strings and text handling

| Severity | Lifetime | Typical size | Detectability | Fix cost |
|---|---|---|---|---|
| **Critical** (feed handlers / FIX) | Both | 26 B + 2 B/char per string; O(n) per transform | High | Medium–High (UTF-8 pipeline redesign) |

### 7.4.1 Mechanism

.NET strings are immutable UTF-16 heap objects (~26 B overhead + 2 bytes/char, padded).
**Every transformation allocates a new string.** A FIX/ITCH/JSON feed handler written in
"normal C#" allocates 5–20 strings per message:

```csharp
// Anti-pattern feed handler — each line allocates:
string msg   = Encoding.ASCII.GetString(buffer, 0, len);  // 1 string, O(n); >85KB → LOH
string[] kvs = msg.Split('\u0001');                       // 1 array + N strings
foreach (var kv in kvs)
{
    var parts = kv.Split('=');                            // 1 array + 2 strings each
    if (parts[0] == "44")
        price = decimal.Parse(parts[1]);                  // ok, but upstream already burned
}
string key = symbol + ":" + venue;                        // 1 string (concat)
log.Info($"order {id} px {price}");                       // string + (historically) boxes
```

Hidden allocators frequently missed in review:

| API | Allocation | Allocation-free alternative |
|---|---|---|
| `Substring`, `Remove`, `Replace`, `Trim`, `ToUpper(Invariant)`, `PadLeft` | new string each | `ReadOnlySpan<char>` slicing (`AsSpan(start, len)`), `MemoryExtensions.Trim`, ordinal-ignore-case comparisons instead of normalizing |
| `Split` | string[] + element strings | `Span`-based manual tokenization, or `MemoryExtensions.Split` (Span-returning ranges, .NET 8+) |
| `int/decimal/double.ToString()`, `DateTime.ToString(fmt)` | new string + possible culture lookups | `ISpanFormattable.TryFormat(Span<char>…)`; `Utf8Formatter.TryFormat` for UTF-8 |
| `Encoding.GetString` | new string O(n) | parse from bytes directly: `Utf8Parser`, `System.Text.Json` UTF-8 readers, `ReadOnlySpan<byte>` |
| `string.Concat`/`+` in loops | O(n²) garbage | pooled `StringBuilder` (but see chunk allocs §7.6.4) or `string.Create` |
| `$"..."` interpolation pre-.NET 6 | `string.Format` + boxing | .NET 6+ lowers to `DefaultInterpolatedStringHandler` (no boxing for `ISpanFormattable` args; still allocates the final string). Custom handlers (`AppendInterpolatedStringHandler`, logger handlers) can be fully alloc-free |
| `Guid.ToString()`, `Enum.ToString()` | new string; Enum historically did reflection | cache enum-name maps at startup; `TryFormat` for Guid |
| String keys built per-lookup (`symbol + venue`) | new string per lookup | composite struct keys with `IEquatable<T>`, or interned/pooled keys |

### 7.4.2 Lifetime behavior — the promotion problem

Feed-handler strings are often *stored* (last-trade caches, order books keyed by string
symbol, audit trails), making them **mid-life**: they survive gen0/gen1 and die in gen2 —
the most expensive possible lifetime. Long messages (snapshot payloads, JSON blobs
>85,000 B as UTF-16, i.e. >~42,500 chars) land directly on the **LOH** (Section 3),
fragmenting it.

### 7.4.3 The canonical fix: stay in UTF-8 bytes end-to-end

Production low-latency .NET feed handlers do not materialize strings on the hot path at all:

1. Receive into **pooled / pinned (POH) byte buffers** (Section 3.6).
2. Tokenize with `ReadOnlySpan<byte>` (`IndexOf((byte)0x01)` is SIMD-vectorized).
3. Parse numerics with **`System.Buffers.Text.Utf8Parser`**
   (`Utf8Parser.TryParse(span, out decimal value, out int consumed)`) — zero allocation.
4. Map symbols via a **`ReadOnlySpan<byte>` → int dictionary** (custom open-addressing map
   with byte-sequence hashing, or .NET 9+ `Dictionary` *alternate lookup*
   `GetAlternateLookup<ReadOnlySpan<byte>>()`; on .NET 8 a custom map is required since
   spans can't be dictionary keys directly). Interned `string`s materialize **once per
   symbol at startup**, never per message.
5. Outbound messages built with `TryFormat`/`Utf8Formatter` into pre-allocated buffers.

This is a redesign, not a tweak — which is why it appears in the decision matrices of
Section 8 as a high-cost/high-payoff strategy reserved for the actual hot path.

### 7.4.4 Detection

Allocation profiles dominated by `System.String`, `System.Char[]`, `System.String[]`
indicate this pattern; PerfView's allocation stacks attribute them to `Split`/`Substring`/
`Format` frames precisely.

---

## 7.5 `async`/`await` state machines and Task plumbing

| Severity | Lifetime | Typical size | Detectability | Fix cost |
|---|---|---|---|---|
| **High** | Ephemeral → mid-life (pending continuations live as long as the awaited operation) | 0 B (sync completion) to ~150–400+ B per truly-async await chain hop | Medium (compiler `<MethodName>d__N` types) | Medium–High (architectural) |

### 7.5.1 Mechanism

The compiler rewrites every `async` method into a **state-machine struct**
(`<MethodName>d__7 : IAsyncStateMachine`). Allocation behavior depends on the *runtime path*:

- **Synchronous completion** (the awaited task was already complete): in Release builds on
  modern .NET, the state machine **stays on the stack**; an `async Task<T>` method may even
  return a **cached completed task** (cached `Task<bool>` true/false, small-int caches,
  `Task.CompletedTask`) → **zero allocations**. This is why `async` looks free in
  benchmarks that never actually suspend.
- **First genuine suspension**: the runtime allocates, per async method in the chain:
  1. the **boxed state machine** — since .NET Core 2.1 the struct is hoisted into a single
     `AsyncStateMachineBox<TStateMachine>` object that also *is* the returned `Task`
     (merging what used to be 2–3 allocations into one): ~56 B + size of all hoisted
     locals/parameters/awaiters;
  2. possibly an **`ExecutionContext`/context-capture cost** and, with a custom
     `SynchronizationContext`/scheduler, additional continuation wrapper objects;
  3. for `Task.Delay`, a **`DelayPromise` + a `TimerQueueTimer`**; for `WhenAll`, a
     promise + defensive array copy.

  An await *chain* (A awaits B awaits C awaits the socket) pays this **per method** —
  deep async call stacks multiply the cost.
- **Every async hop also costs scheduling latency** (thread-pool dispatch, typically
  ~1–30 µs and unbounded under pool starvation), which for HFT is usually a bigger problem
  than the bytes. This is why hot paths in low-latency shops are **synchronous,
  thread-affine, and often spinning** (Section 8), with async confined to control plane,
  connectivity management, and cold I/O.

### 7.5.2 `ValueTask`, pooling, and `IValueTaskSource`

| Tool | Effect | Caveats |
|---|---|---|
| `ValueTask`/`ValueTask<T>` return types | Zero alloc when completing synchronously (result inline in the struct) | Still allocates a box on true suspension unless backed by a reusable source; single-consumption rules (`await` once, no concurrent ops) |
| `IValueTaskSource<T>` + `ManualResetValueTaskSourceCore<T>` | Fully **amortized-zero-allocation** async: the source object is pooled/reused across operations. This is how `Socket`'s modern async methods (via cached `AwaitableSocketAsyncEventArgs`), `System.IO.Pipelines`, and Kestrel achieve near-zero steady-state async allocation | Significant implementation complexity (versioning tokens, completion races) |
| `[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]` on an `async ValueTask<T>` method (or the `DOTNET_System.Threading.Tasks.UseAsyncPooling`-style env switch in some versions) | Pools the state-machine boxes | Pool contention/locality tradeoffs; measure — Microsoft found it a wash for many workloads and kept it opt-in |
| `ConfigureAwait(false)` | Avoids context-capture continuation wrappers (and deadlocks); minor alloc/latency win | Mandatory hygiene in library code anyway |
| `SocketAsyncEventArgs` (classic) | Pre-allocated, reusable async socket ops; pin-once buffers | The pre-Pipelines workhorse of low-latency .NET networking; still fully supported |

### 7.5.3 The lifetime trap

A pending async operation roots its boxed state machine, **all hoisted locals** (buffers!
display classes! `this`!) until completion. Thousands of in-flight awaits (e.g., one
pending read per connection) create a standing population of mid-life objects that the GC
must trace every gen2/background cycle. Pooled `IValueTaskSource` implementations fix the
allocation but the *rooting* of hoisted state remains — keep hoisted state small.

### 7.5.4 Policy

- Hot path (tick → order): **no `await`**. Synchronous, pinned-thread, busy-poll or
  low-latency blocking I/O.
- Warm path (order lifecycle, venue session management): `ValueTask` +
  `IValueTaskSource`/Pipelines, `ConfigureAwait(false)`, bounded in-flight counts.
- Cold path (admin, persistence, monitoring): ordinary async/await — productivity wins.

### 7.5.5 Detection

Allocation profiles: `AsyncStateMachineBox`, `<MethodName>d__N`, `Task<T>`,
`TimerQueueTimer`, `DelayPromise`. `dotnet-counters` thread-pool queue length spikes reveal
the scheduling-latency side.

---

## 7.6 Collection growth and churn

| Severity | Lifetime | Typical size | Detectability | Fix cost |
|---|---|---|---|---|
| **High** | Ephemeral garbage + mid-life survivors + **LOH** for big arrays | O(capacity) per growth step; discarded old arrays | Medium (shows as `T[]`, `Dictionary.Entry[]` allocations) | Low (pre-size) to Medium (pool) |

### 7.6.1 `List<T>` doubling

`List<T>` starts at capacity 0, allocates 4 on first `Add`, then **doubles**: growing to
1,048,576 elements allocates ~21 successive arrays and discards 20 of them — for an 8-byte
element type that is ~16 MB of pure garbage, and every array beyond 10,625 elements
(8-byte elements, >85,000 B) was an **LOH allocation** (Section 3). The same doubling logic
drives `Queue<T>`, `Stack<T>`, `HashSet<T>`, `Dictionary<K,V>` (prime-stepped),
`MemoryStream`, and `StringBuilder`-adjacent buffers.

**Fixes:** construct with capacity (`new List<Order>(expectedMax)`); call
`EnsureCapacity` before bulk inserts (available on `List/Dictionary/HashSet/Queue/Stack`
in modern .NET); `CollectionsMarshal.SetCount`/`AsSpan` for advanced fill patterns;
**never `Clear()`-and-let-die per cycle — reuse** (`Clear` keeps capacity; this is the
correct per-tick reset for scratch lists).

### 7.6.2 `Dictionary<K,V>` / `HashSet<T>` rehashing

Growth allocates a **new bucket `int[]` and a new `Entry[]`** and rehashes everything —
an O(n) stop on whatever thread triggered it (a microburst hazard in itself) plus two
discarded arrays. Entry arrays for big dictionaries are LOH-resident. `Remove` does not
shrink; `TrimExcess` reallocates (do it only off-hours). Pre-size to the worst-case symbol/
order count at startup. For per-tick scratch sets, reuse a cleared instance.

### 7.6.3 `ConcurrentQueue<T>` / channels — segment churn

`ConcurrentQueue<T>` allocates **32-slot segments** as it grows and retires them as
consumers drain — a steady allocation stream proportional to throughput (segments are
reused only in limited fast paths). `Channel<T>` (unbounded) sits on the same machinery and
also allocates on the async waiting path. **High-rate inter-thread handoff in low-latency
.NET uses pre-allocated ring buffers instead** — `Disruptor-net` (the LMAX pattern), or a
hand-rolled bounded SPSC/MPSC ring over an array, optionally with padded sequence fields to
prevent false sharing. Bounded `Channel<T>` with `SingleReader/SingleWriter` hints is the
acceptable middle ground for warm paths.

### 7.6.4 `StringBuilder` chunking

`StringBuilder` is a linked list of chunks (default initial 16 chars), allocating new chunks
as it grows, plus the final `ToString()` string. Pool builders
(`StringBuilderCache`-style or `ObjectPool<StringBuilder>` from
`Microsoft.Extensions.ObjectPool`) with a sane initial capacity; for hot output paths skip
it entirely and `TryFormat` into pooled `char[]`/`byte[]`.

### 7.6.5 Arrays and buffers — `ArrayPool<T>`

Per-message `new byte[n]` is the classic feed-handler bug: ephemeral garbage at small n,
**LOH churn** at n ≥ 85,000 B. `ArrayPool<T>.Shared` (bucketed by power-of-two, returns
arrays ≥ requested length) eliminates it:

```csharp
byte[] buf = ArrayPool<byte>.Shared.Rent(len);
try { /* fill, parse via buf.AsSpan(0, len) */ }
finally { ArrayPool<byte>.Shared.Return(buf); }   // clearArray: false on hot path
```

Caveats: rented arrays are over-sized (track your length separately); double-return is a
corruption bug (debug-only guards advisable); `Shared` is per-core-bucketed and can still
contend — dedicated pools (`ArrayPool<T>.Create(maxLength, perBucket)`) or fixed
pre-allocated buffer rings are common in hot paths. For buffers handed to native/socket I/O
repeatedly, allocate **once, at startup, on the POH** (`GC.AllocateArray<byte>(len,
pinned: true)`) and avoid per-operation pinning entirely (Section 3.6).

### 7.6.6 Detection

Allocation stacks ending in `List<T>.Grow`/`SetCapacity`, `Dictionary<K,V>.Resize`,
`ConcurrentQueue<T>+Segment`; LOH allocation events (`GCAllocationTick` with
`Kind=Large`) attribute big-array churn precisely.

---

## 7.7 Secondary sources (frequently found in real profiles)

These rank below the big six in typical profiles but routinely contribute the "long tail"
that keeps gen0 from ever going quiet.

| # | Source | Mechanism | Remediation |
|---|---|---|---|
| 7.7.1 | **Iterator methods (`yield return`)** | Compiler-generated class per call (state machine implementing `IEnumerable<T>`+`IEnumerator<T>`, ~48–80 B + hoisted locals) | Return concrete collections or write struct enumerators for hot sequences |
| 7.7.2 | **`params` arrays** | `M(a, b, c)` with `params object[]`/`params T[]` allocates an array per call (plus boxing for `object[]`). C# 13 `params ReadOnlySpan<T>` overloads remove it | Provide fixed-arity overloads (the BCL pattern: `Concat(string,string,string)`); prefer span-params where available |
| 7.7.3 | **Events / multicast delegates** | `+=`/`-=` allocate a **new delegate and a new invocation-list array** each time (delegates are immutable). Subscribe/unsubscribe churn intra-day = steady garbage; handlers also root subscribers (promotion/leaks) | Subscribe once at startup; for dynamic listeners use pre-sized arrays of handler interfaces; weak-event patterns only off hot path (they allocate too) |
| 7.7.4 | **Exceptions as control flow** | Exception object + stack trace capture + `string` message; cost is microseconds-to-milliseconds *and* allocations; first-chance handling machinery disturbs latency even when caught | `Try*` patterns exclusively on hot path (`TryParse`, `TryGetValue`, `TryDequeue`); exceptions reserved for genuinely exceptional, session-fatal conditions |
| 7.7.5 | **`DateTime`/`TimeSpan` formatting & parsing, culture lookups** | `ToString`/`Parse` allocate; culture-sensitive paths allocate more | `TryFormat` with `CultureInfo.InvariantCulture`; in HFT keep time as `long` nanoseconds/ticks end-to-end, format only at the edge |
| 7.7.6 | **`Guid.NewGuid().ToString()` for order IDs** | string per order; Guid gen itself is fine | `long`/`ulong` sequence IDs; format on demand with `TryFormat` |
| 7.7.7 | **Serializers / JSON** | Newtonsoft.Json allocates heavily (tokens, boxed values, strings); `System.Text.Json` is far better but `JsonDocument`/`Deserialize<T>` still allocate | Hot path: hand-rolled binary or fixed-layout structs over spans; warm path: `Utf8JsonReader`/`Writer` over pooled buffers, source-generated contexts |
| 7.7.8 | **Timers** | Each `System.Threading.Timer` / `Task.Delay` allocates timer nodes; high-frequency timer churn also contends on the timer queue | One coalesced wheel/scheduler thread for all timeouts, pre-allocated timeout entries |
| 7.7.9 | **`ToString()`/`Equals` on structs via `Console.WriteLine`/`Debug.Assert`/interpolation** | Boxing + strings sneak in through diagnostics left in hot code | Strip via `[Conditional]`, analyzers; never `Console` on hot path |
| 7.7.10 | **Defensive copies vs. allocation trade** | Not an allocation, but the fear of struct copying drives devs back to classes; note `in` parameters, `ref readonly`, and `ref struct` keep large structs cheap *without* heap | Code-style guidance: structs + `ref`/`in` plumbing |
| 7.7.11 | **Library internals you don't control** | e.g., some vendor APIs allocate per callback (string symbols, event args objects) | Wrap at the boundary: translate vendor objects into pooled internal representations immediately; pressure vendors for span/struct APIs |
| 7.7.12 | **Reflection / `dynamic`** | `dynamic` call sites allocate binder caches and box arguments; reflection `Invoke` boxes everything | Ban on hot path; source generators / compiled delegates at startup |

---

## 7.8 Consolidated severity matrix

Severity assumes a 50k–500k msg/s feed handler + strategy loop targeting ≤100 µs p99
wire-to-wire, on .NET 8 Server GC.

| Source | Steady-state rate if unmitigated | Lifetime risk | LOH risk | Fix cost | Priority |
|---|---|---|---|---|---|
| Strings / text (§7.4) | 10s of MB/s | Mid-life (caches) | **Yes** (large messages) | Med–High | **1** |
| LINQ (§7.1) | MB/s–10s MB/s | Ephemeral | Yes (`OrderBy`/`ToArray` buffers) | Low–Med | **2** |
| Collection churn (§7.6) | MB/s | Mixed | **Yes** (big arrays/entry tables) | Low | **3** |
| Closures (§7.2) | MB/s | Ephemeral→Mid-life (stored delegates) | No | Low | **4** |
| Boxing (§7.3) | 100s KB/s–MB/s | Ephemeral→Mid-life (stored boxes) | No | Low–Med | **5** |
| Async machinery (§7.5) | 100s KB/s + *latency jitter* | Mid-life (pending ops) | No | Med–High | **6** (but #1 for jitter) |
| Exceptions (§7.7.4) | bursty | Ephemeral | No | Low | situational |
| Events/timers/iterators/params (§7.7) | long tail | Mixed | No | Low | cleanup pass |

**Detection toolchain summary** (applies to all entries): ETW/EventPipe
`GCAllocationTick` + PerfView allocation stacks for production; BenchmarkDotNet
`[MemoryDiagnoser]` + hot-path "0 B allocated" CI gates for regression prevention;
Roslyn heap-allocation analyzers + `BannedSymbols.txt` for prevention at authoring time;
`dotnet-counters` (`Allocation Rate`, `% Time in GC`, gen0/1/2 counts) for continuous
monitoring. References for each tool are annotated in
[Section 9](09-annotated-references.md).

---

*Previous: [Section 4 — GC Modes & Latency Settings](04-gc-modes-and-latency-settings.md) ·
Next: [Section 8 — Mitigation Strategies & Decision Matrices](08-mitigation-strategies-and-decision-matrices.md)*
