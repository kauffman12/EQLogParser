using EQLogParser;

namespace EQLogParser;

/*
 * "What has this ambiguous spell name cast in the last few seconds?" is asked by EQDataStore.FindPreviousCast while
 * it resolves an abbreviation, and it is asked against a history that grows all night. It used to answer by allocating
 * a result list with capacity for the spell's ENTIRE history and walking every entry of it.
 *
 * Measured over Incogitable (figures in docs/DesignNotes.md): 214,487 queries visited 149,232,044 entries to return 164,263
 * matches — about 700 entries examined per match, the whole night searched for a question about eight seconds. The
 * bounded scan visits 376,345 and returns exactly the same matches (952 MiB capture: 53.7 M visited for ~235 K
 * matches; beta: 67.8 M for ~265 K). Read that as a removed scaling hazard, not as a headline speedup — the
 * measurement found no proportional whole-load improvement and none is promised here.
 *
 * What these tests pin is the half that a benchmark cannot see: WHICH casts come back. A scan that stops early is only
 * correct while the entries behind it are older, and this data comes from a text log, so the store tracks whether every
 * append for that name landed ascending and falls back to the full walk the moment one does not. Three captures showed
 * no violation; an assumption here would fail as a silently wrong board (a resolved abbreviation picking the wrong
 * spell rank), which is the failure this repo has repeatedly refused to buy with speed.
 */
[TestClass]
[DoNotParallelize]
public sealed class RecentCastQueryTest
{
    // Base time far from zero so "recent" windows behave like the real dotnet-epoch seconds the parser feeds in.
    private const double T0 = 638_000_000_000d;

    [TestInitialize]
    public void Setup() => RecordsStore.Instance.Clear(false);

    [TestCleanup]
    public void Cleanup() => RecordsStore.Instance.Clear(false);

    // Only HasAmbiguity gets a cast into the index at all — an unambiguous spell never needs this question asked.
    private static SpellCast Cast(string spell, string caster) => new()
    {
        Spell = spell,
        Caster = caster,
        SpellData = new SpellData { Name = spell, HasAmbiguity = true }
    };

    private static void AddCast(string spell, string caster, double atSeconds) =>
        RecordsStore.Instance.Add(Cast(spell, caster), atSeconds);

    [TestMethod]
    public void ABackwardsAppendCannotMoveTheSortedGlobalAnchorBackIntoPrunedHistory()
    {
        AddCast("Chokidin", "Old", T0);
        AddCast("Nictinus", "Anchor", T0 + 1000);
        AddCast("Chokidin", "Backwards", T0 + 5);
        Assert.IsEmpty(RecordsStore.Instance.GetCastsBySpellName("Chokidin", 8),
            "an unpruned scan also returns nothing: newest timestamp is still T0+1000");
        Assert.AreEqual(1, RecordsStore.Instance.GetCastsBySpellName("Nictinus", 8).Count);
        AddCast("Chokidin", "InWindow", T0 + 995);
        var casts = RecordsStore.Instance.GetCastsBySpellName("Chokidin", 8);
        Assert.AreEqual(1, casts.Count);
        Assert.AreEqual("InWindow", casts[0].Cast.Caster);
    }

    [TestMethod]
    public void ARecentCastQueryReturnsTheWindowNewestFirst()
    {
        for (var i = 0; i <= 12; i++)
        {
            AddCast("Chokidin", "Bithika", T0 + i);
        }

        // The anchor is the store's newest cast (T0 + 12) and the test is >= anchor - duration, so a duration of 8
        // reaches T0 + 4 and holds nine one-second casts. Stated as arithmetic because the boundary IS the contract:
        // off-by-one here drops the oldest recent cast and an ambiguous abbreviation resolves to the wrong rank.
        var casts = RecordsStore.Instance.GetCastsBySpellName("Chokidin", 8);

        Assert.AreEqual(9, casts.Count, "the window is inclusive of its oldest second");
        for (var i = 0; i < casts.Count; i++)
        {
            Assert.AreEqual(T0 + 12 - i, casts[i].BeginTime,
              $"entry {i} is out of order: a caller takes the FIRST usable cast, so newest-first is the contract");
        }
    }

    [TestMethod]
    public void CastersAreNotPartOfTheQuery()
    {
        // The store answers "what did this NAME cast"; FindPreviousCast filters by caster afterwards. Proving the
        // filter still lives in the caller keeps a future "optimisation" from folding it in and changing which spell
        // wins an ambiguous match.
        AddCast("Chokidin", "Bithika", T0);
        AddCast("Chokidin", "Ammeren", T0 + 1);

        var casts = RecordsStore.Instance.GetCastsBySpellName("Chokidin", 8);

        Assert.AreEqual(2, casts.Count);
        Assert.AreEqual("Ammeren", casts[0].Cast.Caster);
        Assert.AreEqual("Bithika", casts[1].Cast.Caster);
    }

