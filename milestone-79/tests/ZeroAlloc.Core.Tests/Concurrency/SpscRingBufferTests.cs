using System;
using System.Threading;
using NUnit.Framework;
using ZeroAlloc.Concurrency;

namespace ZeroAlloc.Tests.Concurrency;

[TestFixture]
public sealed class SpscRingBufferTests
{
    [Test]
    public void Constructor_RoundsCapacityUpToPowerOfTwo()
    {
        Assert.That(new SpscRingBuffer<int>(1).Capacity, Is.EqualTo(1));
        Assert.That(new SpscRingBuffer<int>(3).Capacity, Is.EqualTo(4));
        Assert.That(new SpscRingBuffer<int>(1000).Capacity, Is.EqualTo(1024));
        Assert.That(new SpscRingBuffer<int>(1024).Capacity, Is.EqualTo(1024));
    }

    [Test]
    public void Constructor_RejectsInvalidCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new SpscRingBuffer<int>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new SpscRingBuffer<int>(-5));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new SpscRingBuffer<int>((1 << 30) + 1));
    }

    [Test]
    public void Enqueue_ReturnsFalseWhenFull()
    {
        var rb = new SpscRingBuffer<int>(4);
        for (int i = 0; i < 4; i++)
        {
            Assert.That(rb.TryEnqueue(i), Is.True, $"slot {i} should accept");
        }
        Assert.That(rb.TryEnqueue(99), Is.False, "fifth enqueue must fail");
        Assert.That(rb.Count, Is.EqualTo(4));
    }

    [Test]
    public void Dequeue_ReturnsFalseWhenEmpty()
    {
        var rb = new SpscRingBuffer<int>(4);
        Assert.That(rb.TryDequeue(out int item), Is.False);
        Assert.That(item, Is.EqualTo(0));
        Assert.That(rb.IsEmpty, Is.True);
    }

    [Test]
    public void SingleThreaded_FifoOrderPreservedAcrossWraparound()
    {
        var rb = new SpscRingBuffer<int>(8);
        for (int round = 0; round < 100; round++)
        {
            for (int i = 0; i < 5; i++)
            {
                Assert.That(rb.TryEnqueue(round * 5 + i), Is.True);
            }
            for (int i = 0; i < 5; i++)
            {
                Assert.That(rb.TryDequeue(out int v), Is.True);
                Assert.That(v, Is.EqualTo(round * 5 + i));
            }
        }
        Assert.That(rb.IsEmpty, Is.True);
    }

    [Test]
    public void Peek_DoesNotRemove()
    {
        var rb = new SpscRingBuffer<int>(4);
        rb.TryEnqueue(42);
        Assert.That(rb.TryPeek(out int peeked), Is.True);
        Assert.That(peeked, Is.EqualTo(42));
        Assert.That(rb.Count, Is.EqualTo(1));
        Assert.That(rb.TryDequeue(out int dequeued), Is.True);
        Assert.That(dequeued, Is.EqualTo(42));
    }

    [Test]
    public void DequeueBatch_DrainsUpToSpanLength()
    {
        var rb = new SpscRingBuffer<int>(16);
        for (int i = 0; i < 10; i++) rb.TryEnqueue(i);

        Span<int> batch = stackalloc int[4];
        Assert.That(rb.DequeueBatch(batch), Is.EqualTo(4));
        for (int i = 0; i < 4; i++) Assert.That(batch[i], Is.EqualTo(i));

        Span<int> rest = stackalloc int[32];
        Assert.That(rb.DequeueBatch(rest), Is.EqualTo(6));
        for (int i = 0; i < 6; i++) Assert.That(rest[i], Is.EqualTo(4 + i));

        Assert.That(rb.DequeueBatch(rest), Is.EqualTo(0));
    }

    [Test]
    public void ReferenceTypes_AreClearedOnDequeue_NoLifetimeExtension()
    {
        var rb = new SpscRingBuffer<object>(4);
        WeakReference weak = AllocateAndCycle(rb);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.That(weak.IsAlive, Is.False, "dequeued reference must not be retained by the buffer");
    }

    private static WeakReference AllocateAndCycle(SpscRingBuffer<object> rb)
    {
        object payload = new byte[128];
        var weak = new WeakReference(payload);
        rb.TryEnqueue(payload);
        rb.TryDequeue(out _);
        return weak;
    }

    [Test]
    public void Concurrent_ProducerConsumer_TransfersAllItemsInOrder()
    {
        const int total = 1_000_000;
        var rb = new SpscRingBuffer<long>(1024);
        long consumerSum = 0;
        long expectedSum = 0;
        Exception? consumerError = null;

        var consumer = new Thread(() =>
        {
            try
            {
                long expectedNext = 0;
                var spinner = new SpinWait();
                while (expectedNext < total)
                {
                    if (rb.TryDequeue(out long v))
                    {
                        if (v != expectedNext)
                        {
                            throw new InvalidOperationException($"Out of order: got {v}, expected {expectedNext}");
                        }
                        consumerSum += v;
                        expectedNext++;
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
        { IsBackground = true, Name = "spsc-consumer" };

        consumer.Start();

        var producerSpin = new SpinWait();
        for (long i = 0; i < total; i++)
        {
            expectedSum += i;
            while (!rb.TryEnqueue(i))
            {
                producerSpin.SpinOnce();
            }
            producerSpin = default;
        }

        Assert.That(consumer.Join(TimeSpan.FromSeconds(60)), Is.True, "consumer did not finish");
        Assert.That(consumerError, Is.Null, consumerError?.ToString() ?? string.Empty);
        Assert.That(consumerSum, Is.EqualTo(expectedSum));
        Assert.That(rb.IsEmpty, Is.True);
    }

    [Test]
    public void SteadyState_EnqueueDequeue_IsAllocationFree()
    {
        var rb = new SpscRingBuffer<long>(64);
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
        }, label: "SpscRingBuffer enqueue/dequeue");
    }
}
