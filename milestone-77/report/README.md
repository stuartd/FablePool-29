# Latency & GC Pause Research Survey

**Project:** Solve Garbage Collection in C# for HFT — Milestone 1
**Status:** Complete
**Scope:** A technical survey of why .NET garbage collection causes latency spikes in
high-frequency-trading contexts, what the measured pause behavior actually is, where
allocations come from in typical trading code, and which mitigation strategies
production low-latency .NET shops use — with decision matrices and annotated
references — to ground the build milestones that follow.

---

## Contents

### Core report

| File | Section | What it covers |
|---|---|---|
| `01-executive-summary.md` | 1 | Findings in brief; the latency-contract framing; why "tune the GC" is the second line of defense, not the first |
| `02-gc-architecture.md` | 2 | Generational GC mechanics: allocation contexts, budgets, card tables/write barriers, mark/plan/relocate/compact, promotion, suspension mechanics |
| `03-loh-and-poh.md` | 3 | Large Object Heap behavior (85 KB threshold, sweep-by-default, fragmentation, on-demand compaction) and the Pinned Object Heap; pinning pathologies |
| `04-gc-modes-and-latency-settings.md` | 4 | Server vs Workstation GC; background/concurrent GC tradeoffs; latency modes incl. `SustainedLowLatency`; `TryStartNoGCRegion` semantics and failure modes |
| `05-gc-regions-dotnet7.md` | 5 | The regions heap layout in .NET 7+, differences from segments, decommit behavior, DATAS in .NET 8/9, what changed for pause distributions |
| `06-measured-pause-characteristics.md` | 6 | Measured pause data: per-generation/per-kind pause histograms, Server vs Workstation comparisons, background-GC suspension windows, environment sensitivity |
| `07-allocation-source-catalog.md` | 7 | The allocation catalog: LINQ, closures/display classes, boxing, string handling, async state machines, collection resizing, hidden BCL allocations — each with mechanics, profiler signature, and remedy |
| `08-mitigation-strategies-and-decision-matrices.md` | 8 | Production mitigation strategies (pooling, structs/spans, off-heap, process separation, NoGC windows, native interop) compared in decision matrices by cost, risk, and latency payoff |
| `09-annotated-references.md` | 9 | Annotated references [R1]–[R24]: runtime documentation, GC team writings, production engineering reports, measurement literature |
| `10-consolidated-recommendations.md` | 10 | Tiered recommendation set (Tier 0–4), one-page decision flow, hard-won caveats, and the derived scope/acceptance criteria for subsequent milestones |

### Appendices (operational companions)

| File | What it provides |
|---|---|
| `appendix-a-measurement-methodology.md` | Reproducible pause/jitter measurement harness: EventListener suspension monitor, `GCMemoryInfo` accounting, jitter loop, environment checklist (Linux/Windows), external-tool cross-checks, reporting schema used by Section 6 |
| `appendix-b-gc-configuration-reference.md` | Every GC knob relevant to low-latency deployment: runtimeconfig/env-var syntax, defaults, recommendations, interactions (hard limits ↔ NoGC budgets, DATAS, large pages), plus a reference profile and runtime-switchable controls with code |
| `appendix-c-allocation-tooling-and-ci-enforcement.md` | Finding allocations (PerfView/dotnet-trace/dotMemory workflows, profiler signatures per catalog category) and keeping them out: analyzer configuration, banned-API lists, the byte-exact CI allocation-budget gate, production GC tripwires |
| `appendix-d-glossary.md` | Normalized terminology with cross-references |

## Reading guide

- **Decision-makers:** Sections 1 → 10 (and the matrices in Section 8).
- **Engineers implementing the next milestones:** Sections 7 → 8 → 10, then
  Appendices A–C, with Sections 2–5 as reference depth.
- **Performance engineers validating a deployment:** Section 6 + Appendix A
  (re-measure locally; do not trust published numbers, including ours), then
  Appendix B for the configuration profile.

## Conventions

- Reference tags `[R1]`–`[R24]` resolve in `09-annotated-references.md`.
- All pause numbers distinguish **suspension time** (threads stopped) from
  **collection duration** (GC active, possibly concurrent); see Appendix A §A.1.
- Code listings target **.NET 8** with BCL-only APIs unless a package is explicitly
  named (Appendix C §C.6 lists targeted package version lines).
- Configuration syntax is given for runtimeconfig.json first, env vars second;
  numeric env vars noted as hex where applicable (Appendix B §B.1).

## Scope checklist (milestone definition → delivered location)

| Funded scope item | Delivered in |
|---|---|
| Generational GC mechanics | §2 |
| LOH behavior | §3 |
| Server vs Workstation GC | §4 |
| Background/concurrent GC tradeoffs | §4 |
| Sustained-low-latency mode | §4 (+ App. B §B.5) |
| GC regions in .NET 7+ | §5 (+ DATAS in App. B §B.4) |
| Real measured pause characteristics | §6 (+ reproducibility in App. A) |
| Allocation-source catalog (LINQ, closures, boxing, strings, async state machines, collection resizing) | §7 (+ detection signatures in App. C §C.1.2) |
| Production mitigation-strategy comparison with decision matrices | §8 (+ tiered synthesis & decision flow in §10) |
| Annotated references | §9 |

## Maintenance note

This is a documentation-only milestone; there is nothing to compile. Code listings in
the appendices are designed to be lifted directly into the next milestones' projects;
when doing so, add the packages from Appendix C §C.6 with caret constraints and
generate lockfiles with a normal first build — do not hand-author lockfiles.
