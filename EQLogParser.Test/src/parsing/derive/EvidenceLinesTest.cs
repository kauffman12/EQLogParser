using System.Text.RegularExpressions;
using EQLogParser;

namespace EQLogParser;

/*
 * A hover can say more than the one rule that won. The identity list used to print a single proof clause and nothing else, which
 * read as thin exactly where the capture knew the most: a curse whose facts all landed on raid members hovered "No Caster in
 * Line" while its own row was the answer to "is this ours?" (docs/DesignNotes.md → the R21 chapter).
 *
 * So a row now carries:
 *   - TYPE DIRECTION for a Spell name — "Enemy Spell" when everything it hit was one of ours, "Our Spell" when everything it hit
 *     was a monster, plain "Spell" when the targets disagree or read Unknown. The judgement in the cell, the reason in the hover;
 *   - `Row.OtherEvidence` — every OTHER claim on the name plus one fact clause ("Damaged players"), one phrase per line, ranked
 *     by IdentityVocabulary.ClaimRanks and capped at four (the head proof line makes five).
 *
 * The cost rules that shaped this (the operator's constraint, and a fair one): WHOSE-SIDE is resolved once per name into a
 * pool-sized array rather than per fact, claims are enumerated from the timeline's own list without copying it, and the result is
 * retained as ONE string per row — the pane already kept one. A row with a single claim and no direction hovers exactly as it
 * did before this existed: empty tail, one line.
 */
[TestClass]
public class EvidenceLinesTest
{
  private const int MaxExtraLines = 4;

  [TestInitialize]
  public void Setup()
  {
    PlayerRegistry.Instance.Clear();
    IdentityOverrideStore.Instance.Init("Evidence Lines Test");
  }

  [TestCleanup]
  public void Cleanup() => PlayerRegistry.Instance.Clear();

  private static string Stamp(int seconds)
  {
    var t = new DateTime(2026, 3, 1, 20, 0, 0).AddSeconds(seconds);
    return $"[{t:ddd MMM dd HH:mm:ss yyyy}]";
  }

