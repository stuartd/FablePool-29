using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace ZeroAlloc.Concurrency;

/// <summary>
/// A bounded, lock-free, wait-free single-producer / single-consumer (SPSC) ring buffer.
/// </summary>
/// <typeparam name="T">The element type. Both value types and reference types are supported;
/// slots holding reference types are cleared on dequeue so the buffer never extends object lifetimes.</typeparam>
/// <remarks>
/// <para>
/// <b>Threading contract:</b> exactly one thread may call the producer methods
/// (<see cref="TryEnqueue(in T)"/>) and exactly one thread may call the consumer methods
/// (<see cref="TryDequeue(out T)"/>, <see cref="TryPeek(out T)"/>, <see cref="DequeueBatch(Span{T})"/>).
/// Producer and consumer may be (and usually are) different threads. Violating this contract
/// produces undefined results.
/// </para>
/// <para>
/// <b>Allocation behavior:</b> the only managed allocation is the backing array created in the
/// constructor. All enqueue/dequeue operations are allocation-free and never take locks; they use
/// a single volatile store for publication, plus cached views of the opposite index so that the
/// common case touches only cache lines owned by the calling thread.
/// </para>
/// <para>
/// Indices are monotonically increasing 64-bit sequence numbers; at one billion operations per
/// second a wraparound would take roughly 292 years, so overflow is not handled.
/// </para>
/// </remarks>
public sealed class SpscRingBuffer<T>
{
    private const int CacheLineSize = 64;

    /// <summary>
    /// Producer- and consumer-owned indices, padded onto distinct cache lines to
    /// eliminate false sharing between the two threads.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 5 * CacheLineSize)]
    private struct Indices
    {
        /// <summary>Next sequence number the producer will write. Written only by the producer.</summary>
        [FieldOffset(1 * CacheLineSize)] public long Tail;

        /// <summary>Producer's cached view of <see cref="Head"/>. Written only by the producer.</summary>
        [FieldOffset(2 * CacheLineSize)] public long CachedHead;

        /// <summary>Next sequence number the consumer will read. Written only by the consumer.</summary>
        [FieldOffset(3 * CacheLineSize)] public long Head;

        /// <summary>Consumer's cached view of <see cref="Tail"/>. Written only by the consumer.</summary>
        [FieldOffset(4 * CacheLineSize)] public long CachedTail;
    }

    private readonly T[] _buffer;
    private readonly int _mask;
    private Indices _idx;

    /// <summary>
    /// Initializes a new ring buffer with at least <paramref name="minCapacity"/> slots.
    /// The actual capacity is rounded up to the next power of two.
    /// </summary>
    /// <param name="minCapacity">Minimum number of slots; must be between 1 and 2^30 inclusive.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="minCapacity"/> is less than 1 or greater than 2^30.
    /// </exception>
    public SpscRingBuffer(int minCapacity)
    {
        if (minCapacity < 1 || minCapacity > (1 << 30))
        {
            throw new ArgumentOutOfRangeException(nameof(minCapacity), minCapacity,
                "Capacity must be between 1 and 2^30.");
        }

        int capacity = (int)BitOperations.RoundUpToPowerOf2((uint)minCapacity);
        _buffer = new T[capacity];
        _mask = capacity - 1;
    }

    /// <summary>Gets the fixed capacity of the buffer (always a power of two).</summary>
    public int Capacity => _buffer.Length;

    /// <summary>
    /// Gets an approximate count of items currently in the buffer. The value is exact only when
    /// observed from a quiescent state; under concurrent use it is a best-effort snapshot.
    /// This property is allocation-free.
    /// </summary>
    public int Count
    {
        get
        {
            long tail = Volatile.Read(ref _idx.Tail);
            long head = Volatile.Read(ref _idx.Head);
            long count = tail - head;
            return count < 0 ? 0 : (int)count;
        }
    }

    /// <summary>Gets a best-effort indication of whether the buffer is empty.</summary>
    public bool IsEmpty => Volatile.Read(ref _idx.Tail) == Volatile.Read(ref _idx.Head);

    /// <summary>
    /// Attempts to enqueue an item. <b>Producer thread only.</b>
    /// </summary>
    /// <param name="item">The item to enqueue.</param>
    /// <returns><see langword="true"/> if the item was enqueued; <see langword="false"/> if the buffer is full.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(in T item)
    {
        // Plain read: Tail is only ever written by this (the producer) thread.
        long tail = _idx.Tail;

        if (tail - _idx.CachedHead >= _buffer.Length)
        {
            // Our cached view says full; refresh from the real consumer index.
            _idx.CachedHead = Volatile.Read(ref _idx.Head);
            if (tail - _idx.CachedHead >= _buffer.Length)
            {
                return false; // genuinely full
            }
        }

        _buffer[tail & _mask] = item;
        // Release-publish: makes the slot write visible before the new tail.
        Volatile.Write(ref _idx.Tail, tail + 1);
        return true;
    }

    /// <summary>
    /// Attempts to dequeue an item. <b>Consumer thread only.</b>
    /// </summary>
    /// <param name="item">Receives the dequeued item, or <c>default</c> if the buffer is empty.</param>
    /// <returns><see langword="true"/> if an item was dequeued; <see langword="false"/> if the buffer is empty.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out T item)
    {
        // Plain read: Head is only ever written by this (the consumer) thread.
        long head = _idx.Head;

        if (head >= _idx.CachedTail)
        {
            // Our cached view says empty; refresh from the real producer index.
            _idx.CachedTail = Volatile.Read(ref _idx.Tail);
            if (head >= _idx.CachedTail)
            {
                item = default!;
                return false; // genuinely empty
            }
        }

        int slot = (int)(head & _mask);
        item = _buffer[slot];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            _buffer[slot] = default!; // do not extend object lifetimes
        }

        // Release: makes the slot clear visible before the new head.
        Volatile.Write(ref _idx.Head, head + 1);
        return true;
    }

    /// <summary>
    /// Attempts to read the next item without removing it. <b>Consumer thread only.</b>
    /// </summary>
    /// <param name="item">Receives the next item, or <c>default</c> if the buffer is empty.</param>
    /// <returns><see langword="true"/> if an item is available; otherwise <see langword="false"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPeek(out T item)
    {
        long head = _idx.Head;

        if (head >= _idx.CachedTail)
        {
            _idx.CachedTail = Volatile.Read(ref _idx.Tail);
            if (head >= _idx.CachedTail)
            {
                item = default!;
                return false;
            }
        }

        item = _buffer[head & _mask];
        return true;
    }

    /// <summary>
    /// Dequeues up to <paramref name="destination"/>.Length items into the destination span in
    /// a single pass, amortizing the publication cost. <b>Consumer thread only.</b>
    /// </summary>
    /// <param name="destination">The span receiving dequeued items.</param>
    /// <returns>The number of items actually dequeued (0 if the buffer is empty).</returns>
    public int DequeueBatch(Span<T> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        long head = _idx.Head;
        long available = _idx.CachedTail - head;
        if (available <= 0)
        {
            _idx.CachedTail = Volatile.Read(ref _idx.Tail);
            available = _idx.CachedTail - head;
            if (available <= 0)
            {
                return 0;
            }
        }

        int toRead = (int)Math.Min(available, destination.Length);
        for (int i = 0; i < toRead; i++)
        {
            int slot = (int)((head + i) & _mask);
            destination[i] = _buffer[slot];
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                _buffer[slot] = default!;
            }
        }

        Volatile.Write(ref _idx.Head, head + toRead);
        return toRead;
    }
}
