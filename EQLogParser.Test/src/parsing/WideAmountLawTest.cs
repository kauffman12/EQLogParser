using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable enable annotations

namespace EQLogParser
{
  /*
   * A wide amount through the stations after the parse: record, row, blob, board. The domain itself and its two parse helpers are
   * AmountDomainTest; what is held here is that a number which survived the parse survives every station after it (docs/DesignNotes.md →
   * "Amount widths measured across 44 million hits").
   *
   * THE DOMAIN IS THE SAME AT EVERY STATION, because a narrowed copy is worse than a refused line: a 48-bit amount truncated to its low 32 bits
   * reads as a plausible smaller hit, and every total above it silently disagrees with the log. So the row refuses what it cannot carry (its
   * constructor is the door behind the parser), the blob carries both halves or says the chunk needs neither, and the sums stay signed.
   *
   * ONE BEHAVIOUR CHANGED ON THE WAY IN, deliberately: a damage or heal line whose amount does not parse used to produce uint.MaxValue, so a
   * corrupt token became a 4.29 billion hit on the board. Now the line makes no record. Refusing a bad line is worth more than storing a
   * confident enormous number that no player swung.
   */
  [DoNotParallelize]
  [TestClass]
  public sealed class WideAmountLawTest
  {
    private const long Ceiling = TextUtils.MaxAmount;              // 281474976710655 — fifteen digits
    private const long OnePast = TextUtils.MaxAmount + 1L;
    private const long Wide = 1_099_511_627_775L;                  // 2^40 - 1, comfortably past uint.MaxValue
    private const long T = 63_800_000_000L;

    [TestInitialize]
    public void Setup()
    {
      ConfigUtil.PlayerName = "TestPlayer";
      Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;   // data/spells.txt
      PipelineHarness.SharedDataStore();
      HealTap.Clear();
      PlayerRegistry.Instance.Clear();
    }

    [TestCleanup]
    public void Cleanup()
    {
      HealTap.Clear();
      PlayerRegistry.Instance.Clear();
    }

    #region line to record

    [TestMethod]
    public void AWideDamageLineBecomesARecordHoldingTheSameNumber()
    {
      foreach (var amount in new[] { "4294967296", Wide.ToString(), Ceiling.ToString() })
      {
        var record = DamageLineParser.ParseLine($"Kilsef crushes an abyssal terror for {amount} points of damage.");
        Assert.IsNotNull(record, $"a melee line carrying {amount} is a hit like any other");
        Assert.AreEqual(long.Parse(amount), record.Total, "the record holds the printed number, not a narrowed copy");
      }
    }

    [TestMethod]
    public void ADamageLineWhoseAmountIsNotAnAmountMakesNoRecordAtAll()
    {
      // This is the behaviour change that matters: before, an unparseable amount became uint.MaxValue — a 4.29 billion hit.
      var junk = DamageLineParser.ParseLine("Kilsef crushes an abyssal terror for 12a34 points of damage.");
      Assert.IsNull(junk, "junk is no record");

      var tooWide = DamageLineParser.ParseLine($"Kilsef crushes an abyssal terror for {OnePast} points of damage.");
      Assert.IsNull(tooWide, "an amount past the domain is refused rather than carried narrow");

      var stats = new PlayerSubStats { Name = "Kilsef" };
      if (junk is not null) StatsUtil.UpdateDamageStats(stats, junk);
      if (tooWide is not null) StatsUtil.UpdateDamageStats(stats, tooWide);
      Assert.AreEqual(0L, stats.Total, "and nothing reached the board either");
    }