    [TestMethod]
    public void AQuietSpellNameDoesNotBorrowAnotherNamesRecentCasts()
    {
        AddCast("Chokidin", "Bithika", T0);
        AddCast("Nictinus", "Ammeren", T0 + 1);

        Assert.AreEqual(0, RecordsStore.Instance.GetCastsBySpellName("Virtuto", 60).Count,
          "a name never cast must answer empty, not the whole store");
        Assert.AreEqual(1, RecordsStore.Instance.GetCastsBySpellName("Nictinus", 60).Count);
    }

    [TestMethod]
    public void TheAnswerIsNotSizedForTheWholeHistory()
    {
        // Two thousand casts from earlier in the night, then one inside the window. Pre-sizing the result at the
        // history's length made every query a multi-thousand-slot allocation, on a path that runs several times per
        // ambiguous line: measured 0.77 matches per query, so the capacity was roughly nine hundred times the answer.
        for (var i = 0; i < 2_000; i++)
        {
            AddCast("Chokidin", "Bithika", T0 + i);
        }

        AddCast("Chokidin", "Bithika", T0 + 100_000);

        var casts = RecordsStore.Instance.GetCastsBySpellName("Chokidin", 8);

        Assert.AreEqual(1, casts.Count);
        Assert.IsTrue(casts.Capacity < 64,
          $"a one-cast answer allocated {casts.Capacity} slots; it must be grown to what it holds, not to the " +
          "2,001-entry history");
    }

    [TestMethod]
    public void CastsInsideOneSecondAreNotCutByTheWindow()
    {
        // A burst inside a single second is the common shape (several interrupts land together), and the window test
        // is inclusive, so equal timestamps must all survive — and must not be mistaken for out-of-order arrivals.
        AddCast("Chokidin", "Bithika", T0);
        AddCast("Chokidin", "Ammeren", T0);
        AddCast("Chokidin", "Pickter", T0);

        var casts = RecordsStore.Instance.GetCastsBySpellName("Chokidin", 0);

        Assert.AreEqual(3, casts.Count, "a zero-length window still includes the anchor second itself");
    }

    [TestMethod]
    public void ABackwardsAppendBringsTheFullWalkBack()
    {
        /*
         * The safety law. Eleven ascending casts, then ONE appended with an older timestamp — a restored or
         * concatenated file does this to a name's list. A scan that assumed order would meet the old entry at the end
         * of the list, conclude everything behind it is older, and return nothing. The store notices the backwards
         * append on the way in and walks the whole list instead, so every cast inside the window still comes back:
         * the fast path is what is lost, never a match.
         */
        for (var i = 0; i <= 10; i++)
        {
            AddCast("Chokidin", "Bithika", T0 + i);
        }

        AddCast("Chokidin", "Ammeren", T0 - 50);

        var casts = RecordsStore.Instance.GetCastsBySpellName("Chokidin", 20);

        // The anchor is the store's newest cast (T0 + 10), so the eleven ascending casts are all inside the window;
        // the stray at T0 - 50 is not, and must not appear.
        Assert.AreEqual(11, casts.Count, "an out-of-order append must cost the fast path, not eleven matches");
        Assert.AreEqual(T0 + 10, casts[0].BeginTime);
        Assert.AreEqual(T0, casts[^1].BeginTime);
        Assert.IsFalse(casts.Any(c => c.BeginTime < T0), "the stale entry leaked into a window it is outside");
    }

    [TestMethod]
    public void ABackwardsAppendOnOneSpellLeavesOtherNamesAlone()
    {
        // The flag belongs to one spell name's list, so one corrupted history cannot retire the fast path for the
        // whole store — and, more importantly for correctness here, cannot change another name's answer.
        AddCast("Chokidin", "Bithika", T0);
        AddCast("Nictinus", "Ammeren", T0 + 1);
        AddCast("Chokidin", "Pickter", T0 - 50);
        AddCast("Nictinus", "Dangle", T0 + 2);

        // Anchor is Nictinus's newest (T0 + 2); a 5-second window holds both of its casts and only the recent
        // Chokidin one, whether or not that second name's history is flagged as unordered.
        Assert.AreEqual(2, RecordsStore.Instance.GetCastsBySpellName("Nictinus", 5).Count);
        Assert.AreEqual(1, RecordsStore.Instance.GetCastsBySpellName("Chokidin", 5).Count);
    }

    [TestMethod]
    public void AnEmptyStoreAnswersEmptyRatherThanThrowing()
    {
        Assert.AreEqual(0, RecordsStore.Instance.GetCastsBySpellName("Chokidin", 8).Count);

        // A cast recorded with no ambiguity never enters the index at all.
        RecordsStore.Instance.Add(new SpellCast { Spell = "Boon of the Clear Mind", Caster = "Bithika", SpellData = new SpellData { Name = "Boon of the Clear Mind", HasAmbiguity = false } }, T0);
        Assert.AreEqual(0, RecordsStore.Instance.GetCastsBySpellName("Boon of the Clear Mind", 60).Count);
    }

