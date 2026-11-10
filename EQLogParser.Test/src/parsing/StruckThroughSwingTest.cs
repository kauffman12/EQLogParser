namespace EQLogParser;

/*
 * One swing can be two lines, and the boards must charge it once.
 *
 * When a defense is struck through EQ prints the defender's outcome sentence AND the blow that got through it:
 *
 *   [Fri Oct 23 21:54:35 2026] A silk moth tries to gore Ryft, but Ryft ripostes! (Strikethrough)
 *   [Fri Oct 23 21:54:35 2026] Ryft begins casting Crippling Snare.
 *   [Fri Oct 23 21:54:35 2026] A silk moth is tormented by Ryft's frost for 111948 points of non-melee damage.
 *   [Fri Oct 23 21:54:35 2026] A silk moth gores Ryft for 132465 points of damage. (Riposte Strikethrough)
 *
 * Both records stay — an outcome word decides, a tag never un-happens it — but they are one swing at one defender,
 * so the number line says so on itself (DamageRecord.AttemptCountedEarlier / ModifiersCountedEarlier) and the
 * counting layer opens no second attempt and scores no second Strikethrough. Before that, a tank's `# Attempts`
 * counted the swing twice and `# Strikethrough` did too, which `MeleeUndefended = MeleeHits - StrikethroughHits`
 * then ate. Reported by an operator who counted one fight by hand; the measurement and the trade:
 * docs/DesignNotes.md → "One swing, two lines: the outcome sentence and the number line".
 *
 * What this file pins:
 *   1. The pair has to match in BOTH names and in DIRECTION. A successful riposte's return blow is the reverse pair
 *      and is a separate event on a different board — it may not claim its own sentence.
 *   2. The window is one second, because that is where every paired line in these captures sits, and a plain hit
 *      from the same mob seconds later is its own swing.
 *   3. A sentence whose number line never arrives still stands on its own (the moth shape: four pierced ripostes in
 *      a row, none of them followed by damage).
 *   4. Subtracting the already-scored tag takes the Riposte bit with the Strikethrough bit, or the number line reads
 *      as IsRiposte() and scores a PROTECTED riposte — plus the mob's damage into TotalRiposte.
 *   5. Absorb scores no modifiers, so it leaves its number line owing all of them.
 *   6. The preview door (DamageLineParser.ParseLine(string)) is not a sighting: it may not touch the carry.
 */
[TestClass]
[DoNotParallelize]
public class StruckThroughSwingTest
{
  private readonly List<DamageRecord> _seen = [];

  [TestInitialize]
  public void Setup()
  {
    Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;
    ConfigUtil.PlayerName = "Kizant";
    RecordsStore.Instance.Clear(false);
    PlayerRegistry.Instance.Clear();
    DamageLineParser.ResetProcessState();
    DamageLineParser.EventsDamageProcessed += OnDamage;
  }

  [TestCleanup]
  public void Cleanup()
  {
    DamageLineParser.EventsDamageProcessed -= OnDamage;
    DamageLineParser.ResetProcessState();
    RecordsStore.Instance.Clear(false);
    PlayerRegistry.Instance.Clear();
    ConfigUtil.PlayerName = "";
  }

  // ---- the shape the operator reported ------------------------------------------------------

  [TestMethod]
  public void APiercedRiposteAndItsNumberLineAreOneSwing()
  {
    Feed("A silk moth tries to gore Ryft, but Ryft ripostes! (Strikethrough)", 100);
    Feed("Ryft begins casting Crippling Snare.", 100);
    Feed("A silk moth is tormented by Ryft's frost for 111948 points of non-melee damage.", 100);
    Feed("A silk moth gores Ryft for 132465 points of damage. (Riposte Strikethrough)", 100);

    var sentence = Single(Labels.Riposte);
    var landed = Single(Labels.Melee);
    Assert.AreEqual(3, _seen.Count, "the cast line makes no record; the sentence, the DoT and the blow do");
    Assert.IsFalse(sentence.AttemptCountedEarlier, "the sentence is the swing's first line");
    Assert.AreEqual((short)0, sentence.ModifiersCountedEarlier);
    Assert.IsTrue(landed.AttemptCountedEarlier, "the number line is the same swing as the sentence three lines back");
    Assert.AreEqual(LineModifiersParser.Strikethrough, landed.ModifiersCountedEarlier);

    // What the tank's row pays for those four log lines.
    var row = RowAt(_seen, "Ryft");
    Assert.AreEqual(1u, row.MeleeAttempts, "one swing, one attempt");
    Assert.AreEqual(1u, row.RiposteHits, "the player DID riposte — the tag says the mob struck through it, not that it never happened");
    Assert.AreEqual(1u, row.StrikethroughHits, "the swing was struck through once, not once per line");
    Assert.AreEqual(1u, row.MeleeHits);
    Assert.AreEqual(132465L, row.Total);
    Assert.AreEqual(0u, row.MeleeUndefended, "a struck-through blow is not an undefended one");
    Assert.AreEqual(0L, row.TotalRiposte, "the mob's damage may not land in the riposte total");
  }

