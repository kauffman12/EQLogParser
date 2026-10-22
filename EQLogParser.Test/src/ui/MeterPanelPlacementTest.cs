// Where the damage meter's Setup companion lands, and where it goes when the stage grows. What breaks if these assertions go: the
// panel jumps back to the primary monitor (the bug this arithmetic replaced was `SystemParameters.WorkArea`, which is the PRIMARY
// work area in DIPs), it lands half off a monitor left of the primary, or a widening meter slides over the top of it. Both halves
// are pure pixel arithmetic on purpose: the window that calls them converts its own DIP size through the stage's DPI and reads every
// screen figure from MonitorFromWindow, which is Windows-only plumbing (`DamageMeterSettingsWindow`); the arithmetic is what decides
// whether the panel is on screen and how far apart the pair sits.
// docs/DesignNotes.md → "Damage meter column switches: hiding one reserves no width".

namespace EQLogParser;

[TestClass]
public sealed class MeterPanelPlacementTest
{
  // The panel's own size and the gap, in pixels at 100 % scale; the vertical axis is held away from every clamp unless a test aims at it.
  private const int PanelWidth = 350;
  private const int PanelHeight = 400;
  private const int Gap = 12;

  [TestMethod]
  public void ItOpensRightOfTheStageOnEveryMonitor()
  {
    foreach (var origin in new[] { -1920, 0, 1920 })
    {
      var (left, top) = MeterPanelPlacement.PlaceBesideStage(origin + 300, origin + 700, 100, PanelWidth, PanelHeight, Gap,
        origin, 0, origin + 1920, 1080);

      Assert.AreEqual(origin + 700 + Gap, left, $"origin {origin}: the chosen gap was not kept");
      Assert.AreEqual(100, top, $"origin {origin}: a stage near the top must not be pushed down");
    }
  }

  // The whole reason for the flip: a meter parked against its monitor's right edge gets the panel on its LEFT rather than a panel
  // clamped onto the stage.
  [TestMethod]
  public void ItFlipsToTheLeftWhenTheRightSideHasNoRoom()
  {
    var (left, _) = MeterPanelPlacement.PlaceBesideStage(1500, 1900, 100, PanelWidth, PanelHeight, Gap, 0, 0, 1920, 1080);

    Assert.AreEqual(1500 - PanelWidth - Gap, left);
  }

  /*
   * A work area narrower than stage + panel on BOTH sides has no good answer: the flip lands off the monitor's left edge, and the clamp
   * pulls the panel back to that edge — overlapping the stage rather than sitting where nothing could grab it. Overlap is the cheap
   * failure and an off-screen panel is the expensive one, which is why the clamp exists at all. Same answer with negative coordinates,
   * which is the whole point of doing this arithmetic in desktop pixels.
   */
  [TestMethod]
  public void ItPinsThePanelToWorkAreasLeftEdgeWhenNeitherSideFits()
  {
    var (right, _) = MeterPanelPlacement.PlaceBesideStage(1930, 2250, 100, PanelWidth, PanelHeight, Gap, 1920, 0, 2300, 1080);
    Assert.AreEqual(1920, right, "a flip that lands off the monitor's left must come back to its left edge");

    var (left, _) = MeterPanelPlacement.PlaceBesideStage(-2290, -1970, 100, PanelWidth, PanelHeight, Gap, -2300, 0, -1920, 1080);
    Assert.AreEqual(-2300, left, "negative desktop coordinates clamp the same way");

    // A panel wider than the whole work area is the same case with no stage in the way: it sits at the left edge, reachable.
    var (wide, _) = MeterPanelPlacement.PlaceBesideStage(1930, 2000, 100, 600, PanelHeight, Gap, 1920, 0, 2300, 1080);
    Assert.AreEqual(1920, wide);
  }

  // Vertically: the stage's top is the anchor, and each edge pulls the panel back on screen rather than letting it hang off.
  [TestMethod]
  public void ItClampsVerticallyToBothEdgesOfWorkArea()
  {
    Assert.AreEqual(200, MeterPanelPlacement.PlaceBesideStage(300, 700, 200, PanelWidth, PanelHeight, Gap, 0, 0, 1920, 1080).Top);
    Assert.AreEqual(680, MeterPanelPlacement.PlaceBesideStage(300, 700, 900, PanelWidth, PanelHeight, Gap, 0, 0, 1920, 1080).Top,
      "a stage near the bottom must not push the panel below the work area");

    // A monitor ABOVE the primary has a negative work area; the same clamp reads it without a sign assumption.
    Assert.AreEqual(-1080, MeterPanelPlacement.PlaceBesideStage(300, 700, -1150, PanelWidth, PanelHeight, Gap, 0, -1080, 1920, 0).Top);
    Assert.AreEqual(-400, MeterPanelPlacement.PlaceBesideStage(300, 700, -100, PanelWidth, PanelHeight, Gap, 0, -1080, 1920, 0).Top);
  }

  // The caller passes pixels for every figure, so the same question at 150 % scales by 150 % — nothing inside may assume a DIP.
  [TestMethod]
  public void OpeningPlacementScalesWithoutMixingDipsIntoPixels()
  {
    foreach (var scale in new[] { 1.0, 1.5 })
    {
      var (left, top) = MeterPanelPlacement.PlaceBesideStage((int)(300 * scale), (int)(700 * scale), (int)(100 * scale),
        (int)(PanelWidth * scale), (int)(PanelHeight * scale), (int)(Gap * scale), 0, 0, (int)(1920 * scale), (int)(1080 * scale));

      Assert.AreEqual((int)(712 * scale), left);
      Assert.AreEqual((int)(100 * scale), top);
    }
  }

