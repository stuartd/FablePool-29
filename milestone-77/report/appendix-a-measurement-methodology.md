# Appendix A — GC Pause & Jitter Measurement Methodology (Reproducible Harness)

> Companion to Section 06 (Measured Pause Characteristics). Section 06 reports the
> numbers; this appendix specifies *exactly how to reproduce them* on your own
> hardware, because pause distributions are heavily machine-, OS-, and
> version-dependent and published figures (including ours) must never be trusted for
> a specific deployment without local re-measurement.
>
> All code targets .NET 8 and uses only BCL APIs. No NuGet dependencies are required
> for the core harness; optional tooling (dotnet-trace, PerfView, BenchmarkDotNet) is
> noted where relevant.

---

## A.1 What to measure, and why each measurement exists

| Measurement | Question it answers | Primary tool |
|---|---|---|
| GC suspension intervals (SuspendEE→RestartEE) | "How long were my threads actually stopped?" | In-process `EventListener` (§A.2) or ETW/EventPipe (§A.7) |
| Per-GC pause durations & generations | "Which generation/kind caused each pause?" | `GC.GetGCMemoryInfo(GCKind)` (§A.3) |
| Wall-clock jitter on an isolated spinning thread | "What does the *application* experience, from all causes (GC + OS + JIT)?" | Jitter loop (§A.4) |
| Allocation rate per thread | "How fast am I feeding the GC?" | `GC.GetAllocatedBytesForCurrentThread` (§A.3.3) |
| Attribution split GC vs non-GC | "Is this tail event the GC's fault?" | Correlate §A.2 timestamps with §A.4 gaps |

The crucial methodological point: **measure suspension, not collection duration.** A
background Gen2 GC may run for 80 ms while only suspending threads for two windows of
50–300 µs each (Section 04 §4.4). Reporting "an 80 ms GC" as an 80 ms pause is the
most common error in GC latency literature.

## A.2 In-process suspension monitor (EventListener)

The CLR's `Microsoft-Windows-DotNETRuntime` event source emits
`GCSuspendEEBegin`/`GCRestartEEEnd` pairs that bracket every execution-engine
suspension, including the brief suspensions inside background GC. Subscribing
in-process via `EventListener` requires no external tooling and works identically on
Windows and Linux.

```csharp
// GcSuspensionMonitor.cs — .NET 8, no external dependencies.
// Records every execution-engine suspension interval into a fixed,
// preallocated log-2 histogram. The monitor itself allocates only during
// construction and (unavoidably, inside the runtime) per dispatched event;
// it is a development/soak tool, not a production hot-path component.

using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Threading;

public sealed class GcSuspensionMonitor : EventListener
{
    // Keyword 0x1 = GC on Microsoft-Windows-DotNETRuntime.
    private const EventKeywords GcKeyword = (EventKeywords)0x1;
    private const string RuntimeSource = "Microsoft-Windows-DotNETRuntime";

    // Histogram: bucket i covers [2^i, 2^(i+1)) microseconds, i in [0, 30].
    private readonly long[] _bucketsUs = new long[31];
    private long _count, _maxUs, _totalUs;
    private long _suspendStartTimestamp; // Stopwatch ticks; 0 = not in suspension
    private EventSource? _runtime;

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == RuntimeSource)
        {
            _runtime = source;
            EnableEvents(source, EventLevel.Informational, GcKeyword);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        // Event names carry version suffixes that vary across runtime versions
        // (e.g. GCSuspendEEBegin_V1). Match on prefix to stay version-tolerant.
        string? name = e.EventName;
        if (name is null) return;

        if (name.StartsWith("GCSuspendEEBegin", StringComparison.Ordinal))
        {
            Interlocked.Exchange(ref _suspendStartTimestamp, Stopwatch.GetTimestamp());
        }
        else if (name.StartsWith("GCRestartEEEnd", StringComparison.Ordinal))
        {
            long start = Interlocked.Exchange(ref _suspendStartTimestamp, 0);
            if (start == 0) return; // restart without observed begin (startup race)

            long elapsedTicks = Stopwatch.GetTimestamp() - start;
            long us = elapsedTicks * 1_000_000 / Stopwatch.Frequency;
            Record(us);
        }
    }

    private void Record(long us)
    {
        int bucket = us <= 1 ? 0 : Math.Min(30, 63 - System.Numerics.BitOperations
            .LeadingZeroCount((ulong)us));
        Interlocked.Increment(ref _bucketsUs[bucket]);
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _totalUs, us);
        long observedMax;
        while (us > (observedMax = Interlocked.Read(ref _maxUs)) &&
               Interlocked.CompareExchange(ref _maxUs, us, observedMax) != observedMax)
        { /* retry */ }
    }

    public void Report(System.IO.TextWriter w)
    {
        long count = Interlocked.Read(ref _count);
        w.WriteLine($"EE suspensions observed : {count}");
        if (count == 0) return;
        w.WriteLine($"Total suspension time   : {Interlocked.Read(ref _totalUs)} us");
        w.WriteLine($"Max single suspension   : {Interlocked.Read(ref _maxUs)} us");
        w.WriteLine("Histogram (us bucket -> count):");
        for (int i = 0; i < _bucketsUs.Length; i++)
        {
            long c = Interlocked.Read(ref _bucketsUs[i]);
            if (c == 0) continue;
            w.WriteLine($"  [{1L << i,10} .. {(1L << (i + 1)) - 1,10}] : {c}");
        }
    }

    public override void Dispose()
    {
        if (_runtime is not null) DisableEvents(_runtime);
        base.Dispose();
    }
}
```

