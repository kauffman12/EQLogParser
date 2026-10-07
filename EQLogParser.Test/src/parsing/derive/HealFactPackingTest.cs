using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser;

/*
 * A heal row is 24 bytes because two of its fields stopped owning a byte each: the label (Heal vs Hot — one bit) rides in
 * the flags byte, and the modifier mask fits in one byte because that is what heal lines actually carry (measured:
 * Twincast|Crit|Lucky = 0x7 across 1.3 M heals on a 663 MB capture, and plain 0 on an EMU capture whose line shape has no
 * modifier text at all). docs → "A row of facts is 24 bytes".
 *
 * That is a packing decided by measurement, so it is guarded by measurement rather than by luck. Each guard below is the
 * failure this design would have: a mask value that does not fit (the healing board's Twincast filter quietly stops
 * firing and every total reads high), "no modifier text" colliding with the real value 0 (an EMU log's every-heal), or a
 * third label appearing (it would be written as Heal and vanish). None of those throw; all of them are counters.
 */
[TestClass]
public class HealFactPackingTest
{
    [TestInitialize]
    public void Reset() => HealFact.ResetPackingCounters();

    [TestMethod]
    public void EveryMaskAHealLineHasEverCarriedReadsBackExactly()
    {
      // The measured reachable set, including 0: an EMU/TSS capture writes a mask of 0 on every heal, which is why the
      // "no text" sentinel could not have been 0.
      foreach (var mask in new short[] { LineModifiersParser.None, 0, 1, 2, 3, 6, 7 })
      {
        var f = Row(mask, LabelTypes.Heal);
        Assert.AreEqual(mask, f.ModMask, $"mask {mask} did not survive the byte");
      }

      Assert.AreEqual(0, HealFact.MasksBeyondByte, "the reachable set must need no lossy path at all");
      Assert.AreEqual(0, HealFact.MaskSentinelCollisions);
    }

    [TestMethod]
    public void TheWholeLowByteSurvivesSoThePackingIsNotLimitedToWhatWasSeen()
    {
      // Anything built from the eight bits a byte can hold (Twincast…Headshot) round trips, not just the three that
      // appeared in a capture: future content is allowed to be ordinary, it is only a bit ABOVE the byte that is a problem.
      for (var mask = 0; mask < HealFact.MaskNone; mask++)
      {
        var f = Row((short)mask, LabelTypes.Heal);
        Assert.AreEqual((short)mask, f.ModMask, $"mask 0x{mask:X2} did not survive the byte");
      }

      Assert.AreEqual(0, HealFact.MasksBeyondByte);
    }

    [TestMethod]
    public void AMaskAboveTheByteIsCountedAndNotSilentlyTruncated()
    {
      // Flurry (1024) on a heal line — melee vocabulary that no capture has ever written there. The value cannot fit; what
      // must not happen is silence, because the symptom is a filter reading false and a board that looks merely busy.
      var f = Row(1024, LabelTypes.Heal);
      Assert.AreEqual(1, HealFact.MasksBeyondByte,
        "a mask the byte cannot hold is counted — that counter is the signal to widen the field back to a short");
      Assert.AreNotEqual((short)1024, f.ModMask, "and it genuinely does not survive: which is why the counter exists");

      var also = Row(LineModifiersParser.None - 1, LabelTypes.Heal);   // -2: only -1 means "no text"
      Assert.AreEqual(2, HealFact.MasksBeyondByte, "a mask of -2 is the same kind of surprise");
      Assert.AreEqual(LineModifiersParser.None, also.ModMask, "and it reads as 'no text' rather than as a bit pattern");
    }

    [TestMethod]
    public void TheSentinelForNoTextIsCountedIfARealMaskWearsIt()
    {
      var f = Row(HealFact.MaskNone, LabelTypes.Heal);   // a literal 0xFF mask: reads as "no text", and says so
      Assert.AreEqual(1, HealFact.MaskSentinelCollisions);
      Assert.AreEqual(LineModifiersParser.None, f.ModMask);
    }

    [TestMethod]
    public void TheLabelRidesInTheFlagsByteWithoutBecomingAFlag()
    {
      var heal = Row(2, LabelTypes.Heal);
      var hot = Row(2, LabelTypes.Hot);

      Assert.AreEqual(LabelTypes.Heal, heal.TypeId);
      Assert.AreEqual(LabelTypes.Hot, hot.TypeId, "the one bit distinguishes the only two words a heal line can be");
      Assert.AreEqual(Labels.Heal, LabelTypes.LabelOf(heal.TypeId));
      Assert.AreEqual(Labels.Hot, LabelTypes.LabelOf(hot.TypeId));

      // The flag reader must not see the label's bit — this is what keeps FactsCarryOnlyWhatTheirOwnLinesSay true: a
      // presentation bit in `Flags` would be an unaccounted-for value on every Hot row.
      Assert.AreEqual((byte)0, heal.Flags);
      Assert.AreEqual((byte)0, hot.Flags, "Hot is a label, not a flag; Flags hides it");

      var petHeal = Row(2, LabelTypes.Hot, HealFact.FlagOwnerInLine);
      Assert.IsTrue(petHeal.OwnerInLine, "the real flag still rides alongside it");
      Assert.AreEqual(HealFact.LineDerivedFlagMask, petHeal.Flags, "and Flags reports exactly the line-derived bits");
      Assert.AreEqual(LabelTypes.Hot, petHeal.TypeId);
    }

    [TestMethod]
    public void ALabelThisStreamCannotHoldIsCountedRatherThanWrittenAsHeal()
    {
      // Three proofs against one silent Heal: a future third heal word must arrive as a counter and a widened field, not
      // as rows that quietly mean something else.
      var wrong = Row(0, LabelTypes.Melee);

      Assert.AreEqual(1, HealFact.UnknownTypeLabels,
        "a label the bit cannot carry is counted — writing it as 'Heal' would erase an event type from every board");
      Assert.AreEqual(LabelTypes.Heal, wrong.TypeId, "the fallback is the ordinary heal word");
    }

    private static HealFact Row(short mask, byte typeId, byte flags = 0)
      => new(1, FixtureTime.BaseL, 2, 3, total: 100, overTotal: 0, typeId, flags, mask, subIdx: HealFact.NoSpell);
}
