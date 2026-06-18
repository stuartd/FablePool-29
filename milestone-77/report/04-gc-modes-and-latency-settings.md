# 4. GC Modes, Latency Settings, and Configuration Knobs

This section catalogs every mode and switch that changes GC pause behavior, what each actually does mechanically, and where it helps or hurts an HFT deployment. It concludes with the blessed baseline configurations referenced by §9.

## 4.1 Workstation GC

- **One logical heap**, GC work done (mostly) on the triggering thread (plus background GC's dedicated thread).
- Designed for client apps sharing the machine; smaller gen 0 budgets → **more frequent, shorter** ephemeral GCs.
- For a process with few threads and a small heap, Workstation GC often has the **lowest per-pause times** — and the lowest *throughput* under allocation pressure.
- Config: `"System.GC.Server": false` (default for non-ASP.NET apps), env `DOTNET_gcServer=0`.

**HFT relevance**: a small, single-purpose hot-path process (the architecture §8 recommends) frequently does best on Workstation GC, contrary to web-era folk wisdom that "Server GC is better." Decide by measurement (§6.5), not defaults.

## 4.2 Server GC

- **One heap per logical core** by default (allocation contexts map threads to heaps), each with its own gen 0/1/2 + LOH share; collections run **in parallel** on dedicated, high-priority GC threads (one per heap), with ephemeral GCs since .NET Core also parallelized.
- Larger gen 0 budgets → **fewer, but individually larger** ephemeral GCs; far higher allocation throughput.
- **Hazards for latency work**:
  - During GC, *N* high-priority GC threads burst onto *N* cores. If your market-data thread is pinned to core 7 and a GC thread is affinitized there, the GC wins. On machines shared with kernel-bypass NIC polling threads this is a recurring production incident pattern.
  - More heaps = more total gen 0 budget = larger survivor scans per GC if allocation is spread across threads.
- **Mandatory tuning on big boxes**:
  - `System.GC.HeapCount` (`DOTNET_GCHeapCount`, hex when set via the legacy env var — a classic operational footgun; the `runtimeconfig.json` value is decimal): cap heap count to the cores actually granted to GC.
  - `System.GC.HeapAffinitizeMask` / `System.GC.HeapAffinitizeRanges` (e.g. `"0-3,8-11"`): confine GC threads to designated cores, keeping them off trading-pinned cores.
  - `System.GC.NoAffinitize: true` to unpin GC threads entirely (then rely on OS CPU sets).
- .NET 8 adds **DATAS** (Dynamic Adaptation To Application Sizes), default-on in .NET 9: dynamically scales heap count from 1 upward. Good for cloud density; **turn it off for latency determinism** (`System.GC.DynamicAdaptationMode: 0`) — heap-count changes at runtime are exactly the nondeterminism HFT configs exist to remove.

## 4.3 Concurrent / Background GC

Terminology: ".NET Framework concurrent GC" (pre-4.0) evolved into **background GC** (BGC), the modern default (`System.GC.Concurrent`, default `true`).

Mechanics of a background gen 2 GC:

1. **Initial STW pause**: snapshot roots, set up concurrent mark (short — typically sub-ms to a few ms).
2. **Concurrent mark**: a dedicated BGC thread (per heap, for Server) marks the gen 2 graph **while mutators run**, tracking concurrent mutations via the write barrier (and a "more conservative" revisit logic). Mutators may experience modest slowdowns (write-watch overhead, memory bandwidth competition) but not stops.
3. While BGC is in progress, **foreground ephemeral GCs (gen 0/1) can run** — these are full STW pauses nested inside the background collection.
4. **Final STW pause**: catch-up marking of mutated references, sweep planning (BGC sweeps; it does not compact gen 2).
5. Concurrent sweep.

Trade-offs:

| | Background gen 2 | Blocking gen 2 |
|---|---|---|
| STW time | 2 short pauses (sub-ms–low ms each) | entire collection (10s–100s of ms) |
| Compaction | **never** (sweep only) → fragmentation accumulates | optional, reclaims fragmentation |
| CPU during GC | concurrent thread(s) steal cycles/bandwidth from mutators | concentrated in pause |
| When forced blocking anyway | low-memory situations, `GC.Collect()` default, hard-limit pressure | — |

**HFT relevance**: BGC is unambiguously correct to leave enabled. But note: (a) ephemeral pauses are untouched; (b) because BGC never compacts gen 2, a long-running process accumulates gen 2 fragmentation that only a *blocking* compacting GC fixes — schedule it off-hours (§3.4); (c) the concurrent phase competes for memory bandwidth with the order book — on saturated memory-bound systems, BGC's "no pause" phase is still measurable as throughput jitter (§6.6).

## 4.4 `GCLatencyMode.LowLatency`

`GCSettings.LatencyMode = GCLatencyMode.LowLatency` (Workstation, .NET Framework heritage):

- Suppresses discretionary gen 2 GCs; only allocation pressure/OS memory pressure forces them.
- Intended for *short* windows (the documented example was UI animations).
- Largely superseded by `SustainedLowLatency` and `TryStartNoGCRegion`. Not recommended: weaker guarantees than NoGC regions, same foot-guns.

## 4.5 `GCLatencyMode.SustainedLowLatency` (SLL)

```csharp
GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
```

What it actually does (this is the most-misquoted setting in .NET latency lore):

- While set, the collector **avoids blocking gen 2 collections**: full GCs happen as background GCs where possible; discretionary full-compacting collections are deferred.
- It does **not** change gen 0/1 behavior, does **not** bound any pause, and does **not** survive genuine memory pressure — a hard OS/container squeeze still produces a blocking compacting GC.
- Cost: deferring compaction means higher steady-state memory and fragmentation; you are trading RAM for pause smoothness.
- Designed to be held for long stretches (a trading session) — that is the "sustained" — and dropped during maintenance windows so a real compacting GC can run.

**Verdict**: appropriate as the *session-hours default* for any trading process that has not achieved zero allocation, combined with off-hours hygiene (drop to `Interactive`, `CompactOnce` + `GC.Collect`). It is a smoothing measure, not a fix.

## 4.6 NoGC regions: `GC.TryStartNoGCRegion`

The only API that *guarantees* no GC pauses for a window:

```csharp
// Request budget: total, or split (LOH separately); optionally allow a blocking
// preparatory GC to create room.
if (GC.TryStartNoGCRegion(totalSize: 200_000_000,
                          disallowFullBlockingGC: false))
{
    try
    {
        RunCriticalWindow();   // all allocation comes from the pre-committed budget
    }
    finally
    {
        if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
            GC.EndNoGCRegion();
    }
}
```

Semantics and sharp edges (verified against runtime behavior through .NET 8):

- **Budget ceiling**: the request must fit the ephemeral capacity — bounded by segment size × heap count on segment-based runtimes; on regions-based runtimes the practical ceilings are similar in spirit. With Server GC on a many-core box, budgets of several hundred MB to a few GB are attainable; Workstation defaults cap far lower. If the budget can't be honored, `TryStartNoGCRegion` returns `false` (or throws on argument errors) — **handle the false path; do not assume**.
- To make room, the runtime may perform a **blocking GC at entry** (unless `disallowFullBlockingGC: true`, in which case entry may simply fail). Therefore: enter the region *before* the latency-critical time (e.g., 30 s before the open), never reactively.
- **Exhausting the budget ends the guarantee**: the allocation that exceeds it triggers a GC (the region exits "induced"). Hence NoGC regions are only safe when allocation in the window is *bounded and audited* — which the later analyzer milestone will enforce.
- `EndNoGCRegion` throws if a GC already ended the region — hence the `LatencyMode == NoGCRegion` guard, the canonical correct pattern.
- The GC you avoided is **deferred, not canceled**: expect a substantial collection after exit. Pattern: exit at a scheduled lull and immediately run controlled hygiene.
- Finalizers, other processes, and OS memory pressure are unaffected; the guarantee covers GC suspensions only.

**Verdict**: the precision tool for *bounded* critical windows — opens, closes, auctions, expiry rolls, scheduled news. Production-proven (multiple shops report exactly this pattern; refs §10). Unsuitable as an all-day strategy unless allocation is near zero anyway — at which point you barely need it.

## 4.7 Hard limits, conserve levels, and trimming

- `System.GC.HeapHardLimit` / `HeapHardLimitPercent` (and per-heap variants): cap total GC heap; nearing the cap triggers aggressive (blocking, compacting, LOH-compacting) GCs. Useful to make memory behavior deterministic in containers; **size with ≥40–50% headroom** over steady-state live set or the cap itself becomes a pause generator.
- `System.GC.ConserveMemory` (0–9): trades GC aggressiveness for footprint. Latency deployments leave at 0.
- `System.GC.RetainVM: true`: keep decommitted segments/regions reserved for reuse — reduces VirtualAlloc/decommit churn and the associated page-fault jitter when the heap re-grows. **Recommended `true`** for trading processes (RAM is provisioned anyway).
- `System.GC.HighMemoryPercent`: threshold (default 90) at which the GC turns aggressive due to machine-wide pressure; on dedicated boxes with big RAM, raising it avoids spurious aggression caused by page cache.
- `DOTNET_GCgen0size`: force a large gen 0 budget (e.g. 256 MB/heap) → far fewer ephemeral GCs, each somewhat longer; with near-zero survival this is a strong lever — frequency drops, survivor-copy stays tiny. A core trick in low-allocation shops. Measure survivor bytes before and after (§6.5).

## 4.8 Things that look relevant but aren't (or are traps)

- **`GC.Collect()` "preventively" on a timer**: induces blocking GCs (and by default full compacting ones); with BGC available this almost always *worsens* tails. Only legitimate at scheduled maintenance points with `GCCollectionMode.Aggressive`/`Optimized` and full understanding.
- **`GCSettings.LatencyMode = Batch`**: maximizes throughput, disables concurrency — the opposite of what HFT wants on a mixed process (it can be right for an *offline* batch risk job).
- **`System.GC.CpuGroup`, large pages (`GCLargePages`)**: large pages (with `HeapHardLimit`) remove page-fault jitter on huge heaps and measurably stabilize tails on big-memory boxes — worth evaluating in §6 methodology, but operationally heavy (locked-pages privilege, fragmentation at OS level).
- **Disabling the GC outright** via custom CLR hosting or `DOTNET_GCName` to a null GC (the runtime supports pluggable GCs via `IGCToCLR`; a "no-op GC" exists in runtime tests): real, and effectively "malloc-and-never-free". A process that allocates ~0 after warm-up and is recycled daily *can* run this way; it forfeits all safety nets (any leak = OOM death). Documented here as the extreme end of the spectrum; evaluated in §8.9.

## 4.9 Blessed baseline configurations (pre-registration for §9)

**Profile A — dedicated hot-path process (the reference architecture):**

```json
{
  "configProperties": {
    "System.GC.Server": false,
    "System.GC.Concurrent": true,
    "System.GC.RetainVM": true,
    "System.GC.LOHThreshold": 85000
  }
}
```
Plus: `DOTNET_GCgen0size=0x10000000` (256 MB), `SustainedLowLatency` during session, NoGC regions for critical windows, zero steady-state allocation as the actual strategy.

**Profile B — mixed-duty trading process (hot + warm paths, many threads):**

```json
{
  "configProperties": {
    "System.GC.Server": true,
    "System.GC.Concurrent": true,
    "System.GC.HeapCount": 8,
    "System.GC.HeapAffinitizeRanges": "16-23",
    "System.GC.RetainVM": true,
    "System.GC.DynamicAdaptationMode": 0
  }
}
```
GC cores (16–23) disjoint from trading-pinned cores; SLL during session; scheduled off-hours compaction.

Both profiles are inputs to the measurement matrix in §6.5 and the decision matrices in §9; neither is a substitute for allocation elimination (§7–§8).
