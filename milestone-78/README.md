# Zero-Allocation Hot Path — Engineering Design Document

**Project:** Solve Garbage Collection in C# for HFT
**Milestone #2:** Allocation-Free Architecture Design Document
**Status:** In progress (multi-part document set)
**Target runtime:** .NET 8 (LTS) and later; all API references verified against .NET 8 unless noted.

## What this document set is

Milestone #1 established empirically that .NET GC pauses (even Server GC + Background GC, even
`SustainedLowLatency`) are incompatible with single-digit-microsecond tail latency targets on the
order-handling hot path. The conclusion of that survey was that the only robust strategy is to
**never give the GC a reason to run** on the hot path: zero managed allocations after warmup,
combined with mechanical enforcement so the property cannot silently regress.

This milestone delivers the complete engineering design for that architecture. It is written so a
team can implement it directly: every section specifies concrete APIs, ownership rules, failure
modes, and test/CI enforcement — not aspirations.

## Document map

| # | File | Contents |
|---|------|----------|
| 00 | `docs/design/00-overview.md` | Goals, non-goals, latency budget, architectural summary, glossary |
| 01 | `docs/design/01-memory-ownership-model.md` | Ownership model: who allocates, who owns, who frees; lifetime classes; handle types |
| 02 | `docs/design/02-object-pooling.md` | Pooling strategy: pool taxonomy, sizing, exhaustion policy, leak detection, API spec |
| 03 | `docs/design/03-message-types.md` | Struct-based message types: layout rules, wire mapping, versioning, API spec |
| 04 | `docs/design/04-span-memory-rules.md` | `Span<T>`/`Memory<T>` usage rules, lifetime discipline, escape analysis checklist |
| 05 | `docs/design/05-ring-buffers.md` | Pre-allocated SPSC/MPSC ring buffers for market data and order flow; API spec |
| 06 | `docs/design/06-unmanaged-arenas.md` | Unmanaged memory arenas: `NativeMemory`, arena lifecycle, alignment, NUMA |
| 07 | `docs/design/07-threading-and-pinning.md` | Threading model, core pinning, isolation, textual thread-topology diagrams |
| 08 | `docs/design/08-no-alloc-contract.md` | "No allocations after warmup" contract: warmup protocol, EventPipe tracking, CI gates |
| 09 | `docs/design/09-failure-mode-analysis.md` | FMEA: pool exhaustion, ring overrun, arena corruption, pinning failures, GC intrusion |
| 10 | `docs/design/10-migration-guide.md` | Retrofitting existing C# trading codebases: phased plan, refactoring catalog |
| 11 | `docs/design/11-api-reference.md` | Consolidated API surface of the `Fp.HotPath.*` namespaces |

## How to read it

Read `00-overview.md` first; it defines the lifetime taxonomy and naming conventions used by every
other document. Documents 01–07 are the architecture; 08–09 are the enforcement and safety case;
10–11 are for adopters.

## Conventions

- Code blocks are **normative C# (.NET 8)** unless marked `// illustrative`.
- `MUST` / `MUST NOT` / `SHOULD` / `MAY` follow RFC 2119 semantics.
- The reference namespace for all proposed APIs is `Fp.HotPath` (FablePool Hot Path).
