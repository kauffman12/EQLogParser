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

    [TestMethod]
    public void SubtypeIdsRoundTripPastTheOldSignedShortWrap()
    {
        // The stored id is a ushort with NoSubtype reserved at the top, so the 32,769th distinct verb must come back as a
        // usable id - the old short return wrapped to negative at exactly this index and the capture's clamp rewrote the
        // wrap to 0, which is the FIRST subtype: a capture that ever grew past 32k verbs would have read its newest word
        // as its oldest one.
        var t = new DamageFactTable();

        const int past = ushort.MaxValue - 1;   // capacity; the sentinel top value stays out of the table
        for (var i = 0; i < past; i++)
        {
            var name = "v" + i;
            var id = t.InternSubtype(name);
            Assert.AreEqual(i, id, "id drifted from the intern order at index " + i);
        }

        // The reported boundary, one on each side of where a signed short turns negative.
        Assert.AreEqual((ushort)32767, t.InternSubtype("v" + 32767));
        var wrapped = t.InternSubtype("v" + 32768);
        Assert.AreEqual((ushort)32768, wrapped, "the 32,769th verb wrapped to a negative id");
        Assert.AreEqual("v" + 32768, t.SubtypeOf(wrapped), "the wrap aliased the new verb onto an earlier one");
        Assert.AreEqual("v0", t.SubtypeOf(0), "subtype 0 was disturbed by the high ids");
    }

    [TestMethod]
    public void SubtypeInternAnswersTheSentinelForAnEmptyWord()
    {
        // No modifier text is stored as NoSubtype itself, which is why the capture no longer needs a ternary to map it -
        // and SubtypeOf must keep reading that sentinel back as "nothing" rather than as an index.
        var t = new DamageFactTable();

        Assert.AreEqual(DamageFactTable.NoSubtype, t.InternSubtype(string.Empty));
        Assert.AreEqual(DamageFactTable.NoSubtype, t.InternSubtype(null));
        Assert.IsNull(t.SubtypeOf(DamageFactTable.NoSubtype));
    }

    [TestMethod]
    public void NameAndAuxIdsKeepWorkingPastEveryOldSixteenBitCeiling()
    {
        /*
         * Both pools used to be 16-bit and THREW at short.MaxValue (32,767 — the message claimed 65,535, so even the error lied),
         * and it threw on the ingest path where a per-line catch swallowed it: past that name the capture silently stopped taking
         * lines. The ids are int now, so this test inverts the old law — there is no boundary left to refuse at, and what has to be
         * proven is that entries past every old width are DISTINCT, STABLE and readable. A truncated id would not fail; it would
         * alias one entity onto another, which is worse than the crash it replaced.
         */
        var t = new DamageFactTable();
        const int Past = 70_000;   // past short.MaxValue AND past ushort.MaxValue: both old ceilings cleared in one run

        var firstMismatch = -1;
        for (var i = 0; i < Past; i++)
        {
            if (t.InternName("n" + i) != i || t.InternAux("x" + i) != i) { firstMismatch = i; break; }
        }

        Assert.AreEqual(-1, firstMismatch, $"an id pool stopped matching its own count at entry {firstMismatch}");

        // A repeat of an entry that arrived past the old ceiling returns ITS id — no second copy, no truncated one.
        Assert.AreEqual(65_535, t.InternName("n65535"));
        Assert.AreEqual(Past - 1, t.InternName("n" + (Past - 1)));

        // No aliasing across the old boundary: a wide id reads back its OWN string. A u16 would have sent #69,999 to whoever owned #4,464.
        Assert.AreEqual("N65535", t.NameOf(65_535));              // the pool stores CapitalizeFirst — display form, not fixture spelling
        Assert.AreEqual("N" + (Past - 1), t.NameOf(Past - 1));
        Assert.AreEqual("x" + (Past - 1), t.AuxOf(Past - 1));
        Assert.AreEqual(65_535, t.NameIndexOf("n65535"), "a read path must find a wide name without interning a second copy of it");
        Assert.AreEqual(-1, t.NameIndexOf("never seen at all"), "an absent name still answers -1 rather than an id");

        // And a fact can ADDRESS those names, which is the whole point — this row was impossible to express before.
        t.AddFact(new DamageFact(1, 1_700_000_000L, Past - 1, 65_535, 500, LabelTypes.Dd, 0, 0, DamageFactTable.NoSubtype));
        Assert.AreEqual(Past - 1, t.Facts[0].AtkIdx);
        Assert.AreEqual(65_535, t.Facts[0].DefIdx);
    }
}
