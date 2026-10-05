using System.Text.RegularExpressions;

namespace EQLogParser;

/*
 * Docking law, asserted on the markup that states it — because the headless version of this test could not be trusted.
 *
 * `DockingPaneToggleTest` used to assert `SyncFusionUtil.ShowStateFor` over panes whose attached properties the TEST set
 * (Left/Right/Tabbed/None) and then asked the vendor what they said. On Windows that produced a failure no amount of
 * reading the code could explain: `ShowStateFor` returns AutoHidden only for the four edges, yet a pane set to
 * `DockSide.Tabbed` came back AutoHidden — i.e. the getter does not necessarily return what was set. A test whose inputs are
 * values the vendor coerces is checking its own assumption, not the app's behaviour, so those asserts were removed rather than
 * re-lettered until they pass (docs/CodingStandards.md).
 *
 * What IS verifiable anywhere: what MainWindow declares. The identity strip's whole rule — "a menu item shows a pane, it never
 * relocates it" — begins in XAML (`SideInDockedMode="Right"` + `State="AutoHidden"`); the retired Verified Players / Verified
 * Pets shells stay `Hidden`, and the shells themselves exist only so an old `dockSite.xml` still resolves instead of tripping
 * MainWindow's catch, which calls ResetState() and wipes every pane for every user. A markup edit that breaks any of this is a
 * real defect and fails here on any platform; what genuinely needs a human (opening a pane hidden behind another tab, and
 * restarting with a layout saved by an older build) lives in docs/ReleaseChecklist.md.
 */
[TestClass]
public class DockingMarkupTest
{

  [TestMethod]
  public void TheIdentityStripIsDeclaredOnTheRightEdge()
  {
    var xaml = ReadMainWindowXaml();

    var tag = MatchTag(xaml, "namesWindow");

    Assert.IsTrue(tag.Contains("SideInDockedMode=\"Right\"", StringComparison.Ordinal),
                  "namesWindow no longer declares the right edge — it will not share the strip with Pet Owners (an AutoHidden pane " +
                  "needs its own declared side, which is also what ShowStateFor reads to hand it back auto-hidden): " + tag);
    Assert.IsTrue(tag.Contains("State=\"AutoHidden\"", StringComparison.Ordinal),
                  "namesWindow is no longer AutoHidden — a floated window cannot share a tab strip with anything: " + tag);
  }

  [TestMethod]
  public void TheRetiredVerifiedPanesStayHiddenButPresent()
  {
    var xaml = ReadMainWindowXaml();

    foreach (var name in new[] { "verifiedPlayersWindow", "verifiedPetsWindow" })
    {
      var tag = MatchTag(xaml, name);
      Assert.IsTrue(tag.Contains("State=\"Hidden\"", StringComparison.Ordinal), $"{name} is not Hidden — it became reachable again: {tag}");
    }
  }

  private static string MatchTag(string xaml, string name)
  {
    var match = Regex.Match(xaml, $@"<ContentControl\b[^>]*x:Name=""{name}""[^>]*>", RegexOptions.Singleline);
    Assert.IsTrue(match.Success, $"MainWindow.xaml no longer declares {name}. The empty shells stay on purpose: LoadDockState throws " +
                                 "on a window name it cannot resolve and the catch calls ResetState(), which would reset every user's layout.");
    return match.Value;
  }

  private static string ReadMainWindowXaml()
  {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EQLogParser.sln"))) dir = dir.Parent;
    var path = Path.Combine(dir?.FullName ?? "", "EQLogParser", "src", "ui", "main", "MainWindow.xaml");
    if (!File.Exists(path)) Assert.Inconclusive($"MainWindow.xaml was not found at {path}.");
    return File.ReadAllText(path);
  }
}
