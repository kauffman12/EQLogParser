using System.Xml.Linq;

namespace EQLogParser;

// Pin declarations that meet across files; worker ownership and estimation belong in behavioral tests.
[TestClass]
public class NpcCombatStatsMarkupTest
{
  private const string WindowName = "npcCombatStatsWindow";

  [TestMethod]
  public void TheMenuOpensTheWindowTheDockSiteRegistered()
  {
    var registration = Read("EQLogParser", "src", "ui", "main", "MainActions.cs");
    var menu = Read("EQLogParser", "src", "ui", "main", "MainWindow.xaml");

    StringAssert.Contains(registration, $"typeof(NpcCombatStatsViewer), \"{WindowName}\"",
      "the pane must be added to the dock site or the menu item opens nothing");
    StringAssert.Contains(menu, $"Tag=\"{WindowName}\"",
      "the menu's check mark and toggle are driven by the icon Tag; a different string silences both");
  }

  [TestMethod]
  public void ThePaneOpensOnAllFightsAndCarriesBothFilterDropdowns()
  {
    XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    var root = XDocument.Parse(Read("EQLogParser", "src", "ui", "stats", "NpcCombatStatsViewer.xaml"));
    var fightOption = root.Descendants(ui + "ComboBox")
      .Single(e => e.Attribute(xaml + "Name")?.Value == "fightOption");
    var items = fightOption.Elements(ui + "ComboBoxItem").Select(e => e.Value.Trim()).ToList();
    Assert.AreEqual("0", fightOption.Attribute("SelectedIndex")?.Value);
    Assert.AreEqual("All Fights", items[0], "the table defaults to the whole log loaded");
    Assert.AreEqual("Selected Fights", items[1]);

    var names = root.Descendants(ui + "ComboBox").Select(e => e.Attribute(xaml + "Name")?.Value).ToList();
    CollectionAssert.AreEquivalent(new[] { "fightOption", "zoneList", "npcList" }, names,
      "zone and name are the two filters the table offers");
  }

  private static string RepoRoot()
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EQLogParser.sln")))
    {
      directory = directory.Parent;
    }

    return directory?.FullName ?? "";
  }

  private static string Read(params string[] parts)
  {
    var path = Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray());
    if (!File.Exists(path)) Assert.Inconclusive($"{path} was not found.");
    return File.ReadAllText(path);
  }
}
