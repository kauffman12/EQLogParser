using System.Xml.Linq;

namespace EQLogParser;

[TestClass]
public sealed class AnimationOptInMarkupTest
{
  private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
  private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

  [TestMethod]
  public void EveryDeclaredFrozenGridConfiguresTheBorderFinisher()
  {
    var sourceRoot = Path.Combine(FindRoot(), "EQLogParser", "src");
    var checkedGrids = 0;
    foreach (var path in Directory.EnumerateFiles(sourceRoot, "*.xaml", SearchOption.AllDirectories))
    {
      var document = XDocument.Load(path);
      foreach (var grid in document.Descendants().Where(e => e.Name.LocalName is "SfDataGrid" or "SfTreeGrid"))
      {
        if (!IsPositive(grid.Attribute("FrozenColumnCount")) && !IsPositive(grid.Attribute("FooterColumnCount")))
        {
          continue;
        }
        var name = (string?)grid.Attribute(Xaml + "Name");
        Assert.IsFalse(string.IsNullOrEmpty(name), $"Frozen grid needs a name for explicit initialization: {path}");
        var code = File.ReadAllText(path + ".cs");
        StringAssert.Contains(code, $"SyncFusionUtil.ConfigureFrozenCellAnimations({name});", path);
        checkedGrids++;
      }
    }
    Assert.IsTrue(checkedGrids > 0, "The sweep must find the frozen grids, not pass on an empty input.");
  }

  [TestMethod]
  public void CharacterStatusGifsStartPausedAndOptIntoVisibilityPlayback()
  {
    var document = XDocument.Load(Path.Combine(FindRoot(), "EQLogParser", "App.xaml"));
    var template = document.Descendants(Wpf + "DataTemplate")
      .Single(e => (string?)e.Attribute(Xaml + "Key") == "CharacterNodeTemplate");
    var image = template.Descendants(Wpf + "Image").Single();
    XNamespace gif = "http://wpfanimatedgif.codeplex.com";
    XNamespace local = "clr-namespace:EQLogParser";

    Assert.AreEqual("False", (string?)image.Attribute(gif + "ImageBehavior.AutoStart"));
    Assert.AreEqual("True", (string?)image.Attribute(local + "VisibleGifAnimation.Enabled"));
  }

  private static bool IsPositive(XAttribute? attribute) => int.TryParse(attribute?.Value, out var value) && value > 0;

  private static string FindRoot()
  {
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
      if (File.Exists(Path.Combine(dir.FullName, "EQLogParser.sln"))) return dir.FullName;
    }
    Assert.Inconclusive("No solution root found above the test assembly.");
    return string.Empty;
  }
}
