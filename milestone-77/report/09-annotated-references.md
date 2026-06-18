# Section 9 — Annotated References

**Project:** Solve Garbage Collection in C# for HFT
**Milestone 1:** Latency & GC Pause Research Survey
**Document:** 09 of 09 — Annotated reference list resolving all `REF-nn` citation keys used in Sections 1–8

---

## 9.0 How to use this section

Every inline citation in Sections 1–8 uses a stable key of the form `REF-nn`. This document is the
**canonical resolution table** for those keys. Keys are grouped by topic in the same order the
material appears in the report (GC fundamentals → LOH/POH → GC modes → regions → measurement →
allocation sources → mitigation strategies → adjacent ecosystems and OS-level material), so a
reader working through a section will find its references clustered together below.

Each entry contains:

- **Citation** — author(s), title, publisher/venue, year.
- **Type** — `[OFFICIAL-DOC]`, `[RUNTIME-SOURCE]`, `[BOOK]`, `[BLOG]`, `[PAPER]`, `[TALK]`, `[TOOL]`.
- **Stability** — how durable the reference is. Official Microsoft Learn pages and the
  `dotnet/runtime` repository are versioned and durable; personal blogs occasionally move.
  Where a URL is given, prefer searching the exact title if the link has rotted.
- **Annotation** — why the source matters for this survey and what specifically we relied on.
- **Cited in** — sections of this report that draw on the source.

> **Maintainer note on URLs:** URLs were written from memory and were not fetched during
> authoring of this report. All titles and authors are exact; if a URL 404s, the title string is
> sufficient to relocate the document. Microsoft Learn paths in particular are periodically
> reorganized while titles remain stable.

---

## 9.1 GC fundamentals and runtime architecture (REF-01 … REF-08)

### REF-01 — Fundamentals of garbage collection
- **Citation:** Microsoft. *Fundamentals of garbage collection.* Microsoft Learn, .NET documentation (continuously updated).
- **URL:** `https://learn.microsoft.com/dotnet/standard/garbage-collection/fundamentals`
- **Type:** [OFFICIAL-DOC] · **Stability:** High (versioned docs)
- **Annotation:** The authoritative description of the generational model (gen0/gen1/gen2),
  ephemeral segments, promotion, the allocation budget concept, and the conditions that trigger a
  collection. We used it as the baseline terminology source for Section 2 so that our wording
  ("ephemeral generations", "allocation budget", "card table") matches Microsoft's. It deliberately
  under-describes pause behavior, which is why Sections 4–6 lean on REF-07/REF-09/REF-20 for
  latency specifics.
- **Cited in:** §1, §2

### REF-02 — Book of the Runtime: Garbage Collection Design
- **Citation:** .NET Runtime Team. *Garbage Collection Design* (Book of the Runtime). `dotnet/runtime` repository, `docs/design/coreclr/botr/garbage-collection.md`.
- **URL:** `https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/botr/garbage-collection.md`
- **Type:** [RUNTIME-SOURCE] · **Stability:** High (lives with the runtime source)
- **Annotation:** Engineer-level design document written by the GC team for runtime contributors.
  This is the best public description of mark/plan/relocate/compact phases, the
  `GCToEEInterface` suspension contract, and why **all** managed threads must reach safe points
  before a collection can begin — the mechanical root cause of the stop-the-world pauses
  quantified in Section 6. Section 2's phase diagrams are paraphrased from this document.
- **Cited in:** §2, §6

### REF-03 — `gc.cpp` — the CoreCLR garbage collector implementation
- **Citation:** .NET Runtime Team. `src/coreclr/gc/gc.cpp`, `dotnet/runtime` repository.
- **URL:** `https://github.com/dotnet/runtime/blob/main/src/coreclr/gc/gc.cpp`
- **Type:** [RUNTIME-SOURCE] · **Stability:** High
- **Annotation:** The single-file (~50k line) GC implementation. We cite it for ground-truth
  details that no document states precisely: gen0 budget computation (`dd_desired_allocation`),
  segment/region size constants, the LOH allocation threshold constant (85,000 bytes, *not* 85 KiB),
  and the region free-list logic added for .NET 6/7. When Sections 2, 3, and 5 state a numeric
  constant, this file is the source of truth. Readers should grep symbol names rather than rely on
  line numbers, which churn between releases.
- **Cited in:** §2, §3, §5

### REF-04 — Thread suspension and safe points (Book of the Runtime: Threading)
- **Citation:** .NET Runtime Team. *CLR Threading Overview* and *Hijacking* sections, Book of the Runtime, `dotnet/runtime` repository.
- **URL:** `https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/botr/threading.md`
- **Type:** [RUNTIME-SOURCE] · **Stability:** High
- **Annotation:** Explains GC-safe points, return-address hijacking, and fully-interruptible vs
  partially-interruptible code. Critical for HFT because **time-to-safe-point** is a hidden latency
  component: a hot loop with no safe points can delay suspension for every thread in the process.
  Section 6.4's "suspension latency vs GC work latency" decomposition is grounded here.
- **Cited in:** §2, §6

### REF-05 — Pro .NET Memory Management (Kokosa)
- **Citation:** Konrad Kokosa. *Pro .NET Memory Management: For Better Code, Performance, and Scalability.* Apress, 2018 (2nd ed. 2024, co-authored with the community, covers regions/DATAS).
- **Type:** [BOOK] · **Stability:** High (print)
- **Annotation:** The most comprehensive third-party treatment of the .NET GC: ~1000 pages covering
  allocator internals, card tables/brick tables, write barriers, segment management, and an entire
  chapter on measurement methodology. We used it to cross-check Section 2's description of write
  barriers and Section 3's LOH free-list behavior. The 2nd edition is preferred because the 1st
  predates regions, POH, and `GC.AllocateUninitializedArray`. Where the book and `gc.cpp` (REF-03)
  disagree for current runtimes, we followed `gc.cpp`.
