using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * WHICH class the Identity Table shows, when two lanes have answers (2026-11).
 *
 * The operator's report: "i can change type but not class. the dropdown comes up, i click, and it goes away" — with the log line proving the
 * write landed (`identity cell: class of Farzi -> Berserker`, then `-> Monk`). Nothing was broken in the write: the CLASS CELL read
 * `GetLastKnownPlayerClass`, which prefers this capture's cast-learned window and treats the saved word as a fallback only. On the rows whose
 * class pencil is showing at all — Player rows that CAST something, which is what teaches a window — an operator's pick therefore could not
 * be seen, in the one window where a class is written.
 *
 * Two lanes, two questions, and only the pane's order changes:
 *   board / `GetPlayerClass(name, t)`  → "what did this log watch?" (time-correct windows; a mid-log reclass must still read per second)
 *   Identity Table                     → "what did we decide?" (the saved word first, the capture's answer when nothing is saved)
 * Clearing the saved word — the dropdown's blank entry, or the take-back — hands the cell back to what this log says. Pinned below, together
 * with the fact that boards are NOT moved by the saved word, which is the whole reason this fix is a display law rather than a registry one.
 * (docs/DesignNotes.md → "The class the window writes and the class the log watched")
 */
[TestClass]
public sealed class ClassColumnPrecedenceTest
{
  private const string Server = "ClassColumn Test";
  private const string Name = "Probeclassed";

  private string _root = string.Empty;
  private string _savedConfigDir = string.Empty;
  private string _savedServerName = string.Empty;
  private Func<string, bool>? _originalIsValidClass;

  [TestInitialize]
  public void Setup()
  {
    _savedConfigDir = ConfigUtil.ConfigDir;
    _savedServerName = ConfigUtil.ServerName;

    _root = Path.Combine(Path.GetTempPath(), "classcol-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_root);
    ConfigUtil.ConfigDir = _root;
    ConfigUtil.ServerName = Server;

    PlayerRegistry.Instance.Clear();
    IdentityOverrideStore.Instance.Init(Server);
    IdentityPriorStore.Instance.Init(Server);
    IdentityLookup.LiveVerdict = null;
    TakenBackNames.Clear();

    // The class word validator is a host hook (App.xaml.cs wires the real one); tests own it, as FrenzyClassTest does.
    _originalIsValidClass = CombatRecordLookup.IsValidClassName;
    CombatRecordLookup.IsValidClassName = word => word is "Bard" or "Cleric";
  }

  [TestCleanup]
  public void Cleanup()
  {
    CombatRecordLookup.IsValidClassName = _originalIsValidClass ?? (_ => false);

    IdentityLookup.LiveVerdict = null;
    PlayerRegistry.Instance.Clear();
    IdentityPriorStore.Instance.Init("classcol-cleanup-" + Guid.NewGuid().ToString("N"));
    IdentityOverrideStore.Instance.Init("classcol-cleanup-" + Guid.NewGuid().ToString("N"));
    TakenBackNames.Clear();
    ConfigUtil.ConfigDir = _savedConfigDir;
    ConfigUtil.ServerName = _savedServerName;
    try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { }
  }

  /*
   * The census with no capture: overrides + registry + ledger. Listing the name needs one of those three, and an operator verdict is the
   * cheapest honest one — it says nothing about class, which is the thing under test.
   */
  private static ClassificationReport Census() =>
      ClassificationReport.Build(null, null, null, IdentityOverrideStore.Instance, PlayerRegistry.Instance, IdentityPriorStore.Instance);

  // A name listed by a verdict, with a cast-learned class window of its own (confidence 2 = the CastLineParser peer).
  private static void LearnFromTheCapture(string className, double atSeconds = 100.0) =>
      PlayerRegistry.Instance.SetActivePlayerClass(Name, className, 2, atSeconds);

  [TestMethod]
  public void AClassThisCaptureLearnedIsWhatTheRowShowsWhileNothingIsSaved()
  {
    ClassificationCommands.ApplyVerdict(Name, IdentityKind.Player);
    LearnFromTheCapture("Cleric");

    Assert.AreEqual(string.Empty, PlayerRegistry.Instance.GetDefaultPlayerClass(Name), "control: nothing saved");
    Assert.AreEqual("Cleric", Census().Find(Name)!.Class, "with no saved word, the capture's answer is the whole answer");
  }

  /*
   * THE FIX. The saved word shows in the column that writes it, and the board question keeps its own answer. Assert BOTH directions: a
   * test that only checked the cell would pass if somebody "fixed" this by making the registry return the default everywhere, which would
   * erase mid-log reclasses from every board — the class law is that class is per-second evidence.
   */
  [TestMethod]
  public void ASavedClassShowsInTheColumnThatWritesItWhileTheBoardQuestionStaysTimeCorrect()
  {
    ClassificationCommands.ApplyVerdict(Name, IdentityKind.Player);
    LearnFromTheCapture("Cleric");

    Assert.IsTrue(PlayerRegistry.Instance.SetDefaultPlayerClass(Name, "Bard"), "the operator's word is accepted");

    Assert.AreEqual("Bard", Census().Find(Name)!.Class,
                    "the cell that the pencil writes into must show what was written — capture-first order made an accepted pick invisible (Farzi)");
    Assert.AreEqual("Cleric", PlayerRegistry.Instance.GetPlayerClass(Name, 200.0),
                    "and no board moves: a board reports what this log watched, per second, with the saved word as fallback only");
  }

  /*
   * Taking the saved word back — the dropdown's blank entry, or the take-back button, which forgets this lane too — hands the cell back to
   * this capture. This is why the blank entry is a VERB and not "picked nothing".
   */
  [TestMethod]
  public void ForgettingTheSavedWordHandsTheCellBackToTheCapture()
  {
    ClassificationCommands.ApplyVerdict(Name, IdentityKind.Player);
    LearnFromTheCapture("Cleric");
    PlayerRegistry.Instance.SetDefaultPlayerClass(Name, "Bard");

    Assert.IsTrue(PlayerRegistry.Instance.SetDefaultPlayerClass(Name, string.Empty), "an empty word clears the lane rather than storing one");
    Assert.AreEqual(string.Empty, PlayerRegistry.Instance.GetDefaultPlayerClass(Name));
    Assert.AreEqual("Cleric", Census().Find(Name)!.Class, "no saved answer means the capture's answer shows again");
  }

  /*
   * The write answers (2026-11). Its result used to be dropped while the pane printed its success line, so a refusal looked like an accepted
   * pick in the log — which is how this defect stayed invisible for a report that quoted two INFO lines.
   */
  [TestMethod]
  public void AWordThisBuildDoesNotAcceptIsRefusedRatherThanReportedAsWritten()
  {
    Assert.IsFalse(PlayerRegistry.Instance.SetDefaultPlayerClass(Name, "Zebra"), "the validator is the door");
    Assert.AreEqual(string.Empty, PlayerRegistry.Instance.GetDefaultPlayerClass(Name), "and nothing was written behind it");

    Assert.IsTrue(PlayerRegistry.Instance.SetDefaultPlayerClass(Name, "Bard"));

    // The player's own name is not a durable row either: the parsers resolve "You" before this point.
    Assert.IsFalse(PlayerRegistry.Instance.SetDefaultPlayerClass("You", "Bard"));
  }
}
