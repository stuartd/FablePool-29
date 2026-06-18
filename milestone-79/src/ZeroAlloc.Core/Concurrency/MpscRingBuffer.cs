using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace ZeroAlloc.Concurrency;

/// <summary>
/// A bounded, lock-free multi-producer / single-consumer (MPSC) ring buffer based on
/// Dmitry Vyukov's sequenced-cell bounded queue, restricted to a single consumer so the
/// dequeue path needs no CAS at all.
/// </summary>
/// <typeparam name="T">The element type. Slots holding reference types are cleared on dequeue.</typeparam>
/// <remarks>
/// <para>
/// <b>Threading contract:</b> any number of threads may call <see cref="TryEnqueue(in T)"/>
/// concurrently. Exactly one thread may call <see cref="TryDequeue(out T)"/> and
/// <see cref="DequeueBatch(Span{T})"/>. The enqueue path is lock-free (a failed CAS means another
/// producer made progress); the dequeue path is wait-free.
/// </para>
/// <para>
/// <b>Allocation behavior:</b> the only managed allocation is the cell array created in the
/// constructor. Enqueue/dequeue are allocation-free.
/// </para>
/// <para>
/// Each cell carries a sequence number used as a turn indicator: a cell whose sequence equals the
/// producer's claimed position is free to write; a cell whose sequence equals
/// <c>position + 1</c> is full and ready to read. After consumption the consumer advances the
/// cell's sequence by the full capacity so the cell becomes writable for the next lap.
/// </para>
/// </remarks>
public sealed class MpscRingBuffer<T>
{
    private const int CacheLineSize = 64;

    /// <summary>A storage cell pairing the payload with its lap-sequence number.</summary>
    private struct Cell
    {
        /// <summary>Turn indicator; see the class remarks for the protocol.</summary>
        public long Sequence;

        /// <summary>The stored payload (valid only when the sequence marks the cell as full).</summary>
        public T Item;
    }

    /// <summary>Producer/consumer cursors padded onto separate cache lines.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 3 * CacheLineSize)]
    private struct Cursors
    {
        /// <summary>Next position producers will claim (CAS-incremented).</summary>
        [FieldOffset(1 * CacheLineSize)] public long Tail;

        /// <summary>Next position the consumer will read. Written only by the consumer.</summary>
        [FieldOffset(2 * CacheLineSize)] public long Head;
    }

    private readonly Cell[] _cells;
    private readonly int _mask;
    private readonly long _capacity;
    private Cursors _cur;

    /// <summary>
    /// Initializes a new ring buffer with at least <paramref name="minCapacity"/> slots.
    /// The actual capacity is rounded up to the next power of two.
    /// </summary>
    /// <param name="minCapacity">Minimum number of slots; must be between 2 and 2^30 inclusive.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="minCapacity"/> is less than 2 or greater than 2^30.
    /// </exception>
    public MpscRingBuffer(int minCapacity)
    {
        if (minCapacity < 2 || minCapacity > (1 << 30))
        {
            throw new ArgumentOutOfRangeException(nameof(minCapacity), minCapacity,
                "Capacity must be between 2 and 2^30.");
        }

        int capacity = (int)BitOperations.RoundUpToPowerOf2((uint)minCapacity);
        _cells = new Cell[capacity];
        _mask = capacity - 1;
        _capacity = capacity;

        for (int i = 0; i < capacity; i++)
        {
            _cells[i].Sequence = i;
        }
    }

    /// <summary>Gets the fixed capacity of the buffer (always a power of two).</summary>
    public int Capacity => _cells.Length;

    /// <summary>
    /// Gets an approximate count of items currently in the buffer. Best-effort under concurrency.
    /// </summary>
    public int Count
    {
        get
        {
            long tail = Volatile.Read(ref _cur.Tail);
            long head = Volatile.Read(ref _cur.Head);
            long count = tail - head;
            if (count < 0) return 0;
            return count > _capacity ? (int)_capacity : (int)count;
        }
    }

    /// <summary>
    /// Attempts to enqueue an item. <b>Safe to call from any number of producer threads.</b>
    /// </summary>
    /// <param name="item">The item to enqueue.</param>
    /// <returns>
    /// <see langword="true"/> if the item was enqueued; <see langword="false"/> if the buffer was
    /// full at the time of the attempt.
    /// </returns>
    public bool TryEnqueue(in T item)
    {
        Cell[] cells = _cells;
        int mask = _mask;
        SpinWait spinner = default;

        while (true)
        {
            long pos = Volatile.Read(ref _cur.Tail);
            ref Cell cell = ref cells[(int)(pos & mask)];
            long seq = Volatile.Read(ref cell.Sequence);
            long diff = seq - pos;

            if (diff == 0)
            {
                // Cell is free for this lap; try to claim the position.
                if (Interlocked.CompareExchange(ref _cur.Tail, pos + 1, pos) == pos)
                {
                    cell.Item = item;
                    // Release-publish: mark cell as full for the consumer.
                    Volatile.Write(ref cell.Sequence, pos + 1);
                    return true;
                }
                // Another producer claimed this slot; retry at the new tail.
            }
            else if (diff < 0)
            {
                // The cell still holds last lap's data: the queue is full.
                return false;
            }
            // diff > 0: another producer claimed this position but we read a stale tail; retry.

            spinner.SpinOnce();
        }
    }

    /// <summary>
    /// Attempts to dequeue an item. <b>Consumer thread only.</b> Wait-free.
    /// </summary>
    /// <param name="item">Receives the dequeued item, or <c>default</c> if no item is ready.</param>
    /// <returns><see langword="true"/> if an item was dequeued; otherwise <see langword="false"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out T item)
    {
        // Plain read: Head is written only by this (the consumer) thread.
        long pos = _cur.Head;
        ref Cell cell = ref _cells[(int)(pos & _mask)];
        long seq = Volatile.Read(ref cell.Sequence);

        if (seq - (pos + 1) < 0)
        {
            // The producer claiming this slot has not finished publishing (or the queue is empty).
            item = default!;
            return false;
        }

        item = cell.Item;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            cell.Item = default!;
        }

        // Free the cell for the next lap.
        Volatile.Write(ref cell.Sequence, pos + _capacity);
        Volatile.Write(ref _cur.Head, pos + 1);
        return true;
    }

    /// <summary>
    /// Dequeues up to <paramref name="destination"/>.Length ready items. <b>Consumer thread only.</b>
    /// </summary>
    /// <param name="destination">The span receiving dequeued items.</param>
    /// <returns>The number of items dequeued (0 if no items were ready).</returns>
    public int DequeueBatch(Span<T> destination)
    {
        int written = 0;
        while (written < destination.Length && TryDequeue(out T item))
        {
            destination[written++] = item;
        }
        return written;
    }
}
