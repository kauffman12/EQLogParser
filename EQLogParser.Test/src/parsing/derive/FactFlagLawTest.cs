using EQLogParser;

namespace EQLogParser;

/*
 * A captured fact carries what its own line says, and nothing the registry happened to believe.
 *
 * It did not always. DamageFact held bits for "the registry thought this attacker was player-side" and "…this defender",
 * HealFact held the healer/healed pair, and CombatCapture asked PlayerRegistry four questions per event to fill them —
 * ~15 million lookups over a 952 MiB capture. They were deleted for the reason RangeSpike's own census produced, not
 * because of the cost: **32 of 999 attacker names carried BOTH values of one side bit inside a single sequential pass**
 * (`Betebeatz` reads side=0 on 9,512 facts and side=1 on 5,342), and 28 healer names flipped across 52,302 heal facts.
 * A bit that changes mid-file for the same name is not a property of the sentence — it is a timestamped snapshot of an
 * opinion, which no replay from another prefix reproduces and no sharded reader can reproduce at all (that census is how
 * the parallel-parsing design learned "stop stamping opinions into facts" was a prerequisite).
 *
 * Where that knowledge legitimately lives: WHEN the registry learned a name travels as an IdentityEvent with its Seq and
 * TimeS — the non-lossy form — and what a name IS gets decided per name by the rule book over captured evidence. So these
 * tests hold the boundary rather than any arithmetic:
 *
 *   1. two names the registry has already verified produce a fact with NO flags at all;
 *   2. the bits that remain still fire, from the text alone (an ownership word; a spell standing in for an absent caster);
 *   3. nothing on either stream sets a bit outside the line-derived mask — the assertion that catches an opinion stamped
 *      back in, because the deleted bits read as legitimate ones on every warm single pass.
 *
 * The retired VALUES stay retired rather than recycled: see the notes in DamageFactTable/HealFactTable and
 * TheLiveBitsAreTheDocumentedValuesAndRetiredOnesStayOut. Older spool files hold 4/8 (damage) and 2/4 (heal) with the old
 * meaning, so a new flag takes a higher bit.
 */
[TestClass]
[DoNotParallelize]
public sealed class FactFlagLawTest
{
    private DamageFactTable _facts = null!;
    private HealFactTable _heals = null!;
    private CombatCapture _capture = null!;

    [TestInitialize]
    public void Setup()
    {
        // Parser process state, the record store and the registry: this file asks what the CAPTURE wrote, and a leftover
        // repeat-cache instance or verified name would answer for whichever test ran before it.
        DamageLineParser.ResetProcessState();
        HealingLineParser.ClearCaches();
        RecordsStore.Instance.Clear(false);
        PlayerRegistry.Instance.Clear();

        _facts = new DamageFactTable(64);
        _heals = new HealFactTable(_facts, 64);
        _capture = new CombatCapture(_facts, _heals);
        _capture.Start();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _capture.Stop();
        DamageLineParser.ResetProcessState();
        HealingLineParser.ClearCaches();
        RecordsStore.Instance.Clear(false);
        PlayerRegistry.Instance.Clear();
    }

    private static LineData Line(string action, double time) =>
        new() { Action = action, BeginTime = time, Split = action.Split(' ') };

    // Anything outside the mask must never appear — retired side bits included.
    private static byte ForeignBits(byte flags, byte lineDerivedMask) => (byte)(flags & ~lineDerivedMask);

    [TestMethod]
    public void VerifiedPlayersProduceAFactWithNoFlagsAtAll()
    {
        // The exact condition the deleted pair used to record: both names known to the registry when the line is
        // captured. That fact used to carry bits 4 and 8, indistinguishable from line-derived ones and disagreed on by
        // any two runs whose warm-up differed.
        PlayerRegistry.Instance.AddVerifiedPlayer("Bithika", 10);
        PlayerRegistry.Instance.AddVerifiedPlayer("Ammeren", 10);

        Assert.IsTrue(DamageLineParser.Process(Line("Bithika hits Ammeren for 10 points of damage.", 20)));
        Assert.AreEqual(1, _facts.FactCount);

        var fact = _facts.Facts[0];
        Assert.AreEqual((byte)0, fact.Flags, "the registry's opinion is not a property of the line");
        Assert.AreEqual((byte)0, ForeignBits(fact.Flags, DamageFact.LineDerivedFlagMask));
    }