  private static ClassificationReport Census(out EntityTimeline timeline, params (int S, string Line)[] lines)
  {
    var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evidence-" + Guid.NewGuid().ToString("N")));
    var log = Path.Combine(dir.FullName, "eqlog_Evidence_Eqgate.txt");
    try
    {
      File.WriteAllLines(log, lines.Select(l => $"{Stamp(l.S)} {l.Line}"));
      var run = PipelineHarness.RunFileDerived(log);
      timeline = new EntityTimeline();
      ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);
      return ClassificationReport.Build(timeline, run.Facts, run.HealFacts,
                                        IdentityOverrideStore.Instance, PlayerRegistry.Instance);
    }
    finally
    {
      try { Directory.Delete(dir.FullName, true); } catch (IOException) { }
    }
  }

  /*
   * The reported case, seen from the other side: a caster-less damage line that lands on US. The name is still not a fighter
   * (Kind stays Spell — it has no hands), but the cell can honestly carry the judgement the facts support, and the hover states
   * what made it.
   */
  [TestMethod]
  public void ACasterLessSpellThatHurtUsReadsEnemySpell()
  {
    var report = Census(out _,
                        (0, "You have taken 12000 damage from Tinstag Rk. II by ."),
                        (2, "You have taken 9000 damage from Tinstag Rk. II by ."));

    var row = report.Find("Tinstag Rk. II");
    Assert.IsNotNull(row, "the substituted spell name is a row: " + string.Join(", ", report.Rows.Select(r => r.Name)));
    Assert.AreEqual(IdentityKind.Spell, row.Kind);
    Assert.AreEqual("Enemy Spell", row.TypeDisplay);
    Assert.AreEqual(2, row.HitsOnRaid);
    Assert.AreEqual(0, row.HitsOnMobs);

    // The phrase says the reason for the word, in the same words a person would use. No counts, no rule codes.
    Assert.AreEqual("Damaged players", row.OtherEvidence);
  }

  /// <summary>The opposite direction — our own dot on the raid's targets — reads "Our Spell".</summary>
  [TestMethod]
  public void ACasterLessSpellThatHurtMonstersReadsOurSpell()
  {
    var report = Census(out _,
                        (0, "A gnoll has taken 335500 damage from Strangle XVII Rk. III by ."),
                        (2, "A kobold has taken 21000 damage from Strangle XVII Rk. III by ."));

    var row = report.Find("Strangle XVII Rk. III");
    Assert.IsNotNull(row, "the substituted spell name is a row: " + string.Join(", ", report.Rows.Select(r => r.Name)));
    Assert.AreEqual(IdentityKind.Spell, row.Kind);
    Assert.AreEqual("Our Spell", row.TypeDisplay);
    Assert.AreEqual("Damaged monsters", row.OtherEvidence);
  }

  /*
   * Targets that DISAGREE get the plain word. "Both" is not a judgement about who owns the spell — the same DoT can tick on a
   * charmed raid member and on the raid's actual target inside one pull — so the cell declines and the hover says both halves.
   */
  [TestMethod]
  public void ASpellHittingBothSidesStaysPlainlyASpell()
  {
    var report = Census(out _,
                        (0, "You have taken 4000 damage from Doomsigil XII Rk. III by ."),
                        (2, "A gnoll has taken 4000 damage from Doomsigil XII Rk. III by ."));

    var row = report.Find("Doomsigil XII Rk. III");
    Assert.IsNotNull(row, "the substituted spell name is a row: " + string.Join(", ", report.Rows.Select(r => r.Name)));
    Assert.AreEqual(IdentityKind.Spell, row.Kind);
    Assert.AreEqual("Spell", row.TypeDisplay);
    Assert.AreEqual("Damaged players and monsters", row.OtherEvidence);

    // ...and only Spell rows ever get a direction word: a person's cell is the kind, whatever they hit.
    Assert.AreEqual("Player", IdentityVocabulary.TypeWordFor(IdentityKind.Player, 99, 0));
    Assert.AreEqual("NPC", IdentityVocabulary.TypeWordFor(IdentityKind.Npc, 0, 99));
  }

  /*
   * More than one rule spoke about this name — npcs.txt and a charm line — so the hover lists both instead of only the winner.
   * ("Frost" is in the shipped npcs.txt; that data file is already a fixture for R6 across this suite.)
   */
  [TestMethod]
  public void EveryRuleThatSpokeAboutANameGetsALine()
  {
    var report = Census(out _,
                        (0, "You have taken 44 damage from Frost by Claw of Frost."),
                        (5, "Frost has been charmed."));

    var row = report.Find("Frost");
    Assert.IsNotNull(row, "Frost is a row: " + string.Join(", ", report.Rows.Select(r => r.Name)));
    Assert.IsFalse(string.IsNullOrEmpty(row.OtherEvidence),
                   "two rules claimed this name; the hover of a name the rules DISAGREE about is the one that must say so");
    CollectionAssert.Contains(Rows(row), IdentityVocabulary.ProofText("R6-npcdb", IdentityKind.Npc));
  }

  /*
   * The cap and its two honesty rules: never more than the budget, never a rule code, and never a phrase that changed between
   * two passes over the same capture (the census runs on a timer while somebody reads it — flickering wording is worse than a
   * shorter one).
   */
  [TestMethod]
  public void ExtraLinesAreCappedCarryNoCodesAndDoNotMove()
  {
    var first = Census(out _,
                       (0, "You have taken 44 damage from Frost by Claw of Frost."),
                       (2, "Ashk has taken 500 damage from Flame Lick by Grub."),
                       (5, "Frost has been charmed."),
                       (7, "A gnoll has taken 900 damage from Doomsigil XII Rk. III by ."));
    var again = Census(out _,
                       (0, "You have taken 44 damage from Frost by Claw of Frost."),
                       (2, "Ashk has taken 500 damage from Flame Lick by Grub."),
                       (5, "Frost has been charmed."),
                       (7, "A gnoll has taken 900 damage from Doomsigil XII Rk. III by ."));

    foreach (var row in first.Rows)
    {
      var lines = Rows(row);
      Assert.IsTrue(lines.Count <= MaxExtraLines, $"{row.Name} exceeds the hover budget ({lines.Count}): {row.OtherEvidence}");

      foreach (var line in lines)
      {
        Assert.IsFalse(line.Contains('['), $"{row.Name}: rule code reached the hover: {line}");
        Assert.IsFalse(Regex.IsMatch(line, @"\bR\d+-"), $"{row.Name}: rule code reached the hover: {line}");
        Assert.AreNotEqual("Unmapped", line, $"{row.Name}: unmapped provenance reached the hover");
        Assert.AreNotEqual(string.Empty, line, $"{row.Name}: blank line in the hover");

        /*
         * An evidence line is never JUST the verdict: "NPC" as its own line would be the Type column printed twice and would say
         * nothing about why. A phrase may still NAME a source whose own words contain one — "In the NPC DB" does, and that is the
         * database's name, not a second opinion about the kind.
         */
        var kinds = new[] { IdentityKind.Npc, IdentityKind.Player, IdentityKind.Pet, IdentityKind.Merc, IdentityKind.Unknown };
        Assert.IsFalse(kinds.Any(k => string.Equals(line.Trim(), IdentityVocabulary.TypeWord(k), StringComparison.Ordinal)),
                       $"{row.Name}: a verdict word dressed as evidence ({line})");
      }

      var twin = again.Find(row.Name);
      Assert.IsNotNull(twin);
      Assert.AreEqual(row.OtherEvidence, twin.OtherEvidence, $"{row.Name}: the hover changed between two identical passes");
      Assert.AreEqual(row.TypeDisplay, twin.TypeDisplay, $"{row.Name}: the cell word changed between identical passes");
    }
  }

  private static List<string> Rows(ClassificationReport.Row row)
    => row.OtherEvidence.Length == 0 ? [] : row.OtherEvidence.Split('\n').ToList();
}
