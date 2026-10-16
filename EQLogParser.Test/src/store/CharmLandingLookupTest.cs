#nullable enable annotations

namespace EQLogParser;

[TestClass]
public class CharmLandingLookupTest
{
  [TestMethod]
  public void TailBucketsPreserveLongestMatchAndStillValidateTheWholeSuffix()
  {
    string[] suffixes = [" moans.  ", " fiercely moans.", " is charmed!", " becomes a thrall",
      "'s eyes glaze over.", " FIERCELY MOANS."];
    WithSuffixes(suffixes, store =>
    {
      Assert.IsTrue(store.TryGetCharmLanding("Creature FIERCELY MOANS.", out var target, out var message));
      Assert.AreEqual("Creature", target);
      Assert.AreEqual("fiercely moans.", message);
      Assert.IsFalse(store.TryGetCharmLanding("Creature groans.", out _, out _), "same tail key is not a full match");
      Assert.IsFalse(store.TryGetCharmLanding("  fiercely moans.", out _, out _),
        "a matched longest suffix with an empty subject must not fall through to a shorter one");
      Assert.IsTrue(store.TryGetCharmLanding("Creature IS CHARMED!", out target, out _));
      Assert.AreEqual("Creature", target);
      Assert.IsTrue(store.TryGetCharmLanding("Creature becomes a thrall", out target, out _));
      Assert.AreEqual("Creature", target);
      Assert.IsTrue(store.TryGetCharmLanding("Creature's eyes glaze over.", out target, out _));
      Assert.AreEqual("Creature", target);
      AssertOriginalAnswers(store, suffixes);
    });
  }

  [TestMethod]
  [DataRow("!")]
  [DataRow("ed")]
  [DataRow("mad")]
  public void ShortSuffixesDoNotDisappearBehindAFourCharacterKey(string shortSuffix)
  {
    string[] suffixes = [" moans.", shortSuffix, " fiercely moans.", " is charmed!", " becomes a thrall"];
    WithSuffixes(suffixes, store => AssertOriginalAnswers(store, suffixes));
  }

  [TestMethod]
  public void AnEmptySpellTableHasNoLandingMatches()
  {
    WithSuffixes([], store =>
    {
      foreach (var action in new string?[] { null, "", "!", "Creature moans.", "Creature is charmed!" })
        Assert.IsFalse(store.TryGetCharmLanding(action, out _, out _));
    });
  }

  [TestMethod]
  public void ACaseFoldedSurrogatePairAcrossTheKeyBoundaryStillMatches()
  {
    // Deseret case variants differ in the low surrogate, exactly where a four-character tail cuts this suffix.
    WithSuffixes(["\U00010400abc"], store =>
    {
      Assert.IsTrue(store.TryGetCharmLanding("Creature\U00010428ABC", out var target, out _));
      Assert.AreEqual("Creature", target);
    });
  }

  [TestMethod]
  public void AOneCharacterKeyStillHandlesSupplementaryCasePairs()
  {
    WithSuffixes(["!", "\U00010400"], store =>
    {
      Assert.IsTrue(store.TryGetCharmLanding("Creature\U00010428", out var target, out _));
      Assert.AreEqual("Creature", target);
    });
  }

  [TestMethod]
  public void EveryShippedSuffixKeepsTheOriginalMatchersAnswers()
  {
    var store = PipelineHarness.SharedDataStore();
    var suffixes = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "data", "spells.txt"))
      .Select(line => line.Split('^'))
      .Where(fields => fields[15] == "1" && !string.IsNullOrWhiteSpace(fields[19]))
      .Select(fields => fields[19]).ToArray();
    Assert.IsTrue(suffixes.Length > 0);
    AssertOriginalAnswers(store, suffixes);
  }

  private static void AssertOriginalAnswers(EQDataStore store, string[] messages)
  {
    var suffixes = messages.Select(message => message.TrimEnd()).Distinct(StringComparer.OrdinalIgnoreCase)
      .OrderByDescending(suffix => suffix.Length).ToArray();
    foreach (var suffix in suffixes)
    {
      foreach (var action in new[]
      {
        "Creature" + suffix, "Creature" + suffix.ToUpperInvariant(), "Creature" + suffix.ToLowerInvariant(),
        suffix, " " + suffix, "  " + suffix, "Creature" + suffix + "x"
      })
      {
        var expected = OriginalMatch(action, suffixes, out var expectedTarget, out var expectedMessage);
        var actual = store.TryGetCharmLanding(action, out var target, out var message);
        Assert.AreEqual(expected, actual, action);
        Assert.AreEqual(expectedTarget, target, action);
        Assert.AreEqual(expectedMessage, message, action);
      }
    }
  }

  private static bool OriginalMatch(string action, string[] suffixes, out string? target, out string? message)
  {
    target = message = null;
    foreach (var suffix in suffixes)
    {
      if (action.Length <= suffix.Length || !action.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
      target = action[..^suffix.Length].Trim();
      if (target.Length == 0) return false;
      message = suffix.Trim();
      return true;
    }
    return false;
  }

  private static void WithSuffixes(string[] suffixes, Action<EQDataStore> check)
  {
    PipelineHarness.EnsureDataStore();
    var cwd = Environment.CurrentDirectory;
    var folder = Directory.CreateTempSubdirectory("charm-lookup-").FullName;
    try
    {
      Directory.CreateDirectory(Path.Combine(folder, "data"));
      File.WriteAllLines(Path.Combine(folder, "data", "spells.txt"), suffixes.Select((suffix, i) =>
        string.Join("^", (i + 1).ToString(), "Charm " + i, "1", "60", "0", "0", "5", "0", "0", "0", "1",
          "0", "0", "0", "0", "1", "0", "0", "You have been charmed.", suffix, "You are no longer charmed.")));
      Environment.CurrentDirectory = folder;
      check(new EQDataStore());
    }
    finally
    {
      Environment.CurrentDirectory = cwd;
      Directory.Delete(folder, true);
    }
  }
}
