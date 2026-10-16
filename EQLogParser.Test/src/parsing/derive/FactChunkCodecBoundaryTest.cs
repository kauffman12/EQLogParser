namespace EQLogParser;

[TestClass]
public class FactChunkCodecBoundaryTest
{
  [TestMethod]
  [DataRow(1)]
  [DataRow(2)]
  [DataRow(DamageFactTable.FactChunkSize)]
  public void DamageCodecCarriesExtremeVarintsWithinItsScratchBound(int count)
  {
    var rows = new DamageFact[count];
    for (var i = 0; i < count; i++)
    {
      var (seq, time, first, second) = Fields(i);
      rows[i] = new DamageFact(seq, time, first, second, uint.MaxValue, LabelTypes.Dd,
        DamageFact.FlagAttackerIsSpell | DamageFact.FlagOwnerInLine, -1, DamageFactTable.NoSubtype);
    }

    var blob = DamageFactChunkCodec.Encode(rows, count);
    var decoded = DamageFactChunkCodec.Decode(blob, count);
    Assert.HasCount(count, decoded);
    for (var i = 0; i < count; i++) Assert.AreEqual(rows[i], decoded[i], $"damage row {i}");
  }

  [TestMethod]
  [DataRow(1)]
  [DataRow(2)]
  [DataRow(HealFactTable.HealChunkSize)]
  public void HealCodecCarriesExtremeVarintsWithinItsScratchBound(int count)
  {
    var rows = new HealFact[count];
    for (var i = 0; i < count; i++)
    {
      var (seq, time, first, second) = Fields(i);
      rows[i] = new HealFact(seq, time, first, second, uint.MaxValue, uint.MaxValue, LabelTypes.Hot,
        HealFact.FlagOwnerInLine, -1, HealFact.NoSpell);
    }

    var blob = HealFactChunkCodec.Encode(rows, count);
    var decoded = HealFactChunkCodec.Decode(blob, count);
    Assert.HasCount(count, decoded);
    for (var i = 0; i < count; i++) Assert.AreEqual(rows[i], decoded[i], $"heal row {i}");
  }

  private static (int Seq, long Time, int First, int Second) Fields(int row)
  {
    // Consecutive sequence values span more than int.MaxValue; epoch/sentinel jumps and full-width ids
    // keep this a codec-boundary fixture rather than an assertion about typical capture compression.
    var seq = (row & 1) == 0 ? int.MinValue : int.MaxValue;
    var time = (row % 3) switch
    {
      0 => 0L,
      1 => FactTime.EpochSeconds + int.MaxValue,
      _ => FactTime.EpochSeconds + int.MinValue + 1
    };
    var first = row % 7 == 0 ? -1 : int.MinValue;
    return (seq, time, first, int.MaxValue);
  }
}