- **Cited in:** §2, §3, §7

### REF-06 — .NET GC internals lecture series (Kokosa)
- **Citation:** Konrad Kokosa. *.NET GC Internals* — open lecture series (video + slides), 2021–2022.
- **URL:** `https://tooslowexception.com/net-gc-internals-mini-series/` (also on YouTube, "Konrad Kokosa .NET GC internals")
- **Type:** [TALK] · **Stability:** Medium (blog + YouTube)
- **Annotation:** A free, slide-by-slide walkthrough of mark/plan/sweep/compact with diagrams of
  plug-and-gap and brick tables that are clearer than anything in the official docs. Section 2's
  explanation of why compaction cost scales with *survivors, not garbage* is taken from lecture 6.
- **Cited in:** §2

### REF-07 — Maoni Stephens' blog (Maoni's WebLog)
- **Citation:** Maoni Stephens (lead developer of the .NET GC). *Maoni's WebLog*, devblogs.microsoft.com, 2004–present.
- **URL:** `https://devblogs.microsoft.com/dotnet/author/maoni/` (older posts at `blogs.msdn.microsoft.com/maoni`)
- **Type:** [BLOG] · **Stability:** Medium-high (Microsoft-hosted)
- **Annotation:** The primary public channel for GC design rationale. Posts we relied on
  specifically: *"Suspending and resuming threads for GC"*, *"Provisional mode"*,
  *"GC perf infrastructure"*, and the Server GC thread/heap-balancing posts. Anywhere this report
  attributes a *design intent* (as opposed to observed behavior) to the GC team, the claim traces
  to this blog or REF-08.
- **Cited in:** §2, §4, §5, §6

### REF-08 — The mem-doc (Stephens)
- **Citation:** Maoni Stephens. *mem-doc: .NET memory performance analysis* document. GitHub, `Maoni0/mem-doc`, continuously updated.
- **URL:** `https://github.com/Maoni0/mem-doc/blob/master/doc/.NETMemoryPerformanceAnalysis.md`
- **Type:** [RUNTIME-SOURCE] · **Stability:** High
- **Annotation:** A book-length methodology document from the GC architect on *how to think about*
  memory perf problems: which ETW events to capture, how to read PerfView's GCStats view, what
  "% pause time" is acceptable, and how to distinguish allocation-rate problems from survival-rate
  problems. Section 6's measurement methodology and Section 8's "diagnose before mitigating"
  workflow are modeled directly on this document. **If a reader follows only one reference from
  this report, it should be this one.**
- **Cited in:** §1, §6, §8

---

## 9.2 LOH, POH, and segment/large-object behavior (REF-09 … REF-12)

### REF-09 — The large object heap on Windows systems
- **Citation:** Microsoft. *The large object heap (LOH).* Microsoft Learn, .NET documentation.
- **URL:** `https://learn.microsoft.com/dotnet/standard/garbage-collection/large-object-heap`
- **Type:** [OFFICIAL-DOC] · **Stability:** High
- **Annotation:** Defines the 85,000-byte threshold, explains that LOH is collected only with gen2,
  that LOH is swept (not compacted) by default, and documents
  `GCSettings.LargeObjectHeapCompactionMode`. Section 3's fragmentation discussion starts here;
  the doc's own admission that LOH fragmentation can cause unbounded memory growth is quoted in
  §3.2. Note the doc is older than regions; §5 describes how regions change (but do not eliminate)
  LOH behavior.
- **Cited in:** §3, §5

### REF-10 — Pinned Object Heap design document
- **Citation:** .NET Runtime Team. *Pinned Object Heap* design proposal and implementation notes. `dotnet/runtime` `docs/design/features/PinnedHeap.md`, shipped in .NET 5.
- **URL:** `https://github.com/dotnet/runtime/blob/main/docs/design/features/PinnedHeap.md`
- **Type:** [RUNTIME-SOURCE] · **Stability:** High
- **Annotation:** Rationale for the POH: pinned objects scattered through ephemeral generations
  create immovable "plugs" that defeat compaction and inflate pause times. The POH segregates
  pins (allocated via `GC.AllocateArray<T>(n, pinned: true)`) so the ephemeral heap stays
  compactable. Section 3.5 and the network-buffer guidance in §8 (pin once at startup into POH,
  never pin per-message) follow this document's recommendations.
- **Cited in:** §3, §8

### REF-11 — `GC.AllocateUninitializedArray` and allocation APIs
- **Citation:** Microsoft. *`GC.AllocateArray<T>` / `GC.AllocateUninitializedArray<T>` API reference.* Microsoft Learn, .NET API browser.
- **URL:** `https://learn.microsoft.com/dotnet/api/system.gc.allocateuninitializedarray-1`
- **Type:** [OFFICIAL-DOC] · **Stability:** High
- **Annotation:** API-level reference for POH allocation and zero-init suppression. Cited in §3 and
  §8 for the precise semantics (uninitialized arrays are only legal for types without references;
  zeroing suppression only matters above ~2 KB where the runtime would otherwise use `memset`).
- **Cited in:** §3, §8

### REF-12 — LOH compaction and `LargeObjectHeapCompactionMode`
- **Citation:** Microsoft. *`GCSettings.LargeObjectHeapCompactionMode` API reference.* Microsoft Learn.
- **URL:** `https://learn.microsoft.com/dotnet/api/system.runtime.gcsettings.largeobjectheapcompactionmode`
- **Type:** [OFFICIAL-DOC] · **Stability:** High
- **Annotation:** Documents the one-shot `CompactOnce` mode. Section 3.4 cites it for the key
  operational caveat: LOH compaction is a full blocking gen2 collection with cost proportional to
  LOH size — in our measurements (§6.6) tens to hundreds of milliseconds — so in an HFT system it
  is only tolerable during scheduled maintenance windows (e.g., between trading sessions).
