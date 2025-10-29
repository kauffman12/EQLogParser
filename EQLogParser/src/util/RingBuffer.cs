using System;

namespace EQLogParser
{
  // not thread-safe
  internal class RingBuffer<T>
  {
    private readonly T[] _buf;
    private int _head;  // next write index
    private int _count;

    internal RingBuffer(int capacity)
    {
      ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
      _buf = new T[capacity];
    }

    internal int Capacity => _buf.Length;
    internal int Count => _count;

    // Overwrite oldest when full
    internal void Add(T item)
    {
      _buf[_head] = item;
      _head = (_head + 1) % _buf.Length;
      if (_count < _buf.Length) _count++;
    }

    internal void Clear()
    {
      Array.Clear(_buf, 0, _buf.Length);
      _head = 0;
      _count = 0;
    }

    // Get item "k from newest" (k=0 => newest, k=Count-1 => oldest)
    internal T GetFromNewest(int k)
    {
      if (k < 0 || k >= _count) throw new ArgumentOutOfRangeException(nameof(k));
      // newest index is (_head - 1 + Capacity) % Capacity
      var idx = _head - 1 - k;
      if (idx < 0) idx += _buf.Length * ((-idx / _buf.Length) + 1);
      idx %= _buf.Length;
      return _buf[idx];
    }

    // Removes the oldest element (the one added earliest).
    // Returns false if the buffer is empty.
    internal bool TryRemoveOldest(out T value)
    {
      if (_count == 0)
      {
        value = default!;
        return false;
      }

      // Oldest is (_head - _count + Capacity) % Capacity
      var oldest = _head - _count;
      if (oldest < 0) oldest += _buf.Length; // normalize once (since |oldest| < Capacity)

      value = _buf[oldest];
      _buf[oldest] = default!;
      _count--;
      return true;
    }
  }
}
