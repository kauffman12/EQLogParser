using EQLogParser;

namespace EQLogParser;

/*
 * R24 - a spell whose own target slot is a pet proves that the name it HIT is a pet, and what that proof is worth.
 *
 * The fixture line is verbatim from eqlog_Incogitable_xegony.txt:
 *
 *   [Sat Jan 04 16:25:05 2025] Wanabe hit Fred for 176000 points of unresistable damage by Elemental Conversion VI.
 *
 * Fred is a shaman's wolf with a custom name. The log never writes `Wanabe`s pet`, so R5's possessive sweep cannot
 * reach him; the client's target frame prints NPC for him the same way it prints NPC for a skeleton. What settles it
 * is the spell: Elemental Conversion's row in the spell data carries Target = Pet2 (38), as do Valiant/Relentless
 * Symbiosis and Warder's Gift - the game itself says what that damage can land on. Census (two captures, 11 M lines):
 * 75 such damage facts, 7.4 M damage, every defender a custom summon name, ZERO mobs and ZERO names any Targeted
 * (Player) frame had given (docs/DesignNotes.md → "What NPC means here").
 *
 * The two things this buys, in the order they matter:
 *
 *   1. NO CREDIT for our own side's damage on our own pet. A meter that pays out a pet burn pads a raider with a
 *      mechanic the raid chose to run; raider-on-raider and raider-on-mercenary were already dropped as friendly
 *      fire, and this closes the last raid-side defender that used to earn. (OurPetTest holds the same policy for a
 *      pet proven by heal breadth instead of by a spell.)
 *   2. The Type cell reads PET, because NPC in this application means non-pet, non-player, non-mercenary, and an
 *      uncontradicted positive claim removes the ground under that residual word. A PLAYER or MERCENARY verdict still
 *      outranks the sighting (a person is not a summon), as does an operator's own claim.
 */
[TestClass]
public class PetSlotSpellTest
{
  private const string Owner = "Wanabe";
  private const string Pet = "Fred";
  private const string Mob = "a bone walker";

  private static DerivedFight? Row(List<DerivedFight> rows, string name)
    => rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

  [TestMethod]
  public void APetOnlySpellNamesItsDefenderAsAPetEvenAgainstTargetedNpc()
  {
    var run = RunDerive(Lines().ToArray());
    var timeline = new EntityTimeline();
    ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);

    // Positive claim beats the residual: the frame's "not a player" is not "not a pet".
    Assert.AreEqual(IdentityKind.Pet, timeline.IdentityWithSource(Pet, out var source));
    Assert.AreEqual("R24-petslot", source, "the target frame's NPC verdict still holds the name");