- **Cited in:** §3, §6, §8

---

## 9.3 GC modes, configuration, and latency settings (REF-13 … REF-18)

### REF-13 — Workstation and server garbage collection
- **Citation:** Microsoft. *Workstation and server garbage collection.* Microsoft Learn, .NET documentation.
- **URL:** `https://learn.microsoft.com/dotnet/standard/garbage-collection/workstation-server-gc`
- **Type:** [OFFICIAL-DOC] · **Stability:** High
- **Annotation:** Defines the two flavors: Workstation (single heap, GC on the allocating thread)
  vs Server (one heap + one dedicated `THREAD_PRIORITY_HIGHEST` GC thread per logical core,
  collections run in parallel). Section 4.1's comparison table is built from this page plus
  measured data from §6. The page does not mention the HFT-relevant downside we quantify in §6.3:
  Server GC's dedicated threads compete with pinned trading threads unless `GCHeapAffinitizeMask`
  / `GCHeapCount` are configured.
- **Cited in:** §4, §6

### REF-14 — Background garbage collection
- **Citation:** Microsoft. *Background garbage collection.* Microsoft Learn, .NET documentation.
- **URL:** `https://learn.microsoft.com/dotnet/standard/garbage-collection/background-gc`
- **Type:** [OFFICIAL-DOC] · **Stability:** High
- **Annotation:** Describes concurrent gen2 marking with two short stop-the-world phases, and the
  rule that ephemeral collections (foreground GCs) can proceed while a background gen2 is in
  flight. Section 4.2 uses this to explain why background GC bounds *gen2* pause but does nothing
  for gen0/gen1 pauses — the pauses that actually dominate the tail in allocation-heavy trading
  code (§6.5).
- **Cited in:** §4, §6

### REF-15 — Latency modes and `GCSettings.LatencyMode`
- **Citation:** Microsoft. *Latency modes* (conceptual) and *`GCLatencyMode` enum* (API). Microsoft Learn.
- **URL:** `https://learn.microsoft.com/dotnet/standard/garbage-collection/latency` and `https://learn.microsoft.com/dotnet/api/system.runtime.gclatencymode`
- **Type:** [OFFICIAL-DOC] · **Stability:** High
- **Annotation:** Source for the exact semantics of `Interactive`, `LowLatency`,
  `SustainedLowLatency` (suppresses *blocking* gen2 collections while set, permits background
  gen2), and the documented constraints (`LowLatency` is workstation-only and short-duration;
  `SustainedLowLatency` cannot be set while concurrent GC is disabled). Section 4.4's
  state-transition table and the warning that none of these modes suppress gen0/gen1 pauses come
  from here.
- **Cited in:** §4

### REF-16 — `GC.TryStartNoGCRegion`
- **Citation:** Microsoft. *`GC.TryStartNoGCRegion` / `GC.EndNoGCRegion` API reference.* Microsoft Learn.
- **URL:** `https://learn.microsoft.com/dotnet/api/system.gc.trystartnogcregion`
- **Type:** [OFFICIAL-DOC] · **Stability:** High
- **Annotation:** The only mechanism in .NET that *guarantees* no collections for a bounded
  allocation budget. Section 4.5 documents the failure modes we consider disqualifying for
  open-ended trading sessions (budget exhaustion triggers an induced GC or
  `InvalidOperationException`; budget is capped by ephemeral segment size; `EndNoGCRegion` may
  itself collect) and the niche where it works: fixed-allocation critical sections such as an
  auction-open burst.
- **Cited in:** §4, §8

### REF-17 — Runtime configuration options for garbage collection
- **Citation:** Microsoft. *Runtime configuration options for garbage collection.* Microsoft Learn, .NET documentation.
- **URL:** `https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector`
- **Type:** [OFFICIAL-DOC] · **Stability:** High
- **Annotation:** Reference for every `runtimeconfig.json` / environment-variable GC knob used in
  §4.6 and the §8 tuning matrix: `System.GC.Server`, `System.GC.Concurrent`,
  `System.GC.HeapCount`, `System.GC.HeapAffinitizeMask`/`HeapAffinitizeRanges`,
  `System.GC.HeapHardLimit(Percent)`, `System.GC.ConserveMemory`, `System.GC.RetainVM`, and
  gen0 size (`GCgen0size` legacy / `System.GC.Gen0Size`). We flagged in §4.6 which of these are
  officially documented vs honored-but-undocumented; this page is the line between the two.
- **Cited in:** §4, §6, §8

### REF-18 — Middle ground between Server and Workstation GC (heap count tuning)
- **Citation:** Maoni Stephens. *"Middle ground between server and workstation GC"* (blog post) and related `GCHeapCount` guidance. Maoni's WebLog.
- **URL:** `https://devblogs.microsoft.com/dotnet/middle-ground-between-server-and-workstation-gc/`
- **Type:** [BLOG] · **Stability:** Medium-high
- **Annotation:** The GC architect's explicit endorsement of running Server GC with *fewer heaps
  than cores* — exactly the configuration §8 recommends for HFT hosts where most cores are
  isolated for trading threads. Justifies our decision-matrix row "Server GC, `HeapCount` =
  #non-isolated cores, affinitized away from trading cores."
- **Cited in:** §4, §8

---

## 9.4 GC regions and .NET 7+ evolution (REF-19 … REF-22)

### REF-19 — Maoni Stephens on regions
- **Citation:** Maoni Stephens. *"Put a DPAD on that GC!"* and *"Internals of the POH"* / regions discussion posts; plus the .NET 6/7 region rollout notes. Maoni's WebLog, 2021–2022.
- **Type:** [BLOG] · **Stability:** Medium
- **Annotation:** First-party explanation of the segments→regions redesign: many small (4 MB on
  64-bit) regions per generation instead of few large segments, enabling cheaper memory give-back,
  per-generation accounting, and future features (DATAS). Section 5's "what regions change / what
  they don't" analysis is anchored here. Key claim we carried into §5.4: regions are a
  *throughput and memory-footprint* feature; they were not designed to reduce, and do not
  materially reduce, individual pause times.