    [TestMethod]
    public void AVerifiedPetNamedWithoutAPossessiveCarriesNoFlag()
    {
        // Ownership TEXT is the flag; the registry's pet list is not. This pair used to differ depending on whether the
        // mapping had been learned yet — a timestamp wearing the shape of a fact.
        PlayerRegistry.Instance.AddVerifiedPet("Wolf");

        Assert.IsTrue(DamageLineParser.Process(Line("Wolf bites Ammeren for 9 points of damage.", 20)));
        Assert.AreEqual((byte)0, _facts.Facts[0].Flags);
    }

    [TestMethod]
    public void AnOwnershipWordInTheNameIsStillCaptured()
    {
        // The live bit, measured reachable: 546,376 of this capture's 2,285,746 damage facts carry it. R5 sweeps the name
        // pool for the same words, and FightSummarySource folds a pet's damage onto its owner straight from this byte —
        // so it is evidence with consumers, unlike what was removed beside it.
        Assert.IsTrue(DamageLineParser.Process(Line("Grudg`s pet hits Waxwork Abolishion for 771 points of damage.", 20)));

        var fact = _facts.Facts[0];
        Assert.IsTrue(fact.OwnerInLine, "the ownership word in the name is line-derived and must survive");
        Assert.AreEqual(DamageFact.FlagOwnerInLine, fact.Flags);
    }

    [TestMethod]
    public void TheSpellStandingInForAnAbsentCasterIsStillFlagged()
    {
        // "X has taken N damage from <effect> by ." — no caster, so the parser substitutes the spell and says so. The
        // substitution is what the line wrote (1,149 of this capture's damage facts), and the boards route on it.
        Assert.IsTrue(DamageLineParser.Process(Line("Bithika has taken 18724 damage from Sonic Bang by .", 30)));

        var fact = _facts.Facts[0];
        Assert.IsTrue(fact.AttackerIsSpell);
        Assert.IsFalse(fact.OwnerInLine);
        Assert.AreEqual(DamageFact.FlagAttackerIsSpell, fact.Flags);
    }

    [TestMethod]
    public void VerifiedHealerAndHealedProduceAFactWithNoFlagsAtAll()
    {
        PlayerRegistry.Instance.AddVerifiedPlayer("Fllint", 10);
        PlayerRegistry.Instance.AddVerifiedPlayer("Ammeren", 10);

        Assert.IsTrue(HealingLineParser.Process(Line("Fllint healed Ammeren for 11820 hit points by Blessing of the Ancients III.", 20)));
        Assert.AreEqual(1, _heals.HealCount);

        var heal = _heals.Heals[0];
        Assert.AreEqual((byte)0, heal.Flags, "the registry's opinion about healer or healed is not part of the sentence");
        Assert.AreEqual((byte)0, ForeignBits(heal.Flags, HealFact.LineDerivedFlagMask));
    }

    [TestMethod]
    public void APossessivePetHealLineReachesNoFactAtAll()
    {
        /*
         * The honest companion to the test above. HealFact.FlagOwnerInLine exists, and the shape that would set it —
         * "Reisil`s pet healed itself for 15750 hit points by Venom Claw XVI." — never produces a record: the healing
         * parser refuses a healer name that is not player-shaped, so over 1,243,469 heal facts on a real capture the bit
         * is set on ZERO. That is recorded here rather than deleted because the bit costs nothing (it rides padding), and
         * because "no line writes it today" is a measurement, not a proof: if HealingLineParser ever accepts a possessive
         * healer, this test names the shape to come back and re-measure. Deleting a field nobody has measured across
         * captures is how OverTotal sat on DamageFact for years reading zero.
         */
        Assert.IsFalse(HealingLineParser.Process(Line("Reisil`s pet healed itself for 15750 hit points by Venom Claw XVI.", 20)));
        Assert.AreEqual(0, _heals.HealCount);

        // And the player-shaped sibling does arrive, with no flags — so the refusal is about the healer's shape, not the line.
        Assert.IsTrue(HealingLineParser.Process(Line("Stormclaw healed Ammeren for 300 hit points by Venom Claw XVI.", 21)));
        Assert.AreEqual((byte)0, _heals.Heals[0].Flags);
    }

