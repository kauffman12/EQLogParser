using EQLogParser;

namespace EQLogParser;

/*
 * The ordinal door, pinned before anything behind it moves.
 *
 * Every consumer that wants one fact speaks an ordinal: FightFactIndex stores ordinals, the board's blocks are ordinal runs, and a
 * materialized selection is a list of them. So "At(ordinal) reads the same row the flat array does" is the property that lets storage
 * change shape - chunks with shift/mask addressing, columnar cold rows, compression - without moving one ordinal. These assertions exist
 * now, while the buffer IS contiguous, so the equivalence is proven against the layout the rest of the code was written against rather
 * than being asserted against a new implementation's own behaviour.
 */
[TestClass]
public sealed class FactAddressTest
{
  [TestInitialize]
  public void Setup() => PlayerRegistry.Instance.Clear();

  [TestMethod]
  public void EveryOrdinalReadsTheRowTheSpanShows()
  {
    var facts = new DamageFactTable(4);
    for (var i = 0; i < 500; i++)
    {
      facts.AddFact(new DamageFact(i, FactTime.FromEpochSeconds(1_000_000 + i), facts.InternName($"Fighter {i % 7}"),
        facts.InternName($"A crypt broker"), (uint)(i * 3), LabelTypes.Melee, 0, 0, DamageFactTable.NoSubtype));
    }

    var span = facts.Facts;
    Assert.AreEqual(500, span.Length);
    for (var i = 0; i < span.Length; i++)
    {
      // Whole-row equality: the door may not hand back a row that differs in ANY field, and naming every field here would
      // rot the day one is added, so the struct's own equality is the assertion.
      Assert.IsTrue(span[i].Equals(facts.At(i)), $"ordinal {i} reads a different row through the door than through the span");
    }
  }

  [TestMethod]
  public void AnOrdinalOutsideTheRowsReadsBackZeroRatherThanThrowing()
  {
    var facts = new DamageFactTable(4);
    facts.AddFact(new DamageFact(1, FactTime.FromEpochSeconds(1_000_000), facts.InternName("Bithika"),
      facts.InternName("Fenkel"), 77, LabelTypes.Melee, 0, 0, DamageFactTable.NoSubtype));

    Assert.AreEqual(77u, facts.At(0).Total);
    Assert.AreEqual(0, facts.At(1).Seq, "past the end is a zero row, not an exception on the walk");
    Assert.AreEqual(0, facts.At(-1).Seq, "and a negative ordinal is the same answer, not a wrap into the buffer");
  }

  [TestMethod]
  public void HealOrdinalsReadTheSameRows()
  {
    var facts = new DamageFactTable(4);
    var heals = new HealFactTable(facts);   // one name pool for both streams; the heal table reaches through it
    for (var i = 0; i < 60; i++)
    {
      heals.AddHeal(new HealFact(i, FactTime.FromEpochSeconds(1_000_000 + i), facts.InternName($"Healer {i % 3}"),
        facts.InternName("Bithika"), (uint)(100 + i), 0u, LabelTypes.Hot, 0, 0, ushort.MaxValue));
    }

    var span = heals.Heals;
    Assert.AreEqual(60, span.Length);
    for (var i = 0; i < span.Length; i++)
    {
      Assert.IsTrue(span[i].Equals(heals.At(i)), $"heal ordinal {i} reads a different row through the door");
    }

    Assert.AreEqual(0, heals.At(-1).Seq);
    Assert.AreEqual(0, heals.At(60).Seq);
  }
}