  [TestMethod]
  public void ASecondPiercedRiposteOfTheSamePairAnswersTheNewerSentence()
  {
    Feed("A killer moth tries to gore Shmid, but Shmid ripostes! (Strikethrough)", 100);
    Feed("A killer moth tries to gore Shmid, but Shmid ripostes! (Strikethrough)", 100);
    Feed("A killer moth gores Shmid for 5000 points of damage. (Riposte Strikethrough)", 100);

    Assert.IsTrue(Single(Labels.Melee).AttemptCountedEarlier, "the newer sentence is the one this number line answers");

    // Three lines, two swings: each sentence opened an attempt and only one number line folded into one.
    var row = RowAt(_seen, "Shmid");
    Assert.AreEqual(2u, row.MeleeAttempts);
    Assert.AreEqual(2u, row.RiposteHits);
    Assert.AreEqual(2u, row.StrikethroughHits);
    Assert.AreEqual(1u, row.MeleeHits);
  }

  [TestMethod]
  public void ASentenceWhoseNumberLineNeverArrivesStillScoresItsOwnSwing()
  {
    // The moth shape, straight out of eqlog_Kizant_xegony-strangle.txt: pierced ripostes with no damage line at all.
    Feed("A killer moth tries to gore Shmid, but Shmid ripostes! (Strikethrough)", 100);
    Feed("A killer moth tries to gore Shmid, but misses!", 100);

    var row = RowAt(_seen, "Shmid");
    Assert.AreEqual(2u, row.MeleeAttempts, "two swings, two attempts, no number line anywhere");
    Assert.AreEqual(1u, row.RiposteHits);
    Assert.AreEqual(1u, row.StrikethroughHits);
    Assert.AreEqual(0u, row.MeleeHits);
    Assert.AreEqual(0u, row.MeleeUndefended, "tags with no hits take the subtraction below zero; on unsigned counters that used to read 4,294,967,1xx");
  }

  // ---- what may NOT claim a sentence --------------------------------------------------------

  [TestMethod]
  public void ASuccessfulRipostesReturnBlowIsItsOwnSwingOnItsOwnRow()
  {
    Feed("A silk moth tries to gore Ryft, but Ryft ripostes!", 100);
    Feed("Ryft slashes A silk moth for 40000 points of damage. (Riposte)", 100);

    Assert.AreEqual(Labels.Riposte, Single(Labels.Riposte).Type);
    Assert.IsFalse(Single(Labels.Melee).AttemptCountedEarlier,
      "the return blow is the REVERSE pair — the mob's swing at Ryft and Ryft's blow at the mob are two events the boards count on two rows");

    // The riposte's own damage is still a riposte hit on the row of the one who landed it.
    var row = new PlayerSubStats { Name = "Ryft" };
    StatsUtil.UpdateDamageStats(row, Single(Labels.Melee));
    Assert.AreEqual(1u, row.RiposteHits, "IsRiposte() still reads the bare Riposte bit on an unpaired line");
    Assert.AreEqual(40000L, row.TotalRiposte);
  }

  [TestMethod]
  public void ANumberLineSecondsAfterTheSentenceIsItsOwnSwing()
  {
    Feed("A silk moth tries to gore Ryft, but Ryft ripostes! (Strikethrough)", 100);
    Feed("A silk moth gores Ryft for 9000 points of damage. (Riposte Strikethrough)", 105);

    Assert.IsFalse(Single(Labels.Melee).AttemptCountedEarlier,
      "the window is one second: every paired line in these captures sits inside it, and a later swing of the same mob must not lose its attempt to an older sentence");
  }

  [TestMethod]
  public void AnotherDefenderDoesNotClaimTheSentence()
  {
    Feed("A silk moth tries to gore Ryft, but Ryft ripostes! (Strikethrough)", 100);
    Feed("A silk moth gores Shmid for 9000 points of damage.", 100);

    Assert.IsFalse(Single(Labels.Melee).AttemptCountedEarlier, "the pair has to match in both names");
  }

