using EQLogParser;

namespace EQLogParser;

/*
 * The name pool: one entity, one id, and a display form that is derived from the name rather than chosen by
 * whichever line reached the table first.
 *
 * `EntityNameKeyTest` covers the LOOKUP side (identity and affiliation keys ignore case). This covers the storage
 * side, which had the same split with different consequences: `DamageFactTable._nameMap` keyed ordinally, so
 * `a bone walker has been charmed.` interned one id and every damage record about that mob interned another. Two
 * ids for one mob means every per-id structure sees half of it — the name pool paid for both (measured: 285 of
 * 2,721 entries on one capture, 40 of 558 on another), and the string an id carries IS the row's displayed name,
 * so whether the fight list read "a bone walker" or "A bone walker" was decided by arrival order. Sorting cannot
 * repair a decision that was made by accident of ingest.
 *
 * So the map is `OrdinalIgnoreCase` — the same comparer rule as the entity stores, because EQ itself cannot host
 * two entities whose names differ only by letter case — and the stored string is `TextUtils.CapitalizeFirst`, the
 * finishing touch `ParserUtil.UpdateAttacker/UpdateDefender/UpdateSlain` already give every name that comes from a
 * damage line. Nothing else about spelling changes: inner capitals arrive as written.
 */
[TestClass]
public class NamePoolTest
{
    [TestMethod]
    public void OneEntityOneIdWhateverTheSpelling()
    {
        var t = new DamageFactTable();

        var lower = t.InternName("a bone walker");
        var upper = t.InternName("A Bone Walker");

        Assert.AreEqual(lower, upper, "two spellings of one entity took two ids");
        Assert.AreEqual(1, t.InternedNames.Count, "the pool paid for one entity twice");

        // The stored form is the incoming name with its first letter capitalized — which is exactly what makes it
        // arrival-order-independent in this pipeline: the two spellings an entity can arrive under are `raw` and
        // `CapitalizeFirst(raw)` (the parsers apply that one touch, the evidence lines do not), so they differ ONLY
        // at index 0 and capitalising index 0 makes them identical. Inner capitals come from whatever arrived first
        // and stay as written (`InnerSpellingSurvivesCanonicalisation`).
        Assert.AreEqual("A bone walker", t.NameOf(lower), "the stored form is not capitalized on the first letter");
    }

    [TestMethod]
    public void DisplayFormDoesNotDependOnArrivalOrder()
    {
        // The whole point of deriving the display string instead of keeping the first one seen: the charm line and
        // the damage line race, and a row's name must not be the loser of that race.
        var evidenceFirst = new DamageFactTable();
        evidenceFirst.InternName("a scalewrought handler");
        evidenceFirst.InternName("A scalewrought handler");

        var factsFirst = new DamageFactTable();
        factsFirst.InternName("A scalewrought handler");
        factsFirst.InternName("a scalewrought handler");

        Assert.AreEqual(evidenceFirst.NameOf(0), factsFirst.NameOf(0), "arrival order still decides the displayed name");
        Assert.AreEqual("A scalewrought handler", evidenceFirst.NameOf(0));
    }

    [TestMethod]
    public void InnerSpellingSurvivesCanonicalisation()
    {
        // Only the first letter is touched. A name EQ writes with capitals inside it is not rewritten into
        // sentence case, and a name that already starts upper costs no allocation at all.
        var t = new DamageFactTable();
        Assert.AreEqual("Tik`Tick", t.NameOf(t.InternName("Tik`Tick")));
        Assert.AreEqual("BriRN", t.NameOf(t.InternName("BriRN")));

        // An empty name stays empty rather than becoming a string the log never wrote.
        Assert.AreEqual(string.Empty, t.NameOf(t.InternName(string.Empty)));
    }

    [TestMethod]
    public void TheHealStreamInternsIntoTheSameCanonicalPool()
    {
        // Same string, same index in both streams — and now the same canonical form, so a healer verified by her
        // damage line and healed under the spelling a heal line wrote is one entry, not two.
        var facts = new DamageFactTable();
        var heals = new HealFactTable(facts);

        var fromDamage = facts.InternName("a healer named pooled");
        var fromHeal = heals.InternName("A Healer Named Pooled");

        Assert.AreEqual(fromDamage, fromHeal, "the two streams disagreed about who this is");
        Assert.AreEqual("A healer named pooled", heals.NameOf(fromHeal));
    }

    [TestMethod]
    public void ARealCaptureLeavesNoSplitSpellingsInThePool()
    {
        // End to end at the seam that split: a fixture whose charm confirm writes lower-case while every damage
        // record about the same mob is capitalized. The pool must hold one entry for it, and no entry may still
        // read lower-case — which is the same assertion the bench prints over 20 MB of names.
        var fixture = Path.Combine(AppContext.BaseDirectory, "mini-data", "derive", "namekey-fixture.txt");
        Assert.IsTrue(File.Exists(fixture), $"missing fixture: {fixture}");

        var facts = PipelineHarness.RunFileDerived(fixture).Facts;

        var walker = facts.InternedNames.Count(n => n.Equals("a namekey pet", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(1, walker, "both spellings of the charmed pet are in the pool");
        Assert.IsTrue(facts.InternedNames.All(n => n.Length == 0 || !char.IsLower(n[0])),
                      "a stored name still starts lower-case, so some line bypassed canonicalisation");

        // One entity means one row too: the fight list is keyed by these ids.
        var timeline = new EntityTimeline();
        _ = ClassificationRules.Apply(facts, timeline);
        var rows = FightProjection.Build(facts, timeline);
        Assert.AreEqual(1, rows.Count(r => r.Name.Equals("a namekey pet", StringComparison.OrdinalIgnoreCase)),
                        "the same mob appears as more than one fight row");
    }
}
