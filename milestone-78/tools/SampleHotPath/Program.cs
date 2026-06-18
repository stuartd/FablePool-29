// SampleHotPath — a minimal, deliberately allocation-free steady-state workload
// used to demonstrate and CI-test the AllocationGate tool end to end.
//
// Behaviour:
//   1. Warmup phase: pre-allocates all state, exercises the hot kernel to force
//      JIT compilation, performs a final full blocking GC, and switches to
//      SustainedLowLatency mode. Warmup is ALLOWED to allocate.
//   2. Emits the contract marker event: FablePool-Contract / WarmupComplete.
//   3. Hot phase: runs a struct/array-only kernel (EWMA over a pre-allocated
//      price ring) for --duration seconds, allocating nothing.
//   4. Emits HotPhaseComplete and prints a summary (allocations after the hot
//      phase ends are irrelevant to the gate's measurement window).
//
// Any managed allocation accidentally introduced into step 3 will surface as
// GCAllocationTick events and fail the gate — that is the point of this sample.

using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime;

namespace FablePool.Tools.SampleHotPath;

[EventSource(Name = "FablePool-Contract")]
internal sealed class ContractEventSource : EventSource
{
    public static readonly ContractEventSource Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void WarmupComplete() => WriteEvent(1);

    [Event(2, Level = EventLevel.Informational)]
    public void HotPhaseComplete(long iterations) => WriteEvent(2, iterations);
}

internal static class Program
{
    // Sinks defeat dead-code elimination without allocating.
    private static double s_sink;

    private static int Main(string[] args)
    {
        double durationSeconds = 30;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--duration" && i + 1 < args.Length)
                durationSeconds = double.Parse(args[++i]);
        }

        const int RingSize = 1 << 16; // 65,536 slots, power of two for mask indexing

        // ---- Warmup phase (allocation permitted) -------------------------------
        var prices = new double[RingSize];
        var sizes = new long[RingSize];
        var rng = new Random(42);
        double px = 100.0;
        for (int i = 0; i < RingSize; i++)
        {
            px += (rng.NextDouble() - 0.5) * 0.01;
            prices[i] = px;
            sizes[i] = 1 + rng.Next(1, 500);
        }

        // Exercise the kernel so it is fully JIT-compiled before the marker.
        double warmupResult = 0;
        for (int pass = 0; pass < 64; pass++)
            warmupResult += RunKernel(prices, sizes, RingSize - 1, iterations: RingSize);
        s_sink = warmupResult;

        // Construct everything used after the marker BEFORE the marker.
        var clock = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(durationSeconds);

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        Console.WriteLine($"[sample] warmup complete; entering hot phase for {durationSeconds:0.#}s");
        ContractEventSource.Log.WarmupComplete();

        // ---- Hot phase (zero allocations) --------------------------------------
        clock.Restart();
        long iterationsDone = 0;
        double hotResult = 0;
        while (true)
        {
            hotResult += RunKernel(prices, sizes, RingSize - 1, iterations: 4096);
            iterationsDone += 4096;
            if (clock.Elapsed >= budget) // Stopwatch.Elapsed is a struct; no allocation
                break;
        }
        s_sink = hotResult;

        // ---- Cooldown (allocation permitted again) ------------------------------
        ContractEventSource.Log.HotPhaseComplete(iterationsDone);
        GCSettings.LatencyMode = GCLatencyMode.Interactive;
        Console.WriteLine($"[sample] hot phase done: {iterationsDone:N0} iterations, checksum={s_sink:F6}");
        Console.WriteLine($"[sample] GC counts during process lifetime: gen0={GC.CollectionCount(0)} " +
                          $"gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)}");
        return 0;
    }

    /// <summary>
    /// Allocation-free kernel: volume-weighted EWMA over a pre-allocated price ring.
    /// Operates exclusively on locals and pre-allocated arrays.
    /// </summary>
    private static double RunKernel(double[] prices, long[] sizes, int mask, int iterations)
    {
        double ewma = prices[0];
        const double alpha = 0.0625;
        int idx = 0;
        for (int i = 0; i < iterations; i++)
        {
            idx = (idx + 1) & mask;
            double weighted = prices[idx] * sizes[idx];
            double norm = weighted / sizes[idx];
            ewma += alpha * (norm - ewma);
            // Simulated signal decision on pure value types:
            if (ewma > norm) ewma -= 1e-9; else ewma += 1e-9;
        }
        return ewma;
    }
}