    [TestMethod]
    public void AWideHealCarriesBothItsAmountsAndTheSignRulingSurvives()
    {
      Process($"Reisil healed Kilsa for {Wide} ({Ceiling}) hit points by Greater Renewal.");
      var landed = HealTap.All().Single().Item2;
      Assert.AreEqual(Wide, landed.Total, "the landed amount kept all its bits");
      Assert.AreEqual(Ceiling, landed.OverTotal, "and so did the parenthesised one");

      // Mixed lanes: a narrow landing with a wide ask is the common shape of an over-topped heal.
      HealTap.Clear();
      Process("Reisil healed Kilsa for 4096 (5000000000) hit points by Greater Renewal.");
      var mixed = HealTap.All().Single().Item2;
      Assert.AreEqual(4096L, mixed.Total);
      Assert.AreEqual(5_000_000_000L, mixed.OverTotal, "a paren past 32 bits is not the landed amount's problem");

      // The HOT sign ruling (measured at 84 lines in one capture) is untouched by the wider domain.
      HealTap.Clear();
      Process($"Tilwedarx healed Foob over time for -{Wide} ({Ceiling}) hit points by Abundant Healing LXIX.");
      var hot = HealTap.All().Single().Item2;
      Assert.AreEqual(Labels.Hot, hot.Type);
      Assert.AreEqual(Wide, hot.Total, "the magnitude of a negative HOT tick is what landed");
      Assert.AreEqual(Ceiling, hot.OverTotal);

      // And a heal line whose amount is junk still makes nothing.
      HealTap.Clear();
      Process("Reisil healed Kilsa for 99999999999999999999 hit points by Greater Renewal.");
      Assert.AreEqual(0, HealTap.All().Count, "a 20-digit heal is refused, not clamped");
    }

    [TestMethod]
    public void TheBoardAddsWideAmountsWithoutNarrowingAnything()
    {
      var stats = new PlayerSubStats { Name = "Kilsef" };
      foreach (var amount in new[] { 5_000_000_000L, Wide, Ceiling })
      {
        StatsUtil.UpdateDamageStats(stats, new DamageRecord { Attacker = "Kilsef", Defender = "Npc", Type = Labels.Melee, SubType = "hits", Total = amount });
      }

      Assert.AreEqual(5_000_000_000L + Wide + Ceiling, stats.Total, "a sum of three wide hits is the arithmetic sum");
      Assert.AreEqual(Ceiling, stats.Max, "the biggest hit stays the biggest");
      Assert.AreEqual(5_000_000_000L, stats.Min, "and the smallest stays the smallest, two digits narrower");

      // Healing, where the overheal column is the one that could go negative.
      var heals = new PlayerSubStats { Name = "Reisil" };
      StatsUtil.UpdateHealStats(heals, new HealRecord { Healer = "Reisil", Healed = "Kilsa", Type = Labels.Heal, SubType = "Renewal", Total = 0L, OverTotal = Ceiling });
      Assert.AreEqual(Ceiling, heals.Extra, "asked for everything, landed nothing: the overheal is the ceiling");
      Assert.IsTrue(heals.Extra >= 0L, "and never a wrapped negative");

      StatsUtil.UpdateHealStats(heals, new HealRecord { Healer = "Reisil", Healed = "Kilsa", Type = Labels.Heal, SubType = "Renewal", Total = Ceiling, OverTotal = 0L });
      Assert.AreEqual(Ceiling, heals.Extra, "a heal with no paren claims nothing further was asked");
    }

    #endregion

    #region row and blob

