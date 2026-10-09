namespace EQLogParser;

/*
 * TEMPORARY PROBE (delete after reading): what an operator's verdict actually does to the boards.
 *
 * Field report 2026-10-09: "changed a player to an NPC and it refreshed and they were removed, but then i reselected fights
 * and they came back as if they were a player"; "tried changing one of the players to be someone's pet ... they stayed
 * listed as a player". This walks the SAME seam the app walks (seed → rules → overrides → projection+index →
 * FightSummarySource.Build) twice, before and after the write, and prints where the name sits each time.
 *
 * EQLP_OVERRIDE_PROBE=<log> [EQLP_PROBE_NAMES=Jondolar,Trelania] [EQLP_PROBE_OWNER=Atvar] dotnet test EQLogParser.Test --filter TempOverrideProbe
 */
[TestClass]
[DoNotParallelize]
public class TempOverrideProbeTest
{
  [TestMethod]
  public void ProbeWhatAVerdictDoesToTheBoards()
  {
    var path = Environment.GetEnvironmentVariable("EQLP_OVERRIDE_PROBE");
    if (string.IsNullOrEmpty(path) || !File.Exists(path)) Assert.Inconclusive("set EQLP_OVERRIDE_PROBE=<log>");

    PipelineHarness.EnsureDataStore();
    DamageLineParser.ResetProcessState();
    HealingLineParser.ClearCaches();
    RecordsStore.Instance.Clear(false);
    PlayerRegistry.Instance.Clear();
    IdentityOverrideStore.Instance.Apply([], null);

    var names = (Environment.GetEnvironmentVariable("EQLP_PROBE_NAMES") ?? "Jondolar,Trelania")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var owner = Environment.GetEnvironmentVariable("EQLP_PROBE_OWNER") ?? "Atvar";

    var run = PipelineHarness.RunFileDerived(path!);
    var facts = run.Facts;
    Console.WriteLine($"[capture] {facts.FactCount:N0} damage facts, {run.HealFacts?.HealCount ?? 0:N0} heals");

    foreach (var name in names)
      Report(facts, run.Timeline, "as captured", name);

    // ---- baseline pass: what the app's full pass does ----
    var before = Pass(facts, run);
    foreach (var name in names)
    {
      Console.WriteLine($"[pass:none] {name} rows={RowCount(before.Fights, name)} " +
                        $"asAttacker={AttacksIn(before, name, true):N0}/{SumIn(before, name, true):N0} " +
                        $"asTakenByOtherRows={AttacksIn(before, name, false):N0} ownerField={OwnerField(before, name)}");
    }

    // What PopulateSpecials keys a ConcurrentDictionary on: a null here throws and takes the WHOLE board build down.
    {
      var resists = RecordsStore.Instance.GetAllResists().ToList();
      var nullAttacker = resists.Count(r => r.Item2.Attacker is null);
      var nullSpell = resists.Count(r => r.Item2.Spell is null);
      Console.WriteLine($"[resists] total={resists.Count:N0} nullAttacker={nullAttacker:N0} nullSpell={nullSpell:N0}");
      foreach (var r in resists.Where(r => r.Item2.Attacker is null || r.Item2.Spell is null).Take(5))
        Console.WriteLine($"    resist sample: attacker={r.Item2.Attacker ?? "<null>"} spell={r.Item2.Spell ?? "<null>"}");
    }

    DumpBoard(before, "none", names[0]);

    // ---- the operator sets one of them NPC (the sanctioned write) ----
    IdentityOverrideStore.Instance.Set(names[0], IdentityKind.Npc);
    var npc = Pass(facts, run);
    Report(facts, npc.Timeline, "after Set Npc", names[0]);
    Console.WriteLine($"[pass:npc] {names[0]} rows={RowCount(npc.Fights, names[0])} " +
                      $"asAttacker={AttacksIn(npc, names[0], true):N0}/{SumIn(npc, names[0], true):N0} " +
                      $"asTakenByOtherRows={AttacksIn(npc, names[0], false):N0} ownerField={OwnerField(npc, names[0])}");
    DumpBoard(npc, "npc", names[0]);
    DumpBoard(npc, "npc", names.Length > 1 ? names[1] : names[0]);

    // ---- and the pet claim: mapping only (what `Assign X as Pet of Y` writes today) ----
    IdentityOverrideStore.Instance.Remove(names[0]);
    if (names.Length > 1)
    {
      // The door the operator clicks (it now forgets + asserts the kind too, which is what this probe exists to see).
      PetAssignment.Assign(names[1], owner);
      var pet = Pass(facts, run);
      Report(facts, pet.Timeline, "after pet mapping", names[1]);
      Console.WriteLine($"[pass:petmap] {names[1]} rows={RowCount(pet.Fights, names[1])} " +
                        $"asAttacker={AttacksIn(pet, names[1], true):N0}/{SumIn(pet, names[1], true):N0} " +
                        $"ownerField={OwnerField(pet, names[1])} timelineOwnerOf={pet.Timeline.OwnerOf(names[1]!, 0)}");

      // And taking it back: the name should return to its own row.
      ClassificationCommands.ApplyVerdict(names[1], IdentityKind.Unknown);
      var petKind = Pass(facts, run);
      Report(facts, petKind.Timeline, "after taking the pet claim back", names[1]);
      Console.WriteLine($"[pass:unset] {names[1]} rows={RowCount(petKind.Fights, names[1])} " +
                        $"asAttacker={AttacksIn(petKind, names[1], true):N0}/{SumIn(petKind, names[1], true):N0} " +
                        $"ownerField={OwnerField(petKind, names[1])} timelineOwnerOf={petKind.Timeline.OwnerOf(names[1]!, 0)}");
      DumpBoard(pet, "petmap", names[1]);
      DumpBoard(petKind, "petset", names[1]);

      IdentityOverrideStore.Instance.Remove(names[1]);
    }

    PlayerRegistry.Instance.Clear();
  }

