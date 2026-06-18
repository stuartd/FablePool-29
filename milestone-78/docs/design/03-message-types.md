# 03 — Struct-Based Message Types

## 1. Scope

This document specifies how every hot-path datum that crosses a boundary — market data in, signals
between stages, orders out — is represented: as **fixed-layout `unmanaged` structs**, decoded and
encoded **in place** over ring/buffer memory, with no managed allocation, no copies beyond what
the wire requires, and no virtual dispatch.

## 2. Design rules for message structs

| Rule | Statement | Rationale |
|------|-----------|-----------|
| T-1 | Every message type is a `readonly struct` (views) or mutable `struct` (builders), `unmanaged`, with `[StructLayout(LayoutKind.Explicit)]` or `Sequential, Pack = N` and a `static` asserted size | Layout must be deliberate, not JIT-chosen; size asserts catch accidental growth |
| T-2 | No reference-type fields, ever (no `string`, no arrays) | M-8; keeps slabs reference-free |
| T-3 | Variable-length content is represented as (offset,length) into the surrounding buffer, exposed as `ReadOnlySpan<byte>` properties on a `ref struct` view | Avoids copies and allocation |
| T-4 | No interfaces implemented by message structs except `IEquatable<T>` where needed; **never** pass a message struct as an interface (boxing) | Boxing is allocation |
| T-5 | Message structs passed by `in`/`ref`/`out`, never by value when > 16 bytes | Copy cost + register pressure |
| T-6 | All enums backed by explicit integral types sized for the wire (`: byte`, `: ushort`) | Layout determinism |
| T-7 | Prices and quantities are fixed-point integers (`long` ticks / `long` lots via `Px`/`Qty` wrapper structs), never `decimal`/`double` for money | `decimal` ops are slow and `ToString` allocates; determinism |
| T-8 | Timestamps are `long` nanoseconds since session epoch (`Ts` wrapper struct) | No `DateTime`/`DateTimeOffset` arithmetic surprises; cheap |

## 3. The two-layer model: wire views vs. domain messages

There are two kinds of "message" and they are kept distinct:

1. **Wire views** (`ref struct`): zero-copy readers/writers over raw protocol bytes sitting in
   RX ring memory or TX staging buffers. Lifetime = strictly inside the ring slot borrow (L3).
   Examples: `ItchAddOrderView`, `OuchEnterOrderWriter`.
2. **Domain messages** (plain `unmanaged struct`): normalized internal representation that flows
   *through* rings between stages. Fixed size, self-contained (no offsets into external buffers).
   Examples: `BookUpdate`, `OrderCmd`, `ExecReport`.

The RX stage's job is exactly: wire view → domain message, written directly into the next ring's
claimed slot. The TX stage: domain message → wire writer over the NIC staging buffer. **No
intermediate representation exists.**

### 3.1 Why domain messages are self-contained (no spans inside)

A struct flowing through a ring outlives the RX ring slot it was decoded from (the RX slot gets
committed and rewritten). Any (offset,length) into the original packet would dangle. Therefore
domain messages copy the *few fields that matter* (price, size, ids — tens of bytes) and drop the
rest. This is the one deliberate copy in the pipeline and it is cheap, cache-resident, and
removes an entire class of lifetime bugs. Variable-length data that genuinely must survive
(e.g., free-text reject reasons) is copied into an inline fixed buffer (§7) or, if large and rare,
into a `BufferPool` block whose handle travels in the message (M-6).

## 4. Domain message catalog (normative core set)

