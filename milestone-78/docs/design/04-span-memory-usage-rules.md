# Design Doc 04 — `Span<T>` / `Memory<T>` Usage Rules

Status: Final draft for review
Depends on: 01-memory-ownership-model.md, 03-message-types.md
Audience: All hot-path engineers; enforced by analyzer + code review

---

## 1. Purpose

`Span<T>` and `Memory<T>` are the only sanctioned mechanisms for passing views over
buffers (managed arrays, stack memory, unmanaged arenas) through the hot path without
copying and without allocating. This document defines the *rules of engagement*: when
each type may be used, lifetime rules, the API patterns we standardize on, and the
patterns that are banned because they silently re-introduce allocations or unsafe
lifetimes.

These rules are mechanical on purpose. Every rule maps to either a Roslyn analyzer
diagnostic (our `FablePool.Analyzers` package, see Doc 08) or a code-review checklist
item.

---

## 2. Type selection matrix

| Scenario | Required type | Rationale |
|---|---|---|
| Synchronous parse/format within one frame | `Span<T>` / `ReadOnlySpan<T>` | Stack-only; cannot escape; zero overhead |
| View over arena memory passed across sync calls | `Span<T>` from pointer | Arena outlives call; no GC pin needed |
| Handing a buffer slice to another *thread* | `BufferLease` (Doc 01) carrying offset+length, never `Memory<T>` | Explicit ownership transfer; refcounted |
| Async I/O (network reads on edge threads only) | `Memory<T>` over pooled array | Async requires heap-storable view; edge-only |
| Storing a view in a field of a `class` | **Banned on hot path** — use `(buffer-id, offset, length)` triple | Fields holding `Memory<T>` hide lifetime |
| Storing a view in a `ref struct` | `Span<T>` field (C# 11 `ref` fields) | Compiler-enforced non-escape |
| Constant lookup tables | `ReadOnlySpan<byte>` over `static readonly` data or UTF-8 literals | JIT emits data section reference; no allocation, no static-init cost |

Rule of thumb: **`Span<T>` everywhere on the hot path; `Memory<T>` only at async
edges; raw `(id, offset, length)` triples whenever a view must be stored or cross a
thread boundary.**

---

## 3. Lifetime rules

### 3.1 The Frame Rule

A `Span<T>` obtained inside a processing frame (one iteration of an engine thread's
poll loop, see Doc 07) is valid **only until the frame's buffer is released**, i.e.
until `BufferLease.Release()` or `Arena.Reset()` runs. The compiler enforces stack
non-escape; it does *not* enforce that the underlying memory is still owned. Our rule:

> **R-04-01**: A span over leased/arena memory MUST NOT be used after the lease or
> arena epoch that produced it ends. Methods that retain leases must take the
> `BufferLease`, not the span.

Practically: any method signature that accepts `Span<byte> payload` is declaring
"I will be done with this before I return." Any method that needs the data later
takes `in BufferLease lease` and slices its own span on demand.

### 3.2 Arena epochs and spans

Spans over an unmanaged arena (Doc 06) carry an implicit epoch. Debug builds wrap
arena spans in `ArenaSpan<T>`:

```csharp
public readonly ref struct ArenaSpan<T> where T : unmanaged
{
    private readonly Span<T> _span;
#if DEBUG
    private readonly Arena _arena;
    private readonly ulong _epoch;
#endif

    public Span<T> Get()
    {
#if DEBUG
        if (_arena.Epoch != _epoch)
            ThrowHelper.ThrowArenaEpochViolation(_arena.Name, _epoch, _arena.Epoch);
#endif
        return _span;
    }
}
```

Release builds compile `ArenaSpan<T>` down to a bare `Span<T>` (the `#if DEBUG`
fields vanish; `Get()` inlines to a field load). This gives us use-after-reset
detection in soak tests at zero release-build cost.

### 3.3 `scoped` and `ref` fields

All hot-path code targets C# 11+ semantics:

- Parameters that must not be captured into returned ref structs are annotated
  `scoped`: `void Parse(scoped ReadOnlySpan<byte> input, out Quote quote)`.
- `ref struct` cursors store spans in `ref` fields rather than re-slicing
  (e.g. `BufferReader` below).

> **R-04-02**: New hot-path APIs MUST annotate span parameters `scoped` unless the
> span is intentionally returned/stored in a ref struct, in which case the escape
> must be documented in XML doc comments.

---

## 4. Standard reader/writer cursors

All parsing and formatting goes through two ref structs. No hot-path code calls
`BitConverter`, `Encoding.GetString`, or `MemoryStream` — ever.

### 4.1 `BufferReader`

```csharp
public ref struct BufferReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    public BufferReader(ReadOnlySpan<byte> buffer) { _buffer = buffer; _position = 0; }

    public int Position => _position;
    public int Remaining => _buffer.Length - _position;

    // Little-endian primitives (wire format is LE; see Doc 03 §6).
    public byte   ReadByte();
    public ushort ReadUInt16();
    public uint   ReadUInt32();
    public ulong  ReadUInt64();
    public long   ReadInt64();
    public PriceTicks ReadPrice();      // ReadInt64 reinterpreted
    public Qty        ReadQty();        // ReadInt64 reinterpreted

    /// Returns a sub-span; valid for the same lifetime as the source buffer.
    public ReadOnlySpan<byte> ReadBytes(int count);

    /// Reads a fixed-width ASCII field into an InlineString8/16 (Doc 03).
    public InlineString8 ReadSymbol8();

    /// Non-throwing variants for protocol boundaries.
    public bool TryReadUInt32(out uint value);
    public bool TryReadBytes(int count, out ReadOnlySpan<byte> bytes);
}
```

Implementation notes (normative):

- All `Read*` use `BinaryPrimitives.ReadXxxLittleEndian` over a slice; `ReadBytes`
  uses `_buffer.Slice(_position, count)`. Both are bounds-checked once by the slice.
- The throwing variants throw only on malformed input from the wire — that path is a
  session-fatal protocol error (Doc 10), so throw cost is irrelevant; the throw
  helpers live in a non-inlined `ThrowHelper` to keep the hot method small.
- `BufferReader` is 12–16 bytes and lives entirely in registers/stack after inlining.

### 4.2 `BufferWriter`

```csharp
public ref struct BufferWriter
{
    private readonly Span<byte> _buffer;
    private int _position;

    public BufferWriter(Span<byte> buffer);

    public int BytesWritten => _position;
    public int Capacity     => _buffer.Length;

    public void WriteByte(byte value);
    public void WriteUInt16(ushort value);
    public void WriteUInt32(uint value);
    public void WriteUInt64(ulong value);
    public void WriteInt64(long value);
    public void WritePrice(PriceTicks value);
    public void WriteQty(Qty value);
    public void WriteBytes(scoped ReadOnlySpan<byte> bytes);
    public void WriteSymbol8(in InlineString8 symbol);

    /// Integer-to-ASCII without allocation (uses Utf8Formatter).
    public void WriteAsciiUInt64(ulong value, int minDigits = 0);

    /// Reserve a length-prefix slot; patch later.
    public LengthSlot ReserveUInt16();
    public void Patch(LengthSlot slot);   // writes (current pos - slot pos - 2)
}
```

Capacity violations are programming errors (buffers are sized at design time for
max message size, Doc 03 §5), so `BufferWriter` throws `BufferOverrunException` —
which is treated as a fail-fast condition (Doc 10 §4).

### 4.3 Text formatting rules

- ASCII/UTF-8 numeric formatting: `System.Buffers.Text.Utf8Formatter` /
  `Utf8Parser` only. Never `int.ToString()`, never interpolation, never
  `string.Format` on the hot path.
- Fixed decimal prices: format from `PriceTicks` (scaled `long`) via integer
  formatting plus a manual decimal-point insertion helper
  `PriceFormat.Write(BufferWriter, PriceTicks, byte decimals)` — specified in the
  API reference (Doc 11). No `decimal`, no `double.ToString`.
- Logging on the hot path uses the binary structured logger (Doc 05 §8: a dedicated
  ring buffer of fixed log records), never string-building.

---

## 5. Banned patterns

Each pattern below is rejected by `FablePool.Analyzers` with the listed diagnostic
when it appears in code marked `[HotPath]` (assembly/namespace/method attribute,
Doc 08 §3).

| ID | Pattern | Why banned | Sanctioned alternative |
|---|---|---|---|
| FP0401 | `span.ToArray()` | Allocates an array | Copy into leased buffer via `CopyTo` |
| FP0402 | `Encoding.*.GetString(span)` | Allocates a string | Compare/parse bytes directly; `InlineString8` |
| FP0403 | `Memory<T>` field on a `class` reachable from hot path | Hidden lifetime, hidden pin | `(BufferId, int, int)` triple + lease |
| FP0404 | `span[..n].ToString()` / interpolation of spans | Allocates | `BufferWriter` + binary log |
| FP0405 | `MemoryMarshal.AsMemory` on hot path | Defeats span lifetime checking | Redesign: pass lease |
| FP0406 | LINQ over spans via `ToArray`/`AsEnumerable` | Allocates enumerators + array | Plain `for` loop |
| FP0407 | `stackalloc` with non-constant size, or size > 1024 bytes | Stack-overflow risk, probe cost | Constant-size `stackalloc` ≤ 1 KiB, or arena scratch |
| FP0408 | `params ReadOnlySpan<T>` callers passing arrays | Allocates the array at call site | Pre-built static spans / explicit overloads |
| FP0409 | Capturing a span-producing lambda (defensive: lambdas banned wholesale on hot path) | Closure allocation | Static lambdas with state parameter, or plain methods |

### 5.1 `stackalloc` policy detail

`stackalloc` is permitted only as: `Span<byte> tmp = stackalloc byte[N];` where `N`
is a `const` ≤ 1024, declared *outside* loops. Inside loops, the same stack slot must
be hoisted above the loop. The JIT reuses the slot either way, but hoisting makes
the intent reviewable and avoids `localloc`-in-loop deoptimizations on older
runtimes.

### 5.2 Spans over unmanaged memory

Creating spans from arena pointers is done exclusively through arena APIs
(`arena.AllocSpan<T>(count)`, Doc 06 §4), never via ad-hoc
`new Span<T>(ptr, len)` in business logic. This centralizes the unsafe surface to
one audited assembly (`FablePool.Memory`), which is the only hot-path assembly
allowed `AllowUnsafeBlocks=true` besides the transport layer.

---

## 6. Interop with the BCL

Sanctioned allocation-free BCL surface (whitelist; anything else needs review):

- `System.Buffers.Binary.BinaryPrimitives` — all methods.
- `System.Buffers.Text.Utf8Parser` / `Utf8Formatter` — all methods.
- `MemoryExtensions`: `SequenceEqual`, `IndexOf`, `CopyTo`, `Slice`, `Fill`,
  `Clear`, `BinarySearch` (with `IComparable` struct comparers only).
- `System.Runtime.InteropServices.MemoryMarshal`: `Cast`, `Read`, `Write`,
  `CreateSpan`/`CreateReadOnlySpan` (inside `FablePool.Memory` only),
  `GetReference`.
- `System.Numerics.BitOperations` — all methods.
- `Vector128/256<T>` intrinsics for SIMD scans (checksum, delimiter search) —
  contained in `FablePool.Memory.Simd`, with scalar fallbacks selected at startup
  (never per-call `IsSupported` branching in loops; the JIT folds `IsSupported`,
  but we still isolate SIMD code paths for testability).

Known BCL traps documented for reviewers:

- `MemoryExtensions.Split` (string-like span split) returns ranges — fine — but the
  `string.Split` family is banned.
- `Span<T>.ToString()` on `Span<char>` allocates; on other `T` it returns a type
  name string. Both banned (FP0404).
- `Utf8Parser.TryParse` for `decimal`/`double` is allocation-free but slow;
  prices must come in as scaled integers wherever the venue allows; otherwise parse
  digits manually into `PriceTicks` via `PriceFormat.TryParse`.

---

## 7. Testing requirements

Every cursor/parse/format API in this doc ships with:

1. **Round-trip property tests** (write→read equality) under `tests/MessageCodec.Tests`.
2. **Allocation tests**: each public API invoked 1e6 times under
   `GC.GetAllocatedBytesForCurrentThread()` bracketing; delta must be 0 after
   warmup iteration (Doc 08 §5 has the harness).
3. **Bounds fuzzing**: truncated/oversized inputs must throw the documented
   exception or return `false` from `Try*` — never read out of bounds (debug builds
   run with `ArenaSpan` checks + ASAN-style arena guard pages, Doc 06 §7).

---

## 8. Summary of normative rules

- R-04-01: Spans don't outlive their lease/epoch.
- R-04-02: `scoped` on all non-escaping span parameters.
- R-04-03: All parsing/formatting via `BufferReader`/`BufferWriter`/`Utf8Formatter`.
- R-04-04: `Memory<T>` only on async edges; never stored on hot-path classes.
- R-04-05: Banned patterns FP0401–FP0409 enforced by analyzer in `[HotPath]` scopes.
- R-04-06: Raw span construction from pointers only inside `FablePool.Memory`.
- R-04-07: `stackalloc` constant-sized, ≤ 1 KiB, hoisted out of loops.
