using System.Collections.Concurrent;
using System.Reflection;

using EQLogParser;

namespace EQLogParser;

/*
 * A read of this application's memory may not CREATE memory (2026-11, found by replaying the reported gesture over
 * `eqlog_Kizant_xegony-09-03-26.txt`).
 *
 * `PlayerRegistry.IsVerifiedPet` used to answer "is this a pet?" by ADDING an ownerless pet-map row whenever the name stood in
 * `petnames.txt`. Nothing distinguished that row from one a log line had earned — except the save path, which filters
 * game-generated names out of petmapping.txt — and `RegistrySeed.ApplyPetMappings` walks the map on every pass and files a Pet
 * claim plus a Strong `PetOfPlayer` affiliation for whatever it finds there. So the answer to "what IS this name?" depended on
 * whether some surface had happened to ask about it: `HealingStatsBuilder` asks per name (`IsPetOrPlayerOrMerc`).
 *
 * Measured on that capture: classify → build the whole-capture healing board → classify again over an UNCHANGED capture, and
 * `Venartik` read `Pet · RegistrySeed` where the first pass said `Player · R15-healed`. That single move re-routed 8
 * damage-taken facts (308,103 points) and rebuilt every board a second time — the second whole build behind the first Select All.
 * It is also the best candidate for the older note that "two runs over the same file give different row-name sets".
 *
 * The rule these tests hold: asking is free; only an observation (a line that named the pet) or an operator's word writes a pair.
 */
[TestClass]
[DoNotParallelize]
public class MemoryReadIsNotASightingTest
{
    // A person-shaped name, which is exactly what petnames.txt is full of — a named pet the game generated.
    private const string GamePetName = "Venartik";

    [TestInitialize]
    public void Setup() => PlayerRegistry.Instance.Clear();

    [TestCleanup]
    public void Cleanup()
    {
        GameGeneratedPets().TryRemove(GamePetName, out _);
        PlayerRegistry.Instance.Clear();
    }

    /*
     * The predicate itself: still answers yes for a name on the game's pet list, and leaves nothing behind — no pet-map row, no
     * owner. Before the fix this call wrote `Venartik -> Unassigned`, which is a claim about a summon nobody named.
     *
     * This used to assert one more thing through `PlayerRegistry.EventsNewPetMapping`: that a read announced no learned pair. That
     * channel is DELETED (2026-10-09, with the Pet Owners window that was its only subscriber), so the assertion is gone too — and
     * nothing weaker replaces it. What holds the law is the row itself: an invented pair shows up in `GetPetMappings`, which is what
     * `RegistrySeed` walks and what therefore decides whether a name becomes Pet on the next pass. An event was the echo; this is the sound.
     */
    [TestMethod]
    public void AskingWhetherAGameGeneratedNameIsAPetWritesNothing()
    {
        GameGeneratedPets()[GamePetName] = 1;

        var before = PlayerRegistry.Instance.GetPetMappings().Count;

        Assert.IsTrue(PlayerRegistry.Instance.IsVerifiedPet(GamePetName),
            "a name on the game's pet list stopped answering the question — the ANSWER is what the list is for");
        Assert.IsTrue(PlayerRegistry.Instance.IsPetOrPlayerOrMerc(GamePetName), "the combined question changed shape");

        Assert.IsNull(PlayerRegistry.Instance.GetPlayerFromPet(GamePetName),
            "asking invented an owner (even an unassigned one) for a name no line named");
        Assert.IsFalse(PlayerRegistry.Instance.GetPetMappings().Any(m => string.Equals(m.Pet, GamePetName, StringComparison.OrdinalIgnoreCase)),
            "asking added a row to the pet map, which RegistrySeed files as a Pet claim");
        Assert.AreEqual(before, PlayerRegistry.Instance.GetPetMappings().Count, "the pet map grew from a read");
    }

    /*
     * The field sequence, at fixture scale. A name the raid keeps healing is raid-side (R15); something asks the registry about
     * it the way a stats pass does; the next pass must answer the same. Pre-fix, that question minted the ownerless row, the seed
     * claimed Pet at strength 8, R15 never fired for an already-placed name, and the second pass disagreed — which is what made
     * the projection rebuild and the boards build twice over identical facts.
     */
    [TestMethod]
    public void ANameTheRaidKeepsHealingSurvivesBeingAskedAbout()
    {
        GameGeneratedPets()[GamePetName] = 1;

        var run = RunDerive(HealLines(GamePetName, 12).ToArray());

        var first = Warm(run);
        AssertIdentity(first, GamePetName, IdentityKind.Player, "R15-healed");

        // Any surface may ask. HealingStatsBuilder asks this about every name on the board, once per build.
        Assert.IsTrue(PlayerRegistry.Instance.IsPetOrPlayerOrMerc(GamePetName));

        var second = Warm(run);
        AssertIdentity(second, GamePetName, IdentityKind.Player, "R15-healed");

        Assert.AreEqual(IdentityKind.Player, second.IdentityAt(GamePetName, double.PositiveInfinity),
            "the answer moved between two passes over an unchanged capture — every surface fed by it rebuilds");
    }