- **Cited in:** §5

### REF-20 — Performance Improvements in .NET 7 (and .NET 6/8 companions)
- **Citation:** Stephen Toub. *"Performance Improvements in .NET 7."* .NET Blog, August 2022. (Companion posts: .NET 6, 2021; .NET 8, 2023.)
- **URL:** `https://devblogs.microsoft.com/dotnet/performance_improvements_in_net_7/`
- **Type:** [BLOG] · **Stability:** Medium-high (Microsoft-hosted)
- **Annotation:** Encyclopedic, benchmark-backed change logs. We used the GC sections for the
  regions enablement timeline (default-on in .NET 7 for CoreCLR), and the library sections to
  verify which BCL APIs became allocation-free across versions — directly feeding §7's
  "allocation behavior varies by runtime version" caveats (e.g., `Enumerable` optimizations,
  interpolated string handlers in .NET 6, `Regex` source generators).
- **Cited in:** §5, §7

### REF-21 — DATAS: Dynamic Adaptation To Application Sizes
- **Citation:** Maoni Stephens. *"Dynamically Adapting To Application Sizes (DATAS)."* Maoni's WebLog / .NET Blog, 2023; default-on in .NET 9.
- **Type:** [BLOG] + [OFFICIAL-DOC] · **Stability:** Medium-high
- **Annotation:** DATAS dynamically varies Server GC heap count with live data size. Cited in §5.6
  with an explicit **warning for HFT**: heap-count changes at runtime introduce variance, and
  DATAS optimizes footprint, not tail latency. Our recommendation (disable DATAS via
  `System.GC.DynamicAdaptationMode=0` on .NET 9+ for latency-critical processes, pin heap count
  explicitly) follows from this source's own description of its goals.
- **Cited in:** §5, §8

### REF-22 — What's new in .NET 7 runtime / GC release notes
- **Citation:** Microsoft. *What's new in .NET 7* (runtime section) and `dotnet/runtime` release notes for 7.0. Microsoft Learn / GitHub.
- **URL:** `https://learn.microsoft.com/dotnet/core/whats-new/dotnet-7`
- **Type:** [OFFICIAL-DOC] · **Stability:** High
- **Annotation:** Used to pin exact version facts in §5: regions on by default in .NET 7 (opt-out
  via `DOTNET_GCName` segments fallback existed through .NET 7/8), NativeAOT GA status, and the
  documented compatibility switches. Keeps §5 honest about which behaviors are version-gated.
- **Cited in:** §5

---

## 9.5 Measurement, tracing, and pause characterization (REF-23 … REF-29)

### REF-23 — PerfView and the TraceEvent library
- **Citation:** Vance Morrison et al. *PerfView* (tool) and *Microsoft.Diagnostics.Tracing.TraceEvent* (library). GitHub, `microsoft/perfview`.
- **URL:** `https://github.com/microsoft/perfview`
- **Type:** [TOOL] · **Stability:** High
- **Annotation:** The reference tool for GC pause analysis on Windows: the **GCStats** view's
  per-GC table ("Pause MSec", "Suspend MSec", "% Time in GC", per-generation sizes, induced
  flags) is the format Section 6 adopts for reporting measured pauses. TraceEvent is what our
  Section 6 methodology uses to capture `GCStart_V2`/`GCEnd_V2`/`GCSuspendEEBegin` events
  programmatically for in-process pause histograms.
- **Cited in:** §6

### REF-24 — .NET GC ETW events documentation and Maoni's ETW interpretation series
- **Citation:** Microsoft. *Garbage collection ETW events.* Microsoft Learn; and Maoni Stephens, *"GC ETW events"* blog series (parts 1–4).
- **URL:** `https://learn.microsoft.com/dotnet/fundamentals/diagnostics/runtime-garbage-collection-events`
- **Type:** [OFFICIAL-DOC] + [BLOG] · **Stability:** High / Medium
- **Annotation:** Field-by-field semantics for the GC event payloads, including the critical
  distinction between *suspension duration* and *GC work duration*, and the meaning of each
  GC reason code (`AllocSmall`, `AllocLarge`, `Induced`, `LowMemory`...). Section 6's pause
  decomposition and the reason-code distribution table depend on correctly interpreting these
  events; Maoni's series corrects several common misreadings (e.g., concurrent gen2 "duration"
  is not pause time).
- **Cited in:** §6

### REF-25 — EventPipe, dotnet-trace, dotnet-counters, dotnet-gcdump
- **Citation:** Microsoft. *dotnet-trace*, *dotnet-counters*, *dotnet-gcdump* tool documentation and *EventPipe* overview. Microsoft Learn, .NET diagnostics documentation.
- **URL:** `https://learn.microsoft.com/dotnet/core/diagnostics/`
- **Type:** [OFFICIAL-DOC] + [TOOL] · **Stability:** High
- **Annotation:** Cross-platform (Linux-relevant — most HFT deployments) equivalents of the ETW
  pipeline. Section 6's Linux methodology uses `dotnet-trace collect --profile gc-collect`
  (GC-only keywords, low overhead) and notes the overhead caveat: verbose GC keywords can
  themselves perturb sub-100 µs measurements. Also the source for the
  `System.Runtime` counters used in §8's production monitoring recommendations.
- **Cited in:** §6, §8

### REF-26 — BenchmarkDotNet
- **Citation:** Andrey Akinshin et al. *BenchmarkDotNet.* GitHub, `dotnet/BenchmarkDotNet` (NuGet `BenchmarkDotNet`, v0.13+).
- **URL:** `https://github.com/dotnet/BenchmarkDotNet`
- **Type:** [TOOL] · **Stability:** High
- **Annotation:** Used for §7's per-pattern allocation measurements via the `[MemoryDiagnoser]`
  attribute, which reports allocated bytes/op and GC collection counts per benchmark. Section 7's
  tables of "bytes allocated per call" for LINQ operators, closures, boxing, string APIs, and
  async patterns were produced with this methodology. We also cite its documentation for the
  caveat that `MemoryDiagnoser` measures allocation, not pause impact — the two are correlated
  but not identical.