    [TestMethod]
    public void TheFactRowsRefuseAnAmountTheyCannotCarry()
    {
      foreach (var bad in new[] { -1L, OnePast, long.MaxValue })
      {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
          () => new DamageFact(1, T, 1, 2, bad, LabelTypes.Melee, 0, 0, 0), $"a damage row will not hold {bad}");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
          () => new HealFact(1, T, 1, 2, bad, 0L, LabelTypes.Heal, 0, 0, HealFact.NoSpell), $"a heal row will not hold {bad} landed");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
          () => new HealFact(1, T, 1, 2, 0L, bad, LabelTypes.Heal, 0, 0, HealFact.NoSpell), $"a heal row will not hold {bad} asked-for");
      }

      // Both ceilings are storable, and reading them back gives the same number, not a sign-flipped one.
      Assert.AreEqual(Ceiling, new DamageFact(1, T, 1, 2, Ceiling, LabelTypes.Melee, 0, 0, 0).Total);
      var top = new HealFact(1, T, 1, 2, Ceiling, Ceiling, LabelTypes.Heal, 0, 0, HealFact.NoSpell);
      Assert.AreEqual(Ceiling, top.Total);
      Assert.AreEqual(Ceiling, top.OverTotal);

      // The narrow path is untouched: an ordinary amount reads back as it always did.
      Assert.AreEqual(12345L, new DamageFact(1, T, 1, 2, 12345L, LabelTypes.Melee, 0, 0, 0).Total);
    }

    [TestMethod]
    public void ARowAtEitherExtremeReadsItsOverhealWithoutWrapping()
    {
      var askedEverything = new HealFact(1, T, 1, 2, 0L, Ceiling, LabelTypes.Heal, 0, 0, HealFact.NoSpell);
      Assert.AreEqual(Ceiling, askedEverything.AskedFor);
      Assert.AreEqual(Ceiling, askedEverything.Overheal, "the ceiling minus zero is the ceiling, not a negative");

      var landedEverything = new HealFact(2, T, 1, 2, Ceiling, 0L, LabelTypes.Heal, 0, 0, HealFact.NoSpell);
      Assert.AreEqual(Ceiling, landedEverything.AskedFor, "no paren means it asked for what it landed");
      Assert.AreEqual(0L, landedEverything.Overheal, "and it owed nothing");

      // The quirk the row keeps on purpose: a 0 paren is "the line never said", so AskedFor follows Total.
      var nothingAtAll = new HealFact(3, T, 1, 2, 0L, 0L, LabelTypes.Heal, 0, 0, HealFact.NoSpell);
      Assert.AreEqual(0L, nothingAtAll.AskedFor);
      Assert.AreEqual(0L, nothingAtAll.Overheal);
    }

    [TestMethod]
    public void WideRowsComeHomeFromAColdChunkFirstMiddleAndLast()
    {
      var rows = new DamageFact[8];
      for (var i = 0; i < rows.Length; i++) rows[i] = DamageRow(i, 4096 + i);

      // One wide row in each structural position: the first row of a chunk, the last, and one between two narrow ones.
      rows[0] = DamageRow(0, Ceiling);
      rows[3] = DamageRow(3, Wide);
      rows[rows.Length - 1] = DamageRow(rows.Length - 1, uint.MaxValue + 1L);

      var back = DamageFactChunkCodec.Decode(DamageFactChunkCodec.Encode(rows, rows.Length), rows.Length);
      for (var i = 0; i < rows.Length; i++)
      {
        Assert.AreEqual(rows[i].Total, back[i].Total, $"row {i} came home with a different amount");
        Assert.AreEqual(rows[i].Seq, back[i].Seq, $"row {i} kept its sequence");
      }

      var heals = new HealFact[8];
      for (var i = 0; i < heals.Length; i++) heals[i] = HealRow(i, 900 + i, 100);
      heals[0] = HealRow(0, Ceiling, Ceiling);
      heals[4] = HealRow(4, 5_000L, Wide);          // narrow landed, wide ask: the lanes are independent
      heals[heals.Length - 1] = HealRow(heals.Length - 1, Wide, 0L);   // wide landed, no ask at all

      var healedBack = HealFactChunkCodec.Decode(HealFactChunkCodec.Encode(heals, heals.Length), heals.Length);
      for (var i = 0; i < heals.Length; i++)
      {
        Assert.AreEqual(heals[i].Total, healedBack[i].Total, $"heal {i} landed a different number");
        Assert.AreEqual(heals[i].OverTotal, healedBack[i].OverTotal, $"heal {i}'s paren came home different");
      }
    }

    [TestMethod]
    public void AChunkThatPaidForNoHighPlaneStillDecodesAndAWrongRowCountRefuses()
    {
      var narrow = new DamageFact[6];
      for (var i = 0; i < narrow.Length; i++) narrow[i] = DamageRow(i, 1000 + i);

      // Nothing above 32 bits in this chunk: the presence byte stays clear and the rows still round-trip.
      var blob = DamageFactChunkCodec.Encode(narrow, narrow.Length);
      var back = DamageFactChunkCodec.Decode(blob, narrow.Length);
      for (var i = 0; i < narrow.Length; i++) Assert.AreEqual(narrow[i].Total, back[i].Total);

      // The guard that replaced reading capacity as data: a count that disagrees with the blob's length cannot produce rows.
      // (This used to read whatever bytes followed and hand back invented ids and amounts.)
      Assert.ThrowsExactly<System.IO.InvalidDataException>(() => DamageFactChunkCodec.Decode(blob, narrow.Length - 1));
      Assert.ThrowsExactly<System.IO.InvalidDataException>(() => DamageFactChunkCodec.Decode(blob, narrow.Length + 3));

      var heals = new HealFact[6];
      for (var i = 0; i < heals.Length; i++) heals[i] = HealRow(i, 500 + i, 25);
      var healBlob = HealFactChunkCodec.Encode(heals, heals.Length);
      Assert.ThrowsExactly<System.IO.InvalidDataException>(() => HealFactChunkCodec.Decode(healBlob, heals.Length + 1));
    }

    [TestMethod]
    public void AWideRowStoredInATableReadsBackThroughTheOrdinaryDoor()
    {
      var facts = new DamageFactTable(0);
      facts.AddFact(DamageRow(0, Ceiling));
      Assert.AreEqual(Ceiling, facts.At(0).Total, "the table hands back the number, not its low half");

      var heals = new HealFactTable(facts, 0);
      heals.AddHeal(HealRow(0, Wide, Ceiling));
      Assert.AreEqual(Wide, heals.Heals[0].Total);
      Assert.AreEqual(Ceiling, heals.Heals[0].OverTotal);
    }

    #endregion

    private static DamageFact DamageRow(int i, long total) => new(i, T + (i / 3), 1 + (i % 7), 2 + (i % 11), total,
      LabelTypes.Melee, 0, 0, DamageFactTable.NoSubtype);

    private static HealFact HealRow(int i, long total, long overTotal) => new(i, T + (i / 3), 1 + (i % 5), 2 + (i % 9),
      total, overTotal, LabelTypes.Heal, 0, 0, HealFact.NoSpell);

    private static void Process(string action) =>
      HealingLineParser.Process(new LineData { Action = action, BeginTime = (double)T, Split = action.Split(' ') });

    /*
     * The unit tests above build rows by hand; this one walks in through the reader with a synthesized live-format capture, because a wide number has to
     * survive ingest (LogProcessor's tap, the spell lookup, the record shaping) and not only storage. Written the way the game prints it, including the
     * negative HOT tick and its parenthesised ask. docs/DesignNotes.md → "Amount widths measured across 44 million hits".
     */
    [TestMethod]
    public void ACapturePrintedWideReachesTheFactTablesWide()
    {
      var path = Path.Combine(Path.GetTempPath(), "wide-amount-capture.txt");
      try
      {
        File.WriteAllLines(path, new[]
        {
          "[Sun Oct 11 18:45:18 2026] Pickter slashes Scorn for 5309597698 points of damage.",
          "[Sun Oct 11 18:45:19 2026] Beorun healed Stormclaw over time for -4294967296 (8589934592) hit points by Paragon of Spirit XLIV.",
          "[Sun Oct 11 18:45:20 2026] Pickter slashes Scorn for 281474976710656 points of damage.",
        });

        var run = PipelineHarness.RunFileDerived(path);

        Assert.AreEqual(1, run.Facts.FactCount, "the swing past the domain made no row, as it should not; the wide one did");
        foreach (var slice in run.Facts.FactsIn(0, run.Facts.FactCount))
          foreach (ref readonly var row in slice)
            Assert.AreEqual(5_309_597_698L, row.Total, "the hit that broke uint arrives intact");

        Assert.AreEqual(1, run.HealFacts.HealCount);
        foreach (var slice in run.HealFacts.HealsIn(0, run.HealFacts.HealCount))
          foreach (ref readonly var row in slice)
          {
            Assert.AreEqual(4_294_967_296L, row.Total, "the sign-stripped HOT tick, wide");
            Assert.AreEqual(8_589_934_592L, row.OverTotal, "the ask, wide and verbatim");
            // The row's Overheal is the unused part of the ask: 8,589,934,592 wanted, 4,294,967,296 landed.
            Assert.AreEqual(4_294_967_296L, row.Overheal, "the difference of two wide amounts, wide and unwrapped");
          }
      }
      finally
      {
        File.Delete(path);
      }
    }
  }
}