    // And the word the operator sees is not a code.
    Assert.AreNotEqual("R24-petslot", IdentityVocabulary.WhyWord("R24-petslot"));
  }

  [TestMethod]
  public void TheBurnEarnsNoDamageAndOpensNoRow()
  {
    var run = RunDerive(Lines().ToArray());
    var timeline = new EntityTimeline();
    ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);
    var rows = FightProjection.Build(run.Facts, timeline);

    // No encounter against a verb: the burn keys no row for the pet...
    Assert.IsNull(Row(rows, Pet), "a pet burn opened a fight row");

    // ...and none of it reached the raid's damage on the mob it was actually fighting.
    var fought = Row(rows, "A bone walker");
    Assert.IsNotNull(fought, "the real fight lost its row");
    Assert.AreEqual(900, fought.DamageToOwner, "pet-burn damage is on the board: " + fought.DamageToOwner);

    // The fact is captured, merely uncredited: dropping it from the CAPTURE would be a different (wrong) decision.
    var burns = 0;
    foreach (var f in run.Facts.Facts) if (f.Total == 176000) burns++;
    Assert.AreEqual(1, burns, "the burn fact left the capture instead of going uncredited");
  }

  /*
   * The guard on the other side of the recognizer: names collide, and a name some pet-only spell happened to land on
   * is not a summon if this capture watched that same name get a player's target frame. Losing this would be worse
   * than never claiming pets - it would move a person's output behind a pet's name.
   */
  [TestMethod]
  public void ANameTheFrameCalledAPlayerKeepsBeingAPlayer()
  {
    var lines = Lines(npcFrame: false);
    lines.Insert(0, $"[{Timestamp(18, 50, 0)}] Targeted (Player): {Pet}");
    var run = RunDerive(lines.ToArray());

    var timeline = new EntityTimeline();
    ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);

    Assert.AreEqual(IdentityKind.Player, timeline.IdentityWithSource(Pet, out var source));
    Assert.AreNotEqual("R24-petslot", source, "a pet-slot sighting relabelled a raider");
  }

  /*
   * The recognizer reads the spell data, and it reads it in both directions: what can only hit a pet answers true,
   * and ordinary raid damage answers false. Without the false half this file would pass on a recognizer that matched
   * everything - which is exactly how a rule eats a raid night.
   */
  [TestMethod]
  public void TheRecognizerReadsTheSpellDataBothWays()
  {
    // The three families the two census captures actually produced.
    Assert.IsTrue(ClassificationRules.IsPetSlotSpell("Elemental Conversion VI"), "EC VI is not a pet-slot spell");
    Assert.IsTrue(ClassificationRules.IsPetSlotSpell("Valiant Symbiosis"), "Valiant Symbiosis is not a pet-slot spell");
    Assert.IsTrue(ClassificationRules.IsPetSlotSpell("Warder's Gift XV"), "Warder's Gift is not a pet-slot spell");

    // Ordinary single-target damage, a name the data has never heard of, and nothing at all.
    Assert.IsFalse(ClassificationRules.IsPetSlotSpell("Fire Strike"), "a raid spell aimed at pets");
    Assert.IsFalse(ClassificationRules.IsPetSlotSpell("Ninety-Nine Beserks Of The Blood"),
                   "an unknown name claimed a pet");
    Assert.IsFalse(ClassificationRules.IsPetSlotSpell(null));
    Assert.IsFalse(ClassificationRules.IsPetSlotSpell(string.Empty));
  }

  // ---- helpers ----

  private static List<string> Lines(bool npcFrame = true)
  {
    var lines = new List<string>();
    if (npcFrame) lines.Add($"[{Timestamp(18, 52, 0)}] Targeted (NPC): {Pet}");
    lines.Add($"[{Timestamp(19, 0, 0)}] You hit {Mob} for 900 points of damage.");
    lines.Add($"[{Timestamp(19, 0, 5)}] {Mob} hits You for 300 points of damage.");

    // The fixture line, verbatim in shape from eqlog_Incogitable_xegony.txt.
    lines.Add($"[{Timestamp(19, 0, 8)}] {Owner} hit {Pet} for 176000 points of unresistable damage by Elemental Conversion VI.");
    return lines;
  }

  // The log's own stamp shape; the harness parses the bracketed form.
  private static string Timestamp(int hour, int minute, int secondOffset)
  {
    var t = new DateTime(2026, 5, 4, hour, 0, 0, DateTimeKind.Utc).AddMinutes(minute).AddSeconds(secondOffset);
    return $"Mon May {t.Day:00} {t.Hour:00}:{t.Minute:00}:{t.Second:00} 2026";
  }

  private static PipelineHarness.DeriveRunResult RunDerive(params string[] lines)
  {
    var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "derive-petslot-" + Guid.NewGuid().ToString("N")));
    var log = Path.Combine(dir.FullName, "eqlog_Probeone_Eqgate.txt");   // filename seeds ConfigUtil.PlayerName
    try
    {
      File.WriteAllLines(log, lines);
      return PipelineHarness.RunFileDerived(log);
    }
    finally
    {
      try { Directory.Delete(dir.FullName, true); } catch (IOException) { }
    }
  }
}
