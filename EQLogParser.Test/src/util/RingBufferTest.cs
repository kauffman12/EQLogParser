using EQLogParser;

namespace EQLogParserTest
{
  [TestClass]
  public class RingBufferTest
  {
    [TestMethod]
    public void TestConstructor_NegativeCapacity_Throws()
    {
      var caught = false;
      try { new RingBuffer<int>(-1); }
      catch (ArgumentOutOfRangeException) { caught = true; }
      Assert.IsTrue(caught);
    }

    [TestMethod]
    public void TestConstructor_ZeroCapacity_Throws()
    {
      var caught = false;
      try { new RingBuffer<int>(0); }
      catch (ArgumentOutOfRangeException) { caught = true; }
      Assert.IsTrue(caught);
    }

    [TestMethod]
    public void TestConstructor_ValidCapacity_CreatesEmptyBuffer()
    {
      var buffer = new RingBuffer<int>(5);
      Assert.AreEqual(5, buffer.Capacity);
      Assert.AreEqual(0, buffer.Count);
    }

    [TestMethod]
    public void TestAdd_SingleItem()
    {
      var buffer = new RingBuffer<int>(5);
      buffer.Add(42);

      Assert.AreEqual(1, buffer.Count);
      Assert.AreEqual(42, buffer.GetFromNewest(0));
    }

    [TestMethod]
    public void TestAdd_MultipleItems()
    {
      var buffer = new RingBuffer<int>(5);
      buffer.Add(1);
      buffer.Add(2);
      buffer.Add(3);

      Assert.AreEqual(3, buffer.Count);
      Assert.AreEqual(3, buffer.GetFromNewest(0));
      Assert.AreEqual(2, buffer.GetFromNewest(1));
      Assert.AreEqual(1, buffer.GetFromNewest(2));
    }

    [TestMethod]
    public void TestAdd_FullBuffer_OverwritesOldest()
    {
      var buffer = new RingBuffer<int>(3);
      buffer.Add(1);
      buffer.Add(2);
      buffer.Add(3);
      buffer.Add(4); // Should overwrite 1

      Assert.AreEqual(3, buffer.Count);
      Assert.AreEqual(4, buffer.GetFromNewest(0));
      Assert.AreEqual(3, buffer.GetFromNewest(1));
      Assert.AreEqual(2, buffer.GetFromNewest(2));

      // Oldest (1) should be gone
      var caught = false;
      try { buffer.GetFromNewest(3); }
      catch (ArgumentOutOfRangeException) { caught = true; }
      Assert.IsTrue(caught);
    }

    [TestMethod]
    public void TestAdd_WrapsAroundCorrectly()
    {
      var buffer = new RingBuffer<int>(3);
      buffer.Add(1);
      buffer.Add(2);
      buffer.Add(3);
      buffer.Add(4);
      buffer.Add(5);

      Assert.AreEqual(3, buffer.Count);
      Assert.AreEqual(5, buffer.GetFromNewest(0));
      Assert.AreEqual(4, buffer.GetFromNewest(1));
      Assert.AreEqual(3, buffer.GetFromNewest(2));
    }

    [TestMethod]
    public void TestClear_ResetsBuffer()
    {
      var buffer = new RingBuffer<int>(5);
      buffer.Add(1);
      buffer.Add(2);
      buffer.Add(3);

      buffer.Clear();

      Assert.AreEqual(0, buffer.Count);
      var caught = false;
      try { buffer.GetFromNewest(0); }
      catch (ArgumentOutOfRangeException) { caught = true; }
      Assert.IsTrue(caught);
    }

    [TestMethod]
    public void TestGetFromNewest_OutOfRange_Throws()
    {
      var buffer = new RingBuffer<int>(5);
      buffer.Add(1);

      var caught1 = false;
      var caught2 = false;
      try { buffer.GetFromNewest(1); }
      catch (ArgumentOutOfRangeException) { caught1 = true; }
      try { buffer.GetFromNewest(-1); }
      catch (ArgumentOutOfRangeException) { caught2 = true; }
      Assert.IsTrue(caught1);
      Assert.IsTrue(caught2);
    }

    [TestMethod]
    public void TestTryRemoveOldest_EmptyBuffer_False()
    {
      var buffer = new RingBuffer<int>(5);
      var result = buffer.TryRemoveOldest(out var value);

      Assert.IsFalse(result);
      Assert.AreEqual(0, buffer.Count);
    }

    [TestMethod]
    public void TestTryRemoveOldest_SingleItem()
    {
      var buffer = new RingBuffer<int>(5);
      buffer.Add(42);

      var result = buffer.TryRemoveOldest(out var value);

      Assert.IsTrue(result);
      Assert.AreEqual(42, value);
      Assert.AreEqual(0, buffer.Count);
    }

    [TestMethod]
    public void TestTryRemoveOldest_MultipleItems_RemovesOldest()
    {
      var buffer = new RingBuffer<int>(5);
      buffer.Add(1);
      buffer.Add(2);
      buffer.Add(3);

      var result = buffer.TryRemoveOldest(out var value);

      Assert.IsTrue(result);
      Assert.AreEqual(1, value);
      Assert.AreEqual(2, buffer.Count);
      Assert.AreEqual(3, buffer.GetFromNewest(0));
      Assert.AreEqual(2, buffer.GetFromNewest(1));
    }