```csharp
namespace Fp.HotPath.Messages;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// Fixed-point price in ticks. Tick size resolved per-instrument via SymbolTable.
public readonly record struct Px(long Ticks)
{
    public static Px operator +(Px a, long t) => new(a.Ticks + t);
    public static Px operator -(Px a, long t) => new(a.Ticks - t);
    public static bool operator <(Px a, Px b) => a.Ticks < b.Ticks;
    public static bool operator >(Px a, Px b) => a.Ticks > b.Ticks;
    public static bool operator <=(Px a, Px b) => a.Ticks <= b.Ticks;
    public static bool operator >=(Px a, Px b) => a.Ticks >= b.Ticks;
}

public readonly record struct Qty(long Lots);
public readonly record struct Ts(long Nanos);          // session-epoch nanoseconds
public readonly record struct InstrumentId(uint Value); // dense index into SymbolTable
public readonly record struct VenueOrderId(ulong Value);

public enum Side : byte { Buy = 1, Sell = 2 }
public enum BookAction : byte { Add = 1, Modify = 2, Delete = 3, Execute = 4, Clear = 5 }

/// Normalized L3 book event. 64 bytes = one cache line, asserted below.
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct BookUpdate
{
    public Ts            ExchangeTs;     // 8
    public Ts            RxTs;           // 8  hardware/RX timestamp for latency accounting
    public VenueOrderId  OrderId;        // 8
    public Px            Price;          // 8
    public Qty           Quantity;       // 8
    public ulong         SeqNo;          // 8
    public InstrumentId  Instrument;     // 4
    public ushort        VenueId;        // 2
    public BookAction    Action;         // 1
    public Side          Side;           // 1
    public ushort        Flags;          // 2
    private ushort       _pad0;          // 2
    private uint         _pad1;          // 4  → total 64
}

public enum OrderVerb : byte { New = 1, Cancel = 2, Replace = 3 }
public enum Tif : byte { Day = 0, Ioc = 1, Fok = 2, Gtc = 3 }

/// Strategy → TX command. 64 bytes, asserted.
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct OrderCmd
{
    public Ts                  CreatedTs;     // 8
    public Handle<OrderState>  State;         // 8  ownership transfers with the message (M-6)
    public ulong               ClOrdSeq;      // 8  composes ClOrdId deterministically
    public ulong               OrigClOrdSeq;  // 8  for Cancel/Replace
    public Px                  Price;         // 8
    public Qty                 Quantity;      // 8
    public InstrumentId        Instrument;    // 4
    public ushort              VenueId;       // 2
    public OrderVerb           Verb;          // 1
    public Side                Side;          // 1
    public Tif                 Tif;           // 1
    private byte               _pad0;         // 1
    private ushort             _pad1;         // 2
    private uint               _pad2;         // 4  → total 64
}

public enum ExecType : byte { Ack = 1, Fill = 2, PartialFill = 3, CancelAck = 4,
                              ReplaceAck = 5, Reject = 6, CancelReject = 7, Expired = 8 }

/// TX/venue → Strategy execution event. 64 bytes, asserted.
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct ExecReport
{
    public Ts                  ExchangeTs;    // 8
    public Ts                  RxTs;          // 8
    public Handle<OrderState>  State;         // 8  ownership returns to strategy
    public VenueOrderId        VenueOrder;    // 8
    public Px                  LastPx;        // 8
    public Qty                 LastQty;       // 8
    public Qty                 LeavesQty;     // 8
    public InstrumentId        Instrument;    // 4
    public ExecType            Type;          // 1
    public byte                RejectCode;    // 1  normalized reject taxonomy; text stays cold-path
    private ushort             _pad0;         // 2  → total 64
}

/// Compile-time size contract. Runs in the static constructor of the module initializer
/// AND as unit tests; either trips on accidental growth.
internal static class MessageLayoutAsserts
{
    [ModuleInitializer]
    internal static void Assert()
    {
        Check<BookUpdate>(64);
        Check<OrderCmd>(64);
        Check<ExecReport>(64);
        static void Check<T>(int expected) where T : unmanaged
        {
            if (Unsafe.SizeOf<T>() != expected)
                throw new InvalidOperationException(
                    $"{typeof(T).Name} is {Unsafe.SizeOf<T>()} bytes; contract says {expected}.");
        }
    }
}
```

Notes:

- **64-byte sizing is a contract**, not a coincidence: one message per cache line means ring
  slots never share lines between adjacent messages, and SeqNo/timestamp prefetch behaves
  predictably. Types that cannot fit 64 use 128 (two lines) — never odd sizes.
