using System.Collections.Concurrent;

namespace EQLogParser;

[TestClass]
public sealed class RaidRosterStoreTest
{
  private static readonly double Begin = DateUtil.ToDotNetSeconds(new DateTime(2026, 1, 1, 12, 0, 0));

  [TestInitialize]
  public void Setup()
  {
    RaidRosterStore.Instance.Clear();
    PlayerRegistry.Instance.Clear();
    PipelineHarness.SharedDataStore();
  }

  [TestCleanup]
  public void Cleanup()
  {
    RaidRosterStore.Instance.Clear();
    PlayerRegistry.Instance.Clear();
  }

  [TestMethod]
  public void OnlyTheRaidFooterPublishesAndNotifiesACompleteRoster()
  {
    var notifications = 0;
    var namesAtNotification = 0;
    void Updated(long _)
    {
      notifications++;
      namesAtNotification = RaidRosterStore.Instance.GetRosterRecords().Single().Players.Count;
    }

    RaidRosterStore.EventsRosterUpdated += Updated;
    try
    {
      Assert.IsTrue(Parse("[130 Spiritwatcher (Shaman)] Alpha (Group: 3)"));
      Assert.IsTrue(Parse("[130 Exemplar (Cleric)] Bravo (Group: None)"));
      Assert.IsEmpty(RaidRosterStore.Instance.GetRosterRecords());
      Assert.AreEqual(0, notifications, "individual who rows must never offer a partial roster");

      Assert.IsTrue(Parse("There are 2 players in your raid. "));
      var roster = RaidRosterStore.Instance.GetRosterRecords().Single();
      Assert.AreEqual(3, roster.Players["Alpha"]);
      Assert.AreEqual(0, roster.Players["Bravo"]);
      Assert.AreEqual(1, notifications);
      Assert.AreEqual(2, namesAtNotification, "notification follows publication of every name");

      Assert.IsTrue(Parse("There are 2 players in your raid."));
      Assert.AreEqual(1, notifications, "a footer without pending rows cannot republish the previous roster");
    }
    finally
    {
      RaidRosterStore.EventsRosterUpdated -= Updated;
    }
  }

  [TestMethod]
  public void NewRowsCannotMutateAnAlreadyPublishedRoster()
  {
    var store = RaidRosterStore.Instance;
    var capture = store.CaptureVersion;
    store.CapturePlayer(capture, "Alpha", 1, Begin);
    store.FlushCurrentGroup(capture, 1);
    var first = store.GetRosterRecords().Single();

    // Even a second listing in the same log second must accumulate in a separate, unpublished record.
    store.CapturePlayer(capture, "Bravo", 2, Begin);
    Assert.AreSame(first, store.GetRosterRecords().Single());
    Assert.IsFalse(first.Players.ContainsKey("Bravo"));
    store.FlushCurrentGroup(capture, 1);
    Assert.AreEqual("Bravo", store.GetRosterRecords().Single().Players.Keys.Single());
    Assert.AreEqual("Alpha", first.Players.Keys.Single());

    store.CapturePlayer(capture, "Charlie", 3, Begin + 10);
    store.FlushCurrentGroup(capture, 1);
    var sorted = store.GetRosterRecords();
    Assert.AreEqual("Charlie", sorted[0].Players.Keys.Single());
    Assert.AreEqual("Bravo", sorted[1].Players.Keys.Single());
  }

  [TestMethod]
  public void AListingWithoutItsFooterDoesNotBecomeASelectableRoster()
  {
    var store = RaidRosterStore.Instance;
    var capture = store.CaptureVersion;
    store.CapturePlayer(capture, "Incomplete", 1, Begin);
    store.CapturePlayer(capture, "Complete", 2, Begin + 10);
    store.FlushCurrentGroup(capture, 1);

    var roster = store.GetRosterRecords().Single();
    Assert.AreEqual("Complete", roster.Players.Keys.Single());
  }

