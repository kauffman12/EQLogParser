using System;
using System.Collections.Concurrent;

using EQLogParser;

namespace EQLogParser;

/*
 * A ParseLane is the unit of stream ownership: one consumer thread, its parser events, and its cross-line carries.
 * The law these tests pin:
 *
 *   1. An unbound thread IS the Main lane — every pre-lane subscriber (FctManager, DamageRibbon, this suite)
 *      keeps landing exactly where it always landed.
 *   2. The parsers' static events are Main-lane forwarders; a second stream's raises must not reach them, and
 *      Main's raises must not reach the second stream. Fan-out to every capture is the bug this whole layer
 *      exists to prevent: two captures over one file would each write the other's lines.
 *   3. Carries (the sentence before a death, crit pairing) do not cross lanes. This is asserted behaviourally —
 *      a death on an unbound thread after a damage line parsed on another lane must have NO Previous — because
 *      "static field renamed to a lane field" compiles either way and only the run says which one it became.
 *   4. PlayerRegistry is deliberately NOT lane state: a second stream's verified player lands in the shared
 *      registry, because identity memory is process-wide by design (see ParseLane's header for the consequence —
 *      registry events reach every subscribed capture).
 */
[TestClass]
public class ParseLaneTest
{
  private sealed class NullSinks : IChatSink, ITriggerHook
  {
    public void Init()
    {
    }

    public void Add(ChatType chat)
    {
    }

    public void CheckQuickShare(ChatType chat, string action, double beginTime)
    {
    }
  }

  [TestInitialize]
  public void Setup()
  {
    ParseLane.BindCurrentThread(null);
    DamageLineParser.ResetProcessState();
    PlayerRegistry.Instance.Clear();
  }

  [TestCleanup]
  public void Cleanup()
  {
    ParseLane.BindCurrentThread(null);
    DamageLineParser.ResetProcessState();
    PlayerRegistry.Instance.Clear();
  }

  private static LineData Line(string action, double t) => new() { Action = action, BeginTime = t, LineNumber = 1 };

  [TestMethod]
  public void AnUnboundThreadIsTheMainLane()
  {
    Assert.AreSame(ParseLane.Main, ParseLane.Current, "an unbound thread must parse on Main, not on null");

    var lane = new ParseLane();
    ParseLane.BindCurrentThread(lane);
    Assert.AreSame(lane, ParseLane.Current);

    // The release a consumer's finally performs: the pooled thread goes back to being "no particular stream",
    // which means Main — not to a dangling lane it would contaminate on its next task.
    ParseLane.BindCurrentThread(null);
    Assert.AreSame(ParseLane.Main, ParseLane.Current);
  }

  [TestMethod]
  public void StaticParserEventsSubscribeToTheMainLane()
  {
    var hits = 0;
    Action<string, double, byte> probe = (name, time, kind) => hits++;
    ParseLane.Main.Evidence += probe;
    try
    {
      ParseLane.Main.Evidence?.Invoke("Kizant", 1.0, EvidenceFact.EvJoinedRaid);
      Assert.AreEqual(1, hits, "a raise on Main did not reach Main's subscriber");

      // Another lane's raise must stay home. This is the fan-out half of the law: with process-static events,
      // this invoke would have reached every capture in the process.
      var auxLane = new ParseLane();
      auxLane.Evidence?.Invoke("Kizant", 1.0, EvidenceFact.EvJoinedRaid);
      Assert.AreEqual(1, hits, "a raise on another lane leaked into Main's subscribers");

      // And the reverse: a subscriber attached to another lane's field must not see Main's raise.
      var otherHits = 0;
      auxLane.Evidence += (name, time, kind) => otherHits++;
      ParseLane.Main.Evidence?.Invoke("Kizant", 1.0, EvidenceFact.EvJoinedRaid);
      Assert.AreEqual(0, otherHits, "Main's raise reached another lane's subscriber");
    }
    finally
    {
      ParseLane.Main.Evidence -= probe;
    }
  }

