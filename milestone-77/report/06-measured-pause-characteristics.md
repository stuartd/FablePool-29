# Section 6 — Measured Pause Characteristics: Methodology and Data

> Part of the FablePool research survey. Cross-references: §2 (what each GC type does), §4 (modes/settings), §5 (regions), §7 (allocation sources), §8 (mitigations). Citation keys (`[REF-nn]`) resolve in §9.

**Honesty note on data provenance.** This section provides (a) a fully specified, reproducible **measurement methodology** with working harness code, and (b) **indicative pause ranges** synthesized from published measurements by the .NET GC team and practitioners [REF-02][REF-05][REF-11][REF-15][REF-16] plus first-principles cost models. Absolute numbers vary with CPU generation, memory bandwidth, heap count, survivor volume, and OS configuration. Every figure below is labeled with its driver so you can re-derive it; **run the harness on your own production-identical hardware before relying on any specific number.** That qualification run is itself a deliverable of FablePool Milestone 2.

---

## 6.1 What "a pause" is, precisely

For latency engineering, the only number that matters is the **stop-the-world (STW) window**: the interval during which *your* threads cannot run managed code. Define it from runtime events:

```
pause = t(GCRestartEEEnd) − t(GCSuspendEEBegin)
```

- A **blocking GC** (all gen0/gen1; non-concurrent gen2) has **one** STW window covering suspension + mark + plan + relocate/compact + restart.
- A **background GC (BGC)** of gen2 has **two short STW windows** (initial mark setup; sweep handoff) plus long *concurrent* phases that consume CPU and memory bandwidth but do not stop threads. BGC also typically triggers ephemeral GCs that have their own STW windows (§4.4).
- **Suspension itself** is part of the pause: the runtime asks every managed thread to reach a safepoint (via return-address hijacking or polling). Suspension latency grows with thread count and is tail-dominated by threads slow to reach a safepoint.

**Do not** measure GC cost with `Stopwatch` around `GC.Collect()` — induced GCs have different code paths and you'll miss suspension behavior under real thread loads. Use runtime events.

## 6.2 Measurement methodology

### 6.2.1 Out-of-process (production-grade): ETW / EventPipe

Preferred for production because it adds no allocation or contention inside the process.

- **Windows:** ETW provider `Microsoft-Windows-DotNETRuntime`, keyword `GC (0x1)`, Informational level. PerfView's *GCStats* view computes per-GC pause, "% pause time", "Max Suspend Msec" automatically [REF-15].
- **Cross-platform:** `dotnet-trace collect -p <pid> --profile gc-collect`, analyze with PerfView/TraceEvent or `dotnet-trace report`.
- Relevant events: `GCStart_V2`, `GCEnd_V2`, `GCSuspendEEBegin_V1`, `GCRestartEEEnd_V1`, `GCHeapStats_V2`, `GCAllocationTick_V4` (per ~100 KB allocated — also your allocation-rate meter for §7 audits).

### 6.2.2 In-process: `EventListener` harness

Useful in qualification rigs where you want pauses correlated with order-flow timestamps in the same log. Caveat: `EventListener` dispatch itself allocates; keep it on a non-critical thread and accept that it perturbs the measurement slightly (or use it only in qualification, never production).

```csharp
// GcPauseListener.cs — .NET 7+. Records every STW window into an HdrHistogram.
using System.Diagnostics.Tracing;
using HdrHistogram;

public sealed class GcPauseListener : EventListener
{
    private readonly LongHistogram _pausesMicros =
        new(highestTrackableValue: 10_000_000, numberOfSignificantValueDigits: 3);
    private long _suspendStartTicks;
    private int _currentGen = -1;
    private readonly LongHistogram[] _byGen =
        Enumerable.Range(0, 3)
                  .Select(_ => new LongHistogram(10_000_000, 3)).ToArray();

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Informational, (EventKeywords)0x1 /* GC */);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        switch (e.EventName)
        {
            case "GCStart_V2":
                _currentGen = Convert.ToInt32(e.Payload![1]); // "Depth" = condemned gen
                break;
            case "GCSuspendEEBegin_V1":
                Volatile.Write(ref _suspendStartTicks, Stopwatch.GetTimestamp());
                break;
            case "GCRestartEEEnd_V1":
                long start = Volatile.Read(ref _suspendStartTicks);
                if (start == 0) return;
                long micros = (Stopwatch.GetTimestamp() - start) * 1_000_000
                              / Stopwatch.Frequency;
                _pausesMicros.RecordValue(Math.Max(1, micros));
                if ((uint)_currentGen < 3) _byGen[_currentGen].RecordValue(Math.Max(1, micros));
                break;
        }
    }

    public void Dump(TextWriter w)
    {
        w.WriteLine($"All pauses  p50={_pausesMicros.GetValueAtPercentile(50)}us " +
                    $"p99={_pausesMicros.GetValueAtPercentile(99)}us " +
                    $"p99.9={_pausesMicros.GetValueAtPercentile(99.9)}us " +
                    $"max={_pausesMicros.GetMaxValue()}us n={_pausesMicros.TotalCount}");
        for (int g = 0; g < 3; g++)
            w.WriteLine($"gen{g}: p50={_byGen[g].GetValueAtPercentile(50)}us " +
                        $"p99.9={_byGen[g].GetValueAtPercentile(99.9)}us " +
                        $"max={_byGen[g].GetMaxValue()}us n={_byGen[g].TotalCount}");
    }
}
```