- **Cited in:** §6, §7

### REF-27 — HdrHistogram and "How NOT to Measure Latency"
- **Citation:** Gil Tene. *HdrHistogram* (tool, `HdrHistogram/HdrHistogram` + .NET port `HdrHistogram.NET`); and *"How NOT to Measure Latency"* (talk, multiple venues, 2013–2016).
- **Type:** [TOOL] + [TALK] · **Stability:** High
- **Annotation:** The canonical treatment of **coordinated omission**: naive request-timing loops
  systematically hide pause-induced latency because the load generator stalls along with the
  system under test. Section 6.2's methodology mandates corrected histograms and reporting of
  p99/p99.9/p99.99/max rather than means precisely because GC pauses live in the extreme tail.
  Any pause numbers in this report not collected with coordinated-omission awareness are flagged
  as such.
- **Cited in:** §1, §6

### REF-28 — Acquiring high-resolution time stamps
- **Citation:** Microsoft. *Acquiring high-resolution time stamps* (QPC/TSC documentation) and *`Stopwatch`/`Stopwatch.GetTimestamp` API reference.* Microsoft Learn.
- **URL:** `https://learn.microsoft.com/windows/win32/sysinfo/acquiring-high-resolution-time-stamps`
- **Type:** [OFFICIAL-DOC] · **Stability:** High
- **Annotation:** Underpins §6's measurement substrate: `Stopwatch.GetTimestamp()` resolution,
  invariant-TSC requirements, and cross-core timestamp consistency. Cited so readers can validate
  that sub-microsecond pause attribution in §6 is within the clock's error budget.
- **Cited in:** §6

### REF-29 — GC pause measurements in the wild: GCSettings vs reality (Kevin Gosse / Christophe Nasarre)
- **Citation:** Kevin Gosse and Christophe Nasarre. GC investigation series — *"Reading .NET GC internals from a debugger"*, Datadog .NET profiler engineering posts, and the *Criteo .NET performance* post series, 2018–2023.
- **URL:** `https://minidump.net/` (Gosse); Criteo R&D blog / Medium archives (Nasarre)
- **Type:** [BLOG] · **Stability:** Medium (some posts have moved hosts)
- **Annotation:** Production-scale pause forensics from engineers running very large .NET fleets:
  diagnosing multi-second gen2 pauses, Server GC thread starvation under CPU oversubscription,
  and container memory-limit interactions (`GCHeapHardLimit` defaults inside cgroups). Section
  6.7's "pathological pause" case studies and §8's container guidance draw on these write-ups.
  Treated as practitioner evidence, corroborated against REF-07/REF-08 where possible.
- **Cited in:** §6, §8

---

## 9.6 Allocation sources in C# code (REF-30 … REF-36)

### REF-30 — Writing High-Performance .NET Code (Watson)
- **Citation:** Ben Watson. *Writing High-Performance .NET Code*, 2nd edition. Ben Watson, 2018.
- **Type:** [BOOK] · **Stability:** High (print)
- **Annotation:** Written by an engineer from Microsoft Bing's high-scale .NET infrastructure; the
  strongest published catalog of accidental allocation: boxing through interfaces and generics,
  delegate/closure capture, LINQ enumerator allocation, string API costs, and struct
  `GetHashCode`/`Equals` boxing via the default `ValueType` implementations. Section 7's taxonomy
  began as a superset of Watson's chapter 6 and was then re-verified per-pattern on .NET 8 with
  BenchmarkDotNet (REF-26), since several of Watson's 2018 numbers improved (REF-20).
- **Cited in:** §7

### REF-31 — Pro .NET Performance (Goldshtein)
- **Citation:** Sasha Goldshtein, Dima Zurbalev, Ido Flatow. *Pro .NET Performance: Optimize Your C# Applications.* Apress, 2012.
- **Type:** [BOOK] · **Stability:** High (print; pre-Core but mechanics still accurate)
- **Annotation:** Older but valuable for first-principles explanations of boxing IL
  (`box`/`unbox.any`), value-type semantics, and GC interaction with finalization — mechanics that
  have not changed since CLR 2.0. Section 7.3's IL-level walkthrough of where `box` instructions
  appear (interface dispatch on structs, `object`-typed collections, string concatenation with
  value types, non-generic APIs) follows Goldshtein's presentation, updated for modern codegen.
- **Cited in:** §2, §7

### REF-32 — How async/await really works in C#
- **Citation:** Stephen Toub. *"How Async/Await Really Works in C#."* .NET Blog, March 2023.
- **URL:** `https://devblogs.microsoft.com/dotnet/how-async-await-really-works/`
- **Type:** [BLOG] · **Stability:** Medium-high
- **Annotation:** The definitive public description of compiler-generated async state machines:
  when the struct state machine boxes to the heap (first await that doesn't complete
  synchronously), what `AsyncTaskMethodBuilder` allocates, how `Task` caching avoids allocation
  for common results, and the .NET 6+ pooling experiment (`PoolingAsyncValueTaskMethodBuilder`).
  Section 7.6's per-await allocation accounting (state machine box + `Task` + `MoveNext` delegate
  ≈ 100–200 B per cold path) is sourced here, as is §8's guidance on why hot paths in HFT engines
  should be synchronous or use custom awaitables.
- **Cited in:** §7, §8

