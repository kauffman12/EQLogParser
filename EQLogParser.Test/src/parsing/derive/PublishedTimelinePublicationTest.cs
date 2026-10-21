using System.IO;
using System.Linq;

#nullable enable annotations

namespace EQLogParser;

/*
 * The other half of "a published timeline is frozen" (docs/DesignNotes.md → "A published timeline is frozen"): the freeze actually
 * happens where a timeline is published. EntityTimeline refuses writes once frozen — pinned by EntityTimelineConcurrencyTest — but the
 * engine assembly (net10.0-windows) is the only producer of a published timeline, and no headless test can drive it. Deleting the one
 * `Freeze()` call would therefore make every cross-thread lock-free reader unsafe while leaving this suite green, which is exactly the
 * kind of law this repository checks against source instead of trusting.
 */
[TestClass]
[TestCategory("RepositoryValidation")]
public class PublishedTimelinePublicationTest
{
  [TestMethod]
  public void ThePassThatBuildsATimelineFreezesItBeforePublishing()
  {
    var root = FindRepoRoot();
    if (root is null)
    {
      Assert.Inconclusive("the source tree was not found above the test binaries.");
      return;
    }

    var source = File.ReadAllText(Path.Combine(root, "EQLogParser", "src", "control", "util", "DeriveEngine.cs"));
    var start = source.IndexOf("private EntityTimeline Classify()", StringComparison.Ordinal);
    Assert.IsGreaterThan(0, start, "Classify() was renamed; this check cannot find the publication point");

    // Bounded by the next member declaration at the same indent, or by the class closing (Classify is the last member today).
    var rest = source[start..];
    var end = new[] { "\n    private ", "\n    internal ", "\n    public ", "\n  }\n" }
                       .Select(marker => rest.IndexOf(marker, StringComparison.Ordinal))
                       .Where(static index => index >= 0)
                       .DefaultIfEmpty(rest.Length)
                       .Min();
    var body = rest[..end];
    Assert.IsTrue(body.Contains("RegistrySeed.Apply", StringComparison.Ordinal),
      "Classify()'s body was not bounded correctly; this check would pass on the wrong text");

    var freeze = body.IndexOf(".Freeze()", StringComparison.Ordinal);
    var publish = body.IndexOf("return timeline", StringComparison.Ordinal);
    Assert.IsGreaterThan(0, freeze, "Classify() no longer freezes the timeline it publishes");
    Assert.IsGreaterThan(0, publish, "Classify()'s publication line changed shape; re-check where freezing belongs");
    Assert.IsTrue(freeze < publish, "the timeline must be frozen BEFORE it leaves the pass that built it");

    // Freezing before the rule book and the override replay would refuse their own writes.
    Assert.IsGreaterThan(0, body.IndexOf("IdentityOverrideStore.Instance.Apply", StringComparison.Ordinal));
    Assert.IsTrue(body.IndexOf("IdentityOverrideStore.Instance.Apply", StringComparison.Ordinal) < freeze,
      "the override replay must run before the store closes");
  }

  private static string? FindRepoRoot()
  {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EQLogParser.sln"))) dir = dir.Parent;
    return dir?.FullName;
  }
}