- `record struct` is used only for the tiny wrappers (`Px`, `Qty`...) where synthesized equality
  is wanted; their synthesized `ToString` allocates and is therefore banned on the hot path by
  analyzer FP0004 (doc 08) — formatting goes through `ISpanFormattable`-style `TryFormat`
  helpers (doc 04 §6).
- `Handle<OrderState>` inside messages is the M-6 ownership-transfer pattern from doc 01.

## 5. Wire views (zero-copy codec layer)

### 5.1 Read side pattern

```csharp
namespace Fp.HotPath.Messages.Itch; // illustrative protocol; same pattern for any binary feed

/// Zero-copy view over an ITCH 'A' (Add Order) message in RX ring memory.
/// ref struct: cannot escape the ring-slot borrow. All accessors are O(1) reads.
public readonly ref struct ItchAddOrderView
{
    private readonly ReadOnlySpan<byte> _b;

    public ItchAddOrderView(ReadOnlySpan<byte> body) => _b = body;

    public bool   IsValid     => _b.Length >= Length;
    public const int Length   = 36;

    public ulong  Timestamp   => BinaryPrimitives.ReadUInt64BigEndian(_b.Slice(4, 8)) >> 16; // 48-bit field, illustrative
    public ulong  OrderRef    => BinaryPrimitives.ReadUInt64BigEndian(_b.Slice(12));
    public Side   Side        => _b[20] == (byte)'B' ? Side.Buy : Side.Sell;
    public uint   Shares      => BinaryPrimitives.ReadUInt32BigEndian(_b.Slice(21));
    public ReadOnlySpan<byte> RawSymbol => _b.Slice(25, 8);
    public uint   PriceRaw    => BinaryPrimitives.ReadUInt32BigEndian(_b.Slice(33));
}
```

Rules for views:

- **`ref struct` always** — the compiler then enforces the L3 lifetime (can't be stored in a
  field, can't cross `await`/`yield`, can't be boxed).
- Accessors use `System.Buffers.Binary.BinaryPrimitives` exclusively (alloc-free, endian-explicit,
  bounds-checked). Hand-rolled `Unsafe.ReadUnaligned` is permitted only inside this codec layer
  when the perf rig proves a measured win, and must be paired with an explicit length check at
  view construction.
- Validation is **two-tier**: `IsValid` (length/type sanity, always on) and full field validation
  (checked builds + the conformance test suite replaying captured pcaps).

### 5.2 Write side pattern

```csharp
/// Zero-copy writer over TX staging memory. ref struct; write-only; explicit Finish.
public ref struct OuchEnterOrderWriter
{
    private readonly Span<byte> _b;
    public const int Length = 49; // illustrative

    public OuchEnterOrderWriter(Span<byte> dest) { _b = dest.Slice(0, Length); _b[0] = (byte)'O'; }

    public void SetClOrdId(ulong seq, ReadOnlySpan<byte> prefix) { /* fixed-width left-padded numeric; alloc-free */
        Utf8Fixed.WriteU64PaddedLeft(_b.Slice(1, 14), seq, prefix); }
    public void SetSide(Side s)      => _b[15] = s == Side.Buy ? (byte)'B' : (byte)'S';
    public void SetShares(uint q)    => BinaryPrimitives.WriteUInt32BigEndian(_b.Slice(16), q);
    public void SetSymbol(Symbol s)  => s.CopyTo(_b.Slice(20, 8));
    public void SetPrice(uint raw)   => BinaryPrimitives.WriteUInt32BigEndian(_b.Slice(28), raw);
    /* ... remaining fields ... */
    public int Finish()              => Length; // returns bytes written for the TX ring publish
}
```

The codec layer (per venue/protocol) is the **only** code that knows wire offsets. It is
table-driven where the protocol allows, generated where a schema exists (SBE XML → source
generator is the preferred route for SBE venues; the generator runs at build time and emits
exactly this `ref struct` pattern — generator design is a later milestone; the emitted shape is
normative now).

## 6. Initialization contract

Because pools don't zero on acquire (doc 02 §3.2), every domain message and pooled struct has a
single `Init`-style entry point that assigns **every** field:

