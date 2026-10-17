using System.Text.RegularExpressions;

namespace EQLogParser;

/*
 * Every `fa:ImageAwesome Icon="…"` literal in this repository must name a member of FontAwesome5.EFontAwesomeIcon.
 *
 * Why this test exists: the WPF markup compiler stores an enum property as TEXT in the BAML and lets the type converter
 * resolve it when the window loads. A glyph name that does not exist therefore builds at zero warnings, passes every
 * headless suite, and throws XamlParseException ("Invalid enumeration value") on window construction — issue #191 shipped
 * `Solid_File_Excel` for a member named `Solid_FileExcel`, which would have killed MainWindow on startup. Nothing else in
 * this repo can see that from Linux: the app needs Windows to run, and the WPF test assembly only has to build here.
 */
[TestClass]
public sealed class XamlIconNameTest
{
  // Bindings (`Icon="{Binding …}"`) are not literals and are excluded by [^"{].
  private static readonly Regex IconLiteral = new(
    "<fa:ImageAwesome\\b[^>]*?Icon=\"([^{\\\"]+)\"", RegexOptions.CultureInvariant);

  [TestMethod]
  public void EveryGlyphNamedInXamlExistsInTheFont()
  {
    var root = FindSolutionRoot();
    if (root is null)
    {
      Assert.Inconclusive("no EQLogParser.sln found above the test assembly; nothing to sweep");
      return;
    }

    var members = new HashSet<string>(Enum.GetNames(typeof(FontAwesome5.EFontAwesomeIcon)), StringComparer.Ordinal);
    var checkedCount = 0;
    var filesWithGlyphs = 0;
    var bad = new List<string>();

    foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
    {
      if (IsBuildOutput(file)) continue;

      var literals = IconLiteral.Matches(File.ReadAllText(file));
      if (literals.Count == 0) continue;

      filesWithGlyphs++;

      foreach (Match match in literals)
      {
        checkedCount++;
        var glyph = match.Groups[1].Value;
        if (!members.Contains(glyph))
          bad.Add($"{Path.GetRelativePath(root, file)} → {glyph}");
      }
    }

    // The sweep failing silently is the failure mode that matters: an empty list proves nothing. Counts are counted,
    // not hoped for, and a second one guards against "it only read one file".
    Assert.IsTrue(checkedCount > 100, $"only {checkedCount} glyph literals found — the sweep is not reading the app's XAML");
    Assert.IsTrue(filesWithGlyphs > 20, $"only {filesWithGlyphs} files carry fa:ImageAwesome glyphs — check the search path");
    Assert.AreEqual(0, bad.Count,
      "glyph name(s) not in FontAwesome5.EFontAwesomeIcon (builds clean, throws when the window opens):" +
      Environment.NewLine + string.Join(Environment.NewLine, bad));
  }

  private static bool IsBuildOutput(string path)
  {
    var sep = Path.DirectorySeparatorChar;
    return path.Contains($"{sep}bin{sep}", StringComparison.Ordinal) || path.Contains($"{sep}obj{sep}", StringComparison.Ordinal);
  }

  private static string? FindSolutionRoot()
  {
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
      if (File.Exists(Path.Combine(dir.FullName, "EQLogParser.sln")))
        return dir.FullName;

    return null;
  }
}
