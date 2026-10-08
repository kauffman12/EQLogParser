using System.Diagnostics;

using EQLogParser;

namespace EQLogParser;

/*
 * The measurement behind the cell cache's pet rule (docs/DesignNotes.md → "What a cached walk is allowed to carry").
 *
 * `DamageStatsBuilder` learns ownership while it walks (`UpdatePetMapping`) and reads that learning back at the one expression that decides which
 * ROW a record accumulates onto. A record whose own line named an owner is order-free; an owner-less record is placed by whatever the walk had
 * learned so far — so a cached partial walk can disagree with a full one. The design decision says: carry the two maps beside the cells and
 * invalidate a name's cells when a NEW mapping arrives. Whether that policy is cheap or constant churn is exactly what this probe counts, over the
 * real population the walk sees (materialized records of every row, in the builder's own block order).
 *
 * Replays the builder's rule rather than approximating it:
 *   player = record.AttackerOwner ?? _petToPlayer[attacker]
 *   own row  <=>  (player is null && !_playerPets.ContainsKey(attacker)) || player == Unassigned
 *   else       ->  (player ?? attacker) + " +Pets"
 * so a number here is a statement about the code, not about a similar-looking model of it.
 *
 * Runs only when EQLP_PET_LEARN names a log:
 *   EQLP_PET_LEARN=local/logs/live/eqlog_Incogitable_xegony.txt dotnet test --filter PetLearningProbe --logger "console;verbosity=detailed"
 */
[TestClass]
[DoNotParallelize]
public class PetLearningProbeTest
{
  [TestMethod]
  public void PetLearningProbe_HowManyRecordsArePlacedByLearningRatherThanByTheirOwnLine()
  {
    var path = Resolve(Environment.GetEnvironmentVariable("EQLP_PET_LEARN"));
    if (path is null) Assert.Inconclusive("set EQLP_PET_LEARN=<log>");

    var run = PipelineHarness.RunFileDerived(path);
    var facts = run.Facts;
    var timeline = new EntityTimeline();
    var first = facts.Facts.Length > 0 ? facts.Facts[0].TimeS : 0;
    var last = facts.Facts.Length > 0 ? facts.Facts[^1].TimeS : 0;
    RegistrySeed.Apply(timeline, facts, first, last);
    ClassificationRules.Apply(facts, timeline, run.HealFacts);

    var index = new FightFactIndex(timeline);
    var rows = FightProjection.Build(facts, timeline, index.OnFact);
    Sectionizer.StampGroupIds(rows);

    var input = FightSummarySource.Build(rows, index, facts);
    var blocks = input.Fights
      .OrderBy(f => f.Id)
      .SelectMany(f => f.DamageBlocks)
      .OrderBy(b => b.BeginTime)
      .ToList();

    Console.WriteLine($"[petlearn] {path!} | rows {rows.Count:N0} | blocks {blocks.Count:N0}");

    /*
     * PASS 1 — when does each name first get an owner? Collected up front so the counting pass is O(1) per record: asking "is this name learned
     * later?" by scanning forward would cost blocks x names, which turns a probe into a square and gets it deleted for being slow.
     */
    /*
     * Two learning moments matter, and the second one is the big one. A name enters `_petToPlayer` when a record of ITS OWN carries an owner word;
     * it enters `_playerPets` when some OTHER record names it as the owner. The second is what places an owner's own melee hits: while her name is
     * unknown the record forms "X", once it is known the same record goes to "X +Pets" — so a raider with pets has order-dependent placement on
     * every one of her own swings that precedes the first line naming her as an owner.
     */
    var firstPetClaimAt = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    var firstOwnerClaimAt = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    foreach (var block in blocks)
    {
      foreach (var action in block.Actions)
      {
        if (action is not DamageRecord r || string.IsNullOrEmpty(r.AttackerOwner) || r.AttackerOwner == Labels.Unassigned) continue;

        if (r.Attacker is { Length: > 0 } atk
          && (!firstPetClaimAt.TryGetValue(atk, out var seen) || block.BeginTime < seen)) firstPetClaimAt[atk] = block.BeginTime;

        var owner = r.AttackerOwner!;
        if (!firstOwnerClaimAt.TryGetValue(owner, out var seenOwner) || block.BeginTime < seenOwner) firstOwnerClaimAt[owner] = block.BeginTime;
      }
    }

    // The builder's two maps, replayed.
    var playerPets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);          // keys are OWNERS
    var petToPlayer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    long records = 0, ownWord = 0, foldedByLearning = 0, orderDependent = 0, ownerAsAttackerKnown = 0, unassigned = 0;
    double ownWordAmount = 0, foldedAmount = 0, orderDependentAmount = 0;
    var affectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var firstLearnAtFraction = new List<double>();
    var timeSpan = Math.Max(1.0, last - first);

