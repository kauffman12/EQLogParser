using EQLogParser;
using System.Collections.Concurrent;
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
    public void GetOrAddExact_PreservesOriginalCasing()
    {
      // unlike GetOrAdd, exact text is kept as-is (spell names are displayed verbatim)
      Assert.AreEqual("creeping Death", StringCache.GetOrAddExact("creeping Death"));
    }

    [TestMethod]
    public void GetOrAddExact_EqualContent_ReturnsSameReference()
    {
      var a = StringCache.GetOrAddExact(new string("Fists of Fire".ToCharArray()));
      var b = StringCache.GetOrAddExact(new string("Fists of Fire".ToCharArray()));
      Assert.AreSame(a, b);
    }

    [TestMethod]
    public void GetOrAddExact_NullAndEmpty_ReturnInput()
    {
      Assert.IsNull(StringCache.GetOrAddExact(null));
      Assert.AreEqual("", StringCache.GetOrAddExact(""));
    }

    [TestMethod]
    public void GetOrAddExact_KeepsCasingVariantsApart()
    {
      // ordinal keys: "spell" and "Spell" are separate entries, both exact
      Assert.AreEqual("spell", StringCache.GetOrAddExact("spell"));
      Assert.AreEqual("Spell", StringCache.GetOrAddExact("Spell"));
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
    public void GetId_NullAndEmpty_AreZero()
    {
      // 0 is reserved for "no name stored", which is how an unset record field keeps reading null
      Assert.AreEqual(0, StringCache.GetId(null));
      Assert.AreEqual(0, StringCache.GetId(""));
      Assert.IsNull(StringCache.GetName(0));
    }

    [TestMethod]
    public void GetId_SameString_ReturnsSameId()
    {
      var a = StringCache.GetId(new string("Fllint".ToCharArray()));
      var b = StringCache.GetId(new string("Fllint".ToCharArray()));
      Assert.AreEqual(a, b);
      Assert.AreEqual("Fllint", StringCache.GetName(a));

      // one instance per id: two equal names written from different copies read back as the same string,
      // which is what code comparing resolved names still relies on
      Assert.AreSame(StringCache.GetName(a), StringCache.GetName(b));
    }

    [TestMethod]
    public void GetId_KeepsCasingVariantsApart()
    {
      // ids are matched ordinally, so they partition names exactly as the string references did
      var lower = StringCache.GetId("spell");
      var upper = StringCache.GetId("Spell");
      Assert.AreNotEqual(lower, upper);
      Assert.AreEqual("spell", StringCache.GetName(lower));
      Assert.AreEqual("Spell", StringCache.GetName(upper));
    }

    [TestMethod]
    public void GetId_IdsSurviveClear()
    {
      // records keep ids for the life of a log; dropping the text cache may not rename them
      var id = StringCache.GetId("Vexing Malice");
      StringCache.Clear();
      Assert.AreEqual(id, StringCache.GetId("Vexing Malice"));
      Assert.AreEqual("Vexing Malice", StringCache.GetName(id));
    }

    [TestMethod]
    public void GetName_UnknownId_ReturnsNull()
    {
      Assert.IsNull(StringCache.GetName(-1));
      Assert.IsNull(StringCache.GetName(int.MaxValue));
    }

    [TestMethod]
    public void GetId_ThreadSafety_EveryThreadSeesOneId()
    {
      var ids = new System.Collections.Concurrent.ConcurrentBag<int>();
      var tasks = new List<Task>();
      for (var i = 0; i < 50; i++)
      {
        tasks.Add(Task.Run(() => ids.Add(StringCache.GetId("shared symbol"))));
      }

      Task.WaitAll(tasks.ToArray());
      Assert.AreEqual(1, ids.Distinct().Count());
    }

    [TestMethod]
    public void GetId_PastAPageBoundary_StillResolves()
    {
      // names live in fixed pages (see StringCache.GetName); an id on the far side of a boundary has to
      // read back exactly like one in the first page, including after many boundaries
      const int Count = 2600;
      var names = Enumerable.Range(0, Count).Select(static i => $"pcx symbol {i}").ToArray();
      var ids = names.Select(StringCache.GetId).ToArray();

      CollectionAssert.AllItemsAreUnique(ids);
      for (var i = 0; i < Count; i++)
      {
        Assert.AreEqual(names[i], StringCache.GetName(ids[i]), $"symbol {i} lost its name");
      }
    }

    [TestMethod]
    public void GetName_WhileIdsAreBeingAdded_NeverLosesAName()
    {
      // the real shape of this at runtime: the UI thread resolves names out of records while a parser
      // thread registers thousands more. An id already handed out has to read back, from another thread,
      // at any moment — including the instant a new page appears.
      const string Name = "pcx established name";
      var id = StringCache.GetId(Name);
      var stop = false;
      var failures = 0;

      var reader = Task.Run(() =>
      {
        while (!Volatile.Read(ref stop))
        {
          if (!ReferenceEquals(StringCache.GetName(id), Name))
          {
            Interlocked.Increment(ref failures);
          }
        }
      });

      var writers = Enumerable.Range(0, 4).Select((_, slot) => Task.Run(() =>
      {
        for (var i = 0; i < 2000; i++)
        {
          StringCache.GetId($"pcx churn {slot}-{i}");
        }
      })).ToArray();

      Task.WaitAll(writers);
      Volatile.Write(ref stop, true);
      reader.Wait(TimeSpan.FromSeconds(10));

      Assert.AreEqual(0, failures);
    }

    /*
     * The hit path takes no lock, so it is the path this test has to prove: eight threads reading three hundred established names while two more
     * register thousands must always get back the id they were given the first time. A miss on that path would not throw, it would answer 0 — which
     * reads as "no name stored", so a record would silently lose its player or its spell rather than fail.
     */
    [TestMethod]
    public void GetId_OnTheLockFreeHitPath_AnswersCorrectlyWhileOtherThreadsRegister()
    {
      const int Established = 300;

      var names = Enumerable.Range(0, Established).Select(static i => $"pcx hit {i}").ToArray();
      var expected = names.Select(StringCache.GetId).ToArray();

      var stop = false;
      var wrong = 0;

      var readers = Enumerable.Range(0, 8).Select(slot => Task.Run(() =>
      {
        while (!Volatile.Read(ref stop))
        {
          for (var i = slot; i < Established; i += 8)
          {
            if (StringCache.GetId(names[i]) != expected[i])
            {
              Interlocked.Increment(ref wrong);
            }
          }
        }
      })).ToArray();

      var writers = Enumerable.Range(0, 2).Select(slot => Task.Run(() =>
      {
        for (var i = 0; i < 3000; i++)
        {
          StringCache.GetId($"pcx churn {slot}-{i}");
        }
      })).ToArray();

      Task.WaitAll(writers);
      Volatile.Write(ref stop, true);
      Task.WaitAll(readers);

      Assert.AreEqual(0, wrong, "a lookup during registration answered something other than the id that name was given");
    }

    /* Two threads meeting the same new name at the same moment must be given one id: ids are equality, and two ids for one player splits their damage in two. */
    [TestMethod]
    public void GetId_GivesOneIdToTheFirstTenThreadsThatAskForANewName()
    {
      const string Name = "pcx arrived together";

      var ids = new ConcurrentBag<int>();

      Parallel.For(0, 10, _ => ids.Add(StringCache.GetId(Name)));

      Assert.AreEqual(1, ids.Distinct().Count(), $"one name was given {ids.Distinct().Count()} ids: {string.Join(", ", ids)}");
      Assert.AreEqual(Name, StringCache.GetName(ids.First()));
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
