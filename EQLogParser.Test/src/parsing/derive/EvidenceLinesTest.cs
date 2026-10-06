using System.Text.RegularExpressions;
using EQLogParser;

namespace EQLogParser;

/*
 * A hover can say more than the one rule that won. The identity list used to print a single proof clause and nothing else, which
 * read as thin exactly where the capture knew the most: a curse whose facts all landed on raid members hovered "No Caster in
 * Line" while its own row was the answer to "is this ours?" (docs/DesignNotes.md → the R21 chapter).
 *
 * So a row now carries:
 *   - TYPE DIRECTION for a Spell name — "NPC Spell" when everything it hit was one of ours, "Player Spell" when everything it hit
 *     was a monster, plain "Spell" when the targets disagree or read Unknown. The judgement in the cell, the reason in the hover;
 *   - `Row.OtherEvidence` — every OTHER claim on the name plus one fact clause ("Damaged Players"), one phrase per line, ranked
 *     by IdentityVocabulary.ClaimRanks and capped at nine (the head proof line makes ten); a SPELL row also says whether this
 *     build's spell database contains the name, because the three R21 proofs say HOW a name was learned and only one of them
 *     is a data lookup (`ASpellRowSaysWhetherTheSpellDatabaseKnowsTheName`).
 *
 * The cost rules that shaped this (the operator's constraint, and a fair one): WHOSE-SIDE is resolved once per name into a
 * pool-sized array rather than per fact, claims are enumerated from the timeline's own list without copying it, and the result is
 * retained as ONE string per row — the pane already kept one. A row with a single claim and no direction hovers exactly as it
 * did before this existed: empty tail, one line.
 */
[TestClass]
public class EvidenceLinesTest
{
  private const int MaxExtraLines = 9;

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
    Assert.AreEqual("NPC Spell", row.TypeDisplay);
    Assert.AreEqual(2, row.HitsOnRaid);
    Assert.AreEqual(0, row.HitsOnMobs);

