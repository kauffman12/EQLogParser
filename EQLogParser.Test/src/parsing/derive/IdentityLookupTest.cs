using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * The one question this application asks about a name, and the order it is asked in — docs/DesignNotes.md →
 * "The one seam that answers" is that order written out. Every test below is a case where two of the three sources disagree, because
 * that is the only place the ordering has consequences: where they agree any order works.
 *
 * State hygiene: LiveVerdict is process state like the other engine dials, so each test clears it and Cleanup clears it
 * again — a seam left wired from one test would send the next one's questions into a dead capture. Both stores are
 * initialised against an EMPTY server name, which keeps them in memory only (writes have nowhere to go), so this file
 * never touches the machine's real files; and Clear() on the registry because PlayerRegistry is the roster lane's
 * in-memory half and tests must not inherit another class's names.
 */
[TestClass]
public class IdentityLookupTest
{
  private const string Server = "Lookup Test";

  private string _root = string.Empty;
  private string _savedConfigDir = string.Empty;
  private string _savedServerName = string.Empty;

  [TestInitialize]
  public void Setup()
  {
    _savedConfigDir = ConfigUtil.ConfigDir;
    _savedServerName = ConfigUtil.ServerName;

    // A temp config dir and a server name this test class owns: the two stores behind the seam are per-server files and
    // the seam must not answer from whatever the machine's real EQLogParser folder holds.
    _root = Path.Combine(Path.GetTempPath(), "lookup-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_root);
    ConfigUtil.ConfigDir = _root;
    ConfigUtil.ServerName = Server;

    PlayerRegistry.Instance.Clear();
    IdentityOverrideStore.Instance.Init(Server);
    IdentityPriorStore.Instance.Init(Server);
    IdentityLookup.LiveVerdict = null;
  }

  [TestCleanup]
  public void Cleanup()
  {
    IdentityLookup.LiveVerdict = null;
    PlayerRegistry.Instance.Clear();

    // Load a server that does not exist so nothing remembered here answers for the next class.
    IdentityPriorStore.Instance.Init("lookup-cleanup-" + Guid.NewGuid().ToString("N"));
    IdentityOverrideStore.Instance.Init("lookup-cleanup-" + Guid.NewGuid().ToString("N"));
    ConfigUtil.ConfigDir = _savedConfigDir;
    ConfigUtil.ServerName = _savedServerName;
    try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { }
  }

  // Now, in the ledger's own clock: the roster retires against the wall clock, so a roster row written by a test is
  // "seen this second" and cannot be aged out by whatever the machine's clock says.
  private static long NowS() => ((DateTimeOffset) DateTime.UtcNow).ToUnixTimeSeconds();

  private static void Watch(string name, IdentityKind kind) =>
    IdentityLookup.LiveVerdict = (n, _) => string.Equals(n, name, StringComparison.OrdinalIgnoreCase) ? kind : IdentityKind.Unknown;

  [TestMethod]
  public void ANameNothingKnows_IsNotOnesOfOurs()
  {
    Assert.IsFalse(IdentityLookup.IsOneOfUs("Stranger"), "an unknown name answered yes");
    Assert.IsFalse(IdentityLookup.IsOneOfUs(""));
    Assert.IsFalse(IdentityLookup.IsOneOfUs(null));
  }

  [TestMethod]
  public void TheRosterAnswersWhenNothingWasWatched()
  {
    IdentityPriorStore.Instance.RememberRoster("Vexmilla", NowS(), "Cleric");

    Assert.IsTrue(IdentityLookup.IsOneOfUs("vexmilla"), "the ledger's roster is case-insensitive like every other name key");
    Assert.IsFalse(IdentityLookup.IsOneOfUs("Someoneelse"));
  }

  [TestMethod]
  public void APersonWordIsOnesOfOursBeforeAnyStoreIsAsked()
  {
    /*
     * "You fell to X", the shadow "your" a pet line carries, and the "Unassigned" row a stats table builds out of nothing:
     * no capture places them and no store can hold them, so if the lookup asked the rules first these rows would stop
     * being ours — and You-mapping across the app reads exactly those words.
     */
    Assert.IsTrue(IdentityLookup.IsOneOfUs("You"));
    Assert.IsTrue(IdentityLookup.IsOneOfUs("yourself"));
    Assert.IsTrue(IdentityLookup.IsOneOfUs(Labels.Unassigned));
  }

  [TestMethod]
  public void WhatTheCaptureWatchedOutranksWhatWasRemembered()
  {
    IdentityPriorStore.Instance.RememberRoster("Fenvel", NowS(), "Cleric");   // on the roster for three years
    Watch("Fenvel", IdentityKind.Npc);                            // and tonight's log says it is a mob

    Assert.IsFalse(IdentityLookup.IsOneOfUs("Fenvel"),
                   "memory outvoting this capture is the failure the seam exists to end");
  }

  [TestMethod]
  public void AVerdictPutsANameOnOurSideWithNoRosterRowAtAll()
  {
    Watch("Nexaliel", IdentityKind.Player);

    Assert.IsTrue(IdentityLookup.IsOneOfUs("Nexaliel"), "a name this log proved got no board membership because it was never typed anywhere");
  }

  [TestMethod]
  public void ANameTheRulesNeverSawFallsThroughToMemory()
  {
    /*
     * Unknown means "this capture said nothing about the name", which is not an answer about it — so memory speaks. Were
     * Unknown treated as a no, a mid-log attach (facts before the tap were never captured) would drop every regular the
     * operator had saved for the whole session.
     */
    IdentityPriorStore.Instance.RememberRoster("Silentra", NowS(), null);
    Watch("Someoneelse", IdentityKind.Npc);

    Assert.IsTrue(IdentityLookup.IsOneOfUs("Silentra"));
  }

  [TestMethod]
  public void AnOperatorVerdictEndsTheQuestion()
  {
    /*
     * The override is the only source that is not evidence, so it wins in both directions: the capture's verdict cannot
     * argue with a person who decided, and the roster cannot keep somebody on the raid's side after they were marked.
     */
    Watch("Brambel", IdentityKind.Player);
    IdentityOverrideStore.Instance.Set("Brambel", IdentityKind.Npc);
    Assert.IsFalse(IdentityLookup.IsOneOfUs("Brambel"));

    IdentityPriorStore.Instance.RememberRoster("Ghuntor", NowS(), null);
    Watch("Ghuntor", IdentityKind.Npc);
    IdentityOverrideStore.Instance.Set("Ghuntor", IdentityKind.Player);
    Assert.IsTrue(IdentityLookup.IsOneOfUs("Ghuntor"));
  }

  [TestMethod]
  public void APetOrMercVerdictIsNotAnOnesOfOurs()
  {
    /*
     * Mercenary fights beside us and this still answers no, because every caller that meant "player OR merc" says so
     * today (… || PlayerRegistry.IsMerc(name)) — folding mercs in here would silently widen those menus and filters
     * without anybody choosing it. Pet likewise: a pet is our side and not one of us.
     */
    Watch("Stormclaw", IdentityKind.Pet);
    Assert.IsFalse(IdentityLookup.IsOneOfUs("Stormclaw"));

    Watch("Kajid", IdentityKind.Merc);
    Assert.IsFalse(IdentityLookup.IsOneOfUs("Kajid"), "a mercenary is a different question — the callers ask it themselves");
  }

  [TestMethod]
  public void TheTimedFormAsksAboutTheMomentItWasGiven()
  {
    // The seam receives the time it was handed, so a charmed raider can be ours at 20:00 and not at 20:05.
    double asked = double.NaN;
    IdentityLookup.LiveVerdict = (_, t) => { asked = t; return IdentityKind.Unknown; };

    Assert.IsFalse(IdentityLookup.IsOneOfUs("Fenvel", 1_000d));
    Assert.AreEqual(1_000d, asked);

    IdentityLookup.IsOneOfUs("Fenvel");
    Assert.IsTrue(double.IsPositiveInfinity(asked), "the untimed form means 'as far as we know', which is the newest moment");
  }

  [TestMethod]
  public void TheLedgersNonRosterRowsDoNotAnswerThis()
  {
    /*
     * A rule's verdict that happens to live in the same file is not membership: an NPC row keeps the name OUT. (What the
     * ledger remembers about a mob is its own history, and only the roster bit claims the name for the raid.)
     */
    var timeline = new EntityTimeline();
    ClassificationRules.ApplyManualOverride(timeline, "Ghuntor", IdentityKind.Npc);
    IdentityPriorStore.Instance.Record(timeline, ["Ghuntor"], NowS());
    Assert.IsFalse(IdentityPriorStore.Instance.TryGetRoster("Ghuntor"), "the fixture row must not be a roster row");

    Assert.IsFalse(IdentityLookup.IsOneOfUs("Ghuntor"));
  }
}