    foreach (var block in blocks)
    {
      var fractionOfCapture = (block.BeginTime - first) / timeSpan;

      foreach (var action in block.Actions)
      {
        if (action is not DamageRecord record) continue;

        records++;
        var attacker = record.Attacker ?? string.Empty;
        var ownOwner = !string.IsNullOrEmpty(record.AttackerOwner) ? record.AttackerOwner! : null;

        if (ownOwner is not null)
        {
          ownWord++;
          ownWordAmount += record.Total;
        }
        else
        {
          var learned = petToPlayer.TryGetValue(attacker, out var mapped) ? mapped : null;
          var knownOwner = playerPets.Contains(attacker);

          if (learned is not null && learned != Labels.Unassigned)
          {
            foldedByLearning++;
            foldedAmount += record.Total;
          }
          else if (knownOwner)
          {
            ownerAsAttackerKnown++;
          }

          if (ownOwner is null && learned is null && !knownOwner)
          {
            // Own row for now — but if THIS selection learns an owner for the name later, a cold walk over this window
            // and a full walk disagree. That difference is what a cache has to absorb or invalidate.
            // Learned by a LATER record's own owner word: a walk that starts at this window answers "own row", a full walk answers
            // "+Pets". This is the whole invalidation population for a carried cell.
            var laterPet = firstPetClaimAt.TryGetValue(attacker, out var learnT) && learnT > block.BeginTime;
            var laterOwner = firstOwnerClaimAt.TryGetValue(attacker, out var ownerT) && ownerT > block.BeginTime;
            if (laterPet || laterOwner)
            {
              orderDependent++;
              orderDependentAmount += record.Total;
              affectedNames.Add(attacker);
            }
          }

          if (ownOwner is null && learned == Labels.Unassigned) unassigned++;
        }

        // Learning, exactly where the builder does it.
        if (ownOwner is not null && ownOwner != Labels.Unassigned)
        {
          var wasKnown = petToPlayer.ContainsKey(attacker);
          playerPets.Add(ownOwner);
          petToPlayer[attacker] = ownOwner;
          if (!wasKnown) firstLearnAtFraction.Add(fractionOfCapture);
        }
      }
    }

    Console.WriteLine($"[petlearn] records {records:N0}");
    Console.WriteLine($"[petlearn]   own owner word (order-free) : {ownWord:N0} ({Pct(ownWord, records)})  {ownWordAmount:N0} dmg");
    Console.WriteLine($"[petlearn]   owner-less, learned ALREADY : {foldedByLearning:N0} ({Pct(foldedByLearning, records)})  {foldedAmount:N0} dmg  <- carry makes these free");
    Console.WriteLine($"[petlearn]   owner-less, attacker is a known OWNER: {ownerAsAttackerKnown:N0} ({Pct(ownerAsAttackerKnown, records)})");
    Console.WriteLine($"[petlearn]   ORDER-DEPENDENT (placed by own row now, learned later): {orderDependent:N0} ({Pct(orderDependent, records)})  {orderDependentAmount:N0} dmg over {affectedNames.Count:N0} names");
    Console.WriteLine($"[petlearn]   owner-less but explicitly UNASSIGNED: {unassigned:N0}");
    Console.WriteLine($"[petlearn] pets learned {petToPlayer.Count:N0}; owners seen {playerPets.Count:N0}");

    if (firstLearnAtFraction.Count > 0)
    {
      var sorted = firstLearnAtFraction.Order().ToList();
      Console.WriteLine($"[petlearn] first learning point in capture: p50 {sorted[sorted.Count / 2]:P1} p90 {sorted[(int)(sorted.Count * 0.9)]:P1} max {sorted[^1]:P1}");
    }

    // The number the decision hangs on, printed plainly for the doc.
    Console.WriteLine($"[petlearn] DECISION: carry-and-stamp population = {orderDependent:N0} of {records:N0} records ({Pct(orderDependent, records)}) over {affectedNames.Count:N0} names");
    Console.WriteLine($"[petlearn]   names learned as PET later: {firstPetClaimAt.Count:N0}; as OWNER later: {firstOwnerClaimAt.Count:N0}");

    /*
     * Bars, not prints — a gated test that only prints is a print harness (docs law: "Passed" on zero asserts is not coverage). These are the cheap
     * bars the carry-and-stamp design assumes, set well above what the three reference captures measured (0.010 %, 1.148 %, 7.792 %; 64 names worst
     * case; 204 learning events worst case). Breaking one means a content shape where ownership churn is normal, and that changes the cache design —
     * so it should fail a build rather than surprise a user with a board whose pet rows moved.
     */
    Assert.IsTrue(records > 100_000, $"the probe needs a real capture to mean anything; got {records:N0} records");
    Assert.IsTrue(orderDependent <= records * 0.15,
      $"placement churn from owner claims landing outside the selection reached {Pct(orderDependent, records)} — carry-and-stamp is no longer cheap");
    Assert.IsTrue(affectedNames.Count <= 250, $"{affectedNames.Count:N0} names churn placement; a per-name rebuild would be the common path, not the exception");
    Assert.IsTrue(petToPlayer.Count + playerPets.Count <= 600,
      $"ownership memory is {petToPlayer.Count:N0} pets / {playerPets.Count:N0} owners; carrying it per build was justified by it being small");
  }

  private static string Pct(long part, long total) => total == 0 ? "n/a" : (100.0 * part / total).ToString("F3") + "%";

  private static string? Resolve(string? env)
  {
    if (string.IsNullOrWhiteSpace(env)) return null;
    var p = Path.IsPathRooted(env) ? env : Path.Combine(Directory.GetCurrentDirectory(), env);
    return File.Exists(p) ? p : null;
  }

}