  [TestMethod]
  public void AProcessorRaisesOnItsOwnLaneOnly()
  {
    // The real seam: LogProcessor binds its lane on the consumer thread, so the line's evidence lands there —
    // never on Main, where the running session's capture would double-count it.
    var auxLane = new ParseLane();
    string? onOther = null;
    string? onMain = null;
    Action<string, double, byte> mainProbe = (name, time, kind) => onMain = name;
    auxLane.Evidence += (name, time, kind) => onOther = name;
    PreLineParser.EventsEvidence += mainProbe;

    try
    {
      using var items = new BlockingCollection<LogReaderItem>(new ConcurrentQueue<LogReaderItem>());
      using var processor = new LogProcessor("auxLane.txt", new NullSinks(), new NullSinks(), auxLane);
      processor.LinkTo(items);

      const string line = "[Sun Apr 26 18:41:02 2026] Bithika joined the raid.";
      var dt = DateUtil.ParseStandardDate(line);
      items.Add(new LogReaderItem(line, DateUtil.ToDotNetSeconds(dt), false));
      items.CompleteAdding();
      Assert.IsTrue(processor.Completion!.Wait(TimeSpan.FromSeconds(60)));

      Assert.AreEqual("Bithika", onOther, "the second processor's evidence did not reach its own lane");
      Assert.IsNull(onMain, "a second stream's line fanned into the Main lane — the exact double-count lanes prevent");

      // Law 4: identity memory is process-wide even for a second stream.
      Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPlayer("Bithika"),
        "the shared registry must learn names from every stream; only EVENTS and carries are lane state");
    }
    finally
    {
      PreLineParser.EventsEvidence -= mainProbe;
    }
  }

  [TestMethod]
  public void CarriesDoNotCrossLanes()
  {
    // DeathRecord.Previous is the visible edge of the PreviousAction carry. Parsed in order on one lane, the
    // death quotes the line before it; parsed afterwards on an UNBOUND thread (Main), the same death must have
    // no Previous at all — if the carry were still process-wide, Main would inherit the other lane's sentence and
    // this test would fail while every single-lane test stayed green.
    DeathEvent? onOther = null;
    DeathEvent? onMain = null;
    var auxLane = new ParseLane();
    auxLane.NewDeath += e => onOther = e;
    DamageLineParser.EventsNewDeath += e => onMain = e;

    try
    {
      ParseLane.BindCurrentThread(auxLane);
      DamageLineParser.Process(Line("Ghoul hits Kizant for 12 damage.", 100));
      DamageLineParser.Process(Line("Kizant has been slain by a ghoul.", 101));
      ParseLane.BindCurrentThread(null);

      // Main parses a death with nothing before it in ITS stream. Same process, same second, no carry.
      DamageLineParser.Process(Line("Vex has been slain by a ghoul.", 102));
    }
    finally
    {
      ParseLane.BindCurrentThread(null);
      auxLane.NewDeath -= e => onOther = e;
      DamageLineParser.EventsNewDeath -= e => onMain = e;
    }

    Assert.IsNotNull(onOther);
    Assert.AreEqual("Ghoul hits Kizant for 12 damage.", onOther.Record.Previous,
      "the bound lane's own carry did not reach its death record");
    Assert.IsNotNull(onMain);
    Assert.IsNull(onMain.Record.Previous,
      "the previous sentence crossed lanes — carries must belong to the stream that parsed them");
  }

  [TestMethod]
  public void EachLaneHasItsOwnSpellTypeMemo()
  {
    var auxLane = new ParseLane();
    Assert.AreNotSame(ParseLane.Main.SpellTypeCache, auxLane.SpellTypeCache);
    Assert.AreEqual(-1, auxLane.LastRolledLine, "a fresh lane arrives with clean carries — that is what 'born clean' means");
  }
}
