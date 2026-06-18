using System;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace ZeroAlloc.Tests;

/// <summary>
/// Test helper that asserts a code path performs zero managed (GC heap) allocations
/// in steady state, using <see cref="GC.GetAllocatedBytesForCurrentThread"/>.
/// </summary>
/// <remarks>
/// The action is executed several times before measurement so that JIT compilation,
/// tiering and any one-time lazy initialization inside the measured code complete
/// before bytes are counted. The measured region runs entirely on the calling thread,
/// so background JIT activity does not pollute the counter (it allocates natively,
/// not on this thread's GC budget).
/// </remarks>
public static class AllocationAssert
{
    /// <summary>
    /// Asserts that <paramref name="action"/> allocates zero managed bytes per invocation
    /// after warm-up.
    /// </summary>
    /// <param name="action">The code path under audit. Must be repeatable.</param>
    /// <param name="warmupIterations">Invocations performed before measurement begins.</param>
    /// <param name="measuredIterations">Invocations performed inside the measured window.</param>
    /// <param name="label">Optional label included in the failure message.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Zero(Action action, int warmupIterations = 64, int measuredIterations = 256,
        string label = "code path")
    {
        ArgumentNullException.ThrowIfNull(action);

        for (int i = 0; i < warmupIterations; i++)
        {
            action();
        }

        // Drain any pending finalization noise before measuring.
        GC.Collect();
        GC.WaitForPendingFinalizers();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < measuredIterations; i++)
        {
            action();
        }
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(delta, Is.EqualTo(0),
            $"Expected zero allocations for {label}, but {delta} bytes were allocated " +
            $"across {measuredIterations} iterations ({(double)delta / measuredIterations:F1} B/op).");
    }

    /// <summary>
    /// Asserts that <paramref name="action"/> allocates at most <paramref name="maxBytesPerOp"/>
    /// managed bytes per invocation after warm-up. Useful for auditing intentionally-allocating
    /// cold paths.
    /// </summary>
    /// <param name="maxBytesPerOp">Maximum tolerated managed bytes per invocation.</param>
    /// <param name="action">The code path under audit.</param>
    /// <param name="warmupIterations">Invocations performed before measurement begins.</param>
    /// <param name="measuredIterations">Invocations performed inside the measured window.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void AtMost(long maxBytesPerOp, Action action,
        int warmupIterations = 64, int measuredIterations = 256)
    {
        ArgumentNullException.ThrowIfNull(action);

        for (int i = 0; i < warmupIterations; i++)
        {
            action();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < measuredIterations; i++)
        {
            action();
        }
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        double perOp = (double)delta / measuredIterations;

        Assert.That(perOp, Is.LessThanOrEqualTo(maxBytesPerOp),
            $"Expected at most {maxBytesPerOp} B/op, measured {perOp:F1} B/op.");
    }
}