  private sealed record PassResult(EntityTimeline Timeline, List<DerivedFight> Fights, FightFactIndex Index, SummaryInput Input);

  // The app's full pass, in the app's order: seed → rules → overrides → projection with its index → materialize.
  private static PassResult Pass(DamageFactTable facts, PipelineHarness.DeriveRunResult run)
  {
    var line = new EntityTimeline();
    RegistrySeed.Apply(line, facts, double.NegativeInfinity, double.PositiveInfinity);
    ClassificationRules.Apply(facts, line, run.HealFacts);
    IdentityOverrideStore.Instance.Apply(line);

    var index = new FightFactIndex(line);
    var fights = FightProjection.Build(facts, line, index.OnFact);
    Sectionizer.StampGroupIds(fights);

    // Everything selected: what the operator did ("i tried changing... then i reselected fights" — a group select).
    return new PassResult(line, fights, index, FightSummarySource.Build(fights, index, facts));
  }

  // Runs the real damage builder over what this pass materialized and says whether the name is on the board at all.
  private static void DumpBoard(PassResult p, string label, string target)
  {
    var builder = DamageStatsBuilder.Instance;
    builder.EventsGenerationStatus += static _ => { };
    var options = new GenerateStatsOptions { Source = $"probe {label}", AllRanges = p.Input.AllRanges, MinSeconds = 0 };
    foreach (var fight in p.Input.Fights) options.Npcs.Add(fight);
    builder.BuildTotalStats(options);

    var stats = builder.GetLastStats()?.CombinedStats;
    if (stats is null) { Console.WriteLine($"[board:{label}] no board"); return; }

    var hit = stats.StatsList.Where(s => s.Name.Contains(target, StringComparison.OrdinalIgnoreCase)).ToList();
    Console.WriteLine($"[board:{label}] rows={stats.StatsList.Count:N0} raid={stats.RaidStats.Total:N0} " +
                      $"nameRows={hit.Count} classMapHasIt={stats.PlayerClasses.ContainsKey(target)}");
    foreach (var row in hit)
      Console.WriteLine($"    row <{row.Name}> total={row.Total:N0} class={row.ClassName ?? "-"} top={row.IsTopLevel}");
  }

  private static void Report(DamageFactTable facts, EntityTimeline line, string stage, string name)
  {
    var kind = line.IdentityWithSource(name, out var source);
    Console.WriteLine($"[{stage}] {name}: kind={kind} source={source ?? "-"} " +
                      $"lookupKind={IdentityLookup.KindAt(name)} ownerOf={line.OwnerOf(name!, double.NegativeInfinity)}");
  }

  private static int RowCount(List<DerivedFight> rows, string name)
    => rows.Count(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

  // How many materialized records name this one as the ATTACKER (that is what puts a row on the damage board).
  private static int AttacksIn(PassResult p, string name, bool damageSide)
    => Rows(p, name, damageSide).Count;

  private static double SumIn(PassResult p, string name, bool damageSide)
    => Rows(p, name, damageSide).Sum(r => r.Total);

  private static List<DamageRecord> Rows(PassResult p, string name, bool damageSide)
  {
    var list = new List<DamageRecord>();
    foreach (var fight in p.Input.Fights)
    {
      var blocks = damageSide ? fight.DamageBlocks : fight.TankingBlocks;
      foreach (var block in blocks)
        foreach (var action in block.Actions)
          if (action is DamageRecord record && string.Equals(record.Attacker, name, StringComparison.OrdinalIgnoreCase))
            list.Add(record);
    }
    return list;
  }

  private static string OwnerField(PassResult p, string name)
  {
    foreach (var fight in p.Input.Fights)
      foreach (var block in fight.DamageBlocks)
        foreach (var action in block.Actions)
          if (action is DamageRecord record && string.Equals(record.Attacker, name, StringComparison.OrdinalIgnoreCase))
            return record.AttackerOwner ?? "null";
    return "-";
  }
}
