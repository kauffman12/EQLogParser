namespace EQLogParser;

[TestClass]
public sealed class ColdChunksConcurrencyTest
{
  [TestMethod]
  public void PendingEncodingLeavesRowsReadableAndDoesNotBlockAppend()
  {
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var encodes = 0;
    var store = Store(rows =>
    {
      Interlocked.Increment(ref encodes);
      entered.Set();
      if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
      return Encode(rows);
    });
    var first = store.NewTail(0);
    first[0] = 123;
    try
    {
      store.NewTail(1)[0] = 456;
      Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
      Assert.AreSame(first, store.Read(0), "the only truth cannot disappear while its replacement is being encoded");
      Assert.AreEqual(123, store.Read(0)[0]);
      store.NewTail(2)[0] = 789; // append continues while the encoder is stopped
      Assert.AreEqual(456, store.Read(1)[0]);
    }
    finally
    {
      release.Set();
    }
    Assert.AreEqual(2, store.Settle());
    Assert.AreEqual(2, store.Settle());
    Assert.AreEqual(2, encodes, "a barrier never schedules a duplicate encoder");
    Assert.AreEqual(32L, store.BlobBytes, "one blob per completed chunk, counted once");
  }

  [TestMethod]
  public void ConcurrentColdReadersShareOneDecodeAndKeepTheBacking()
  {
    var decodes = 0;
    var store = new ColdChunks<int>(4, 0, Encode, blob =>
    {
      Interlocked.Increment(ref decodes);
      return Decode(blob);
    }, ex => Assert.Fail(ex.ToString()));
    store.NewTail(0)[0] = 123;
    store.NewTail(1);
    store.Settle();
    var readers = Enumerable.Range(0, 20).Select(_ => Task.Run(() => store.Read(0))).ToArray();
    Task.WaitAll(readers);
    foreach (var reader in readers)
    {
      Assert.AreEqual(123, reader.Result[0]);
      Assert.AreSame(readers[0].Result, reader.Result);
    }
    Assert.AreEqual(1, decodes);
    Assert.AreEqual(1, store.BlobCount);
    Assert.AreEqual(16L, store.BlobBytes);
  }

  [TestMethod]
  public void AFullReadKeepsOnlyABoundedWarmCacheAndOldReferencesStayValid()
  {
    var store = Store();
    for (var i = 0; i < 30; i++) store.NewTail(i)[0] = i + 1;
    Assert.AreEqual(29, store.Settle());
    var held = store.Read(0);
    for (var pass = 0; pass < 3; pass++)
      for (var i = 0; i < 30; i++) Assert.AreEqual(i + 1, store.Read(i)[0]);
    Assert.IsTrue(store.RawCount <= ColdChunks<int>.WarmLimit + 1, "a classification pass must not permanently warm the whole capture");
    Assert.AreEqual(1, held[0], "eviction cannot invalidate an array/span already handed out");
    Assert.AreNotSame(held, store.Read(0), "the old chunk really was evicted");
    Assert.AreEqual(29 * 16L, store.BlobBytes);
  }

  [TestMethod]
  public void GrowthAndColdReadsNeverLoseAReferenceArrayPublication()
  {
    var store = Store();
    store.NewTail(0)[0] = 123;
    store.NewTail(1)[0] = 456;
    store.Settle();
    var reader = Task.Run(() =>
    {
      for (var i = 0; i < 10000; i++) Assert.AreEqual(123, store.Read(0)[0]);
    });
    for (var i = 2; i < 100; i++) store.NewTail(i)[0] = i;
    store.Settle();
    reader.GetAwaiter().GetResult();
    for (var i = 2; i < 100; i++) Assert.AreEqual(i, store.Read(i)[0]);
  }

  [TestMethod]
  public void DecodesAreRetainedUntilTheLastOverlappingReadLeavesEvenOnAThrow()
  {
    var store = Store();
    for (var i = 0; i < 20; i++) store.NewTail(i)[0] = i;
    store.Settle();
    using var first = store.RetainDecoded();
    Assert.ThrowsExactly<InvalidOperationException>(() =>
    {
      using var second = store.RetainDecoded();
      for (var i = 0; i < 20; i++) store.Read(i);
      Assert.AreEqual(20, store.RawCount);
      throw new InvalidOperationException();
    });
    Assert.AreEqual(20, store.RawCount, "first reader still owns its decodes");
    first.Dispose();
    Assert.IsTrue(store.RawCount <= ColdChunks<int>.WarmLimit + 1);
    first.Dispose(); // idempotent; the using declaration will dispose too
  }

  [TestMethod]
  public void AnEncodingFailureKeepsRowsAndDoesNotStopTheNextChunk()
  {
    var failures = 0;
    var store = new ColdChunks<int>(4, 0, rows => rows[0] == 123 ? throw new InvalidOperationException() : Encode(rows),
      Decode, _ => Interlocked.Increment(ref failures));
    store.NewTail(0)[0] = 123;
    store.NewTail(1)[0] = 456;
    store.NewTail(2);
    Assert.AreEqual(1, store.Settle());
    Assert.AreEqual(1, failures);
    Assert.AreEqual(123, store.Read(0)[0]);
    Assert.AreEqual(456, store.Read(1)[0]);
  }

  private static ColdChunks<int> Store(Func<int[], byte[]>? encode = null) =>
    new(4, 0, encode ?? Encode, Decode, ex => Assert.Fail(ex.ToString()));

  private static byte[] Encode(int[] rows)
  {
    var bytes = new byte[rows.Length * sizeof(int)];
    Buffer.BlockCopy(rows, 0, bytes, 0, bytes.Length);
    return bytes;
  }

  private static int[] Decode(byte[] bytes)
  {
    var rows = new int[bytes.Length / sizeof(int)];
    Buffer.BlockCopy(bytes, 0, rows, 0, bytes.Length);
    return rows;
  }
}
