using System;
using System.Collections.Generic;
using EQLogParser;

namespace EQLogParserTest
{
  [TestClass]
  public class CollectionUtilTest
  {
    private class TestItem
    {
      public int Value { get; set; }
      public string? Name { get; set; }
    }

    [TestMethod]
    public void BinarySearch_Found_ReturnsIndex()
    {
      var list = new List<TestItem>
      {
        new() { Value = 10 },
        new() { Value = 20 },
        new() { Value = 30 },
        new() { Value = 40 },
        new() { Value = 50 }
      };

      var index = CollectionUtil.BinarySearch(list, item => item.Value.CompareTo(30));
      Assert.AreEqual(2, index);
    }

    [TestMethod]
    public void BinarySearch_NotFound_ReturnsBitwiseComplementOfInsertionPoint()
    {
      var list = new List<TestItem>
      {
        new() { Value = 10 },
        new() { Value = 20 },
        new() { Value = 30 },
        new() { Value = 40 },
        new() { Value = 50 }
      };

      // Search for 25 (between 20 and 30)
      var index = CollectionUtil.BinarySearch(list, item => item.Value.CompareTo(25));
      Assert.AreEqual(~2, index); // Should return ~2 (insertion point is 2)
    }

    [TestMethod]
    public void BinarySearch_NotFound_BeforeFirst_ReturnsComplementOfZero()
    {
      var list = new List<TestItem>
      {
        new() { Value = 10 },
        new() { Value = 20 },
        new() { Value = 30 }
      };

      var index = CollectionUtil.BinarySearch(list, item => item.Value.CompareTo(5));
      Assert.AreEqual(~0, index);
    }

    [TestMethod]
    public void BinarySearch_NotFound_AfterLast_ReturnsComplementOfLastPlusOne()
    {
      var list = new List<TestItem>
      {
        new() { Value = 10 },
        new() { Value = 20 },
        new() { Value = 30 }
      };

      var index = CollectionUtil.BinarySearch(list, item => item.Value.CompareTo(35));
      Assert.AreEqual(~3, index);
    }

    [TestMethod]
    public void BinarySearch_EmptyList_ReturnsComplementOfZero()
    {
      var list = new List<TestItem>();

      var index = CollectionUtil.BinarySearch(list, item => item.Value.CompareTo(10));
      Assert.AreEqual(~0, index);
    }

    [TestMethod]
    public void BinarySearch_SingleElement_Found()
    {
      var list = new List<TestItem> { new() { Value = 42 } };

      var index = CollectionUtil.BinarySearch(list, item => item.Value.CompareTo(42));
      Assert.AreEqual(0, index);
    }

    [TestMethod]
    public void BinarySearch_SingleElement_NotFound()
    {
      var list = new List<TestItem> { new() { Value = 42 } };

      var index = CollectionUtil.BinarySearch(list, item => item.Value.CompareTo(10));
      Assert.AreEqual(~0, index);

      index = CollectionUtil.BinarySearch(list, item => item.Value.CompareTo(100));
      Assert.AreEqual(~1, index);
    }

    [TestMethod]
    public void BinarySearch_FirstElement_Found()
    {
      var list = new List<TestItem>
      {
        new() { Value = 10 },
        new() { Value = 20 },
        new() { Value = 30 }
      };

      var index = CollectionUtil.BinarySearch(list, item => item.Value.CompareTo(10));
      Assert.AreEqual(0, index);
    }

    [TestMethod]
    public void BinarySearch_LastElement_Found()
    {
      var list = new List<TestItem>
      {
        new() { Value = 10 },
        new() { Value = 20 },
        new() { Value = 30 }
      };

      var index = CollectionUtil.BinarySearch(list, item => item.Value.CompareTo(30));
      Assert.AreEqual(2, index);
    }

    [TestMethod]
    public void BinarySearch_DuplicateElements_FindsOneOfThem()
    {
      var list = new List<TestItem>
      {
        new() { Value = 10 },
        new() { Value = 20 },
        new() { Value = 20 },
        new() { Value = 20 },
        new() { Value = 30 }
      };

      var index = CollectionUtil.BinarySearch(list, item => item.Value.CompareTo(20));
      Assert.IsTrue(index >= 1 && index <= 3); // Should find one of the 20s
    }

    [TestMethod]
    public void BinarySearch_StringComparison_Found()
    {
      var list = new List<string> { "apple", "banana", "cherry", "date", "elderberry" };

      var index = CollectionUtil.BinarySearch(list, s => string.Compare(s, "cherry", StringComparison.Ordinal));
      Assert.AreEqual(2, index);
    }

    [TestMethod]
    public void BinarySearch_StringComparison_NotFound()
    {
      var list = new List<string> { "apple", "banana", "cherry", "date", "elderberry" };

      var index = CollectionUtil.BinarySearch(list, s => string.Compare(s, "blueberry", StringComparison.Ordinal));
      Assert.AreEqual(~2, index); // Should go between banana and cherry
    }

    [TestMethod]
    public void BinarySearch_DescendingOrder_WithCustomComparer()
    {
      var list = new List<TestItem>
      {
        new() { Value = 50 },
        new() { Value = 40 },
        new() { Value = 30 },
        new() { Value = 20 },
        new() { Value = 10 }
      };

      // Custom comparer for descending order
      var index = CollectionUtil.BinarySearch(list, item => 0.CompareTo(item.Value - 30));
      Assert.AreEqual(2, index);
    }

    [TestMethod]
    public void BinarySearch_LargeList_PerformanceTest()
    {
      var list = new List<int>();
      for (var i = 0; i < 10000; i++)
      {
        list.Add(i * 10);
      }

      var index = CollectionUtil.BinarySearch(list, x => x.CompareTo(50000));
      Assert.AreEqual(5000, index);
    }

    // Note: CollectionUtil.BinarySearch does not validate parameters,
    // so null inputs will cause NullReferenceException (standard .NET behavior)
    [TestMethod]
    public void BinarySearch_NullList_CausesNullReferenceException()
    {
      List<TestItem>? list = null;
      var caught = false;

      try
      {
        CollectionUtil.BinarySearch(list, item => item.Value.CompareTo(10));
      }
      catch (NullReferenceException)
      {
        caught = true;
      }

      Assert.IsTrue(caught);
    }

    [TestMethod]
    public void BinarySearch_NullComparer_CausesNullReferenceException()
    {
      var list = new List<TestItem> { new() { Value = 10 } };
      var caught = false;

      try
      {
        CollectionUtil.BinarySearch(list, null);
      }
      catch (NullReferenceException)
      {
        caught = true;
      }

      Assert.IsTrue(caught);
    }
  }
}