    /*
     * The other direction, so the fix cannot be widened into deleting real memory: a pet that WAS observed (or an operator's
     * word) still earns its ownerless row, which is what keeps a custom-named summon off the enemy column.
     */
    [TestMethod]
    public void AnObservedPetStillEarnsItsRowInTheMap()
    {
        // Parse first: the harness resets this store, so a pair written before it would be cleared away.
        var run = RunDerive(HealLines(GamePetName, 12).ToArray());
        Assert.IsFalse(PlayerRegistry.Instance.GetPetMappings().Any(m => string.Equals(m.Pet, GamePetName, StringComparison.OrdinalIgnoreCase)));

        PlayerRegistry.Instance.AddVerifiedPet(GamePetName);

        var mapping = PlayerRegistry.Instance.GetPetMappings()
            .FirstOrDefault(m => string.Equals(m.Pet, GamePetName, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(mapping, "verifying a pet no longer writes the pair RegistrySeed and the Pet Owners grid read");
        Assert.AreEqual(Labels.Unassigned, mapping!.Owner, "an observed pet with no named owner should read unassigned, not absent");

        /*
         * And the seed files a claim from that row — which is what the invented row used to fake. Whether the name then READS
         * Pet is a precedence question the rule book answers by strength (the seed sits at 8 so a ledger can never change an
         * answer, and a raid that keeps healing it says something too); what belongs here is that an earned row reaches the
         * seeding lane at all, with its provenance intact.
         */
        var timeline = Warm(run);
        Assert.IsTrue(timeline.ClaimsOf(GamePetName).Any(c => c.Kind == IdentityKind.Pet && c.Source == "RegistrySeed"),
            "a verified pet's row never reached RegistrySeed — the lane that keeps a custom-named summon off the enemy column");
    }

    // ---- helpers ----

    // Classify the way a session does: memory first, then the rule book over the capture.
    private static EntityTimeline Warm(PipelineHarness.DeriveRunResult run)
    {
        var timeline = new EntityTimeline();
        var facts = run.Facts;
        var first = facts.FactCount > 0 ? facts.Facts[0].TimeS : 0d;
        var last = facts.FactCount > 0 ? facts.Facts[^1].TimeS : 0d;
        RegistrySeed.Apply(timeline, facts, first, last);
        ClassificationRules.Apply(facts, timeline, run.HealFacts);
        return timeline;
    }

    private static void AssertIdentity(EntityTimeline timeline, string name, IdentityKind expected, string expectedSource)
    {
        var kind = timeline.IdentityWithSource(name, out var source);
        Assert.AreEqual(expected, kind, $"{name} answered {kind}, not {expected}");
        Assert.AreEqual(expectedSource, source, $"{name} was placed by {source ?? "nothing"}, not {expectedSource}");
    }

    // Two verified players healing `target` `count` times, spread over six minutes.
    private static IEnumerable<string> HealLines(string target, int count)
    {
        yield return "[Mon May 04 18:50:01 2026] Targeted (Player): Healerone";
        yield return "[Mon May 04 18:50:01 2026] Targeted (Player): Healertwo";
        for (var i = 0; i < count; i++)
        {
            var caster = i % 2 == 0 ? "Healerone" : "Healertwo";
            yield return $"[{Timestamp(19, 0, i * 30)}] {caster} healed {target} for 5000 (9000) hit points by Blessed Radiance Rk. II.";
        }
    }

    private static string Timestamp(int hour, int minute, int secondOffset)
    {
        var t = new DateTime(2026, 5, 4, hour, 0, 0, DateTimeKind.Utc).AddMinutes(minute).AddSeconds(secondOffset);
        return $"Mon May {t.Day:00} {t.Hour:00}:{t.Minute:00}:{t.Second:00} 2026";
    }

    private static PipelineHarness.DeriveRunResult RunDerive(params string[] lines)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "derive-memory-read-" + Guid.NewGuid().ToString("N")));
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

    /*
     * `_gameGeneratedPets` is what `data\petnames.txt` fills at Init. The test reaches the field because there is no other way to
     * be a player whose pet-name list contains this name, and the whole defect is about that list: the ANSWER ("yes, that shape
     * is a pet") is fine forever; it was the row in `_petToPlayer` that made it a claim.
     */
    private static ConcurrentDictionary<string, byte> GameGeneratedPets() =>
        (ConcurrentDictionary<string, byte>)typeof(PlayerRegistry)
            .GetField("_gameGeneratedPets", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(PlayerRegistry.Instance)!;
}