    [TestMethod]
    public void TestTryRemoveOldest_AfterFullAndOverwrite()
    {
      var buffer = new RingBuffer<int>(3);
      buffer.Add(1);
      buffer.Add(2);
      buffer.Add(3);
      buffer.Add(4); // Overwrites 1

      // Oldest should now be 2
      var result = buffer.TryRemoveOldest(out var value);

      Assert.IsTrue(result);
      Assert.AreEqual(2, value);
      Assert.AreEqual(2, buffer.Count);
      Assert.AreEqual(4, buffer.GetFromNewest(0));
      Assert.AreEqual(3, buffer.GetFromNewest(1));
    }

    [TestMethod]
    public void TestResize_SameSize_NoOp()
    {
      var buffer = new RingBuffer<int>(5);
      buffer.Add(1);
      buffer.Add(2);
      buffer.Add(3);

      buffer.Resize(5);

      Assert.AreEqual(5, buffer.Capacity);
      Assert.AreEqual(3, buffer.Count);
      Assert.AreEqual(3, buffer.GetFromNewest(0));
      Assert.AreEqual(2, buffer.GetFromNewest(1));
      Assert.AreEqual(1, buffer.GetFromNewest(2));
    }

    [TestMethod]
    public void TestResize_Larger_PreservesAllItems()
    {
      var buffer = new RingBuffer<int>(3);
      buffer.Add(1);
      buffer.Add(2);
      buffer.Add(3);

      buffer.Resize(5);

      Assert.AreEqual(5, buffer.Capacity);
      Assert.AreEqual(3, buffer.Count);
      Assert.AreEqual(3, buffer.GetFromNewest(0));
      Assert.AreEqual(2, buffer.GetFromNewest(1));
      Assert.AreEqual(1, buffer.GetFromNewest(2));
    }

    [TestMethod]
    public void TestResize_Smaller_TrimOldest()
    {
      var buffer = new RingBuffer<int>(5);
      buffer.Add(1);
      buffer.Add(2);
      buffer.Add(3);
      buffer.Add(4);
      buffer.Add(5);

      buffer.Resize(3);

      Assert.AreEqual(3, buffer.Capacity);
      Assert.AreEqual(3, buffer.Count);
      Assert.AreEqual(5, buffer.GetFromNewest(0));
      Assert.AreEqual(4, buffer.GetFromNewest(1));
      Assert.AreEqual(3, buffer.GetFromNewest(2));
      // 1 and 2 should be gone
    }

    [TestMethod]
    public void TestResize_AfterWrapAndOverwrite()
    {
      var buffer = new RingBuffer<int>(3);
      buffer.Add(1);
      buffer.Add(2);
      buffer.Add(3);
      buffer.Add(4); // Overwrites 1
      buffer.Add(5); // Overwrites 2

      // Buffer now contains [3, 4, 5] with 5 being newest
      buffer.Resize(2);

      Assert.AreEqual(2, buffer.Capacity);
      Assert.AreEqual(2, buffer.Count);
      Assert.AreEqual(5, buffer.GetFromNewest(0));
      Assert.AreEqual(4, buffer.GetFromNewest(1));
    }

    [TestMethod]
    public void TestResize_ZeroCapacity_Throws()
    {
      var buffer = new RingBuffer<int>(5);
      var caught = false;
      try { buffer.Resize(0); }
      catch (ArgumentOutOfRangeException) { caught = true; }
      Assert.IsTrue(caught);
    }

    [TestMethod]
    public void TestResize_NegativeCapacity_Throws()
    {
      var buffer = new RingBuffer<int>(5);
      var caught = false;
      try { buffer.Resize(-1); }
      catch (ArgumentOutOfRangeException) { caught = true; }
      Assert.IsTrue(caught);
    }

    [TestMethod]
    public void TestStrings_OverwriteBehavior()
    {
      var buffer = new RingBuffer<string>(3);
      buffer.Add("a");
      buffer.Add("b");
      buffer.Add("c");
      buffer.Add("d");

      Assert.AreEqual("d", buffer.GetFromNewest(0));
      Assert.AreEqual("c", buffer.GetFromNewest(1));
      Assert.AreEqual("b", buffer.GetFromNewest(2));
    }

    [TestMethod]
    public void TestObjects_ComplexType()
    {
      var buffer = new RingBuffer<Person>(2);
      buffer.Add(new Person { Name = "Alice", Age = 30 });
      buffer.Add(new Person { Name = "Bob", Age = 25 });
      buffer.Add(new Person { Name = "Charlie", Age = 35 });

      Assert.AreEqual(2, buffer.Count);
      Assert.AreEqual("Charlie", buffer.GetFromNewest(0).Name);
      Assert.AreEqual("Bob", buffer.GetFromNewest(1).Name);
    }

    [TestMethod]
    public void TestClear_ThenAdd_AfterFullBuffer()
    {
      var buffer = new RingBuffer<int>(3);
      buffer.Add(1);
      buffer.Add(2);
      buffer.Add(3);
      buffer.Add(4); // Overwrites 1

      buffer.Clear();
      buffer.Add(10);
      buffer.Add(20);

      Assert.AreEqual(2, buffer.Count);
      Assert.AreEqual(20, buffer.GetFromNewest(0));
      Assert.AreEqual(10, buffer.GetFromNewest(1));
    }

    private class Person
    {
      public string? Name { get; set; }
      public int Age { get; set; }
    }
  }
}
