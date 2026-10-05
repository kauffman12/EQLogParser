using System.Text.RegularExpressions;

namespace EQLogParser;

/*
 * The migration's definition of done, written as an assertion instead of a promise.
 *
 * Every identity claim ("this name is a player / a pet") is supposed to arrive through the rule book in
 * `EQLogParser.Core/src/parsing/derive`, so that it carries provenance, can be outweighed by better evidence, and is remembered
 * in `identity-priors.txt`. The store this replaces — `PlayerRegistry.AddVerifiedPlayer` / `AddVerifiedPet` — has none of that:
 * a parser that calls it directly asserts an identity with no rule code, no strength, and no path to being outvoted. Most of
 * those calls also have an `EventsEvidence` twin two lines away, which is why they can be deleted at all; the ones without a
 * twin would erase memory nothing else records (docs/DesignNotes.md -> "Splitting PlayerRegistry by job, and where a default
 * class really lives").
 *
 * The allowed set below is DEBT, not design. Each entry is one more site that must disappear from the dictionary, never appear
 * in it for the first time: adding a direct write fails here, and completing a removal fails here until the entry is deleted.
 * `PreLineParser` is counted separately because its seven claims go through an injected callback (`addVerifiedPlayer(`) rather
 * than naming the store — a plain grep for "PlayerRegistry" finds nothing at any of them, which is how an early count of the
 * write sites came out wrong.
 */
[TestClass]
public class IdentityWriterInvariantTest
{
  /// <summary>Files still claiming identity directly on the store. Key = file name, value = call sites. Empty = migrated.</summary>
  private static readonly Dictionary<string, int> AllowedStoreWrites = new(StringComparer.OrdinalIgnoreCase)
  {
    ["PreLineParser.cs"] = 7,
    ["LineModifiersParser.cs"] = 4,
    ["ChatDB.cs"] = 3,
    ["DamageLineParser.cs"] = 3,
    ["HealingLineParser.cs"] = 2,
    ["MiscLineParser.cs"] = 1,
    ["CastLineParser.cs"] = 1,
    ["LogProcessor.cs"] = 1,
  };

  [TestMethod]
  public void IdentityClaimsAreWrittenOnlyByTheRuleBook()
  {
    var root = FindRepoRoot();
    if (root is null)
    {
      Assert.Inconclusive("the source tree was not found above the test binaries.");
      return;
    }

    // The store declares these; the derive rules may seed it. Neither is a violation.
    var exempt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PlayerRegistry.cs", "IdentityPriorStore.cs" };
    var pattern = new Regex(@"(?:\.Add(?:VerifiedPlayer|VerifiedPet)|(?<!Set)addVerifiedPlayer)\s*\(");

    var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var file in EnumerateSources(root))
    {
      var name = Path.GetFileName(file);
      if (exempt.Contains(name)) continue;

      var hits = pattern.Matches(File.ReadAllText(file)).Count;
      if (hits > 0) found[name] = hits;
    }

    // Report the two directions separately: a NEW writer is a regression, and a site that vanished should be forgotten here.
    var newWriters = found.Where(kv => !AllowedStoreWrites.ContainsKey(kv.Key)).Select(kv => $"{kv.Key} ({kv.Value})");
    var stale = AllowedStoreWrites.Keys.Where(k => !found.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal);

    Assert.IsFalse(newWriters.Any(),
      "new direct identity claims on PlayerRegistry: " + string.Join(", ", newWriters) +
      ". Publish evidence (EventsEvidence / EvidenceFact) and let the rule book decide the verdict — a parser cannot outweigh " +
      "a Targeted (NPC) frame or a charm sighting, and nothing outside derive is remembered in identity-priors.txt.");

    Assert.AreEqual("", string.Join(", ", stale),
      "these files no longer write identity but are still listed as debt — delete the entries: " + string.Join(", ", stale));

    var drifted = found.Where(kv => AllowedStoreWrites.TryGetValue(kv.Key, out var n) && n != kv.Value)
                       .Select(kv => $"{kv.Key}: listed {AllowedStoreWrites[kv.Key]}, found {kv.Value}");
    Assert.IsFalse(drifted.Any(), "debt count drifted (a site was added or removed): " + string.Join("; ", drifted));
  }

  private static IEnumerable<string> EnumerateSources(string root) =>
    new[] { Path.Combine(root, "EQLogParser"), Path.Combine(root, "EQLogParser.Core") }
      .Where(Directory.Exists)
      .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
      .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
               && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

  private static string? FindRepoRoot()
  {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EQLogParser.sln"))) dir = dir.Parent;
    return dir?.FullName;
  }
}
