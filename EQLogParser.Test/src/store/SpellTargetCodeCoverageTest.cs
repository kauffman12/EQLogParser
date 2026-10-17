namespace EQLogParser;

/*
 * Every Target code spells.txt actually ships is either named by SpellTarget or listed below as deliberately unnamed.
 *
 * WHY. The column decides whether a heal can reach a group, and an unnamed code has always read as "shape unknown" — which
 * silently dropped 46 (`Remote Healing Flood`, `Scorn`, 732 rows), 13 (521 rows) and 10 (364 rows) out of every grouping
 * question this application has ever asked. The codes are a fixed vocabulary from the game's own data; a NEW one appearing in
 * an updated spells.txt is information, and it should arrive as a failing test that names the code rather than as another
 * season of "unknown shape". docs/DesignNotes.md → "Which spell targets could have hit a group".
 */
[TestClass]
public class SpellTargetCodeCoverageTest
{
  /*
   * The unnamed codes, measured over the shipped spells.txt (2026-12). They stay here because naming one is a DATA decision,
   * not a guess: each needs a capture where a spell with that code lands, plus the reading of what it hits. Row counts are in
   * DesignNotes so a future reader can tell which of these is worth measuring next (46 was 732 rows and is now named).
   */
  private static readonly byte[] UnnamedButKnownAbsent =
  [
    0, 9, 10, 11, 13, 15, 16, 17, 18, 21, 32, 33, 34, 37, 43, 44, 47, 48, 50, 51, 52, 53, 54, 55,
  ];

  [TestMethod]
  public void EveryTargetCodeInSpellsTxtIsNamedOrListedAsUnnamed()
  {
    var named = Enum.GetValues<SpellTarget>().Select(v => (byte)v).ToHashSet();
    var unnamed = new HashSet<byte>(UnnamedButKnownAbsent);
    Assert.IsFalse(named.Overlaps(unnamed), "a code is both named by SpellTarget and listed as unnamed; delete the stale listing");

    var seen = TargetCodesInDataFile();
    Assert.IsTrue(seen.Count > 20, $"the data file yielded {seen.Count} target codes — the read is broken, not the vocabulary");

    foreach (var code in seen)
      Assert.IsTrue(named.Contains(code) || unnamed.Contains(code),
                    $"spells.txt ships Target {code}, which no part of SpellTarget names and this test does not list");
  }

  /// <summary>Field 6 of spells.txt (`id^name^group^sub^min1^min2^target^…`), as integers.</summary>
  private static HashSet<byte> TargetCodesInDataFile()
  {
    var path = Path.Combine(AppContext.BaseDirectory, "data", "spells.txt");
    Assert.IsTrue(File.Exists(path), $"no spells.txt beside the test assembly at {path}");

    var codes = new HashSet<byte>();
    foreach (var line in File.ReadLines(path))
    {
      var fields = line.Split('^');
      if (fields.Length < 7 || !int.TryParse(fields[6], out var code) || code is < 0 or > byte.MaxValue) continue;
      codes.Add((byte)code);
    }

    return codes;
  }
}
