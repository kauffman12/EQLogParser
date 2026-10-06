using EQLogParser;

namespace EQLogParser;

/*
 * A damage line that names no source, and the "actor" the parser used to put in its place (docs/DesignNotes.md → "Damage the
 * capture never saw, and damage it invented").
 *
 *   `<X> was chilled to the bone for N points of non-melee damage.`
 *
 * The attacker field was filled with `Labels.Rs` — **"Reverse DS", a damage-TYPE word standing where an entity's name belongs** —
 * and it read as one: **69,651 facts and 5,741,957,900 damage** on a row beside the raiders on `eqlog_Kizant_beta-11-30-25.txt`,
 * holding `Player · A Spell · No Caster in Line`, verdicts no rule signed (R21's spell-shape check matched a parser constant). The
 * number is real and stays; the actor is now called what the line says, and `ParserUtil.IsUnattributedName` refuses those
 * placeholders identity — R7 skips them as attacker and treats them as unclassified as defender, because a name that reliably
 * "hits" monsters is exactly the evidence `R7-graph` reads as one of ours.
 *
 * The file also pins the shape that LOOKED like the same bug and wasn't: `<boss> is pierced by <raider>'s thorns for N points of
 * non-melee damage.` (68,680 lines on beta, every live capture has them). Those facts were never lost — the parser's first
 * `is … by …` branch takes everything between "by" and a trailing `'s` as the attacker, so they are credited to the owner under
 * that player's own name. The claim that 9.6 billion points went missing came from a probe that fed `DamageLineParser.ParseLine`
 * a line WITH its `[timestamp]`: that method takes the action alone, and a stamped line yields a record whose "defender" is the
 * stamp — or nothing at all. Pin both, because the wrong conclusion was drawn twice in this repository from a probe nobody ran.
 */
[TestClass]
public class UnattributedDamageTest
{
  [TestInitialize]
  public void Setup() => PlayerRegistry.Instance.Clear();

  [TestCleanup]
  public void Cleanup() => PlayerRegistry.Instance.Clear();

  private static string Stamp(int seconds)
  {
    var t = new DateTime(2025, 11, 23, 18, 43, 49).AddSeconds(seconds);
    return $"[{t:ddd MMM dd HH:mm:ss yyyy}]";
  }

  private static PipelineHarness.DeriveRunResult Census(out EntityTimeline timeline, params (int S, string Line)[] lines)
  {
    var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "unattr-" + Guid.NewGuid().ToString("N")));
    var log = Path.Combine(dir.FullName, "eqlog_Unattributed_Eqgate.txt");
    try
    {
      File.WriteAllLines(log, lines.Select(l => $"{Stamp(l.S)} {l.Line}"));
      var run = PipelineHarness.RunFileDerived(log);
      timeline = new EntityTimeline();
      ClassificationRules.Apply(run.Facts, timeline, run.HealFacts);
      return run;
    }
    finally
    {
      try { Directory.Delete(dir.FullName, true); } catch (IOException) { }
    }
  }

  /*
   * The line with nothing in its attacker slot: the damage is kept, and what stands in for the source says "no source was named"
   * instead of borrowing a damage-type word. ParseLine takes the ACTION - no `[timestamp]` - which is the seam trap above.
   */
  [TestMethod]
  public void ASourceLessLineKeepsItsNumberAndNamesNoActor()
  {
    var r = DamageLineParser.ParseLine("Tallongast, The Egg was chilled to the bone for 1234 points of non-melee damage.");

    Assert.IsNotNull(r);
    Assert.AreEqual(1234u, r.Total);
    Assert.AreEqual("Tallongast, The Egg", r.Defender);
    Assert.AreEqual(Labels.Unattributed, r.Attacker);

    // old spelling and new are both refused; a real name never is
    Assert.IsTrue(ParserUtil.IsUnattributedName(Labels.Rs), "\"Reverse DS\" is still in old captures and old memory");
    Assert.IsTrue(ParserUtil.IsUnattributedName(Labels.Unattributed));
    Assert.IsTrue(ParserUtil.IsUnattributedName(Labels.Unk));
    Assert.IsFalse(ParserUtil.IsUnattributedName("Piemastaj"));
  }

  /*
   * The guard's real job. Three mob instances over more than a minute is precisely what `R7-graph` claims a person from, and the
   * placeholder used to walk into that. It stays unplaced - while every point of its damage is still counted, because the number
   * was never the thing that was wrong.
   */
  [TestMethod]
  public void APlaceholderNeverEarnsASideFromTheMobsItStoodNextTo()
  {
    var run = Census(out var timeline,
                     (0, "Tallongast, The Egg was chilled to the bone for 1234 points of non-melee damage."),
                     (20, "A gnoll was chilled to the bone for 1234 points of non-melee damage."),
                     (45, "A kobold was chilled to the bone for 1234 points of non-melee damage."),
                     (80, "A skeleton was chilled to the bone for 1234 points of non-melee damage."));

    Assert.AreEqual(IdentityKind.Unknown, timeline.IdentityAt(Labels.Unattributed, double.PositiveInfinity),
                    "the placeholder must not read as one of ours just because it reliably hits monsters");

    var facts = run.Facts.Facts.ToArray().Where(f => run.Facts.NameOf(f.AtkIdx) == Labels.Unattributed).ToList();
    Assert.HasCount(4, facts);
    Assert.AreEqual(4936d, facts.Sum(f => (double)f.Total), "the damage is real and stays on the board");
  }

  /*
   * The cousin that was never broken: `is pierced by <raider>'s thorns for N points of non-melee damage.` — 68,680 lines on this
   * capture, present in all eleven live ones. Credited to the player whose name the possessive carries, under that player's own
   * name (not as an owned pet row), by the first `is … by …` branch. Pinned so the next reader does not "restore" nine billion
   * points that were already there.
   */
  [TestMethod]
  public void AThornsLineIsAlreadyCreditedToTheOwnerItsPossessiveNames()
  {
    var run = Census(out _,
                     (0, "Waxwork Abolishion is pierced by Piemastaj's thorns for 154597 points of non-melee damage."),
                     (3, "Waxwork Abolishion is pierced by Piemastaj's thorns for 148830 points of non-melee damage."));

    var facts = run.Facts.Facts.ToArray().Where(f => run.Facts.NameOf(f.DefIdx) == "Waxwork Abolishion").ToList();
    Assert.HasCount(2, facts, "both lines are captured");
    Assert.IsTrue(facts.All(f => run.Facts.NameOf(f.AtkIdx) == "Piemastaj"),
                  "the owner's own name, not a thorns row: " + string.Join(", ", facts.Select(f => run.Facts.NameOf(f.AtkIdx))));
    Assert.AreEqual(303427d, facts.Sum(f => (double)f.Total));
  }

  /*
   * And the probe that told me otherwise is pinned too: ParseLine answers on the ACTION alone. Give it a stamped line and the
   * "defender" swallows the timestamp — which is how a whole capture got misread as losing damage it had all along.
   */
  [TestMethod]
  public void ParseLineTakesTheActionWithoutItsTimestamp()
  {
    const string action = "Tallongast, The Egg was chilled to the bone for 1234 points of non-melee damage.";

    Assert.AreEqual("Tallongast, The Egg", DamageLineParser.ParseLine(action)!.Defender);
    Assert.AreNotEqual("Tallongast, The Egg",
                       DamageLineParser.ParseLine($"{Stamp(0)} {action}")?.Defender,
                       "a stamped line is garbage at this seam: the stamp becomes part of the name");
  }
}