  [TestMethod]
  [DataRow("48")]
  [DataRow("NotANumber")]
  public void AFooterThatDoesNotMatchTheCapturedCountDiscardsTheListing(string count)
  {
    var store = RaidRosterStore.Instance;
    var capture = store.CaptureVersion;
    store.CapturePlayer(capture, "Previous", 3, Begin - 10);
    store.FlushCurrentGroup(capture, 1);
    var previous = store.GetRosterRecords().Single();

    var notifications = 0;
    void Updated(long _) => notifications++;
    RaidRosterStore.EventsRosterUpdated += Updated;
    try
    {
      Assert.IsTrue(Parse("[130 Spiritwatcher (Shaman)] Partial (Group: 3)"));
      Assert.IsTrue(Parse($"There are {count} players in your raid."));
      Assert.AreSame(previous, store.GetRosterRecords().Single());
      Assert.AreEqual(0, notifications);

      Assert.IsTrue(Parse("[130 Spiritwatcher (Shaman)] Complete (Group: 5)"));
      Assert.IsTrue(Parse("There are 1 players in your raid."));
      Assert.AreEqual("Complete", store.GetRosterRecords()[0].Players.Keys.Single());
      Assert.AreEqual(1, notifications, "the rejected rows must not leak into the next same-second listing");
    }
    finally
    {
      RaidRosterStore.EventsRosterUpdated -= Updated;
    }
  }

  [TestMethod]
  public void SameServerClearRetainsOnlyPublishedRosters()
  {
    var store = RaidRosterStore.Instance;
    var capture = store.CaptureVersion;
    store.CapturePlayer(capture, "Complete", 3, Begin - 10);
    store.FlushCurrentGroup(capture, 1);
    store.CapturePlayer(capture, "Pending", 3, Begin);

    store.Clear(serverChanged: false);
    store.FlushCurrentGroup(store.CaptureVersion, 1);
    Assert.AreEqual("Complete", store.GetRosterRecords().Single().Players.Keys.Single());
  }

  [TestMethod]
  public async Task ARetiredProcessorCannotPublishOrConsumeTheNewCapturesListing()
  {
    var store = RaidRosterStore.Instance;
    var sinks = new NullSinks();
    using var retired = new LogProcessor(sinks, sinks);
    var retiredVersion = store.CaptureVersion;
    store.Clear();
    Assert.AreNotEqual(retiredVersion, store.CaptureVersion);
    Assert.IsTrue(Parse("[130 Spiritwatcher (Shaman)] Current (Group: 2)"));

    using var lines = new BlockingCollection<LogReaderItem[]>();
    retired.LinkTo(lines);
    lines.Add([
      new("[Thu Jan 01 12:00:00 2026] [130 Spiritwatcher (Shaman)] Retired (Group: 9)", Begin, false),
      new("[Thu Jan 01 12:00:00 2026] There are 1 players in your raid.", Begin, false)
    ]);
    lines.CompleteAdding();
    await retired.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Retired"), "the retired processor reached the who parser");
    Assert.IsEmpty(store.GetRosterRecords(), "a footer from another capture must not publish anything");

    Assert.IsTrue(Parse("There are 1 players in your raid."));
    Assert.AreEqual("Current", store.GetRosterRecords().Single().Players.Keys.Single(),
      "a retired row must not enter the pending roster, and its footer must not discard it");
  }

  [TestMethod]
  [DataRow("There are 2 players")]
  [DataRow("There are 2 players in")]
  [DataRow("There are 2 players in your")]
  public void ATruncatedFooterDoesNotThrowOrCompleteTheListing(string footer)
  {
    var store = RaidRosterStore.Instance;
    store.CapturePlayer(store.CaptureVersion, "Pending", 1, Begin);
    Assert.IsFalse(Parse(footer));
    Assert.IsEmpty(RaidRosterStore.Instance.GetRosterRecords());
  }

  private static bool Parse(string action) => MiscLineParser.Process(new LineData
  {
    Action = action, BeginTime = Begin, Split = action.Split(' ')
  }, RaidRosterStore.Instance.CaptureVersion);

  private sealed class NullSinks : IChatSink, ITriggerHook
  {
    public void Init() { }
    public void Add(ChatType chat) { }
    public void CheckQuickShare(ChatType chat, string action, double beginTime) { }
  }
}
