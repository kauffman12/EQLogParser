using EQLogParser;

namespace EQLogParser;

/*
 * Entity names are looked up without caring about letter case, everywhere an entity is asked about.
 *
 * The two halves of the engine spell names differently and neither is wrong:
 *
 *   - FACTS carry sentence-case names. ParserUtil.UpdateAttacker/UpdateDefender/UpdateSlain all finish with
 *     TextUtils.CapitalizeFirst, so a mob is "A bone walker" in every damage, heal and death record.
 *   - EVIDENCE keeps whatever the log wrote. A charm confirm is "a bone walker has been charmed.", a wear-off
 *     names it lower-case, a tell carries the sender as typed.
 *
 * An ordinal key therefore holds half of each entity, and the failure is invisible: the reader gets Unknown (or
 * Enemy) and falls back instead of failing loudly. EQ itself cannot host two entities whose names differ only by
 * case, which is why PlayerRegistry has always been OrdinalIgnoreCase; EntityTimeline is the same store for the
 * same entities. R9's charm windows are where this was found — a charmed mob stayed an enemy row in the fight
 * list while its own accounting happily credited it (docs/combat-mirror-design.md).
 *
 * Lookups canonicalise. Storage used to keep every spelling; it no longer does — `DamageFactTable.InternName` is
 * ignore-case too and displays one form per entity, because an ordinal pool gave one mob two ids and left each
 * per-id structure holding half of it. That half is `NamePoolTest`; this class pins the lookups.
 */
[TestClass]
public class EntityNameKeyTest
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "mini-data", "derive", "namekey-fixture.txt");

    // Direct store behaviour: identity, affiliation, ownership, and the two charm questions the display list asks.
    [TestMethod]
    public void EntityStoresKeyNamesWithoutCaringAboutCase()
    {
        var tl = new EntityTimeline();

        tl.SetIdentity("A Bone Walker", IdentityKind.Npc, RuleStrength.Strong, "test");
        Assert.AreEqual(IdentityKind.Npc, tl.IdentityAt("a bone walker", 5), "identity is per entity, not per spelling");
        Assert.AreEqual(IdentityKind.Npc, tl.IdentityAt("A BONE WALKER", 5));
        Assert.IsTrue(tl.HasIdentity("a bone walker"));

        // A hostile span written capitalized, a friendly one written lower-case: both must be seen.
        tl.AddAffiliation(AffiliationKind.Enemy, "A Bone Walker", 0, 10, RuleStrength.Certain, "test");
        tl.AddAffiliation(AffiliationKind.Friendly, "a bone walker", 20, 30, RuleStrength.Certain, "test", "Charmer");
        Assert.AreEqual(AffiliationKind.Enemy, tl.AffiliationAt("A BONE WALKER", 5, out _));
        Assert.AreEqual(AffiliationKind.Friendly, tl.AffiliationAt("A Bone Walker", 25, out _),
            "a Friendly interval written lower-case has to flip the capitalized question");

        // Ownership: proven under one spelling, read under another — or the list and the credit disagree about
        // whose companion this is.
        tl.AddAffiliation(AffiliationKind.PetOfPlayer, "Player Pet", 60, 120, RuleStrength.Certain, "test", "Ziggy");
        Assert.AreEqual("Ziggy", tl.OwnerOf("player pet", 90));
        Assert.AreEqual("Ziggy", tl.OwnerOf("PLAYER PET", 90));

        // The two questions FightProjection asks R9 — "did it become ours at some point after t" and "was it ours
        // at t". Both key the affiliation list by name, so both are where an ordinal miss shows up as a mob that
        // was simply never charmed.
        tl.AddAffiliation(AffiliationKind.Friendly, "a bone walker", 200, 240, RuleStrength.Certain, "R9-charm", "Charmer");
        Assert.IsTrue(tl.IsCharmedAt("A Bone Walker", 210));
        Assert.AreEqual(200d, tl.CharmStartAfter("A BONE WALKER", 100));

        // One entity per name: an enumeration handing back both spellings would double-count a mob in every view
        // that lists identities.
        Assert.AreEqual(1, tl.NamesWithIdentity().Count(n => n.Equals("a bone walker", StringComparison.OrdinalIgnoreCase)));
    }

    /*
     * End-to-end at the seam that broke: rules over real records, where the charm confirm interned
     * "a namekey pet" and every damage record about the same mob interned "A namekey pet". On a log an ordinal key
     * fails this as "that mob was never charmed", never as an error — which is the whole reason it is asserted.
     */
    [TestMethod]
    public void EvidenceInOneSpellingAnswersForTheCapitalizedFactName()
    {
        Assert.IsTrue(File.Exists(FixturePath), $"missing fixture: {FixturePath}");

        var run = PipelineHarness.RunFileDerived(FixturePath);
        var facts = run.Facts;
        var timeline = new EntityTimeline();
        _ = ClassificationRules.Apply(facts, timeline);

        // Times are computed because the timeline works in dotnet seconds, not log strings.
        var charmAt = DateUtil.StandardDateToDotNetSeconds("[Sun Apr 26 18:00:07 2026] x");

        Assert.AreEqual(IdentityKind.Npc, timeline.IdentityAt("A namekey pet", charmAt),
            "the charm named this entity lower-case while every fact about it is capitalized");
        Assert.IsTrue(timeline.IsCharmedAt("A namekey pet", charmAt + 13),
            "inside the window the capitalized spelling must read as ours");
        Assert.IsFalse(timeline.IsCharmedAt("A namekey pet", charmAt - 600), "and only inside it");
    }
}