  [TestMethod]
  public void ThePreviewDoorLeavesTheCarryAlone()
  {
    // ParseLine(string) asks what a line WOULD make without ingesting it, so it may not register a swing that the
    // next ingested line then answers.
    var preview = DamageLineParser.ParseLine("A silk moth tries to gore Ryft, but Ryft ripostes! (Strikethrough)");
    Assert.IsNotNull(preview);
    Assert.AreEqual(Labels.Riposte, preview.Type);

    Feed("A silk moth gores Ryft for 9000 points of damage. (Riposte Strikethrough)", 100);
    Assert.IsFalse(Single(Labels.Melee).AttemptCountedEarlier, "a preview parse is not a sighting");
  }

  [TestMethod]
  public void AnAbsorbSentenceSuppressesTheAttemptButScoresNoModifiers()
  {
    Feed("A failed reclaimer tries to punch YOU, but YOUR magical skin absorbs the blow! (Strikethrough)", 100);
    Feed("A failed reclaimer punches Kizant for 1200 points of damage. (Strikethrough)", 100);

    Assert.AreEqual(2, _seen.Count, "both lines are records: the outcome and the blow");
    var landed = Single(Labels.Melee);
    Assert.IsTrue(landed.AttemptCountedEarlier);
    Assert.AreEqual((short)0, landed.ModifiersCountedEarlier,
      "the absorb arm reads no modifiers, so the number line still owes its Strikethrough");

    var row = RowAt(_seen, "Kizant");
    Assert.AreEqual(1u, row.MeleeAttempts);
    Assert.AreEqual(1u, row.Absorbs);
    Assert.AreEqual(1u, row.StrikethroughHits);
  }

  // ---- the mask fold ----------------------------------------------------------------------

  [TestMethod]
  public void UnspentTakesTheRiposteBitWithTheStrikethroughBitItRidesWith()
  {
    const int strike = LineModifiersParser.Strikethrough;
    const int riposte = LineModifiersParser.Riposte;
    const short rampage = 8;

    Assert.AreEqual((short)48, LineModifiersParser.Unspent(48, 0), "nothing counted earlier leaves the mask alone");
    Assert.AreEqual((short)0, LineModifiersParser.Unspent((short)strike, (short)strike));
    Assert.AreEqual((short)0, LineModifiersParser.Unspent((short)(riposte | strike), (short)strike),
      "leaving a bare Riposte bit would read as IsRiposte(): a pierced riposte scored as a protected one, with the mob's damage in TotalRiposte");
    Assert.AreEqual(rampage, LineModifiersParser.Unspent((short)(riposte | strike | rampage), (short)strike),
      "Rampage belongs to the swing itself and stays");
    Assert.AreEqual(LineModifiersParser.None, LineModifiersParser.Unspent(LineModifiersParser.None, (short)strike),
      "a line with no suffix has nothing to spend");
  }

  // ---- the rates that divide ----------------------------------------------------------------

  [TestMethod]
  public void ARowWithNothingLeftInTheAccuracyDenominatorAsksNothing()
  {
    var row = new PlayerSubStats { Name = "Ryft" };
    StatsUtil.UpdateDamageStats(row, new DamageRecord { Attacker = "A moth", Defender = "Ryft", Type = Labels.Parry, SubType = "Parries", ModifiersMask = LineModifiersParser.None });
    StatsUtil.UpdateDamageStats(row, new DamageRecord { Attacker = "A moth", Defender = "Ryft", Type = Labels.Block, SubType = "Blocks", ModifiersMask = LineModifiersParser.None });
    Assert.AreEqual(2u, row.MeleeAttempts);

    StatsUtil.CalculateRates(row, new PlayerStats(), null);

    Assert.AreEqual(0f, row.MeleeAccRate, "attempts minus defenses is zero here; 0/0 used to print NaN on the board");
    Assert.IsTrue(float.IsFinite(row.MeleeAccRate));
  }

  // ---- helpers ------------------------------------------------------------------------------

  private void Feed(string action, double beginTime) =>
    DamageLineParser.Process(new LineData { Action = action, BeginTime = beginTime });

  private void OnDamage(DamageProcessedEvent e) => _seen.Add(e.Record);

  private DamageRecord Single(string type) => _seen.Single(r => r.Type == type);

  /*
   * One name's tanking row, counted the way TankingStatsBuilder counts it: every record whose blow came AT that
   * name. The frost DoT in the reported shape is damage to the moth, so it belongs to the moth's row, not Ryft's.
   */
  private static PlayerSubStats RowAt(IEnumerable<DamageRecord> records, string name)
  {
    var row = new PlayerSubStats { Name = name };
    foreach (var record in records.Where(r => string.Equals(r.Defender, name, StringComparison.OrdinalIgnoreCase)))
    {
      StatsUtil.UpdateDamageStats(row, record);
    }

    return row;
  }
}