    [TestMethod]
    public void NothingOnEitherStreamSetsABitOutsideTheLine()
    {
        // A sweep rather than one line, because the failure this guards is a single new stamp that looks right on a warm
        // pass: registry-known names, friendly fire between them, a pet named plainly, a pet named by its owner, a
        // caster-less effect, a plain heal.
        PlayerRegistry.Instance.AddVerifiedPlayer("Bithika", 10);
        PlayerRegistry.Instance.AddVerifiedPlayer("Ammeren", 10);
        PlayerRegistry.Instance.AddVerifiedPet("Wolf");

        DamageLineParser.Process(Line("Bithika hits Ammeren for 10 points of damage.", 20));
        DamageLineParser.Process(Line("Grudg`s pet hits Waxwork Abolishion for 771 points of damage.", 21));
        DamageLineParser.Process(Line("Wolf bites Ammeren for 9 points of damage.", 22));
        DamageLineParser.Process(Line("Bithika has taken 18724 damage from Sonic Bang by .", 23));
        HealingLineParser.Process(Line("Fllint healed Ammeren for 400 hit points by Cure Disease.", 24));
        HealingLineParser.Process(Line("Stormclaw healed Ammeren for 300 hit points by Venom Claw XVI.", 25));

        Assert.IsTrue(_facts.FactCount >= 4);
        var damage = _facts.Facts;
        for (var i = 0; i < damage.Length; i++)
        {
            Assert.AreEqual((byte)0, ForeignBits(damage[i].Flags, DamageFact.LineDerivedFlagMask),
              $"damage fact {i} ({_facts.NameOf(damage[i].AtkIdx)} -> {_facts.NameOf(damage[i].DefIdx)}) carries a flag the line cannot write");
        }

        Assert.AreEqual(2, _heals.HealCount);
        var heals = _heals.Heals;
        for (var i = 0; i < heals.Length; i++)
        {
            Assert.AreEqual((byte)0, ForeignBits(heals[i].Flags, HealFact.LineDerivedFlagMask),
              $"heal fact {i} ({_heals.NameOf(heals[i].HealerIdx)} -> {_heals.NameOf(heals[i].HealedIdx)}) carries a flag the line cannot write");
        }
    }

    [TestMethod]
    public void TheLiveBitsAreTheDocumentedValuesAndRetiredOnesStayOut()
    {
        /*
         * Values pinned, not just masks, because the retired ones matter: bits 4 and 8 on DamageFact and 2 and 4 on
         * HealFact hold an older build's registry opinion (spool files written by RangeSpike carry them). A new flag
         * takes a HIGHER bit so no old file can be misread as claiming it — the moment this test fails is the moment to
         * re-read that note instead of taking the first free value.
         */
        // Read through locals: the analyzer refuses a constant-vs-constant assert (MSTEST0025), and these ARE meant to be
        // constants — that is the whole point of pinning them.
        var damageLive = DamageFact.LineDerivedFlagMask;
        var healLive = HealFact.LineDerivedFlagMask;
        var retiredOnDamage = (byte)(4 | 8);
        var retiredOnHeal = (byte)(2 | 4);

        Assert.AreEqual((byte)(1 | 2), damageLive);
        Assert.AreEqual((byte)1, healLive);

        // ...and the retired values share no bit with what is live now.
        Assert.AreEqual((byte)0, (byte)(damageLive & retiredOnDamage));
        Assert.AreEqual((byte)0, (byte)(healLive & retiredOnHeal));
}
}