**Caveats (verify against your runtime version):**
- Event name suffixes (`_V1`, `_V2`) and payload layouts have changed across runtime
  releases; the prefix-match above is deliberately tolerant. If you need payload
  fields (GC number, reason, generation), inspect `e.Payload` defensively.
- `OnEventWritten` runs on runtime threads; keep it allocation-light (the code above
  performs no managed allocation after construction).
- Suspension intervals include *all* EE suspensions with GC keyword causes; pair the
  data with §A.3 to attribute each suspension to a GC kind.

## A.3 Per-GC accounting via `GC.GetGCMemoryInfo`

### A.3.1 Pause durations by GC kind (.NET 5+)

```csharp
// GcInfoSampler.cs — poll-based per-GC accounting; suitable for a low-priority
// monitoring thread sampling once per second.

using System;

public static class GcInfoSampler
{
    public static void PrintLast()
    {
        foreach (GCKind kind in new[]
                 { GCKind.Ephemeral, GCKind.FullBlocking, GCKind.Background })
        {
            GCMemoryInfo info = GC.GetGCMemoryInfo(kind);
            if (info.Index == 0) continue; // no GC of this kind yet

            Console.WriteLine(
                $"{kind,-12} #{info.Index,-6} gen={info.Generation} " +
                $"compacted={info.Compacted} concurrent={info.Concurrent} " +
                $"heap={info.HeapSizeBytes / 1024 / 1024} MB " +
                $"promoted={info.PromotedBytes / 1024} KB " +
                $"pause%={info.PauseTimePercentage:F3}");

            // PauseDurations: for background GCs this contains the (up to) two
            // suspension windows, NOT the full concurrent phase duration.
            foreach (TimeSpan pause in info.PauseDurations)
                Console.WriteLine($"    pause: {pause.TotalMicroseconds:F1} us");
        }
    }
}
```

`PauseDurations` is the runtime's own ground truth and should agree (within
scheduling noise) with the EventListener intervals from §A.2. If they disagree by more
than ~20%, suspect the monitor thread is being descheduled — fix the environment
(§A.5) before trusting any numbers.

### A.3.2 Session counters

Sample at session start and end; deltas are the session totals:

