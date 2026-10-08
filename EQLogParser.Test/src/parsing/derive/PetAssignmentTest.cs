namespace EQLogParser;

/*
 * What an operator's "this summon belongs to that raider" click does, and what it must not do.
 *
 * Three doors make that claim (the damage summary's `Assign … as Pet of`, the tanking summary's, and the Pet Owners window's
 * owner picker), and they all go through `PetAssignment.Assign` — one seam, because the pair has to reach the boards through
 * exactly one path or the three doors disagree about what clicking does. Two halves are pinned here:
 *
 *   1. THE WRITE: the pair lands in the store (petmapping.txt's lane) and exactly ONE derive pass is asked for. A claim already
 *      in effect is refused before anything is spent — no file write, no pass — because picking the owner that is already on
 *      screen is a click on nothing, and a full pass over a live capture is not free.
 *   2. THE MECHANISM that makes the pass worth asking for: `RegistrySeed.ApplyPetMappings` turns the pair into an ownership
 *      interval, `FightSummarySource` stamps a fact's `AttackerOwner` from `EntityTimeline.OwnerOf`, and only then does the
 *      damage board fold the summon under its person as `Beorun +Pets`. Before the pair exists, "Picklepaw" is a row of its own.
 *
 * The fixture deliberately names the pet WITHOUT an apostrophe-s owner ("Picklepaw bites …" rather than "Beorun`s pet"),
 * because that is the case the operator's claim is for: the line says nothing about ownership, so the only way its damage can
 * reach Beorun is through the mapping. A possessive line would fold with or without this seam (the line is its own evidence).
 */
