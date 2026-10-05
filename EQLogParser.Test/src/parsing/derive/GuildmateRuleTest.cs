using EQLogParser;

namespace EQLogParser;

/*
 * R22: "Your guildmate X has completed … achievement." — the one piece of identity evidence that reached the players a
 * cold pass cannot place, because the client writes it out of its OWN guild list rather than inferring anything from
 * what a name did.
 *
 * Census before writing the rule (docs/DesignNotes.md → "What the cold misses actually are"): 10,541 lines over four
 * captures and EVERY one reads "Your guildmate <name> has completed" — no other verb on any capture; the 433 distinct
 * names across two of them are letters only, no dots, no server qualifiers; and not one of those names is placed Npc
 * or Pet by the rule book. It matters because the alternative evidence for these people does not exist: of the roster
 * names a cold pass could not place on Incogitable, exactly ONE has any attack fact at all, and 107 of 136 have no
 * identity evidence of any kind.
 *
 * The refusals are tested alongside it: tells, shout/ooc, zone chatter and `begins singing` claim NOTHING, because each
 * of those shapes can name an NPC — `] Bane tells General:1, 'WTS Full NoS collect sets 3kr each'` is a bazaar hailer
 * whose name sits in npcs.txt, and `Shalowain begins singing her Rhapsody of Pain.` is a bard in the same file.
 */
[TestClass]
[DoNotParallelize]
public class GuildmateRuleTest
{
    private string? _playerName;

    [TestInitialize]
    public void Setup()
    {
        _playerName = ConfigUtil.PlayerName;
        PlayerRegistry.Instance.Clear();
    }

    [TestCleanup]
    public void Cleanup()
    {
        ConfigUtil.PlayerName = _playerName;
        PlayerRegistry.Instance.Clear();
    }

    // ---- the recognizer (PreLineParser) ----

    /*
     * The real line shapes, copied from a live capture: an achievement of any wording, and one whose name carries an
     * apostrophe — the achievement's name is not part of the match, only the verb that follows the player's.
     */
    [TestMethod]
    public void TheAnnouncementIsRecognisedAndItsNameExtracted()
    {
        Assert.AreEqual("Blazem", GuildmateOf("Your guildmate Blazem has completed Partisan of Candlemaker's Workshop achievement."));
        Assert.AreEqual("Sarnak", GuildmateOf("Your guildmate Sarnak has completed Dragon Slaying achievement."));

        // The shape matches without case; what comes back is what the LINE wrote, because every entity key downstream
        // is case-insensitive and the pipeline capitalises for display on its own.
        Assert.AreEqual("blazem", GuildmateOf("your guildmate blazem HAS COMPLETED Something achievement."));
    }

    /*
     * What must NOT come out of this shape: a server-qualified name (never a key this pipeline takes), an empty one,
     * and any line that wears the prefix without saying what the guildmate did.
     */
    [TestMethod]
    public void ANameThisPipelineCannotKeyOnIsNotRecognised()
    {
        AssertIsNull("Your guildmate Blazem.Rukxan has completed Something achievement.");
        AssertIsNull("Your guildmate has completed Something achievement.");
        AssertIsNull("Your guildmate Blazem");

        // The shapes that were refused rather than made into rules (each of them can name a mob): bazaar chatter in
        // the General channel, and an NPC bard's song.
        AssertIsNull("Bane tells General:1, 'WTS Full NoS collect sets 3kr each'.");
        AssertIsNull("Shalowain begins singing her Rhapsody of Pain.");
    }

    // ---- the claim (ClassificationRules) ----

    /*
     * The announcement alone puts a name on our side, wearing its own word — and nothing else, because an achievement
     * line is not combat: no fact may ride it, or a guild ping would open a fight row.
     */
    [TestMethod]
    public void AGuildmateAnnouncementClaimsTheNameAsAPlayer()
    {
        var run = RunDerive(
            "[Mon May 04 19:00:01 2026] Your guildmate Blazem has completed Partisan of Candlemaker's Workshop achievement.");

        Assert.AreEqual(0, run.Facts.FactCount, "an achievement line created a combat fact");
        Assert.AreEqual(1, run.Facts.EvidenceCount, "the announcement did not reach the evidence table");
        AssertIdentity(Cold(run), "Blazem", IdentityKind.Player, "R22-guildmate");
    }

