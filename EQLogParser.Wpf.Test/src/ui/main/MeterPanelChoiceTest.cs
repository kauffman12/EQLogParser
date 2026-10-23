using System.Windows;

namespace EQLogParser;

/*
 * Which panels the meter has is one stored word that answers two questions: which tabs exist, and which activity may open or keep the
 * window up. Both halves need a pin, and this assembly is where the settings fold and the tab strip live — `DamageMeterConfigState.Load()`
 * is the ONE reader of `OverlayMeterPanels`, and `MeterPanels.None` must be unreachable through it, because a meter with no tabs has
 * nothing left to configure and nothing that could ever wake it. The interlock itself (a lane you do not watch cannot pop the overlay)
 * is Core and pinned in MeterArrivalsTest; a real window on a real fight stays a gate in docs/ReleaseChecklist.md.
 * See docs/DesignNotes.md → "Which panels the meter shows decides what may wake it and what is worth displaying".
 */
[TestClass]
public class MeterPanelChoiceTest
{
  private const string PanelsKey = "OverlayMeterPanels";

  [TestInitialize]
  public void Setup() => ConfigUtil.RemoveSetting(PanelsKey);

  [TestCleanup]
  public void Cleanup() => ConfigUtil.RemoveSetting(PanelsKey);

  [TestMethod]
  public void ANoKeyAtAllMeansBothPanels() =>
    Assert.AreEqual(MeterPanels.Both, DamageMeterConfigState.Load().Panels, "the shipped default is both tabs");

  [TestMethod]
  [DataRow(1, (int) MeterPanels.Damage)]
  [DataRow(2, (int) MeterPanels.Tanking)]
  [DataRow(3, (int) MeterPanels.Both)]
  public void ASavedPanelChoiceRoundTrips(int stored, int expected)
  {
    ConfigUtil.SetSetting(PanelsKey, stored);

    // The key stores the flags value itself, so this is also the pin that the stored shape is what Load() promises.
    Assert.AreEqual((MeterPanels) expected, DamageMeterConfigState.Load().Panels);
  }

  /*
   * A hand-written 0, a set no panel answers to, and a token that is not a number all land on Both. The last one matters on its own:
   * GetSettingAsInteger answers its default for junk, and that default (0) is precisely the value this fold must refuse.
   */
  [TestMethod]
  [DataRow(0)]
  [DataRow(4)]
  [DataRow(-1)]
  public void AnUnreadablePanelChoiceReadsBoth(int stored)
  {
    ConfigUtil.SetSetting(PanelsKey, stored);
    Assert.AreEqual(MeterPanels.Both, DamageMeterConfigState.Load().Panels);
  }

  [TestMethod]
  public void ANonNumericPanelChoiceReadsBoth()
  {
    ConfigUtil.SetSetting(PanelsKey, "both");
    Assert.AreEqual(MeterPanels.Both, DamageMeterConfigState.Load().Panels);
  }

  // A panel that is switched off loses its tab rather than sitting there disabled, and the divider between two tabs is not a divider when one is left.
  [TestMethod]
  [DataRow((int) MeterPanels.Damage)]
  [DataRow((int) MeterPanels.Tanking)]
  public void AHiddenPanelTakesItsTabAndItsDividerAway(int panelsValue)
  {
    var panels = (MeterPanels) panelsValue;
    var visible = new bool[3];
    Sta.Run(() =>
    {
      var toolbar = new DamageOverlayToolbarWindow();
      toolbar.SetAvailableViews(panels);
      visible[0] = ((FrameworkElement) toolbar.FindName("dpsButton")).Visibility == Visibility.Visible;
      visible[1] = ((FrameworkElement) toolbar.FindName("tankButton")).Visibility == Visibility.Visible;
      visible[2] = ((FrameworkElement) toolbar.FindName("rect2")).Visibility == Visibility.Visible;
    });

    Assert.AreEqual((panels & MeterPanels.Damage) != 0, visible[0]);
    Assert.AreEqual((panels & MeterPanels.Tanking) != 0, visible[1]);
    Assert.IsFalse(visible[2], "one tab needs no divider beside it");
  }