Supporting APIs worth wiring into telemetry regardless of method:

- `GC.GetTotalPauseDuration()` (.NET 7+) — cumulative STW time since process start; cheap to poll per second and difference.
- `GC.CollectionCount(g)` — per-generation GC counts; cadence sanity check.
- `GC.GetGCMemoryInfo()` — per-GC `PauseDurations`, promoted bytes, fragmentation, heap size after.

> ⚠ Payload index for `Depth` in `GCStart_V2` and the exact event/field names should be verified against your runtime's event manifest — these are stable in practice but are runtime implementation details [REF-15].

### 6.2.3 Measurement discipline

1. **Pin the harness configuration**: same GC mode, heap count, `TieredCompilation` settings, core isolation, and NUMA layout as production. Tiered JIT recompilation early in a run produces non-GC pauses that pollute the first minutes — discard a warm-up window or disable tiering for qualification.
2. **Drive realistic allocation**: replay captured market data through the actual codec/strategy path. Synthetic uniform allocation understates survivor-driven pauses (§6.3).
3. **Report percentiles from histograms (HdrHistogram), never averages.** A mean gen0 pause is meaningless; HFT risk lives at p99.9–max.
4. **Avoid coordinated omission** [REF-16]: if you measure *request* latency, issue load on a fixed schedule and charge a stalled interval its full intended-start-to-completion time; otherwise a 40 ms gen2 pause hides as one slow sample instead of ~40 ms of queued misery.
5. **Record run metadata** (runtime version, GC env vars, `GC.GetGCMemoryInfo` snapshots) alongside histograms; pause data without configuration is unactionable.

## 6.3 Cost model: what each pause is proportional to

| GC type | STW work is proportional to… | NOT proportional to… |
|---|---|---|
| Gen0 | Bytes/objects that **survive** gen0 (copy cost) + stack/handle root scan + suspension | Bytes allocated in gen0 (dead objects are free) |
| Gen1 | Survivors of gen0+gen1 + dirty-card scan of gen2→ephemeral references | Total heap size |
| Gen2 blocking | **Entire live set** (mark) + relocation if compacting | Dead bytes |
| BGC STW windows | Root scan + bookkeeping (small, bounded) | Live set (that cost moved to concurrent phases) |
| LOH (with gen2) | Free-list sweep; compaction only if requested (§3.3) | — |
| Suspension | Thread count; worst single thread's distance to a safepoint | Heap size |

Two derived quantities you should compute for your own system:

**Gen0 frequency** ≈ allocation rate ÷ gen0 budget (per heap). Example: 200 MB/s steady allocation against a 64 MB effective gen0 budget ⇒ ~3 gen0 GCs/sec ⇒ at 150 µs each, ~0.05 % pause time — *but* a ~3 Hz chance that any given market event lands inside a pause window. Frequency, not just duration, is the HFT risk metric.

**Gen2 blocking pause** ≈ live-set GB × (mark+compact throughput)⁻¹. Practitioner-reported effective throughputs cluster around **tens of ms per GB of live data for mark-only**, and substantially more when compacting with heavy reference density [REF-02][REF-05]. A 4 GB live set can plausibly cost 100–400+ ms blocking. This is the number that ends an HFT career; the entire architecture must ensure it never happens intraday (§8, strategy S-1/S-7).

## 6.4 Indicative pause ranges (64-bit, Server GC, modern x86 server, .NET 7/8)

All values are **synthesized indicative ranges** per the provenance note above. "Typical" ≈ p50–p90 of a well-behaved service; "tail" ≈ p99.9-to-max pathologies and their causes.