    /*
     * Strong, not Certain — the same place R17's drink and R19's eye sit: under the frames the client produces from
     * looking at the entity itself, over a name list. The half that matters for a guild roster is the name list, because
     * person-shaped names DO sit in npcs.txt (`Alleza`, `Mirala`) and the capture cannot tell a mob with such a name
     * from somebody's guildmate — the announcement is the better evidence about the one it names.
     */
    [TestMethod]
    public void AGuildmateTheNpcListKnowsIsStillAPlayer()
    {
        PipelineHarness.EnsureDataStore();
        Assert.IsTrue(EQDataStore.Instance.IsKnownNpc("Alleza"), "npcs.txt no longer knows Alleza — pick a different name for this test");

        var timeline = Cold(RunDerive(
            "[Mon May 04 19:00:01 2026] Your guildmate Alleza has completed Something achievement."));

        AssertIdentity(timeline, "Alleza", IdentityKind.Player, "R22-guildmate");
    }

    /*
     * The announcements do not drown the rest of the file: a guildmate who IS somebody in this capture keeps whatever
     * the capture watched, and the two claims coexist on one row.
     */
    [TestMethod]
    public void AGuildmateWhoFightsKeepsTheFight()
    {
        var run = RunDerive(
            "[Mon May 04 19:00:01 2026] Your guildmate Blazem has completed Partisan of Candlemaker's Workshop achievement.",
            "[Mon May 04 19:00:05 2026] Blazem hits a fang spider for 100 points of damage.",
            "[Mon May 04 19:00:06 2026] Blazem hits a fang spider for 100 points of damage.");

        var timeline = Cold(run);
        Assert.AreEqual(IdentityKind.Player, timeline.Identity("Blazem"));
        Assert.IsTrue(Held(timeline, "Blazem") >= Held(RunIdentityOnly("[Mon May 04 19:00:05 2026] Blazem hits a fang spider for 100 points of damage."), "Blazem"),
            "the announcement left the name weaker than the swing alone");
    }

    // ---- helpers ----

    private static string? GuildmateOf(string line) => PreLineParser.TryGetGuildmate(line, out var name) ? name : null;

    private static void AssertIsNull(string line)
    {
        Assert.IsNull(GuildmateOf(line), $"{line} was taken for a guild announcement");
    }

    private static EntityTimeline Cold(PipelineHarness.DeriveRunResult run)
    {
        var timeline = new EntityTimeline();
        ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);
        return timeline;
    }

    private static EntityTimeline RunIdentityOnly(string line)
    {
        var timeline = new EntityTimeline();
        var run = RunDerive(line);
        ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);
        return timeline;
    }

    private static int Held(EntityTimeline timeline, string name)
    {
        timeline.IdentityAt(name, double.PositiveInfinity, out var strength, out _);
        return strength;
    }

    private static PipelineHarness.DeriveRunResult RunDerive(params string[] lines)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "derive-gm-" + Guid.NewGuid().ToString("N")));
        var log = Path.Combine(dir.FullName, "eqlog_Probeone_Eqgate.txt");   // filename seeds ConfigUtil.PlayerName
        try
        {
            File.WriteAllLines(log, lines);
            return PipelineHarness.RunFileDerived(log);
        }
        finally
        {
            try { Directory.Delete(dir.FullName, true); } catch (IOException) { }
        }
    }

    private static void AssertIdentity(EntityTimeline timeline, string name, IdentityKind expected, string expectedSource)
    {
        var kind = timeline.IdentityWithSource(name, out var source);
        Assert.AreEqual(expected, kind, $"{name}: expected {expected}, got {kind} ({source ?? "no assignment"})");
        Assert.AreEqual(expectedSource, source, $"{name}: wrong rule got there first");
    }
}