### REF-33 — Understanding the Whys, Whats, and Whens of ValueTask
- **Citation:** Stephen Toub. *"Understanding the Whys, Whats, and Whens of ValueTask."* .NET Blog, November 2018.
- **URL:** `https://devblogs.microsoft.com/dotnet/understanding-the-whys-whats-and-whens-of-valuetask/`
- **Type:** [BLOG] · **Stability:** Medium-high
- **Annotation:** Explains `ValueTask`/`IValueTaskSource` and the strict single-consumption rules.
  Section 7.6 cites it for when `ValueTask` actually eliminates allocation (synchronously
  completing paths; pooled `IValueTaskSource` backing) and §8's decision matrix cites its own
  warnings about misuse — relevant because trading codebases frequently cargo-cult `ValueTask`
  without the pooled source that makes it allocation-free.
- **Cited in:** §7, §8

### REF-34 — List<T> growth and collection internals (runtime source)
- **Citation:** .NET Runtime Team. `List<T>`, `Dictionary<TKey,TValue>`, `Queue<T>` implementations, `dotnet/runtime` `src/libraries/System.Private.CoreLib/src/System/Collections/`.
- **URL:** `https://github.com/dotnet/runtime/tree/main/src/libraries/System.Private.CoreLib/src/System/Collections`
- **Type:** [RUNTIME-SOURCE] · **Stability:** High
- **Annotation:** Ground truth for §7.7's collection-resizing analysis: `List<T>` doubling
  (4→8→16…, discarded backing arrays become garbage, arrays > 85,000 B land on the LOH),
  `Dictionary` prime-bucket rehashing allocating both bucket and entry arrays, and the absence of
  shrinking. Source for the report's rule that an unsized `List<byte[]>` reaching 100k elements
  has churned ~25 intermediate arrays, several LOH-sized.
- **Cited in:** §3, §7

### REF-35 — LINQ implementation internals
- **Citation:** .NET Runtime Team. `System.Linq.Enumerable` implementation, `dotnet/runtime` `src/libraries/System.Linq/`; supplemented by Stephen Toub's LINQ sections in REF-20.
- **URL:** `https://github.com/dotnet/runtime/tree/main/src/libraries/System.Linq/src/System/Linq`
- **Type:** [RUNTIME-SOURCE] · **Stability:** High
- **Annotation:** Verifies §7.2's operator-by-operator allocation table: iterator object per
  operator in a chain, closure + delegate per lambda with captures, `Buffer<T>`/array growth in
  `OrderBy`/`ToArray`/`ToList`, and the fused iterator optimizations
  (`Where`+`Select` → `WhereSelectIterator`) that make naive "count the operators" estimates
  wrong. Cited so readers can re-verify against the runtime version they ship.
- **Cited in:** §7

### REF-36 — Roslyn heap allocation analyzers
- **Citation:** *Microsoft.CodeAnalysis.BannedApiAnalyzers* (Microsoft, NuGet) and *ClrHeapAllocationAnalyzer* (community, `microsoft/RoslynClrHeapAllocationAnalyzer`, now community-maintained forks such as `ErrorProne.NET` allocations analyzers).
- **Type:** [TOOL] · **Stability:** Medium (community forks vary)
- **Annotation:** Static-analysis enforcement of "no allocation on the hot path": flags implicit
  boxing, closure captures, params-array allocation, enumerator allocation, and string
  concatenation at build time. Section 7.9 and §8.5 recommend pairing an allocation analyzer
  (advisory) with `BannedApiAnalyzers` (hard-ban LINQ/`string.Format`/etc. in hot-path
  assemblies) — the layered approach used in the production shops surveyed in §8.
- **Cited in:** §7, §8

---

## 9.7 Mitigation techniques, pooling, and zero-allocation APIs (REF-37 … REF-42)

### REF-37 — All About Span: Exploring a New .NET Mainstay
- **Citation:** Stephen Toub. *"C# — All About Span: Exploring a New .NET Mainstay."* MSDN Magazine, January 2018; with the `Span<T>`/`Memory<T>` Microsoft Learn guidance (*Memory- and span-related types*).
- **URL:** `https://learn.microsoft.com/dotnet/standard/memory-and-spans/`
- **Type:** [BLOG/OFFICIAL-DOC] · **Stability:** High
- **Annotation:** Foundation for §8.2's zero-copy parsing strategy: `Span<T>` over stack, pooled,
  or native memory; `stackalloc` without `unsafe`; and the span-based BCL overloads
  (`Utf8Parser`, `int.TryParse(ReadOnlySpan<char>)`, `Encoding.GetChars` span overloads) that let
  a FIX/ITCH parser run allocation-free. The ref-struct restrictions table in §8.2 comes from the
  Learn guidance.
- **Cited in:** §7, §8

### REF-38 — `ArrayPool<T>` and `MemoryPool<T>`
- **Citation:** Microsoft. *`System.Buffers.ArrayPool<T>` API reference* and Adam Sitnik, *"Pooling large arrays with ArrayPool"* (blog, 2016).
- **URL:** `https://learn.microsoft.com/dotnet/api/system.buffers.arraypool-1` · `https://adamsitnik.com/Array-Pool/`
- **Type:** [OFFICIAL-DOC] + [BLOG] · **Stability:** High / Medium
- **Annotation:** The standard buffer-pooling layer. Sitnik's post (he later joined the .NET team)
  measured the rent/return crossover where pooling beats allocation and documented
  `Shared` pool bucket behavior and the dirty-buffer caveat. §8.3 cites both for our guidance:
  `ArrayPool.Shared` for general code, but **custom fixed-size, pre-touched, POH-backed pools**
  for HFT hot paths, because `Shared`'s TLS+global tiers have unbounded worst-case behavior and
  can allocate on miss.
- **Cited in:** §3, §8

