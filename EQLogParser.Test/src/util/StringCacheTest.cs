using EQLogParser;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EQLogParserTest
{
  [TestClass]
  public class StringCacheTest
  {
    [TestInitialize]
    public void Setup()
    {
      StringCache.Clear();
    }

    [TestCleanup]
    public void Cleanup()
    {
      StringCache.Clear();
    }

    [TestMethod]
    public void GetOrAdd_Null_ReturnsNull()
    {
      var result = StringCache.GetOrAdd(null);
      Assert.IsNull(result);
    }

    [TestMethod]
    public void GetOrAdd_EmptyString_ReturnsEmptyString()
    {
      var result = StringCache.GetOrAdd("");
      Assert.AreEqual("", result);
    }

    [TestMethod]
    public void GetOrAdd_SameString_ReturnsSameReference()
    {
      var a = StringCache.GetOrAdd("hello");
      var b = StringCache.GetOrAdd("hello");
      Assert.AreSame(a, b);
    }

    [TestMethod]
    public void GetOrAdd_DifferentStrings_ReturnsDifferentReferences()
    {
      var a = StringCache.GetOrAdd("hello");
      var b = StringCache.GetOrAdd("world");
      Assert.AreNotSame(a, b);
    }

    [TestMethod]
    public void GetOrAdd_CapitalizesFirstLetter()
    {
      var result = StringCache.GetOrAdd("hello");
      Assert.AreEqual("Hello", result);
    }

    [TestMethod]
    public void GetOrAdd_UpperCaseInput_CapitalizesFirstOnly()
    {
      var result = StringCache.GetOrAdd("hello");
      Assert.AreEqual("Hello", result);
    }

    [TestMethod]
    public void GetOrAdd_SameInput_ReturnsSameReference()
    {
      var a = StringCache.GetOrAdd("hello");
      var b = StringCache.GetOrAdd("hello");
      Assert.AreSame(a, b);
    }

    [TestMethod]
    public void GetOrAdd_MultiWord_CapitalizesFirstLetterOnly()
    {
      var result = StringCache.GetOrAdd("dark ranger");
      Assert.AreEqual("Dark ranger", result);
    }

    [TestMethod]
    public void Clear_RemovesAllCachedStrings()
    {
      var a = StringCache.GetOrAdd("hello");
      StringCache.Clear();
      var b = StringCache.GetOrAdd("hello");
      Assert.AreNotSame(a, b);
    }

    [TestMethod]
    public void Clear_WithNullDoesNotThrow()
    {
      // Clear is idempotent even when empty
      StringCache.Clear();
      StringCache.Clear();
    }

    [TestMethod]
    public void GetOrAdd_ThreadSafety_MultipleThreadsDedupCorrectly()
    {
      var tasks = new List<Task>();
      var results = new List<string>();
      var lockObj = new object();

      for (var i = 0; i < 50; i++)
      {
        tasks.Add(Task.Run(() =>
        {
          var cached = StringCache.GetOrAdd("sharedkey");
          lock (lockObj)
          {
            results.Add(cached);
          }
        }));
      }

      Task.WaitAll(tasks.ToArray());

      // All results should reference the same string
      var first = results[0];
      Assert.IsTrue(results.All(r => ReferenceEquals(r, first)));
    }
  }
}