  [TestMethod]
  public void GrowthKeepsTheChosenGapOnEitherSideOfThePrimaryMonitor()
  {
    foreach (var origin in new[] { -1920, 0, 1920 })
    {
      var panelLeft = origin + 600;
      Assert.AreEqual(panelLeft + 60,
        MeterPanelPlacement.FollowWidth(panelLeft, PanelWidth, origin + 300, 60, origin, origin + 1920));
    }
  }

  [TestMethod]
  public void ThePanelsWorkAreaNotThePrimaryScreenClampsGrowth()
  {
    Assert.AreEqual(3490, MeterPanelPlacement.FollowWidth(3480, PanelWidth, 3200, 60, 1920, 3840));
    Assert.AreEqual(-350, MeterPanelPlacement.FollowWidth(-360, PanelWidth, -640, 60, -1920, 0));
    Assert.AreEqual(1920, MeterPanelPlacement.FollowWidth(1950, 500, 1920, 60, 1920, 2300),
      "a panel wider than the work area must keep its left edge accessible");
  }

  [TestMethod]
  public void ShrinkingRepeatedNotificationsAndLeftDockingNeverMoveThePanel()
  {
    Assert.AreEqual(2500, MeterPanelPlacement.FollowWidth(2500, PanelWidth, 2200, -60, 1920, 3840));
    Assert.AreEqual(2500, MeterPanelPlacement.FollowWidth(2500, PanelWidth, 2200, 0, 1920, 3840));
    Assert.AreEqual(2000, MeterPanelPlacement.FollowWidth(2000, PanelWidth, 2400, 60, 1920, 3840));
  }

  /*
   * The vertical half of a follow. A drag delta has no opinion about where the screen ends, so following the stage downward used to
   * walk the panel off the taskbar; the clamp is the same shape the opening dock uses, and it must release when the stage comes back
   * rather than parking the panel at the bottom forever.
   */
  [TestMethod]
  public void FollowingTheStageDownStopsAtTheWorkAreaAndComesBack()
  {
    Assert.AreEqual(350, MeterPanelPlacement.FollowTop(300 + 50, PanelHeight, 0, 1080), "an ordinary drag follows one for one");
    Assert.AreEqual(680, MeterPanelPlacement.FollowTop(900 + 300, PanelHeight, 0, 1080),
      "the bottom edge stays on the monitor instead of trailing off it");
    Assert.AreEqual(630, MeterPanelPlacement.FollowTop(1000, PanelHeight, 0, 1030), "the taskbar counts as the bottom");

    // Dragging back up starts from the clamped top: it must move again rather than stay pinned.
    Assert.AreEqual(380, MeterPanelPlacement.FollowTop(680 - 300, PanelHeight, 0, 1080));
  }

  [TestMethod]
  public void DraggingWithoutResizingClampsBothHorizontalEdgesAndReleasesOnReturn()
  {
    foreach (var origin in new[] { -1920, 0, 1920 })
    {
      var left = MeterPanelPlacement.FollowLeft(origin + 1910, PanelWidth, origin, origin + 1920);
      Assert.AreEqual(origin + 1570, left, "rightward movement without width growth must stay reachable");
      Assert.AreEqual(left - 100, MeterPanelPlacement.FollowLeft(left - 100, PanelWidth, origin, origin + 1920));
      Assert.AreEqual(origin, MeterPanelPlacement.FollowLeft(origin - 100, PanelWidth, origin, origin + 1920));
      Assert.AreEqual(origin + 100, MeterPanelPlacement.FollowLeft(origin + 100, PanelWidth, origin, origin + 1920));
    }
  }

  [TestMethod]
  public void DraggingAnOversizedPanelKeepsItsLeftEdgeReachable()
  {
    Assert.AreEqual(-400, MeterPanelPlacement.FollowLeft(-200, 600, -400, 0));
    Assert.AreEqual(1920, MeterPanelPlacement.FollowLeft(2500, 600, 1920, 2300));
  }

  [TestMethod]
  public void HorizontalDragClampingScalesInPixels()
  {
    foreach (var scale in new[] { 1.0, 1.5, 2.0 })
    {
      Assert.AreEqual((int)(1570 * scale), MeterPanelPlacement.FollowLeft((int)(1910 * scale),
        (int)(PanelWidth * scale), 0, (int)(1920 * scale)));
    }
  }

  [TestMethod]
  public void TheVerticalClampFollowsWhicheverMonitorThePairIsOn()
  {
    // A monitor stacked below the primary has its own work area; a panel dragged down onto it clamps there, not at the primary's bottom.
    Assert.AreEqual(1760, MeterPanelPlacement.FollowTop(2000 + 100, PanelHeight, 1080, 2160));
    Assert.AreEqual(1080, MeterPanelPlacement.FollowTop(500, PanelHeight, 1080, 2160),
      "and it cannot be dragged above that monitor either");

    // Taller than the work area: keep the top edge reachable, which is what the opening dock does for the same shape.
    Assert.AreEqual(0, MeterPanelPlacement.FollowTop(50, 1200, 0, 1080));
  }

  [TestMethod]
  public void PixelPlacementScalesWithoutMixingDipWidthsIntoScreenBounds()
  {
    foreach (var scale in new[] { 1.0, 1.5, 2.0 })
    {
      Assert.AreEqual((int)(660 * scale), MeterPanelPlacement.FollowWidth((int)(600 * scale),
        (int)(PanelWidth * scale), (int)(300 * scale), (int)(60 * scale), 0, (int)(1920 * scale)));
    }
  }
}