### REF-39 — Microsoft.Extensions.ObjectPool and object reuse patterns
- **Citation:** Microsoft. *Object reuse with ObjectPool* (ASP.NET Core docs) and `Microsoft.Extensions.ObjectPool` package documentation.
- **URL:** `https://learn.microsoft.com/aspnet/core/performance/objectpool`
- **Type:** [OFFICIAL-DOC] · **Stability:** High
- **Annotation:** Reference implementation of object pooling used inside ASP.NET Core
  (`StringBuilder` pooling, etc.). §8.3 uses it as the baseline design to compare against the
  HFT-grade alternative (preallocated ring of mutable order/quote objects, no interlocked
  rent/return on the fast path) and cites its `DefaultObjectPool` interlocked-stack design as the
  contention source the ring design avoids.
- **Cited in:** §8

### REF-40 — System.IO.Pipelines
- **Citation:** Microsoft. *System.IO.Pipelines: High performance IO in .NET.* Microsoft Learn; David Fowler, *"System.IO.Pipelines: High performance IO in .NET"* (.NET Blog, 2018).
- **URL:** `https://learn.microsoft.com/dotnet/standard/io/pipelines`
- **Type:** [OFFICIAL-DOC] + [BLOG] · **Stability:** High
- **Annotation:** Pooled, zero-copy buffer management for stream protocols; Kestrel's foundation.
  §8.4 evaluates it for market-data ingest: excellent allocation profile, but its
  awaitable backpressure model implies async scheduling jitter, so the §8 decision matrix scores
  it "good for order-entry gateways (TCP/FIX), not for hot UDP multicast paths" where a dedicated
  spinning receive thread with preallocated buffers wins.
- **Cited in:** §8

### REF-41 — Disruptor pattern and Disruptor-net
- **Citation:** Martin Thompson, Dave Farley, Michael Barker, Patricia Gee, Andrew Stewart. *Disruptor: High performance alternative to bounded queues for exchanging data between concurrent threads.* LMAX technical paper, 2011. .NET port: `disruptor-net/Disruptor-net` (GitHub, NuGet `Disruptor`).
- **URL:** `https://lmax-exchange.github.io/disruptor/` · `https://github.com/disruptor-net/Disruptor-net`
- **Type:** [PAPER] + [TOOL] · **Stability:** High
- **Annotation:** The canonical pattern for allocation-free inter-thread messaging: preallocated
  ring buffer of mutable events, sequence barriers, busy-spin wait strategies, single-writer
  principle. The LMAX paper's measurements (vs `ArrayBlockingQueue`) established that the
  dominant costs are queue allocation churn and cache contention, not raw GC — the architectural
  insight behind §8.6's "steady-state zero allocation" strategy, which is the report's primary
  recommendation and the design center for Milestone 2.
- **Cited in:** §1, §8

### REF-42 — Aeron and Aeron.NET
- **Citation:** Martin Thompson et al. *Aeron: Efficient reliable UDP unicast, multicast, and IPC message transport.* `real-logic/aeron`; .NET client: `AdaptiveConsulting/Aeron.NET`.
- **URL:** `https://github.com/real-logic/aeron` · `https://github.com/AdaptiveConsulting/Aeron.NET`
- **Type:** [TOOL] · **Stability:** High
- **Annotation:** Production-grade low-latency transport whose .NET client (maintained by Adaptive,
  a firm building trading systems on .NET) demonstrates the full zero-allocation discipline in a
  real C# codebase: flyweights over `UnsafeBuffer`, agent/duty-cycle threading instead of async,
  no LINQ, no exceptions on the duty cycle. §8.7 uses Aeron.NET as the primary *evidence* that
  the surveyed mitigation strategy is achievable and maintained at production quality in C#.
- **Cited in:** §8

---

## 9.8 Cross-ecosystem and OS-level context (REF-43 … REF-46)

### REF-43 — Mechanical Sympathy
- **Citation:** Martin Thompson. *Mechanical Sympathy* blog, 2011–2016.
- **URL:** `https://mechanical-sympathy.blogspot.com/`
- **Type:** [BLOG] · **Stability:** Medium (Blogspot archive, stable for a decade)
- **Annotation:** The intellectual source of several principles the report applies to .NET:
  single-writer principle, false sharing and cache-line padding, memory-access patterns dominating
  algorithmic constants, and "garbage-free" Java HFT practice. Cited in §1 and §8 to show that the
  mitigation strategies surveyed are a transplant of a decade-proven JVM-HFT playbook into .NET,
  not novel speculation.
- **Cited in:** §1, §8