  /*
   * WHAT IS DRAWN is the tab that was picked, always: the meter never moves the operator to the other half because that one happens to have
   * data (they clicked Tanking if they wanted tanking). With one half configured, that half is drawn whatever the remembered tab says, so a
   * Tanking-only meter cannot be left asking for a damage board nobody can switch to. Whether the WINDOW is up at all is the other pure
   * function, and rows may not change the tab PAIR -- that is the setting's job alone.
   */
  [TestMethod]
  public void ThePickedTabIsWhatGetsDrawn()
  {
    Assert.IsTrue(DamageOverlayWindow.DrawsDamagePanel(MeterPanels.Both, true));
    Assert.IsFalse(DamageOverlayWindow.DrawsDamagePanel(MeterPanels.Both, false));

    Assert.IsTrue(DamageOverlayWindow.DrawsDamagePanel(MeterPanels.Damage, true));
    Assert.IsTrue(DamageOverlayWindow.DrawsDamagePanel(MeterPanels.Damage, false), "a Damage-only meter draws damage even on a stale tank tab");
    Assert.IsFalse(DamageOverlayWindow.DrawsDamagePanel(MeterPanels.Tanking, true));
    Assert.IsFalse(DamageOverlayWindow.DrawsDamagePanel(MeterPanels.Tanking, false));
  }

  /*
   * The window's right to exist: at least one half the operator ENABLED has a board that lists anyone. Both on with an empty visible half and a
   * busy hidden half keeps the meter up (the tab is right there; nudging it is `SetTabAttention`'s quiet answer), while both empty means no meter
   * anywhere -- and a lane that was switched off neither opens the window nor holds it open. WHAT counts as "lists anyone" is decided one level
   * below this function, by `HasRowEntries`, which reads the board BEFORE the class filter: a lens may not delete the window, only an empty lane
   * can. That predicate is private to the window (it needs the stats model), so feeding these flags against a real filtered board is a gate in
   * docs/ReleaseChecklist.md.
   */
  [TestMethod]
  public void TheWindowSurvivesWhileAnEnabledHalfHasRows()
  {
    Assert.IsTrue(DamageOverlayWindow.AnyPanelHasEntries(true, false, MeterPanels.Both));
    Assert.IsTrue(DamageOverlayWindow.AnyPanelHasEntries(false, true, MeterPanels.Both),
      "the visible tab is empty; the other one is busy, so the meter stays up and says so on the tab");
    Assert.IsFalse(DamageOverlayWindow.AnyPanelHasEntries(false, false, MeterPanels.Both),
      "the caller asks the receipts whether the window survives this");

    Assert.IsFalse(DamageOverlayWindow.AnyPanelHasEntries(false, true, MeterPanels.Damage),
      "tanking rows cannot hold a Damage-only meter open");
    Assert.IsFalse(DamageOverlayWindow.AnyPanelHasEntries(true, false, MeterPanels.Tanking),
      "nor damage rows a Tanking-only one");
    Assert.IsTrue(DamageOverlayWindow.AnyPanelHasEntries(true, true, MeterPanels.Damage));
    Assert.IsTrue(DamageOverlayWindow.AnyPanelHasEntries(false, true, MeterPanels.Tanking));
  }

  [TestMethod]
  public void BothPanelsKeepBothTabsAndTheirDivider()
  {
    var hidden = new List<string>();
    Sta.Run(() =>
    {
      var toolbar = new DamageOverlayToolbarWindow();
      toolbar.SetAvailableViews(MeterPanels.Both);
      hidden.AddRange(from name in new[] { "dpsButton", "tankButton", "rect2" }
        where ((FrameworkElement) toolbar.FindName(name)).Visibility != Visibility.Visible
        select name);
    });

    Assert.AreEqual(0, hidden.Count, $"the default pair lost {string.Join(", ", hidden)}");
  }
}
