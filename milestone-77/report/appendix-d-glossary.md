# Appendix D — Glossary

> Terminology used throughout the survey, normalized so that all sections mean the
> same thing by the same word. Cross-references point to the section where the
> concept is treated in depth.

**Allocation context** — A per-thread chunk of the Gen0 budget from which a thread
bump-allocates without synchronization. Why .NET allocation is ~a pointer increment
in the common case, and why per-thread allocation counting
(`GC.GetAllocatedBytesForCurrentThread`) is cheap. (§02)

**Allocation rate** — Bytes of managed memory allocated per unit time; the primary
driver of ephemeral GC *frequency*. (§02, App. A §A.3.3)

**Background GC (BGC)** — Concurrent Gen2 collection mode in which marking proceeds
while user threads run, with two short stop-the-world suspension windows. Replaced
the older "concurrent GC" naming. (§04 §4.4)

**Boxing** — Heap-allocating a copy of a value type so it can be referenced as
`object`/an interface. A classic hidden allocation: implicit at interface dispatch,
`string.Format(object)`, non-generic collections. (§07.4)

**Budget (generation budget)** — Bytes a generation may accumulate before a
collection of that generation is triggered; dynamically tuned by the GC, partly
configurable (`GCgen0size`). (§02, App. B §B.4)

**Card table / card marking** — Write-barrier data structure recording which regions
of older generations may contain references into younger ones, so ephemeral GCs
avoid scanning the whole heap. The reason every reference write costs a few extra
instructions. (§02)

**Closure / display class** — Compiler-generated heap object capturing local
variables referenced by a lambda. Each capturing lambda call site can allocate one
per invocation. (§07.3)

**Compacting vs sweeping GC** — A compacting collection relocates survivors to
defragment (cost: copying + reference fixup, longer pause); a sweeping collection
threads free space into free lists (cost: future fragmentation). The GC chooses per
collection. (§02, §03)

**DATAS (Dynamic Adaptation To Application Sizes)** — .NET 8+ feature letting Server
GC adapt heap count to workload at runtime; on by default in .NET 9 Server GC.
Footprint-oriented; recommended **off** for latency-deterministic sessions. (§05,
App. B §B.4)

**Ephemeral generations / ephemeral GC** — Gen0 and Gen1, and collections limited to
them. Expected to dominate GC counts (typically > 95%) in healthy workloads. (§02)

**EventPipe** — Cross-platform runtime tracing transport (the mechanism behind
dotnet-trace); the Linux-friendly sibling of ETW. (App. A §A.7)

**Finalization / finalizer queue** — Post-mortem cleanup mechanism; objects with
finalizers survive at least one extra collection and are processed on a dedicated
finalizer thread — a promotion and jitter source. Avoid finalizers on hot types;
use `IDisposable` + pooling. (§02, §07)

**Frozen object heap (FOH)** — Runtime-internal heap area (regions era) for immortal
objects such as string literals; never collected. (§05)

**GC handle** — Runtime handle keeping an object referenced from native code or
infrastructure (pinned handles, dependent handles); scanned during collections. (§02)

**Gen0 / Gen1 / Gen2** — The three SOH generations: newly allocated; survivors of
Gen0; long-lived. Collection cost scales with *survivors*, not with generation size.
(§02)

**Heap balancing** — Server GC mechanism equalizing allocation pressure across
per-core heaps; interacts with thread affinity. (§04 §4.2)

**Induced GC** — Collection triggered by `GC.Collect` rather than budget exhaustion.
Banned on hot paths; used deliberately in maintenance windows. (App. B §B.5)

**Large Object Heap (LOH)** — Where objects ≥ 85,000 B (configurable threshold) are
allocated. Collected only with Gen2; swept by default, compacted only on request or
under hard-limit pressure. The classic source of fragmentation-driven full GCs.
(§03)

**Latency mode (`GCSettings.LatencyMode`)** — Runtime-switchable GC behavioral bias:
`Interactive` (default with concurrent GC), `Batch`, `LowLatency`,
`SustainedLowLatency`, `NoGCRegion` (reported while a NoGC region is active). (§04
§4.5)

**Mark / plan / relocate / compact phases** — Internal phases of a collection:
identify live objects; decide sweep vs compact; update references; move survivors.
Suspension spans these for blocking collections. (§02)

**NoGC region (`GC.TryStartNoGCRegion`)** — A bounded window during which the runtime
commits enough memory up front to guarantee no collection occurs, provided
allocation stays within the requested budget. (§04 §4.6, App. B §B.5)

**OOM vs hard limit** — With `HeapHardLimit` set, exceeding the limit after a
last-chance compacting collection produces `OutOfMemoryException` regardless of
machine memory. Sizing must account for NoGC-region commitments. (App. B §B.3)

**P99 / P99.9 / P99.99** — Latency percentiles. HFT contracts are written against
high percentiles plus a hard ceiling because rare events coincide with the volatile
moments that matter most economically. (§01, §06)

**Pinned Object Heap (POH)** — .NET 5+ heap for objects allocated pinned
(`GC.AllocateArray(pinned: true)`); removes pinning-induced fragmentation from
SOH — important for long-lived I/O buffers. (§03)

**Pinning** — Fixing an object's address (via `fixed`, `GCHandle.Alloc(…, Pinned)`,
or implicit interop pinning) so native code can hold pointers into it; obstructs
compaction and fragments SOH if long-lived. Mitigated by POH or native memory. (§03)

**Promotion** — Survival of an object from one generation to the next; "mid-life
crisis" objects (die in Gen1/Gen2) are the most expensive lifetime pattern. (§02)

**Regions (vs segments)** — .NET 7+ heap layout in which generations are composed of
many small (~4 MiB) regions rather than few large segments, enabling finer-grained
memory management and different decommit behavior. (§05)

**Server GC / Workstation GC** — The two GC flavors: per-core heaps with dedicated,
affinitized GC threads and parallel collection vs a single heap collected (mostly)
on user threads. (§04 §4.1–4.3)

**Small Object Heap (SOH)** — Where objects below the LOH threshold live; comprises
Gen0/1/2. (§02)

**Stop-the-world (STW) / EE suspension** — Suspension of all managed threads at GC
safe points so the runtime can examine/modify the heap. The thing this entire survey
measures and minimizes. (§02, §06, App. A)

**SustainedLowLatency** — Latency mode biasing the GC against full blocking
collections for extended periods while keeping background GC available; the
recommended session mode. Not a guarantee. (§04 §4.5)

**Tiered compilation / tier-up** — JIT strategy compiling methods quickly first, then
recompiling hot ones optimized; the recompilation can appear as mid-session jitter
unrelated to GC. (App. A §A.5, App. B §B.6)

**TLAB-equivalent** — See *allocation context* (the .NET analogue of the JVM's
thread-local allocation buffer).

**Write barrier** — Code emitted at every managed reference store to maintain the
card table; the per-write tax that pays for cheap ephemeral GCs. (§02)

**Zero-allocation (steady state)** — The discipline in which a code path performs no
managed allocation after warmup; verified per-operation by byte-exact assertions
(App. C §C.3) and per-session by GC-count tripwires (App. C §C.5). (§07, §08)

---

*End of Appendix D.*
