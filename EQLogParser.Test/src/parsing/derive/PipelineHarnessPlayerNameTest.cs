using EQLogParser;

namespace EQLogParser;

/*
 * Every capture gets a DEFINED local player, and never the one some earlier test happened to leave behind.
 *
 * `You hit X.` and `You begin casting Y.` put ConfigUtil.PlayerName in the subject slot — CastLineParser drops the cast record
 * entirely when that name is empty — so an unnamed capture has no charm confirm pairing, no R0-local claim and no loot rows, and it
 * fails as "the rules found nothing" rather than as an error. The suite used to satisfy that need by accident: the first
 * eqlog_(Player)_(Server).txt-shaped fixture parsed stamped a name for the rest of the process, so tests further down the list passed.
 * Class order is not the same on `dotnet test` and on Visual Studio's runner, which is how one spelling of this suite showed nine green
 * and the other showed nine failures (each of them failing in isolation as well).
 *
 * These four assert the replacement rule in PipelineHarness.RunCore from both ends: what the parser recorded, not just the field.
 * docs/DesignNotes.md → "A You line needs a local player, and not out of whoever ran first".
 */
[TestClass]
public class PipelineHarnessPlayerNameTest
{
    private static readonly string[] YouHitLine =
    [
        "[Sun Apr 26 18:00:00 2026] You hit a namekey pet for 900 points of damage.",
    ];

    private string _priorPlayerName = string.Empty;
    private DirectoryInfo _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        PipelineHarness.EnsureDataStore();
        _priorPlayerName = ConfigUtil.PlayerName;
        _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "playername-" + Guid.NewGuid().ToString("N")));
    }

    [TestCleanup]
    public void AfterEachTest()
    {
        ConfigUtil.PlayerName = _priorPlayerName;
        try { Directory.Delete(_dir.FullName, true); } catch (IOException) { }
    }

    // Writes one capture file and returns its path. `fileName` decides whether the harness reads a player out of it.
    private string Write(string fileName, string[] lines)
    {
        var path = Path.Combine(_dir.FullName, fileName);
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string FirstAttacker(DamageFactTable facts)
    {
        Assert.IsTrue(facts.Facts.Length > 0, "the capture recorded no damage rows");
        return facts.NameOf(facts.Facts[0].AtkIdx);
    }

    [TestMethod]
    public void AnUnnamedCaptureIsReadAsCastByTheFixturePlayer()
    {
        ConfigUtil.PlayerName = "";      // nothing said: not a statement of "no player", just no name

        var run = PipelineHarness.RunFileDerived(Write("plain-shape.txt", YouHitLine));

        Assert.AreEqual(PipelineHarness.FixturePlayerName, ConfigUtil.PlayerName,
            "a capture with no filename player and no test-set name must end on the fixture player");
        Assert.AreEqual(PipelineHarness.FixturePlayerName, FirstAttacker(run.Facts),
            "the YOU in the line has to reach the fact table as that name");
    }

    // The same file twice: the second run inherits the first one's stamp and must be re-decided, not read as a test's own opinion.
    [TestMethod]
    public void ANameStampedByAnEarlierCaptureIsNotInheritedAsIntent()
    {
        ConfigUtil.PlayerName = "";
        _ = PipelineHarness.RunFileDerived(Write("eqlog_Remoteowner_Remoteserver.txt", YouHitLine));
        Assert.AreEqual("Remoteowner", ConfigUtil.PlayerName, "the filename named that capture's player");

        var run = PipelineHarness.RunFileDerived(Write("plain-shape.txt", YouHitLine));

        Assert.AreEqual(PipelineHarness.FixturePlayerName, ConfigUtil.PlayerName,
            "a name the harness wrote is drift: the next unnamed capture decides again");
        Assert.AreEqual(PipelineHarness.FixturePlayerName, FirstAttacker(run.Facts));
    }

    [TestMethod]
    public void ALogFileNameNamesItsOwnPlayer()
    {
        ConfigUtil.PlayerName = "";

        var run = PipelineHarness.RunFileDerived(Write("eqlog_Feedbackone_Eqgate.txt", YouHitLine));

        Assert.AreEqual("Feedbackone", ConfigUtil.PlayerName);
        Assert.AreEqual("Feedbackone", FirstAttacker(run.Facts),
            "You-mapping and R0-local must behave as they do in production, where the name comes from the filename");
    }

    // A test's own choice outranks the fixture default — this is HealCreditLawTest parsing as "Kizant".
    [TestMethod]
    public void ANameTheTestSetSurvivesItsOwnCapture()
    {
        const string chosen = "Querulous";
        ConfigUtil.PlayerName = chosen;

        var run = PipelineHarness.RunFileDerived(Write("plain-shape.txt", YouHitLine));

        Assert.AreEqual(chosen, ConfigUtil.PlayerName, "a stated condition is not drift");
        Assert.AreEqual(chosen, FirstAttacker(run.Facts));
    }

    /*
     * The consequence that hid longest: a charm whose confirm names the mob lower-case opens its window over the capitalized fact
     * name — but only if the capture has a local player to own it. Run here with nothing else in the process, this is exactly what
     * EntityNameKeyTest asks, and it used to answer only when some earlier fixture had named a player.
     */
    [TestMethod]
    public void ACharmNeedsNoEarlierTestToNameThePlayer()
    {
        ConfigUtil.PlayerName = "";

        var run = PipelineHarness.RunFileDerived(Write("plain-charm.txt",
        [
            "[Sun Apr 26 17:50:00 2026] You hit a namekey pet for 900 points of damage.",
            "[Sun Apr 26 18:00:03 2026] You begin casting Charm XVII.",
            "[Sun Apr 26 18:00:07 2026] a namekey pet has been charmed.",
            "[Sun Apr 26 18:00:20 2026] A namekey pet hits Bob for 250 points of damage.",
        ]));

        Assert.IsTrue(run.Facts.Evidence.ToArray().Any(e => e.Kind == EvidenceFact.EvCharmStart),
            "the confirm paired with nothing, so no charm window exists");

        var timeline = new EntityTimeline();
        _ = ClassificationRules.Apply(run.Facts, timeline);
        var charmAt = DateUtil.StandardDateToDotNetSeconds("[Sun Apr 26 18:00:07 2026] x");
        Assert.IsTrue(timeline.IsCharmedAt("A namekey pet", charmAt + 13), "the window must cover the second after it");
    }
}