[TestClass]
[DoNotParallelize]
public class PetAssignmentTest
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "mini-data", "derive", "pet-assign.txt");

    private int _passes;
    private Action? _originalReroute;

    [TestInitialize]
    public void Setup()
    {
        PipelineHarness.EnsureDataStore();
        DamageLineParser.ResetProcessState();
        HealingLineParser.ClearCaches();
        RecordsStore.Instance.Clear(false);
        PlayerRegistry.Instance.Clear();

        _originalReroute = PetAssignment.Reroute;
        _passes = 0;
    }

    [TestCleanup]
    public void Cleanup()
    {
        PetAssignment.Reroute = _originalReroute;
        UnaskedRefresh.ClearGesture();   // the gesture is process state; do not spend a forced build in the next test class
        DamageLineParser.ResetProcessState();
        HealingLineParser.ClearCaches();
        RecordsStore.Instance.Clear(false);
        PlayerRegistry.Instance.Clear();
    }

    [TestMethod]
    public void APairIsWrittenOnceAndAsksForOnePass()
    {
        PetAssignment.Reroute = () => _passes++;

        Assert.IsTrue(PetAssignment.Assign("Picklepaw", "Beorun"), "the claim was refused");
        Assert.AreEqual("Beorun", PlayerRegistry.Instance.GetPlayerFromPet("Picklepaw"));
        Assert.AreEqual(1, _passes, "an ownership claim changes which row a fact belongs to, so one pass is owed");

        // The write also OWNS the announce that pass produces: the patch announces ContentMoved/RowEdited, and a whole-capture
        // selection declines those (UnaskedRefresh). A click does not get declined by a policy written for traffic.
        Assert.IsTrue(UnaskedRefresh.TryConsumeGesture(out var owed), "the claim has to lend its gesture to the pass it asked for");
        StringAssert.Contains(owed, "Picklepaw");

        // The same claim again — the picker preselecting what is already there, or a double click: nothing to write and no
        // pass to spend. Asserted on the counter, not just the return value, because the pass is the expensive half.
        Assert.IsFalse(PetAssignment.Assign("Picklepaw", "Beorun"), "a claim already in effect must not be written again");
        Assert.AreEqual(1, _passes, "a no-op must not spend a derive pass");

        // Case is not a new claim: every entity key in this pipeline is case-insensitive, and a twin pair would rewrite
        // petmapping.txt to say the same thing and then re-derive for it.
        Assert.IsFalse(PetAssignment.Assign("picklepaw", "beorun"));
        Assert.AreEqual(1, _passes);
    }

    [TestMethod]
    public void ABlankNameOrOwnerWritesNothing()
    {
        PetAssignment.Reroute = () => _passes++;

        Assert.IsFalse(PetAssignment.Assign(null, "Beorun"));
        Assert.IsFalse(PetAssignment.Assign("Picklepaw", null));
        Assert.IsFalse(PetAssignment.Assign("  ", "Beorun"));
        Assert.IsFalse(PetAssignment.Assign("Picklepaw", "   "));
        Assert.AreEqual(0, _passes, "a menu clicked over nothing cannot ask for a build");
        Assert.AreEqual(0, PlayerRegistry.Instance.GetPetMappings().Count);
    }

    [TestMethod]
    public void AnAssignmentWithoutASessionStillSavesThePair()
    {
        // No app wiring (the harness never sets Reroute) — filling the Pet Owners window before opening any log has to be a
        // fact for next time rather than a throw.
        PetAssignment.Reroute = null;

        Assert.IsTrue(PetAssignment.Assign("Picklepaw", "Beorun"));
        Assert.AreEqual("Beorun", PlayerRegistry.Instance.GetPlayerFromPet("Picklepaw"));
    }

    [TestMethod]
    public void TheAssignedSummonFoldsUnderItsPersonOnTheNextPass()
    {
        var run = PipelineHarness.RunFileDerived(FixturePath);

        // Before: nothing owns the summon, so its damage is a row of its own — and no aggregate exists to fold into.
        var before = Board(run);
        var alone = Find(before.Stats, "Picklepaw");
        Assert.IsNotNull(alone, "an unowned summon stands on its own row");
        Assert.AreEqual(450, alone.Total);
        Assert.IsNull(Find(before.Stats, "Beorun +Pets"), "there is no aggregate before anyone claims the summon");

        // The operator's click, exactly as the panes make it: write the pair, ask for the pass.
        var asked = 0;
        PetAssignment.Reroute = () => asked++;
        Assert.IsTrue(PetAssignment.Assign("Picklepaw", "Beorun"));
        Assert.AreEqual(1, asked);

        // After: a NEW pass — which is what RederiveAsync means — re-seeds the pair as an ownership interval, the facts carry
        // it, and the board folds. Re-running Board() is the headless stand-in for that pass; the production path is
        // DeriveEngine.RederiveAsync → RegistrySeed.Apply → the same FightSummarySource.Build this helper calls.
        var after = Board(run);
        var aggregate = Find(after.Stats, "Beorun +Pets");
        Assert.IsNotNull(aggregate, "the claimed summon's damage has to reach its person");
        // The aggregate is the person AND the summon (DamageStatsBuilder folds children into `X +Pets`, and the golden
        // holds that row equal to the sum of its children), so it reads Beorun's 1,300 plus the summon's 450.
        Assert.AreEqual(1750, aggregate.Total, "the aggregate is the person's own damage plus the claimed summon's");

        var kids = after.Children.TryGetValue("Beorun +Pets", out var list) ? list : [];
        CollectionAssert.Contains(kids.Select(k => k.Name).ToList(), "Picklepaw");
        CollectionAssert.Contains(kids.Select(k => k.Name).ToList(), "Beorun");
        Assert.AreEqual(450, kids.First(k => k.Name == "Picklepaw").Total, "the summon's own 250 + 200, and nobody else's");
        Assert.IsNull(Find(after.Stats, "Picklepaw"), "the summon stops being a row of its own once somebody owns it");

        // And the signal every surface repaints from: an ownership interval moves the timeline's digest, which is one of the
        // two terms FightTable.SelectionStamp folds — without it the pane would answer "nothing moved" and keep the old board.
        Assert.AreNotEqual(before.Stamp, after.Stamp, "an ownership change has to move the content stamp, or no pane repaints");
    }

    /// <summary>One full headless pass over the capture: seed → classify → project → materialize → build the damage board.</summary>
    private static (IReadOnlyList<PlayerStats> Stats, Dictionary<string, List<PlayerStats>> Children, long Stamp) Board(
        PipelineHarness.DeriveRunResult run)
    {
        var timeline = new EntityTimeline();
        var facts = run.Facts;
        var first = facts.Facts.Length > 0 ? facts.Facts[0].TimeS : 0;
        var last = facts.Facts.Length > 0 ? facts.Facts[^1].TimeS : 0;
        RegistrySeed.Apply(timeline, facts, first, last);
        ClassificationRules.Apply(facts, timeline, run.HealFacts);

        var index = new FightFactIndex(timeline);
        var rows = FightProjection.Build(facts, timeline, index.OnFact);
        Sectionizer.StampGroupIds(rows);
        var input = FightSummarySource.Build(rows, index, facts);

        var options = new GenerateStatsOptions { Source = "pet assignment test" };
        var range = new TimeRange();
        foreach (var row in input.Fights)
        {
            if (double.IsNaN(row.BeginDamageTime) || double.IsNaN(row.LastDamageTime)) continue;
            range.Add(new TimeSegment(row.BeginDamageTime, row.LastDamageTime));
            options.Npcs.Add(row);
        }

        options.AllRanges = range;
        DamageStatsBuilder.Instance.BuildTotalStats(options);
        var board = DamageStatsBuilder.Instance.GetLastStats()?.CombinedStats;
        Assert.IsNotNull(board, "the fixture produced no damage board");

        return ([.. board.StatsList], board.Children, timeline.StateStamp());
    }

    private static PlayerStats? Find(IReadOnlyList<PlayerStats> stats, string name)
      => stats.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}
