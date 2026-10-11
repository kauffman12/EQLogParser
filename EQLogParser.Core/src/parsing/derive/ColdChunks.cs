#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser;

// Immutable completed chunks have a permanent compressed backing. Decoded arrays are a bounded cache, not a second
// copy of the capture. A caller holding a span/ref keeps its array alive after eviction. The append tail never enters
// this cache. One worker encodes each completed chunk once; publication, decoding and reference-array growth share a lock.
internal sealed class ColdChunks<T>(int chunkSize, int capacity, Func<T[], byte[]> encode,
  Func<byte[], T[]> decode, Action<Exception> failed) where T : struct
{
  internal const int WarmLimit = 4;
  private readonly object _gate = new();
  private T[]?[] _chunks = new T[]?[(int)(((long)Math.Max(0, capacity) + chunkSize - 1) / chunkSize)];
  private readonly Dictionary<int, byte[]> _blobs = [];
  private readonly Queue<int> _warm = [];
  private readonly Queue<int> _pending = [];
  private Task _worker = Task.CompletedTask;
  private bool _working;
  private long _blobBytes;
  private int _readers;

  internal int BlobCount { get { lock (_gate) return _blobs.Count; } }
  internal long BlobBytes { get { lock (_gate) return _blobBytes; } }
  internal int RawCount
  {
    get
    {
      lock (_gate)
      {
        var count = 0;
        foreach (var rows in _chunks) if (rows is not null) count++;
        return count;
      }
    }
  }

  // Single append writer. Only called when buying a new tail, not for every row.
  internal T[] NewTail(int ci)
  {
    lock (_gate)
    {
      if (ci >= _chunks.Length)
      {
        var grown = Math.Max(_chunks.Length * 2, 4);
        while (ci >= grown) grown *= 2;
        var replacement = new T[]?[grown];
        Array.Copy(_chunks, replacement, _chunks.Length);
        Volatile.Write(ref _chunks, replacement);
      }
      var rows = new T[chunkSize];
      Volatile.Write(ref _chunks[ci], rows);
      if (ci > 0)
      {
        _pending.Enqueue(ci - 1);
        if (!_working)
        {
          _working = true;
          _worker = Task.Run(EncodePending);
        }
      }
      return rows;
    }
  }

  internal T[] Read(int ci)
  {
    var rows = Volatile.Read(ref Volatile.Read(ref _chunks)[ci]);
    if (rows is not null) return rows;
    lock (_gate)
    {
      // Recheck after taking the lock: another reader may have decoded, or a grow may have replaced the reference array.
      if (_chunks[ci] is { } existing) return existing;
      rows = decode(_blobs[ci]); // A missing backing is an invariant failure, never a fabricated zero row.
      Volatile.Write(ref _chunks[ci], rows);
      _warm.Enqueue(ci);
      TrimWarm();
      return rows;
    }
  }

  // A barrier for tests/cost probes after append stops. It never starts a duplicate encoder. Returns backed chunks,
  // not work won in a race with the worker. Production reads need no barrier: raw rows stay published until backing exists.
  internal int Settle()
  {
    Task worker;
    lock (_gate) worker = _worker;
    worker.GetAwaiter().GetResult();
    return BlobCount;
  }

  // A multi-stage pass may revisit the same chunks. Keep its decodes until its LAST concurrent reader leaves,
  // then return to the bounded cache. This pays a decode once per pass, not once per rule or selected fight.
  internal IDisposable RetainDecoded()
  {
    lock (_gate) _readers++;
    return new ReadLease(this);
  }

  private sealed class ReadLease(ColdChunks<T> owner) : IDisposable
  {
    private ColdChunks<T>? _owner = owner;
    public void Dispose()
    {
      if (Interlocked.Exchange(ref _owner, null) is not { } store) return;
      lock (store._gate)
      {
        store._readers--;
        store.TrimWarm();
      }
    }
  }

  private void TrimWarm()
  {
    if (_readers != 0) return;
    while (_warm.Count > WarmLimit) Volatile.Write(ref _chunks[_warm.Dequeue()], null);
  }

  private void EncodePending()
  {
    while (true)
    {
      int ci;
      T[] rows;
      lock (_gate)
      {
        if (!_pending.TryDequeue(out ci))
        {
          _working = false;
          return;
        }
        rows = _chunks[ci] ?? throw new InvalidOperationException("An unencoded chunk lost its rows");
      }
      try
      {
        // The supplied encoder verifies its round trip before returning. Failure leaves the only truth (rows) in place.
        var blob = encode(rows);
        lock (_gate)
        {
          _blobs.Add(ci, blob);
          _blobBytes += blob.Length;
          Volatile.Write(ref _chunks[ci], null);
        }
      }
      catch (Exception ex)
      {
        failed(ex);
      }
    }
  }
}