| Event | Typical | Tail | Tail drivers |
|---|---|---|---|
| Thread suspension (component of every pause) | 10–100 µs | 1–10+ ms | Many threads; a thread in a long non-interruptible stretch; OS descheduling of a to-be-suspended thread on a contended core |
| Gen0 GC | 50 µs – 1 ms | 2–10 ms | High survivor volume (mid-life crisis objects, §7.9); heavy pinning on ≤ .NET 6 segments (§5.3); large stacks/handle tables to scan |
| Gen1 GC | 100 µs – 3 ms | 5–20 ms | Big gen1 survivor waves; very large gen2 dirty-card surface |
| Gen2 **blocking** (incl. compaction) | 10–100 ms per GB live | 100 ms – multiple seconds | Large live sets; LOH compaction requested; high reference density |
| BGC: each of the two STW windows | 0.5–5 ms | 10–20 ms | Root volume; suspension tail as above |
| BGC: total wall-clock (concurrent) | 100 ms – seconds | — | Live-set size; competes for CPU/memory bandwidth — raises *jitter*, not pauses, on saturated cores (§4.4) |
| Ephemeral GCs forced during BGC | as gen0/gen1 above | + alloc-wait stalls | Allocation racing the concurrent sweep |
| LOH allocation triggering gen2/BGC | n/a (it's the trigger) | inherits gen2/BGC cost | Any ≥ 85,000 B allocation on the hot path (§3.2, §7.7) |

Reference points from public sources, for calibration:

- The .NET GC team's design targets put **ephemeral GC pauses in the sub-millisecond-to-few-millisecond band** for healthy workloads, with gen0 commonly well under 1 ms on server hardware [REF-02][REF-11].
- Kokosa's measurements and the PerfView GCStats corpus show **full blocking gen2 on multi-GB live sets ranging from tens of ms into seconds**, dominated by live-set mark/copy [REF-05][REF-15].
- ASP.NET/TechEmpower-class telemetry repeatedly demonstrates that **allocation-rate reduction shifts the entire pause histogram left** more reliably than any GC-mode change — the empirical underpinning of §7/§8's priority ordering [REF-04][REF-17].

## 6.5 Worked scenario profiles

To make the ranges concrete, three configurations of the same hypothetical market-data + order-entry process (8 isolated cores, 4 GC heaps, 1.5 GB live set), with expected histogram shapes derived from the §6.3 model:

**Profile A — naïve idiomatic C#** (LINQ on hot path, string-keyed lookups, async per message; ~400 MB/s allocation, frequent 100 KB+ buffers):
- gen0 every ~150 ms; survivor-heavy ⇒ gen0 p50 ~0.4 ms, p99.9 ~5 ms.
- LOH churn drags gen2/BGC every few minutes; BGC STW pairs ~2–4 ms; occasional blocking gen2 if budgets misfire ⇒ **max pause tens-to-hundreds of ms**. Unacceptable.

**Profile B — settings-only tuning** (Server GC, `SustainedLowLatency`, BGC, bigger gen0 budget; same code):
- gen0 cadence drops ~3–5×; gen2 becomes background-only intraday ⇒ max pause now the BGC STW pair + ephemeral tail, **single-digit ms p-max**, but with BGC CPU-bandwidth jitter on saturated cores. Better; still unfit for single-digit-µs tick-to-trade.

**Profile C — zero-alloc hot path** (§8 disciplines: pooled buffers/POH, struct messages, no LINQ/closures/boxing/async on hot path; allocation ~0 intraday, gen2 forced at session boundaries):
- **No intraday GCs at all** in steady state; pause histogram over the trading day is empty except the warm-up window. Residual jitter is OS/scheduler territory, not GC. This is the production pattern reported by low-latency .NET shops [REF-18][REF-19] and the target state for FablePool's later milestones.

The qualitative gap between B and C — *fewer, smaller pauses* vs *no pauses* — is the central empirical finding of this survey.

## 6.6 Section conclusions

1. Measure pauses as **SuspendEE→RestartEE windows from runtime events**, percentile-ranked via HdrHistogram, with coordinated omission handled; everything else is folklore.
2. Pause cost follows **survivors and live set, not allocation volume** — so the two levers are (a) allocate less, (b) keep what survives small and stable.
3. Ephemeral pauses on healthy modern .NET are **sub-millisecond typical with multi-ms tails**; blocking gen2 on real live sets is **tens-to-hundreds of ms** and must be architecturally excluded from trading hours, not merely made rare.
4. Tuning settings (Profile B) buys roughly an order of magnitude; **only allocation discipline (Profile C) buys silence** — the quantitative justification for the mitigation hierarchy in §8.
5. All indicative figures here require **site-specific qualification** with the harness above on production-identical hardware; that run is scoped into FablePool Milestone 2.

---
*Citations: [REF-02] .NET GC fundamentals docs; [REF-04] Stephen Toub performance retrospectives; [REF-05] Kokosa, "Pro .NET Memory Management"; [REF-11] Stephens, GC design notes; [REF-15] PerfView/TraceEvent GCStats documentation; [REF-16] Tene, "How NOT to Measure Latency"; [REF-17] BenchmarkDotNet/MemoryDiagnoser methodology; [REF-18][REF-19] practitioner reports from low-latency .NET shops. Full annotations in §9.*
