# ZeroAlloc — Allocation-Free Core Library for Low-Latency C#

ZeroAlloc is the core library deliverable of FablePool milestone #3 ("ZeroAlloc Core
Library Implementation"). It implements the allocation-free architecture designed in
milestone #2 so that hot trading paths can run **without producing garbage**, which in
turn means the .NET GC has nothing to collect and never pauses the critical path.

## Components

| Component | Namespace | Purpose |
|---|---|---|
| `SpscRingBuffer<T>` | `ZeroAlloc.Concurrency` | Lock-free single-producer / single-consumer bounded queue with cached-index optimization. |
| `MpscRingBuffer<T>` | `ZeroAlloc.Concurrency` | Lock-free multi-producer / single-consumer bounded queue (Vyukov-style sequenced cells). |
| `ObjectPool<T>` | `ZeroAlloc.Pooling` | Object pool with thread-local fast path and shared lock-free overflow. |
| `ArenaAllocator` | `ZeroAlloc.Memory` | Bump-pointer unmanaged arena with O(1) `Reset()` semantics. |
| `Px` (fixed decimal) | `ZeroAlloc.Numerics` | 64-bit fixed-point decimal for prices/quantities; no `decimal`, no boxing. |
| `FixReader` / `FixWriter` | `ZeroAlloc.Fix` | Zero-alloc UTF-8 FIX-style tag=value codec over spans. |
| `PooledBufferWriter` | `ZeroAlloc.Buffers` | `IBufferWriter<byte>` backed by pooled arrays. |
| `ZLog` | `ZeroAlloc.Logging` | Allocation-free structured logging facade with interpolated-string handler. |

## Building

Requires the .NET 8 SDK (any 8.0.x or later; the project does not pin an exact SDK).

```bash
dotnet build ZeroAlloc.sln -c Release
dotnet test  ZeroAlloc.sln -c Release
```

No lockfile is committed; NuGet restore on first build resolves the (few, test-only)
dependencies. If your organization requires a lockfile, run
`dotnet restore --use-lock-file` once and commit the generated `packages.lock.json`.

## Design rules (enforced by tests + the audit checklist)

1. **Steady-state code paths allocate zero managed bytes.** Construction-time
   allocation (arrays, pools, arenas) is allowed once, up front.
2. **No locks on hot paths** — only `Volatile`/`Interlocked` and bounded spinning.
3. **No boxing, no closures, no LINQ, no `params`, no string concatenation** on hot
   paths. See `docs/AllocationAuditChecklist.md`.
4. Every public API carries XML documentation describing its threading contract and
   allocation behavior.

Unit tests include *allocation assertions* (`AllocationAssert`) that measure
`GC.GetAllocatedBytesForCurrentThread()` around hot paths and fail if a single byte
of garbage is produced after warm-up.
