using EQLogParser.Mirror;

namespace EQLogParser;

/*
 * R10: the operator's verdict on a name, and the two properties that make it worth having.
 *
 *   - It outranks every rule (Manual strength) and is replayed into EVERY timeline the session builds, because
 *     rules re-run from scratch on each derive - an override living only in the previous timeline disappears at
 *     exactly the moment the user asked for the change.
 *   - It is stored per server and comes back after a reopen, with "clear" giving the rules' own answer again
 *     rather than a half-applied verdict.
 *
 * ConfigUtil.ConfigDir/ServerName are process globals, so each test parks them in a temp folder and restores
 * them; MirrorOverrideStore.Instance is a singleton for the same reason (AGENTS: anything touching process state
 * resets it, and this assembly does not parallelize).
 */
[TestClass]
public class MirrorOverrideTest
{
  private string _savedConfigDir = "";
  private string _savedServerName = "";
  private string _tempDir = "";

  [TestInitialize]
  public void ParkConfigInATempFolder()
  {
    _savedConfigDir = ConfigUtil.ConfigDir;
    _savedServerName = ConfigUtil.ServerName;

    _tempDir = Path.Combine(Path.GetTempPath(), "mirror-overrides-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_tempDir);
    ConfigUtil.ConfigDir = _tempDir;
    ConfigUtil.ServerName = "Ovrtest";
    MirrorOverrideStore.Instance.Init("Ovrtest");        // nothing on disk: an empty store
  }

  [TestCleanup]
  public void RestoreConfig()
  {
    ConfigUtil.ConfigDir = _savedConfigDir;
    ConfigUtil.ServerName = _savedServerName;
    MirrorOverrideStore.Instance.Init(_savedServerName);

    try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch (IOException) { }
  }

  [TestMethod]
  public void AStoredVerdictOutranksEveryRule()
  {
    var run = RunMirror(
      "[Mon May 04 19:00:00 2026] You hit a cave bear for 900 points of damage.",
      "[Mon May 04 19:00:05 2026] A cave bear hits You for 400 points of damage.");

    var timeline = Rules(run);
    Assert.AreEqual(IdentityKind.Npc, timeline.IdentityWithSource("A cave bear", out _),
                    "the control failed: the rules did not call this an NPC");

    MirrorOverrideStore.Instance.Set("A cave bear", IdentityKind.Player);
    MirrorOverrideStore.Instance.Apply(timeline);

    Assert.AreEqual(IdentityKind.Player, timeline.IdentityWithSource("A cave bear", out var source));
    Assert.AreEqual("R10-manual", source, "the verdict did not arrive through the manual override path");

    // The row goes with it: both names read raid-side, so nothing is an encounter anymore. That is the point of
    // the feature being a re-read rather than a badge on a row.
    Assert.AreEqual(0, FightProjection.Build(run.Facts, timeline).Count,
                    "the name kept a fight row after being declared a raider");
  }

  [TestMethod]
  public void AnOverrideIsReplayedIntoEveryFreshTimeline()
  {
    var run = RunMirror(
      "[Mon May 04 19:00:00 2026] You hit a cave bear for 900 points of damage.",
      "[Mon May 04 19:00:05 2026] A cave bear hits You for 400 points of damage.");

    MirrorOverrideStore.Instance.Set("A cave bear", IdentityKind.Pet);

    // What MirrorSession does on each pass: a timeline built from nothing, rules over it, then the override.
    var pass = Rules(run);
    MirrorOverrideStore.Instance.Apply(pass);
    Assert.AreEqual(IdentityKind.Pet, pass.IdentityWithSource("A cave bear", out _));

    // A second pass (the auto-derive that fires when the next line goes quiet) says the same thing.
    var nextPass = Rules(run);
    MirrorOverrideStore.Instance.Apply(nextPass);
    Assert.AreEqual(IdentityKind.Pet, nextPass.IdentityWithSource("A cave bear", out _));

    // A pet is raid-side but not a raider: off the list, its damage still inside whatever it hit.
    var rows = FightProjection.Build(run.Facts, nextPass);
    Assert.IsNull(rows.FirstOrDefault(r => r.Name == "A cave bear"));
  }

  [TestMethod]
  public void OverridesRoundTripThroughThePerServerFile()
  {
    MirrorOverrideStore.Instance.Set("Danglebait", IdentityKind.Pet);
    MirrorOverrideStore.Instance.Set("Triumph", IdentityKind.Merc);

    var file = Path.Combine(_tempDir, "Ovrtest", "mirror-overrides.txt");
    Assert.IsTrue(File.Exists(file), $"no per-server file was written at {file}");

    var reopened = new MirrorOverrideStore();
    reopened.Init("Ovrtest");
    Assert.IsTrue(reopened.TryGet("Danglebait", out var kind));
    Assert.AreEqual(IdentityKind.Pet, kind);
    Assert.IsTrue(reopened.TryGet("Triumph", out var merc));
    Assert.AreEqual(IdentityKind.Merc, merc);

    // Removing one leaves the other alone, and a reopen sees the removal.
    MirrorOverrideStore.Instance.Remove("Danglebait");
    reopened = new MirrorOverrideStore();
    reopened.Init("Ovrtest");
    Assert.IsFalse(reopened.TryGet("Danglebait", out _), "the cleared verdict came back from the file");
    Assert.IsTrue(reopened.TryGet("Triumph", out _));
  }

  [TestMethod]
  public void ClearingAnOverrideGivesTheRulesOwnAnswerBack()
  {
    var run = RunMirror(
      "[Mon May 04 19:00:00 2026] You hit a cave bear for 900 points of damage.",
      "[Mon May 04 19:00:05 2026] A cave bear hits You for 400 points of damage.");

    MirrorOverrideStore.Instance.Set("A cave bear", IdentityKind.Player);
    var overridden = Rules(run);
    MirrorOverrideStore.Instance.Apply(overridden);
    Assert.AreEqual(IdentityKind.Player, overridden.IdentityWithSource("A cave bear", out _));

    MirrorOverrideStore.Instance.Remove("A cave bear");
    var restored = Rules(run);
    MirrorOverrideStore.Instance.Apply(restored);
    Assert.AreEqual(IdentityKind.Npc, restored.IdentityWithSource("A cave bear", out _),
                    "clearing left a verdict behind - overrides must be additive and reversible");
  }

  // Names are looked up without case everywhere in this pipeline (the parser capitalizes what EQ wrote), and an
  // ordinal key here would silently miss the name a saved verdict was written for.
  [TestMethod]
  public void ASavedVerdictFindsItsNameWhateverTheCase()
  {
    MirrorOverrideStore.Instance.Set("danglebait", IdentityKind.Pet);

    var timeline = new EntityTimeline();
    MirrorOverrideStore.Instance.Apply(timeline);

    Assert.AreEqual(IdentityKind.Pet, timeline.IdentityWithSource("Danglebait", out _));
  }

  // ---- helpers ----

  private static EntityTimeline Rules(PipelineHarness.MirrorRunResult run)
  {
    var timeline = new EntityTimeline();
    ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);
    return timeline;
  }

  private static PipelineHarness.MirrorRunResult RunMirror(params string[] lines)
  {
    var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mirror-ovrrun-" + Guid.NewGuid().ToString("N")));
    var log = Path.Combine(dir.FullName, "eqlog_Probeone_Eqgate.txt");   // filename seeds ConfigUtil.PlayerName
    try
    {
      File.WriteAllLines(log, lines);
      return PipelineHarness.RunFileWithMirror(log);
    }
    finally
    {
      try { Directory.Delete(dir.FullName, true); } catch (IOException) { }
    }
  }
}
