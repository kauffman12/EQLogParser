using EQLogParser;
using System.Collections.Generic;
using System.Collections.Specialized;

namespace EQLogParserTest
{
  [TestClass]
  public class BulkObservableCollectionTest
  {
    [TestMethod]
    public void Constructor_DefaultMaxSize_AllowsUnlimitedItems()
    {
      var collection = new BulkObservableCollection<int>();
      collection.AddRange(new int[1000]);
      Assert.AreEqual(1000, collection.Count);
    }

    [TestMethod]
    public void Constructor_WithMaxSize_CollectionStartsEmpty()
    {
      var collection = new BulkObservableCollection<int>(3);
      Assert.AreEqual(0, collection.Count);
    }

    [TestMethod]
    public void AddRange_InsertsAtFront()
    {
      var collection = new BulkObservableCollection<int>();
      collection.AddRange(new[] { 1, 2, 3 });
      // Items are inserted at index 0, so order is reversed
      Assert.AreEqual(3, collection[0]);
      Assert.AreEqual(2, collection[1]);
      Assert.AreEqual(1, collection[2]);
    }

    [TestMethod]
    public void AddRange_EnforcesMaxSize()
    {
      var collection = new BulkObservableCollection<int>(3);
      collection.AddRange(new[] { 1, 2, 3, 4, 5 });
      Assert.AreEqual(3, collection.Count);
      // Last 3 inserted (5, 4, 3 at front, then trimmed from end)
      Assert.AreEqual(5, collection[0]);
      Assert.AreEqual(4, collection[1]);
      Assert.AreEqual(3, collection[2]);
    }

    [TestMethod]
    public void InsertItem_EnforcesMaxSize()
    {
      var collection = new BulkObservableCollection<int>(3);
      collection.Insert(0, 1);
      collection.Insert(0, 2);
      collection.Insert(0, 3);
      collection.Insert(0, 4);
      Assert.AreEqual(3, collection.Count);
    }

    [TestMethod]
    public void AddRange_SuppressesNotifications()
    {
      var collection = new BulkObservableCollection<int>();
      var changeCount = 0;
      collection.CollectionChanged += (s, e) => changeCount++;

      collection.AddRange(new[] { 1, 2, 3 });
      // Should fire exactly one Reset notification
      Assert.AreEqual(1, changeCount);
    }

    [TestMethod]
    public void InsertItem_FiresNotification()
    {
      var collection = new BulkObservableCollection<int>();
      var changeCount = 0;
      collection.CollectionChanged += (s, e) => changeCount++;

      collection.Insert(0, 42);
      Assert.AreEqual(1, changeCount);
    }

    [TestMethod]
    public void MaxSizeZero_NoRemovalEnforcement()
    {
      // maxSize <= 0 means no enforcement
      var collection = new BulkObservableCollection<int>(0);
      collection.AddRange(new[] { 1, 2, 3 });
      Assert.AreEqual(3, collection.Count);
    }

    [TestMethod]
    public void MaxSizeNegative_NoRemovalEnforcement()
    {
      var collection = new BulkObservableCollection<int>(-1);
      collection.AddRange(new[] { 1, 2, 3 });
      Assert.AreEqual(3, collection.Count);
    }

    [TestMethod]
    public void AddRange_EmptyCollection_NoItemsAdded()
    {
      var collection = new BulkObservableCollection<int>();
      collection.AddRange(Array.Empty<int>());
      Assert.AreEqual(0, collection.Count);
    }

    [TestMethod]
    public void AddRange_NullEnumerable_Throws()
    {
      var collection = new BulkObservableCollection<int>();
      bool threw = false;
      try
      {
        collection.AddRange((IEnumerable<int>)null);
      }
      catch (System.NullReferenceException)
      {
        threw = true;
      }
      Assert.IsTrue(threw);
    }
  }
}