    // The phrase says the reason for the word, in the same words a person would use. No counts, no rule codes.
    AssertSpellTail(row, "Damaged Players");
  }

  /// <summary>The opposite direction — a dot on the raid's targets — reads "Player Spell".</summary>
  [TestMethod]
  public void ACasterLessSpellThatHurtMonstersReadsOurSpell()
  {
    var report = Census(out _,
                        (0, "A gnoll has taken 335500 damage from Strangle XVII Rk. III by ."),
                        (2, "A kobold has taken 21000 damage from Strangle XVII Rk. III by ."));

    var row = report.Find("Strangle XVII Rk. III");
    Assert.IsNotNull(row, "the substituted spell name is a row: " + string.Join(", ", report.Rows.Select(r => r.Name)));
    Assert.AreEqual(IdentityKind.Spell, row.Kind);
    Assert.AreEqual("Player Spell", row.TypeDisplay);
    AssertSpellTail(row, "Damaged NPCs");
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
    AssertSpellTail(row, "Damaged Players and NPCs");

    // ...and only Spell rows ever get a direction word: a person's cell is the kind, whatever they hit.
    Assert.AreEqual("Player", IdentityVocabulary.TypeWordFor(IdentityKind.Player, 99, 0));
    Assert.AreEqual("NPC", IdentityVocabulary.TypeWordFor(IdentityKind.Npc, 0, 99));
  }

  /*
   * A SPELL row says whether the shipped spell database knows its name. Two of R21's three proofs say nothing about the data,
   * which left a reader unable to tell "a rank newer than this build" from "a name that only LOOKED like a spell" — asked
   * directly: "the ones listed as spell maybe also check if they're in the spell database? that seems useful to know". The line
   * is added only when no claim already states membership (R21-spelleffect IS the lookup), so the hover never says it twice.
   */
  [TestMethod]
  public void ASpellRowSaysWhetherTheSpellDatabaseKnowsTheName()
  {
    // These two guards are assertions ABOUT THE SHIPPED DATA: if spells.txt gains or loses a row, this test says so here rather
    // than quietly testing one direction twice.
    Assert.IsTrue(ClassificationRules.SpellNamed("Strangle XVII Rk. III"),
                  "fixture: the shipped spell database must still know this rank (id 72747)");
    Assert.IsFalse(ClassificationRules.SpellNamed("Tinstag Rk. II"),
                   "fixture: this name must stay unknown to the shipped spell database");

    var report = Census(out _,
                        (0, "A gnoll has taken 900 damage from Strangle XVII Rk. III by ."),
                        (2, "You have taken 900 damage from Tinstag Rk. II by ."));

    var known = report.Find("Strangle XVII Rk. III");
    var missing = report.Find("Tinstag Rk. II");
    Assert.IsNotNull(known);
    Assert.IsNotNull(missing);

    // Known: either this table's own clause or the claim that is the lookup — and never the wrong answer.
    var knownLines = Rows(known);
    Assert.IsTrue(knownLines.Contains(IdentityVocabulary.InSpellDbPhrase)
                  || knownLines.Contains(IdentityVocabulary.ProofText("R21-spelleffect", IdentityKind.Spell)),
                  $"the database knows this name but the hover says: {known.OtherEvidence}");
    Assert.IsFalse(knownLines.Contains(IdentityVocabulary.NotInSpellDbPhrase),
                   $"the database knows this name but the hover says: {known.OtherEvidence}");

    CollectionAssert.Contains(Rows(missing), IdentityVocabulary.NotInSpellDbPhrase,
                              $"a Spell name the data has never heard of must say so: {missing.OtherEvidence}");
  }

  /*
   * Pets are not players. Real line from the 11-30-25 capture: `Waxwork Abolishion hits Sancus`s pet for 37072 points of damage.`
   * The pet reads Pet through R5's own ownership cut, so a name whose victims are pets has to say PETS - "Damaged Players" was
   * the word, and Ashenback spends 798 facts on those victims (docs/DesignNotes.md → the fact-clause chapter).
   */
  [TestMethod]
  public void ANameWhoseVictimsArePetsSaysPets()
  {
    var report = Census(out _,
                        (0, "Waxwork Abolishion hits Sancus`s pet for 37072 points of damage."),
                        (2, "Waxwork Abolishion hits Kogbag`s pet for 21299 points of damage."));

    var row = report.Find("Waxwork Abolishion");
    Assert.IsNotNull(row);
    Assert.AreEqual(2, row.HitsOnPets, "both defenders carry an ownership word: " + row.Kind);
    Assert.AreEqual(0, row.HitsOnRaid, "a pet is not a player");
    Assert.AreEqual(IdentityVocabulary.DamagedPetsPhrase, row.OtherEvidence);
  }

  /*
   * And self is neither. The row that started this: Bjpotratz's 61 "raid-side" facts were all one line - her own Evoker beam,
   * refracted back onto her - and the hover said "Damaged Players" about a woman who never touched an ally.
   */
  [TestMethod]
  public void ANameWhoseOnlyVictimIsItselfSaysItself()
  {
    var report = Census(out _,
                        (0, "Bjpotratz hit Bjpotratz for 29847 points of fire damage by Corona Beam Refraction X."),
                        (3, "Bjpotratz hit Bjpotratz for 31120 points of fire damage by Corona Beam Refraction X."));

    var row = report.Find("Bjpotratz");
    Assert.IsNotNull(row);
    Assert.AreEqual(2, row.SelfHits);
    Assert.AreEqual(0, row.HitsOnRaid, "a reflect is not damage done to players");
    Assert.AreEqual(IdentityVocabulary.DamagedSelfPhrase, row.OtherEvidence,
                    $"{row.Name} hit only itself but the hover says \"{row.OtherEvidence}\"");
  }

  /// <summary>Victims of several kinds are all named - the clause never picks a favourite and hides the rest.</summary>
  [TestMethod]
  public void ANameThatHitPetsAndMonstersNamesBoth()
  {
    var report = Census(out _,
                        (0, "Waxwork Abolishion hits Sancus`s pet for 37072 points of damage."),
                        (2, "Waxwork Abolishion kicks A gnoll for 500 points of damage."));

    var row = report.Find("Waxwork Abolishion");
    Assert.IsNotNull(row);
    Assert.AreEqual(IdentityVocabulary.DamagedNpcsAndPetsPhrase, row.OtherEvidence);
  }

  /*
   * THE CLAUSE TABLE IS CLOSED, and every sentence in it stays short enough for a tooltip: one line, no wrapping, the head proof
   * line still fits above it. "Damaged Players, Pets and NPCs" at 30 characters is the longest this can say - which is why the
   * cap is measured against the table rather than remembered (a longer phrase belongs in the cell, not the hover).
   */
  [TestMethod]
  public void TheClauseTableIsEightSentencesAndEveryOneFitsTheHover()
  {
    var expected = new[]
    {
      null, IdentityVocabulary.DamagedPlayersPhrase, IdentityVocabulary.DamagedNpcsPhrase,
      IdentityVocabulary.DamagedPlayersAndNpcsPhrase, IdentityVocabulary.DamagedPetsPhrase,
      IdentityVocabulary.DamagedPlayersAndPetsPhrase, IdentityVocabulary.DamagedNpcsAndPetsPhrase,
      IdentityVocabulary.DamagedEveryonePhrase
    };

    for (var flags = 0; flags < 8; flags++)
    {
      var clause = IdentityVocabulary.DirectionPhrase(flags & 1, (flags >> 1) & 1, (flags >> 2) & 1, 0);
      Assert.AreEqual(expected[flags], clause, $"victim flags {flags}");
      if (clause is not null) Assert.AreEqual(clause.Length, clause.Trim().Length, $"flags {flags} carry stray space");
    }

    // Self answers only when nothing else did: a name with real targets reports those.
    Assert.AreEqual(IdentityVocabulary.DamagedSelfPhrase, IdentityVocabulary.DirectionPhrase(0, 0, 0, 5));
    Assert.IsNull(IdentityVocabulary.DirectionPhrase(0, 0, 0, 0));

    foreach (var clause in new[] { IdentityVocabulary.DamagedPlayersPhrase, IdentityVocabulary.DamagedNpcsPhrase,
                                   IdentityVocabulary.DamagedPetsPhrase, IdentityVocabulary.DamagedSelfPhrase,
                                   IdentityVocabulary.DamagedPlayersAndNpcsPhrase, IdentityVocabulary.DamagedPlayersAndPetsPhrase,
                                   IdentityVocabulary.DamagedNpcsAndPetsPhrase, IdentityVocabulary.DamagedEveryonePhrase })
    {
      Assert.IsTrue(clause.Length <= IdentityVocabulary.MaxClauseLength,
                    $"\"{clause}\" is {clause.Length} characters, over the hover budget of {IdentityVocabulary.MaxClauseLength}");
    }
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

  /*
   * What a SPELL row's tail is allowed to look like: the victim clause FIRST (rank 75 outranks a database claim's 50 — the capture
   * watching something happen beats a lookup), then exactly ONE line about the spell database, phrased either as this table's own
   * clause or as R21's spell-list proof, depending on which claim the timeline got to record.
   */
  private static void AssertSpellTail(ClassificationReport.Row row, string victimClause)
  {
    var lines = Rows(row);
    Assert.AreEqual(2, lines.Count, $"{row.Name}: expected the victim clause plus one spell-database line, got: {row.OtherEvidence}");
    Assert.AreEqual(victimClause, lines[0], $"{row.Name}: the fact clause should outrank the database claim");
    Assert.IsTrue(lines[1] is IdentityVocabulary.InSpellDbPhrase or IdentityVocabulary.NotInSpellDbPhrase
                  || lines[1] == IdentityVocabulary.ProofText("R21-spelleffect", IdentityKind.Spell),
                  $"{row.Name}: a Spell row's second line names the spell database, got \"{lines[1]}\"");
  }
}
