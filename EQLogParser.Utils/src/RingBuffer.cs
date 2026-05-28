using System;

namespace EQLogParser
{
  // not thread-safe
  internal class RingBuffer<T>
  {
    private T[] _buf;
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
      var idx = (_head - 1 - k + _buf.Length) % _buf.Length;
      return _buf[idx];
    }

    internal bool TryRemoveOldest(out T value)
    {
      if (_count == 0)
      {
        value = default!;
        return false;
      }

      var oldest = (_head - _count + _buf.Length) % _buf.Length;
      value = _buf[oldest];
      _buf[oldest] = default!;
      _count--;
      return true;
    }

    // ✅ Resize the buffer (preserves order, trims oldest if shrinking)
    internal void Resize(int newCapacity)
    {
      ArgumentOutOfRangeException.ThrowIfNegativeOrZero(newCapacity);

      if (newCapacity == _buf.Length)
        return; // nothing to do

      // number of items to preserve
      var newCount = Math.Min(_count, newCapacity);
      var newBuf = new T[newCapacity];

      // copy from oldest to newest
      var oldest = (_head - _count + _buf.Length) % _buf.Length;
      for (var i = 0; i < newCount; i++)
      {
        var oldIndex = (oldest + (_count - newCount) + i) % _buf.Length;
        newBuf[i] = _buf[oldIndex];
      }

      _buf = newBuf;
      _count = newCount;
      _head = newCount % newCapacity;
    }
  }
}