### REF-44 — JVM comparison: Azul Zing/C4 and JEP 318 (Epsilon)
- **Citation:** Gil Tene, Balaji Iyengar, Michael Wolf. *C4: The Continuously Concurrent Compacting Collector.* ISMM 2011. And: Aleksey Shipilëv. *JEP 318: Epsilon — A No-Op Garbage Collector.* OpenJDK, 2018.
- **Type:** [PAPER] + [OFFICIAL-DOC] · **Stability:** High
- **Annotation:** Establishes the comparative landscape in §1 and §6.8: the JVM ecosystem solved
  GC pauses two ways — a pauseless concurrent compacting collector (C4/Zing; analogous ambitions
  exist for .NET but nothing shipped is equivalent) and the "just don't collect" Epsilon approach
  (analogous to .NET's `TryStartNoGCRegion` + zero-allocation discipline). Frames the report's
  conclusion: since .NET has no C4 equivalent, the Epsilon-style discipline (allocate nothing in
  steady state) is the only path to deterministic sub-100 µs behavior on .NET today.
- **Cited in:** §1, §4, §6

### REF-45 — Linux low-latency tuning
- **Citation:** Red Hat. *Low Latency Performance Tuning for Red Hat Enterprise Linux* (tuning guide); Linux kernel documentation for `isolcpus`, `nohz_full`, `rcu_nocbs`, IRQ affinity, and `tuned` realtime profiles.
- **Type:** [OFFICIAL-DOC] · **Stability:** High
- **Annotation:** GC pauses are only one jitter source; §6.9 shows measured pause floors are
  contaminated by scheduler and SMI noise unless cores are isolated. This guide is the source for
  the host configuration under which all §6 Linux measurements were taken (isolated cores,
  `nohz_full`, IRQs steered away, CPU governor `performance`) and for §8.8's deployment checklist
  rows (thread affinity for trading threads, GC threads affinitized to housekeeping cores via
  REF-17 knobs).
- **Cited in:** §6, §8

### REF-46 — NativeAOT and runtime trade-offs for latency
- **Citation:** Microsoft. *Native AOT deployment.* Microsoft Learn, .NET documentation (.NET 7+); plus `dotnet/runtime` NativeAOT design docs.
- **URL:** `https://learn.microsoft.com/dotnet/core/deploying/native-aot/`
- **Type:** [OFFICIAL-DOC] · **Stability:** High
- **Annotation:** Evaluated in §8.9 as a complementary mitigation: NativeAOT removes JIT-induced
  jitter (tiered compilation recompiles, on-stack replacement) but **keeps the same GC**, so it is
  orthogonal to pause elimination. Cited for the accurate framing that AOT addresses *warm-up and
  code-gen jitter*, while allocation discipline addresses *GC jitter* — both are needed; neither
  substitutes for the other.
- **Cited in:** §6, §8

---

## 9.9 Citation key → section cross-reference matrix

| Key | Short name | §1 | §2 | §3 | §4 | §5 | §6 | §7 | §8 |
|-----|------------|----|----|----|----|----|----|----|----|
| REF-01 | GC fundamentals (Learn) | ● | ● | | | | | | |
| REF-02 | BotR: GC design | | ● | | | | ● | | |
| REF-03 | gc.cpp | | ● | ● | | ● | | | |
| REF-04 | BotR: threading/safe points | | ● | | | | ● | | |
| REF-05 | Kokosa, Pro .NET Memory | | ● | ● | | | | ● | |
| REF-06 | Kokosa GC internals lectures | | ● | | | | | | |
| REF-07 | Maoni's WebLog | | ● | | ● | ● | ● | | |
| REF-08 | mem-doc | ● | | | | | ● | | ● |
| REF-09 | LOH (Learn) | | | ● | | ● | | | |
| REF-10 | POH design doc | | | ● | | | | | ● |
| REF-11 | AllocateUninitializedArray | | | ● | | | | | ● |
| REF-12 | LOH compaction mode | | | ● | | | ● | | ● |
| REF-13 | Workstation vs Server (Learn) | | | | ● | | ● | | |
| REF-14 | Background GC (Learn) | | | | ● | | ● | | |
| REF-15 | Latency modes / SLL | | | | ● | | | | |
| REF-16 | TryStartNoGCRegion | | | | ● | | | | ● |
| REF-17 | GC runtime config options | | | | ● | | ● | | ● |
| REF-18 | Heap-count middle ground | | | | ● | | | | ● |
| REF-19 | Regions (Stephens) | | | | | ● | | | |
| REF-20 | Perf improvements in .NET 7 | | | | | ● | | ● | |
| REF-21 | DATAS | | | | | ● | | | ● |
| REF-22 | What's new in .NET 7 | | | | | ● | | | |
| REF-23 | PerfView / TraceEvent | | | | | | ● | | |
| REF-24 | GC ETW events | | | | | | ● | | |
| REF-25 | EventPipe / dotnet-trace | | | | | | ● | | ● |
| REF-26 | BenchmarkDotNet | | | | | | ● | ● | |
| REF-27 | HdrHistogram / coordinated omission | ● | | | | | ● | | |
| REF-28 | High-resolution timestamps | | | | | | ● | | |
| REF-29 | Gosse/Nasarre forensics | | | | | | ● | | ● |
| REF-30 | Watson, High-Performance .NET | | | | | | | ● | |
| REF-31 | Goldshtein, Pro .NET Performance | | ● | | | | | ● | |
| REF-32 | Async/await internals (Toub) | | | | | | | ● | ● |
| REF-33 | ValueTask (Toub) | | | | | | | ● | ● |
| REF-34 | Collection internals (source) | | | ● | | | | ● | |
| REF-35 | LINQ internals (source) | | | | | | | ● | |
| REF-36 | Allocation analyzers | | | | | | | ● | ● |
| REF-37 | Span/Memory | | | | | | | ● | ● |
| REF-38 | ArrayPool | | | ● | | | | | ● |
| REF-39 | ObjectPool | | | | | | | | ● |
| REF-40 | System.IO.Pipelines | | | | | | | | ● |
| REF-41 | Disruptor | ● | | | | | | | ● |
| REF-42 | Aeron.NET | | | | | | | | ● |
| REF-43 | Mechanical Sympathy | ● | | | | | | | ● |
| REF-44 | C4 / Epsilon (JVM) | ● | | | ● | | ● | | |
| REF-45 | Linux low-latency tuning | | | | | | ● | | ● |
| REF-46 | NativeAOT | | | | | | ● | | ● |

> **Drift note:** If a `REF-nn` key in Sections 1–8 appears to point at an adjacent topic, this
> table is authoritative — keys were assigned in section order and a small number of entries were
> consolidated during final editing (e.g., separate ETW conceptual/API citations merged into
> REF-24). No key above REF-46 is used anywhere in the report.

---

## 9.10 Reading paths

- **"I own the GC config for a trading system, give me the minimum":** REF-08 → REF-13 → REF-17 → REF-18 → REF-21 → §8 decision matrices.
- **"I need to defend the zero-allocation strategy to skeptics":** REF-41 → REF-42 → REF-43 → REF-44 → §6 measured data → §8.6.
- **"I'm building the measurement harness (Milestone 2 prep)":** REF-23 → REF-24 → REF-25 → REF-27 → REF-28 → §6.2 methodology.
- **"I want to deeply understand the collector itself":** REF-01 → REF-02 → REF-06 → REF-05 → REF-03.

*End of Section 9.*
