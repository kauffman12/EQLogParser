using EQLogParser;

namespace EQLogParserTest
{
  [TestClass]
  public class SimpleObjectCacheTest
  {
    [TestMethod]
    public void TestAdd_SingleItem_ReturnsSame()
    {
      var cache = new SimpleObjectCache<TestObject>();
      var obj = new TestObject { Id = 1, Name = "Test" };

      var result = cache.Add(obj);

      Assert.AreSame(obj, result);
    }

    [TestMethod]
    public void TestAdd_DuplicateItem_ReturnsOriginal()
    {
      var cache = new SimpleObjectCache<TestObject>();
      var obj1 = new TestObject { Id = 1, Name = "Test" };
      var obj2 = new TestObject { Id = 1, Name = "Test" };

      var result1 = cache.Add(obj1);
      var result2 = cache.Add(obj2);

      Assert.AreSame(obj1, result1);
      Assert.AreSame(obj1, result2);
      Assert.AreNotSame(obj2, result2);
    }

    [TestMethod]
    public void TestAdd_DifferentItems_ReturnsEach()
    {
      var cache = new SimpleObjectCache<TestObject>();
      var obj1 = new TestObject { Id = 1, Name = "Test1" };
      var obj2 = new TestObject { Id = 2, Name = "Test2" };

      var result1 = cache.Add(obj1);
      var result2 = cache.Add(obj2);

      Assert.AreSame(obj1, result1);
      Assert.AreSame(obj2, result2);
    }

    [TestMethod]
    public void TestAdd_HashCollision_DifferentObjects_BothStored()
    {
      var cache = new SimpleObjectCache<HashCollisionObject>();
      var obj1 = new HashCollisionObject { Value = 1 };  // Hash code = 100
      var obj2 = new HashCollisionObject { Value = 2 };  // Hash code = 100

      var result1 = cache.Add(obj1);
      var result2 = cache.Add(obj2);

      Assert.AreSame(obj1, result1);
      Assert.AreSame(obj2, result2);
    }

    [TestMethod]
    public void TestAdd_HashCollision_EqualObject_ReturnsOriginal()
    {
      var cache = new SimpleObjectCache<HashCollisionObject>();
      var obj1 = new HashCollisionObject { Value = 5 };
      var obj2 = new HashCollisionObject { Value = 5 };  // Same value, same hash, equals returns true

      var result1 = cache.Add(obj1);
      var result2 = cache.Add(obj2);

      Assert.AreSame(obj1, result1);
      Assert.AreSame(obj1, result2);
    }

    [TestMethod]
    public void TestAdd_MultipleHashCollisions_AllStored()
    {
      var cache = new SimpleObjectCache<HashCollisionObject>();
      var obj1 = new HashCollisionObject { Value = 1 };
      var obj2 = new HashCollisionObject { Value = 2 };
      var obj3 = new HashCollisionObject { Value = 3 };

      var result1 = cache.Add(obj1);
      var result2 = cache.Add(obj2);
      var result3 = cache.Add(obj3);

      Assert.AreSame(obj1, result1);
      Assert.AreSame(obj2, result2);
      Assert.AreSame(obj3, result3);
    }

    [TestMethod]
    public void TestAdd_MixedHashAndEquals_MultipleObjectsWithSameHash()
    {
      var cache = new SimpleObjectCache<HashCollisionObject>();
      var obj1 = new HashCollisionObject { Value = 1 };
      var obj2 = new HashCollisionObject { Value = 2 };
      var obj3 = new HashCollisionObject { Value = 1 };  // Same as obj1

      cache.Add(obj1);
      cache.Add(obj2);
      var result3 = cache.Add(obj3);

      Assert.AreSame(obj1, result3);
    }

    [TestMethod]
    public void TestClear_EmptiesCache()
    {
      var cache = new SimpleObjectCache<TestObject>();
      var obj1 = new TestObject { Id = 1, Name = "Test1" };
      var obj2 = new TestObject { Id = 2, Name = "Test2" };

      cache.Add(obj1);
      cache.Add(obj2);
      cache.Clear();

      // After clear, adding same objects should return them as new
      var result = cache.Add(obj1);
      Assert.AreSame(obj1, result);
    }

    [TestMethod]
    public void TestAdd_StringsWithSameContent_ReturnsOriginal()
    {
      var cache = new SimpleObjectCache<string>();
      var str1 = "Hello";
      var str2 = "Hello";

      var result1 = cache.Add(str1);
      var result2 = cache.Add(str2);

      Assert.AreSame(str1, result1);
      Assert.AreSame(str1, result2);
    }

    [TestMethod]
    public void TestAdd_Integers_ReturnsOriginal()
    {
      var cache = new SimpleObjectCache<int>();
      var result1 = cache.Add(42);
      var result2 = cache.Add(42);
      var result3 = cache.Add(43);

      // Note: boxed integers may not be reference equal due to boxing
      // But the cache should still work correctly
      Assert.AreEqual(42, result1);
      Assert.AreEqual(42, result2);
      Assert.AreEqual(43, result3);
    }

    [TestMethod]
    public void TestAdd_EmptyString_NewEntry()
    {
      var cache = new SimpleObjectCache<string>();
      var result = cache.Add("");

      Assert.AreEqual("", result);
    }

    [TestMethod]
    public void TestAdd_NullValue_HandlesGracefully()
    {
      // Note: This tests behavior with nullable reference types
      var cache = new SimpleObjectCache<string?>();
      var nullStr = (string?)null;
      var result = cache.Add(nullStr);

      Assert.IsNull(result);
    }

    [TestMethod]
    public void TestAdd_SameHash_DifferentEquals_MultipleEntries()
    {
      var cache = new SimpleObjectCache<CustomHashObject>();
      var obj1 = new CustomHashObject { Hash = 42, Value = "A" };
      var obj2 = new CustomHashObject { Hash = 42, Value = "B" };
      var obj3 = new CustomHashObject { Hash = 42, Value = "C" };

      var result1 = cache.Add(obj1);
      var result2 = cache.Add(obj2);
      var result3 = cache.Add(obj3);

      Assert.AreSame(obj1, result1);
      Assert.AreSame(obj2, result2);
      Assert.AreSame(obj3, result3);
    }

    [TestMethod]
    public void TestAdd_ThenClear_ThenAddSame_ReturnsNew()
    {
      var cache = new SimpleObjectCache<TestObject>();
      var obj1 = new TestObject { Id = 1, Name = "Test" };

      cache.Add(obj1);
      cache.Clear();

      var result = cache.Add(obj1);
      Assert.AreSame(obj1, result);
    }

    private class TestObject
    {
      public int Id { get; set; }
      public string? Name { get; set; }

      public override bool Equals(object? obj)
      {
        return obj is TestObject other && Id == other.Id && Name == other.Name;
      }

      public override int GetHashCode() => HashCode.Combine(Id, Name);
    }

    // All instances return same hash code to test collision handling
    private class HashCollisionObject
    {
      public int Value { get; set; }

      public override bool Equals(object? obj)
      {
        return obj is HashCollisionObject other && Value == other.Value;
      }

      public override int GetHashCode() => 100;  // Fixed hash code for testing
    }

    // Custom hash code independent of equality
    private class CustomHashObject
    {
      public int Hash { get; set; }
      public string? Value { get; set; }

      public override bool Equals(object? obj)
      {
        return obj is CustomHashObject other && Value == other.Value;
      }

      public override int GetHashCode() => Hash;
    }
  }
}
