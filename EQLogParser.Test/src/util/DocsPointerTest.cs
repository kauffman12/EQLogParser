using System.Text;
using System.Text.RegularExpressions;

namespace EQLogParser;

/*
 * Every pointer a comment makes to docs/DesignNotes.md has to land on a section that exists.
 *
 * The rule this enforces is in docs/CodingStandards.md: a comment may cite a document, and the citation is only worth
 * what its target is. Writing docs/X.md → "Some heading" reads as verification — a reader stops checking and trusts the
 * reference — and headings get reworded in the ordinary course of editing notes, at which point the sentence is a
 * footnote pointing into empty space. When the file itself is not even in git (most of docs/ is local working material)
 * nobody but the author can follow it at all; that is how nineteen comments came to cite a design map on one machine.
 *
 * Matching is deliberately loose: the first four words of the cited title have to appear, in order, inside SOME heading
 * line (case-insensitive, substring), because citations paraphrase ("The identity rule book" for
 * "The identity rule book (R0–R20) in one breath") and a test that demanded exact equality would be re-lettered rather
 * than read. What it does catch is the failure that happens: a heading renamed or a section deleted, leaving comments
 * that claim a source nobody can open.
 */
[TestClass]
public class DocsPointerTest
{
  private static readonly Regex Pointer = new(@"docs/DesignNotes\.md\s*(?:→|->)\s*""([^""]+)""", RegexOptions.Compiled);

  [TestMethod]
  public void EveryDesignNotesPointerResolvesToAHeading()
  {
    var root = FindRepoRoot();
    if (root is null)
    {
      Assert.Inconclusive("docs/DesignNotes.md was not found above the test binaries, so there is nothing to check here.");
      return;
    }

    var headings = File.ReadAllLines(Path.Combine(root, "docs", "DesignNotes.md"))
                       .Where(static line => line.StartsWith('#'))
                       .Select(static line => line.ToLowerInvariant())
                       .ToList();
    Assert.IsTrue(headings.Count > 20, "the notes were found but contain no headings — the check would pass on nothing");

    var failures = new List<string>();
    foreach (var file in SourceFiles(root))
    {
      var text = ReadText(file);
      if (text.Length == 0) continue;
      foreach (Match match in Pointer.Matches(text))
      {
        var title = match.Groups[1].Value.Trim();
        var key = Key(title);
        if (key.Length == 0) continue;   // an empty citation is a typo in the comment, not a broken pointer
        if (headings.Any(h => h.Contains(key))) continue;

        var line = text[..match.Index].Count(static c => c == '\n') + 1;
        failures.Add($"{Path.GetRelativePath(root, file)}:{line} → \"{title}\"");
      }
    }

    Assert.AreEqual(0, failures.Count, 
                    "DesignNotes pointers that match no heading (rename the pointer or write the section):\n" +
                    string.Join("\n", failures));
  }

  /*
   * The comparison key: four words, lower-cased. Four because two is enough for a coincidence ("A fight", "The name")
   * and six would fail on every paraphrase; paraphrasing is what these citations do.
   *
   * A WRAPPED citation is collapsed first, and only a wrap: the regex captures across newlines, so a phrase broken in the
   * middle of a block comment arrives as "The one seam" + newline + " * that answers", and the four-word key became
   * "the one seam *" — which reported a section that plainly exists as a broken pointer. The collapse covers the newline,
   * the indentation and the comment's own leading asterisk; NOTHING else is stripped, so intra-word punctuation still has
   * to agree with the heading exactly as before (a citation for "R0–R20" keeps its dash). A citation should still be
   * written whole on one line because comments read better that way — the checker just no longer fails when one doesn't.
   */
  private static readonly Regex CommentWrap = new(@"\s*\n\s*\*?\s*", RegexOptions.Compiled);

  private static string Key(string title) =>
    string.Join(' ', CommentWrap.Replace(title, " ").ToLowerInvariant()
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(4));

  /* Code, markup and the agent guide: everything a reader of this repository actually follows. obj/bin are skipped
   * because generated files carry copies of source comments with paths that mean nothing. */
  private static IEnumerable<string> SourceFiles(string root)
  {
    var code = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
      .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                            && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                            && !path.Contains($"{Path.DirectorySeparatorChar}local{Path.DirectorySeparatorChar}")
                            && (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                                || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)));

    return code.Concat([Path.Combine(root, "AGENTS.md")]);
  }

  /* Lenient on purpose: a scratch file somewhere under the tree in some other encoding is not a broken pointer, and
   * this check must never fail because of a byte it cannot decode. */
  private static string ReadText(string path)
  {
    using var reader = new StreamReader(path, new UTF8Encoding(false, false));
    return reader.ReadToEnd();
  }

  private static string? FindRepoRoot()
  {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
      if (File.Exists(Path.Combine(dir.FullName, "docs", "DesignNotes.md"))) return dir.FullName;
      dir = dir.Parent;
    }

    return null;
  }
}