```csharp
public static void InitNew(ref OrderCmd c, Ts now, Handle<OrderState> st, ulong clOrdSeq,
                           InstrumentId ins, ushort venue, Side side, Px px, Qty qty, Tif tif)
{
    c = default;            // single 64-byte zeroing store burst; cheaper than missed-field bugs
    c.CreatedTs = now; c.State = st; c.ClOrdSeq = clOrdSeq; c.Instrument = ins;
    c.VenueId = venue; c.Verb = OrderVerb.New; c.Side = side; c.Price = px;
    c.Quantity = qty; c.Tif = tif;
}
```

`c = default` on a 64-byte struct compiles to a couple of SIMD stores — we *do* pay this zeroing
(unlike pool slabs, where structs can be KBs) because messages are small and the safety is worth
~2 ns. Analyzer FP0010 verifies every public field is reachable-assigned in each `Init*` method.

## 7. Inline fixed-size text: `Symbol`, `ClOrdId`, and friends

Strings are banned (T-2). Identifier-like text uses inline fixed buffers:

```csharp
/// 8-byte right-space-padded ASCII symbol, stored inline. Equality = single ulong compare.
[StructLayout(LayoutKind.Sequential, Size = 8)]
public readonly struct Symbol : IEquatable<Symbol>
{
    private readonly ulong _raw;   // big-endian-packed ASCII

    public static bool TryParse(ReadOnlySpan<byte> ascii, out Symbol s) { /* pads/validates ≤8 chars */ ... }
    public void CopyTo(Span<byte> dest8) => BinaryPrimitives.WriteUInt64BigEndian(dest8, _raw);
    public bool Equals(Symbol other) => _raw == other._raw;
    public override int GetHashCode() => _raw.GetHashCode();
    /// Alloc-free formatting for logs/telemetry. ToString() banned on hot path (FP0004).
    public bool TryFormat(Span<char> dest, out int written) { ... }
}
```

The same pattern (`Inline16`, `Inline32` generic fixed buffers using `[InlineArray]` —
.NET 8's `System.Runtime.CompilerServices.InlineArrayAttribute`) covers longer ids. Hot-path code
keys on `InstrumentId` (dense `uint` from the startup-built `SymbolTable`), never on text;
`Symbol` appears only at wire boundaries and in telemetry.

## 8. Versioning and evolution

- Domain messages are **internal protocol**: both ends deploy together, so versioning is by
  size/layout assert + a `RingSchemaHash` (FNV-1a over field names/offsets/sizes, source-generated)
  checked when a ring is attached — mismatch at startup fails fast, pre-seal. This matters when
  rings are shared-memory across processes (doc 05 §8); in-process it's a free consistency check.
- Adding a field: consume `_pad*` first; growing past 64 → jump to 128 and re-run the ring sizing
  math (doc 05 §4). Never repurpose a field's meaning without renaming it.
- Wire views version with the venue's protocol; each protocol revision is a distinct view type
  (`ItchAddOrderView_v5`) selected by the feed handler's pre-seal configuration — no runtime
  branching per message beyond the protocol's own type byte.

## 9. Dispatch without virtual calls

Stage input is a tagged union over the ring: each slot starts with a `MsgKind : ushort`. Dispatch
is a `switch` on the kind — the JIT compiles dense kinds to a jump table. Patterns rejected:

- Interface/abstract-class messages: boxing + vtable miss.
- `Action<T>` handler maps: delegate invocation indirection and registration-time allocation risk.
- Generic visitor with constrained calls: acceptable performance, but the `switch` is simpler and
  profiles identically; revisit only if kind-count growth makes maintenance painful.

## 10. Testing contract for this layer

1. **Layout tests**: size, offset (`Marshal.OffsetOf` cross-checked with `Unsafe` arithmetic),
   and `RingSchemaHash` golden values.
2. **Codec conformance**: replay captured venue pcaps through views; compare against a slow,
   allocating, obviously-correct reference decoder (cold-path test code may allocate freely).
3. **Round-trip fuzz**: writer → reader property tests over randomized field values, including
   boundary prices/quantities and max-length ids.
4. **Allocation tests**: every codec/dispatch benchmark runs under the allocation assertion
   harness (doc 08 §5) — 0 bytes allocated per op after warmup iteration.