    [TestMethod]
    public void AClearedStoreForgetsTheHistoryAndTheDoubtAboutIt()
    {
        for (var i = 0; i <= 10; i++)
        {
            AddCast("Chokidin", "Bithika", T0 + i);
        }

        AddCast("Chokidin", "Ammeren", T0 - 50);
        RecordsStore.Instance.Clear(false);

        Assert.AreEqual(0, RecordsStore.Instance.GetCastsBySpellName("Chokidin", 60).Count);

        // After a clear the next capture starts unburdened: same name, fresh ascending appends, ordinary answer.
        AddCast("Chokidin", "Bithika", T0 + 500);
        AddCast("Chokidin", "Ammeren", T0 + 501);
        Assert.AreEqual(2, RecordsStore.Instance.GetCastsBySpellName("Chokidin", 8).Count);
    }

    /*
     * The memory half, and the law that makes it safe: these caches exist only to answer an 8-second question, so a cast older
     * than the horizon cannot be returned by any question ever asked — but "cannot be returned" is a statement about TIME, not
     * about position. One field run kept 397,206 of these to the end of the session; the horizon turns that into what happened in
     * the last two minutes. What this asserts is the answer a raid depends on (the window) alongside the sweep's invariant
     * (everything at or after the cutoff survives, whatever order it arrived in), so restoring unbounded growth fails by name and
     * pruning by position would fail here too instead of resolving an abbreviation to the wrong rank.
     */
    [TestMethod]
    public void ACastOlderThanTheHorizonLeavesAndTheEightSecondAnswerIsUnchanged()
    {
        for (var i = 0; i <= 400; i++)
        {
            AddCast("Chokidin", "Bithika", T0 + i);
        }

        // The window the resolution actually asks for, at the newest cast: >= (T0+400) - 8.
        var recent = RecordsStore.Instance.GetCastsBySpellName("Chokidin", 8);
        Assert.AreEqual(9, recent.Count, "eight seconds of one-second casts still answers nine entries after the sweep");
        Assert.AreEqual(T0 + 400, recent[0].BeginTime, "newest first, unchanged by the horizon");
        Assert.AreEqual(T0 + 392, recent[^1].BeginTime, "the oldest second inside the window is inclusive");

        // A question wider than the horizon can only see what was kept: nothing older than newest - horizon may remain.
        var kept = RecordsStore.Instance.GetCastsBySpellName("Chokidin", 100_000);
        // Sweeping on a quarter-horizon cadence bounds this at a horizon plus that cadence, and the point is the ORDER OF MAGNITUDE:
        // 401 casts in, ~2.5 minutes of them retained, versus every cast of the session kept before.
        Assert.IsTrue(kept.Count <= (int)(RecordsStore.CastHistoryHorizonS * 1.3),
            $"the cache stayed bounded ({kept.Count} kept of 401 casts), otherwise this is the growth back");
        Assert.IsTrue(kept.Count >= (int)RecordsStore.CastHistoryHorizonS - 2,
            $"the sweep keeps a horizon of history, not a stub ({kept.Count})");
        foreach (var cast in kept)
        {
            // One cadence interval of slack is by design: a sweep runs on an arriving cast, so the retained set is what that sweep's
            // cutoff allowed — never older than the horizon plus the walk between sweeps, and nothing grows in between because
            // nothing arrives. A second-per-second raid holds a couple of minutes of casts; the session holds no night of them.
            Assert.IsTrue(cast.BeginTime >= T0 + 400 - RecordsStore.CastHistoryHorizonS * 1.25,
                "an entry older than the horizon plus one sweep interval survived — pruning by position rather than by time");
        }
    }

    // The position half of the same law: what is kept is decided by TIME, so a cast parked behind an older one survives.
    [TestMethod]
    public void AnOutOfOrderCastInsideTheHorizonSurvivesTheSweep()
    {
        AddCast("Chokidin", "Bithika", T0 + 10);   // schedules the first sweep
        AddCast("Chokidin", "Bithika", T0 + 500);  // sweeps everything before (T0+380) away
        AddCast("Chokidin", "Reisil", T0 + 499);   // out of order, BEHIND the newer entry it precedes in time
        AddCast("Chokidin", "Bithika", T0 + 505);  // no sweep yet (under the next mark)

        // The resolution's own question at this moment: window >= (T0+505) - 8 holds all three of 500, 499 and 505.
        Assert.AreEqual(3, RecordsStore.Instance.GetCastsBySpellName("Chokidin", 8).Count,
            "nothing inside the eight-second window may be missing — this is what resolves the rank");

        AddCast("Chokidin", "Bithika", T0 + 560);  // sweeps at cutoff (T0+440)
        var kept = RecordsStore.Instance.GetCastsBySpellName("Chokidin", 100_000);
        Assert.IsTrue(kept.Exists(c => c.BeginTime == T0 + 499),
            "the out-of-order cast is inside the horizon, so it answers; a sweep that trimmed by position would have dropped it");

        foreach (var cast in kept)
        {
            Assert.IsTrue(cast.BeginTime >= T0 + 560 - RecordsStore.CastHistoryHorizonS, "nothing older than the horizon remains");
        }
    }
}
