using System.Xml.Linq;

namespace EQLogParser;

[TestClass]
public class DamageBreakdownMarkupTest
{
  [TestMethod]
  [DataRow("DamageBreakdown")]
  [DataRow("TankingBreakdown")]
  public void BothBreakdownsRequestTheGroupedHierarchy(string pane)
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EQLogParser.sln")))
    {
      directory = directory.Parent;
    }

    var path = Path.Combine(directory?.FullName ?? "", "EQLogParser", "src", "ui", "breakdown", pane + ".xaml");
    if (!File.Exists(path)) Assert.Inconclusive($"{pane}.xaml was not found at {path}.");
    XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    var grid = XDocument.Load(path).Descendants()
      .Single(element => element.Attribute(xaml + "Name")?.Value == "dataGrid");

    Assert.AreEqual("RequestTreeItems", grid.Attribute("RequestTreeItems")?.Value);
    Assert.IsNull(grid.Attribute("ChildPropertyName"), "binding directly to SubStats bypasses the spell categories");
    Assert.AreEqual("RootNodesExpanded", grid.Attribute("AutoExpandMode")?.Value,
      "players open initially, but spell categories stay collapsed until requested");
  }
}
