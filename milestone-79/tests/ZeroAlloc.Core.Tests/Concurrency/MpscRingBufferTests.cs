using System;
using System.Threading;
using NUnit.Framework;
using ZeroAlloc.Concurrency;

namespace ZeroAlloc.Tests.Concurrency;

[TestFixture]
public sealed class MpscRingBufferTests
{
    [Test]
    public void Constructor_RoundsCapacityUpToPowerOfTwo()
    {
        Assert.That(new MpscRingBuffer<int>(2).Capacity, Is.EqualTo(2));
        Assert.That(new MpscRingBuffer<int>(5).Capacity, Is.EqualTo(8));
        Assert.That(new MpscRingBuffer<int>(4096).Capacity, Is.EqualTo(4096));
    }

    [Test]
    public void Constructor_RejectsInvalidCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new MpscRingBuffer<int>(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new MpscRingBuffer<int>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new MpscRingBuffer<int>((1 << 30) + 1));
    }

    [Test]
    public void Enqueue_ReturnsFalseWhenFull()
    {
        var rb = new MpscRingBuffer<int>(4);
        for (int i = 0; i < 4; i++)
        {
            Assert.That(rb.TryEnqueue(i), Is.True);
        }
        Assert.That(rb.TryEnqueue(99), Is.False);
        Assert.That(rb.Count, Is.EqualTo(4));
    }

    [Test]
    public void Dequeue_ReturnsFalseWhenEmpty()
    {
        var rb = new MpscRingBuffer<int>(4);
        Assert.That(rb.TryDequeue(out int item), Is.False);
        Assert.That(item, Is.EqualTo(0));
    }

    [Test]
    public void SingleThreaded_FifoOrderPreservedAcrossWraparound()
    {
        var rb = new MpscRingBuffer<int>(8);
        for (int round = 0; round < 100; round++)
        {
            for (int i = 0; i < 6; i++)
            {
                Assert.That(rb.TryEnqueue(round * 6 + i), Is.True);
            }
            for (int i = 0; i < 6; i++)
            {
                Assert.That(rb.TryDequeue(out int v), Is.True);
                Assert.That(v, Is.EqualTo(round * 6 + i));
            }
        }
    }

    [Test]
    public void DequeueBatch_DrainsReadyItems()
    {
        var rb = new MpscRingBuffer<int>(16);
        for (int i = 0; i < 10; i++) rb.TryEnqueue(i);

        Span<int> batch = stackalloc int[6];
        Assert.That(rb.DequeueBatch(batch), Is.EqualTo(6));
        for (int i = 0; i < 6; i++) Assert.That(batch[i], Is.EqualTo(i));

        Span<int> rest = stackalloc int[16];
        Assert.That(rb.DequeueBatch(rest), Is.EqualTo(4));
    }

    [Test]
    public void Concurrent_MultipleProducers_AllItemsArriveExactlyOnce()
    {
        const int producers = 4;
        const int perProducer = 250_000;
        const int total = producers * perProducer;

        var rb = new MpscRingBuffer<long>(2048);
        long consumedSum = 0;
        int consumedCount = 0;
        Exception? consumerError = null;

        // Each producer p enqueues values p * perProducer + i, so every value is unique
        // and the grand total has a closed form we can verify.
        var consumer = new Thread(() =>
        {
            try
            {
                var spinner = new SpinWait();
                while (consumedCount < total)
                {
                    if (rb.TryDequeue(out long v))
                    {
                        consumedSum += v;
                        consumedCount++;
                        spinner = default;
                    }
                    else
                    {
                        spinner.SpinOnce();
                    }
                }
            }
            catch (Exception ex)
            {
                consumerError = ex;
            }
        })
        { IsBackground = true, Name = "mpsc-consumer" };

        var producerThreads = new Thread[producers];
        for (int p = 0; p < producers; p++)
        {
            int producerId = p;
            producerThreads[p] = new Thread(() =>
            {
                var spinner = new SpinWait();
                for (long i = 0; i < perProducer; i++)
                {
                    long value = (long)producerId * perProducer + i;
                    while (!rb.TryEnqueue(value))
                    {
                        spinner.SpinOnce();
                    }
                    spinner = default;
                }
            })
            { IsBackground = true, Name = $"mpsc-producer-{p}" };
        }

        consumer.Start();
        foreach (var t in producerThreads) t.Start();
        foreach (var t in producerThreads)
        {
            Assert.That(t.Join(TimeSpan.FromSeconds(60)), Is.True, $"{t.Name} did not finish");
        }
        Assert.That(consumer.Join(TimeSpan.FromSeconds(60)), Is.True, "consumer did not finish");
        Assert.That(consumerError, Is.Null, consumerError?.ToString() ?? string.Empty);

        long n = total;
        long expectedSum = n * (n - 1) / 2; // sum of 0..total-1, values are a permutation of that range
        Assert.That(consumedCount, Is.EqualTo(total));
        Assert.That(consumedSum, Is.EqualTo(expectedSum), "every value must arrive exactly once");
    }

    [Test]
    public void ReferenceTypes_AreClearedOnDequeue_NoLifetimeExtension()
    {
        var rb = new MpscRingBuffer<object>(4);
        WeakReference weak = AllocateAndCycle(rb);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.That(weak.IsAlive, Is.False);
    }

    private static WeakReference AllocateAndCycle(MpscRingBuffer<object> rb)
    {
        object payload = new byte[128];
        var weak = new WeakReference(payload);
        rb.TryEnqueue(payload);
        rb.TryDequeue(out _);
        return weak;
    }

    [Test]
    public void SteadyState_EnqueueDequeue_IsAllocationFree()
    {
        var rb = new MpscRingBuffer<long>(64);
        AllocationAssert.Zero(() =>
        {
            for (int i = 0; i < 32; i++)
            {
                rb.TryEnqueue(i);
            }
            for (int i = 0; i < 32; i++)
            {
                rb.TryDequeue(out _);
            }
        }, label: "MpscRingBuffer enqueue/dequeue");
    }
}