```csharp
public readonly record struct GcSessionSnapshot(
    int Gen0, int Gen1, int Gen2, long TotalAllocated, TimeSpan TotalPause)
{
    public static GcSessionSnapshot Take() => new(
        GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
        GC.GetTotalAllocatedBytes(precise: false),
        GC.GetTotalPauseDuration()); // .NET 7+

    public void PrintDelta(GcSessionSnapshot start)
    {
        Console.WriteLine($"Gen0 GCs : {Gen0 - start.Gen0}");
        Console.WriteLine($"Gen1 GCs : {Gen1 - start.Gen1}");
        Console.WriteLine($"Gen2 GCs : {Gen2 - start.Gen2}");
        Console.WriteLine($"Allocated: {(TotalAllocated - start.TotalAllocated) / 1024 / 1024} MB");
        Console.WriteLine($"Paused   : {(TotalPause - start.TotalPause).TotalMilliseconds:F3} ms");
    }
}
```

> `GC.GetTotalPauseDuration()` was added in .NET 7; on .NET 6 fall back to summing
> `GCMemoryInfo.PauseDurations` per observed GC index.

### A.3.3 Per-thread allocation rate

`GC.GetAllocatedBytesForCurrentThread()` is exact, cheap (a TLS read of the
allocation-context counter), and safe to call on hot threads. The CI enforcement
harness in Appendix C is built on it. For a live rate, have each hot thread publish
its value to a padded slot read by the monitor thread — never have the monitor call
into other threads.

## A.4 Wall-clock jitter loop (application-experienced latency)

GC suspension is only one jitter source. This loop measures *every* cause — GC, OS
scheduling, SMIs, JIT tier-up, page faults — as the application perceives them, by
spinning on an isolated core and recording every gap above a threshold:

```csharp
// JitterRecorder.cs — run on a dedicated, isolated core (see §A.5).
// Zero allocation in steady state; results stored in a preallocated ring.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

public sealed class JitterRecorder
{
    private readonly long[] _gapTimestamps;
    private readonly long[] _gapMicros;
    private readonly long _thresholdTicks;
    private int _next;
    public volatile bool Stop;

    public JitterRecorder(int capacity = 1 << 16, double thresholdMicroseconds = 2.0)
    {
        _gapTimestamps = new long[capacity];
        _gapMicros = new long[capacity];
        _thresholdTicks = (long)(thresholdMicroseconds * Stopwatch.Frequency / 1e6);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Run()
    {
        long prev = Stopwatch.GetTimestamp();
        while (!Stop)
        {
            long now = Stopwatch.GetTimestamp();
            long gap = now - prev;
            if (gap > _thresholdTicks)
            {
                int i = _next & (_gapTimestamps.Length - 1);
                _gapTimestamps[i] = now;
                _gapMicros[i] = gap * 1_000_000 / Stopwatch.Frequency;
                _next++;
            }
            prev = now;
        }
    }

    public void Report(System.IO.TextWriter w)
    {
        int n = Math.Min(_next, _gapTimestamps.Length);
        w.WriteLine($"Gaps over threshold: {_next} (showing last {n})");
        for (int k = 0; k < n; k++)
            w.WriteLine($"  t={_gapTimestamps[k]} gap={_gapMicros[k]} us");
    }
}
```

**Attribution procedure:** export the gap timestamps and the suspension intervals from
§A.2 (both are `Stopwatch.GetTimestamp()` based, hence directly comparable), and
classify each gap as GC-caused if it overlaps a suspension interval. Everything else
is environment jitter. In our reference runs (Section 06), an untuned Linux host shows
*more* non-GC gaps over 50 µs than GC gaps until core isolation is applied — which is
exactly why §A.5 precedes any conclusions.

## A.5 Environment checklist (do this before believing any number)

### Both platforms
- Dedicated physical cores for hot threads; pin with `Thread` affinity
  (`Process.GetCurrentProcess().ProcessorAffinity` on Windows, or
  `sched_setaffinity` via P/Invoke / `taskset` on Linux).
- Disable CPU frequency scaling and deep C-states (BIOS + OS governor `performance`).
- Disable SMT siblings of isolated cores, or leave the sibling idle and unschedulable.
- Run the benchmark process at elevated priority; do **not** use hard real-time
  priority for GC threads (it can invert against the suspension handshake).
- ≥ 30 minutes of warm load before recording; discard the warmup window.

### Linux
- Kernel parameters: `isolcpus=`, `nohz_full=`, `rcu_nocbs=` for the trading cores.
- Steer NIC and timer IRQs away from isolated cores (`/proc/irq/*/smp_affinity`).
- Consider `transparent_hugepage=never` (THP compaction stalls) and verify
  `vm.stat_interval`, NUMA placement (`numactl --cpunodebind --membind`).

### Windows
- CPU Sets (`SetProcessDefaultCpuSets`) or affinity masks to reserve cores; align
  the GC heap affinity mask with them (Appendix B §B.3).
- High Performance / Ultimate power plan; disable core parking.
- Disable unnecessary services and scheduled tasks on the host.

### Runtime
- Apply the Tier-1 GC configuration (Section 10 §10.2, Appendix B) — measurements
  under Workstation GC are not comparable to a Server-GC deployment.
- Control JIT-induced jitter: publish ReadyToRun (`<PublishReadyToRun>true</>`), and
  either run a deterministic warmup of all hot methods or set
  `DOTNET_TieredCompilation=0` for the measurement run (note: disabling tiering
  changes steady-state codegen too — measure both ways once and document which
  configuration production uses).

## A.6 Driving load: a representative allocation workload

Synthetic max-allocation loops produce GC behavior unlike trading systems. Drive the
harness with a workload shaped like the real one: replayed market data at recorded
arrival times, the actual book-building and signal code, and the actual message sizes.
At minimum, a synthetic driver should mix:
- High-rate small short-lived allocations (decoded message objects) — exercises Gen0.
- A mid-rate population with intermediate lifetime (in-flight orders) — exercises
  Gen1 and promotion behavior.
- Occasional large allocations (snapshot arrays > 85,000 bytes) — exercises LOH.

Run lengths must cover at least one full Gen2/background cycle at the measured
allocation rate — for a well-tuned system that can mean hours; a 60-second run that
observed zero Gen2 GCs proves nothing about Gen2 pauses.

## A.7 External tooling cross-check

In-process measurement should be cross-validated once per environment with an
out-of-process collector (which cannot be perturbed by in-process suspensions):

| Tool | Command | Use |
|---|---|---|
| dotnet-trace | `dotnet-trace collect -p <pid> --profile gc-verbose` | EventPipe capture incl. AllocationTick; open in PerfView/Visual Studio |
| dotnet-counters | `dotnet-counters monitor -p <pid> System.Runtime` | Live alloc rate, GC counts, % time in GC |
| dotnet-gcdump | `dotnet-gcdump collect -p <pid>` | Heap census by type (who is keeping objects alive) |
| PerfView (Windows) | `PerfView /GCCollectOnly /AcceptEULA collect` | Lowest-overhead ETW GC capture; GCStats view gives per-GC suspend MSec — the canonical pause report |
| EventPipe in CI | `DOTNET_EnableEventPipe=1` + config | Headless capture on Linux build agents |

PerfView's **GCStats** view ("Pause MSec" column) is the reference against which the
§A.2 monitor should be validated: per-GC agreement within ~10% is expected.

> Tool invocation syntax above targets dotnet-trace/counters/gcdump 8.x and PerfView
> 3.x; verify flags against the versions you install — these CLIs evolve.

## A.8 Reporting format used in Section 06

All Section 06 tables follow this schema so future re-runs are comparable:

```
machine: <cpu model, core count, RAM, NUMA layout>
os:      <distro/build, kernel params or power plan>
runtime: <.NET version, Server/Workstation, concurrent, heap count, affinity,
          regions/segments, DATAS on/off, latency mode>
workload:<driver description, alloc rate B/s, run length, warmup discarded>
results: per-GC-kind pause histogram (p50/p90/p99/p99.9/max, count),
         jitter-loop gap histogram, GC-attributed fraction of gaps > 50 us
```

A result that omits any line of this schema is not reproducible and was not accepted
into Section 06.

---

*End of Appendix A.*
