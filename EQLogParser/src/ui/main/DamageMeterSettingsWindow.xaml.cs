// The damage meter's setup companion: it stages every option in a DamageMeterConfigState, previews each change on the
// sample-data stage live, and writes nothing until Save. Opened by the meter overlay itself when configure mode
// starts; owned by the stage so it closes with it by the same gesture that ends configure (Save or Cancel). ✕ and Esc
// deliberately do not exist here: the way out is two named decisions, exactly like the FCT panel.

using log4net;
using Syncfusion.Windows.Shared;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace EQLogParser
{
  public sealed partial class DamageMeterSettingsWindow : Window
  {
    private static readonly ILog Log = LogManager.GetLogger(typeof(DamageMeterSettingsWindow));

    // The gap the panel keeps from the stage, in DIPs: it is converted through the stage's DPI like every other size here.
    private const double PanelGapDip = 12;

    private readonly DamageOverlayWindow _stage;

    // The column picker is built from MeterColumns rather than a checkbox per column, so the table, the ini keys and this combo
    // cannot drift apart; _columnCheck maps an entry back to its row in the dropdown for the two passes that fill and read it.
    private readonly List<ComboBoxItemDetails> _columnItems = [];
    private readonly Dictionary<MeterColumn, ComboBoxItemDetails> _columnCheck = new();

    private bool _loading = true;
    private IntPtr _stageHandle;
    private IntPtr _panelHandle;
    private NativeMethods.RECT _lastStageBounds;
    private bool _stageBoundsKnown;
    private bool _nativeFailure;
    private int _nativeFailures;

    internal DamageMeterSettingsWindow(DamageOverlayWindow stage)
    {
      // The same ritual every other window in the app performs: SfSkinManager must stamp this window with the active
      // theme before any content exists, or its ComboBoxes, spinner and pickers draw as bare WPF instead of the skin
      // everything else wears. The static theme event re-stamps live; the unsubscribe on Closed matters because that
      // event outlives every window listening to it.
      ThemeConfig.SetCurrentTheme(this);
      InitializeComponent();

      ThemeConfig.EventsThemeChanged += EventsThemeChanged;
      Closed += (_, _) => ThemeConfig.EventsThemeChanged -= EventsThemeChanged;

      _stage = stage;
      Closed += (_, _) =>
      {
        _stage.LocationChanged -= FollowStage;
        _stage.SizeChanged -= FollowStage;
      };

      var list = EQDataStore.Instance.GetClassList();
      list.Insert(0, Resource.ANY_CLASS);
      classCombo.ItemsSource = list;

      foreach (var column in MeterColumns.All)
      {
        // PickerLabel, not Header: the dropdown explains the one word that is not self-evident ("RDPS (6s rolling)"), while the meter's header cell
        // keeps the bare word — both come from this table, so they cannot drift into naming the same column two different things.
        var item = new ComboBoxItemDetails(column.DefaultOn, column.PickerLabel) { ToolTip = column.Tip };
        _columnItems.Add(item);
        _columnCheck[column.Bit] = item;
      }

      columnCombo.ItemsSource = _columnItems;

      FillFrom(DamageMeterConfigState.Load());

      Loaded += OnLoaded;
    }

    // Docking and following speak native screen pixels end to end (MeterPanelPlacement carries the law). The panel's own DIP size
    // is converted through the STAGE's DPI, because that is the monitor the panel is about to land on.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
      _stageHandle = new WindowInteropHelper(_stage).Handle;
      _panelHandle = new WindowInteropHelper(this).Handle;

      // Subscribed before any placement attempt: a rectangle read that fails at opening costs the initial dock only, and the panel
      // still follows from the stage's next move rather than sitting deaf for the rest of configure.
      _stage.LocationChanged += FollowStage;
      _stage.SizeChanged += FollowStage;

      DockBesideStage();
    }

    private void DockBesideStage()
    {
      if (!NativeMethods.GetWindowRect(_stageHandle, out var stage)
        || !NativeMethods.TryGetMonitorWorkArea(_stageHandle, out var work))
      {
        WarnNativeFailure("could not read the meter's rectangle or its monitor's work area");
        return;
      }

      var dpi = VisualTreeHelper.GetDpi(_stage);
      var (left, top) = MeterPanelPlacement.PlaceBesideStage(stage.Left, stage.Right, stage.Top,
        (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY),
        (int)Math.Ceiling(PanelGapDip * dpi.DpiScaleX), work.Left, work.Top, work.Right, work.Bottom);

      _lastStageBounds = stage;
      _stageBoundsKnown = true;
      MovePanel(left, top);
    }

    // Both events read the same native rectangle: a move/resize notification pair spends each delta only once.
    // A width-only change clamps on the panel's own monitor; a drag follows the stage onto its new monitor.
    private void FollowStage(object sender, EventArgs e)
    {
      if (!NativeMethods.GetWindowRect(_stageHandle, out var stage)
        || !NativeMethods.GetWindowRect(_panelHandle, out var panel))
      {
        WarnNativeFailure("could not read the meter's or panel's rectangle");
        return;
      }

      // A dock that never read the stage's rectangle has no "before" to subtract: adopt this one and move nothing, so the NEXT event
      // moves by its real delta instead of by the distance from a zeroed rectangle.
      if (!_stageBoundsKnown)
      {
        _lastStageBounds = stage;
        _stageBoundsKnown = true;
        ResetNativeFailure();
        return;
      }

      var dx = stage.Left - _lastStageBounds.Left;
      var dy = stage.Top - _lastStageBounds.Top;
      var growth = (stage.Right - stage.Left) - (_lastStageBounds.Right - _lastStageBounds.Left);

      var left = panel.Left + dx;
      var monitorWindow = dx != 0 || dy != 0 ? _stageHandle : _panelHandle;
      if (!NativeMethods.TryGetMonitorWorkArea(monitorWindow, out var work))
      {
        WarnNativeFailure("could not read the monitor's work area");
        return;
      }

      left = MeterPanelPlacement.FollowWidth(left, panel.Right - panel.Left, stage.Left, growth,
        work.Left, work.Right);
      if (dx != 0 || dy != 0)
        left = MeterPanelPlacement.FollowLeft(left, panel.Right - panel.Left, work.Left, work.Right);
      var top = MeterPanelPlacement.FollowTop(panel.Top + dy, panel.Bottom - panel.Top, work.Top, work.Bottom);
      // Only spend the stage delta after the panel moved (or was already at its clamped destination).
      // A refused native call must leave the delta available to the next move/size event.
      if (left != panel.Left || top != panel.Top)
      {
        if (!MovePanel(left, top)) return;
      }
      else ResetNativeFailure();
      _lastStageBounds = stage;
    }

    /*
     * A refused move is the companion sitting still for one gesture, so it is reported and nothing else. This runs from the stage's
     * move/size notifications: a native failure there (the owner closing under the event, a monitor mid-change) must not throw out
     * of a layout handler, and a wedged case gets one WARN rather than a line per drag event.
     */
    private bool MovePanel(int left, int top)
    {
      if (NativeMethods.TryMoveWindow(_panelHandle, left, top, out var error))
      {
        ResetNativeFailure();
        return true;
      }

      WarnNativeFailure($"could not move the panel to ({left},{top}) — error {error}");
      return false;
    }

    /*
     * One WARN per failure EPISODE, because a drag fires move/size events at pointer rate and an alarm per event is a metric wearing
     * an alarm. The episode's count still grows on the Debug line: coalescing the WARN may not delete the number that says how many
     * attempts one condition cost. A placement that works ends the episode, so a later failure is allowed a WARN of its own.
     */
    private void WarnNativeFailure(string detail)
    {
      _nativeFailures++;
      if (_nativeFailure)
      {
        Log.Debug($"Damage Meter Setup: placement still failing ({_nativeFailures} time(s)) — {detail}.");
        return;
      }

      _nativeFailure = true;
      Log.Warn($"Damage Meter Setup: {detail}; the panel stays put until placement can be checked.");
    }

    // A placement that worked ends the episode, so the next distinct failure is allowed one WARN of its own.
    private void ResetNativeFailure() => (_nativeFailure, _nativeFailures) = (false, 0);

    private void EventsThemeChanged(string _) => ThemeConfig.SetCurrentTheme(this);

    private void HeaderDrag(object sender, MouseButtonEventArgs e)
    {
      if (e.ButtonState == MouseButtonState.Pressed)
      {
        DragMove();
      }
    }

    private void RowsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      if (!_loading && rowsUpDown is not null)
      {
        _stage.PreviewMeterState(Snapshot());
      }
    }

    private void ControlChanged(object sender, RoutedEventArgs e)
    {
      if (_loading || fontCombo is null || miniCheck is null)
      {
        return;
      }

      _stage.PreviewMeterState(Snapshot());
    }

    // Like every other multi-pick in the app, closing the dropdown is what commits — four width decisions preview as one layout.
    private void ColumnsDropDownClosed(object sender, EventArgs e)
    {
      if (_loading || columnCombo is null)
      {
        return;
      }

      UiElementUtil.SetComboBoxTitle(columnCombo, "columns");
      _stage.PreviewMeterState(Snapshot());
    }

    private void ColourPicked(object sender, SelectedColorChangedEventArgs e)
    {
      if (_loading || barColor is null)
      {
        return;
      }

      _stage.PreviewMeterState(Snapshot());
    }

    private void SaveClick(object sender, RoutedEventArgs e) => _stage.CommitMeterState(Snapshot());

    // Preview the panel choice on the sample stage; Save alone changes the live meter's activity setting.
    private void PanelsChanged(object sender, SelectionChangedEventArgs e)
    {
      if (_loading || panelsCombo is null) return;
      _stage.PreviewMeterState(Snapshot());
    }

    private void CancelClick(object sender, RoutedEventArgs e) => _stage.DiscardMeterSettings();

    // Read every control into a fresh state copy. Colors travel as #AARRGGBB strings, the same shape the ini keys and
    // the overlay's brush helpers already speak.
    private DamageMeterConfigState Snapshot()
    {
      var s = new DamageMeterConfigState
      {
        MaxRows = (int)rowsUpDown.Value,
        FontSize = TagOf(fontCombo.SelectedItem, 12),
        MiniBars = miniCheck.IsChecked == true,
        Columns = ReadColumns(),
        HideOtherPlayers = hideCheck.IsChecked == true,
        StreamerMode = streamerCheck.IsChecked == true,
        CritRateDisplay = critCombo.SelectedIndex is >= 0 ? critCombo.SelectedIndex : 0,
        Panels = (MeterPanels) TagOf(panelsCombo.SelectedItem, (int) MeterPanels.Both),
        DamageResetMode = TagOf(resetCombo.SelectedItem, 0),
        SelectedClass = classCombo.SelectedItem?.ToString() ?? Resource.ANY_CLASS,
        ProgressColor = ToHex(barColor.Color),
        HighlightColor = ToHex(highlightColor.Color)
      };

      return s;
    }

    private void FillFrom(DamageMeterConfigState s)
    {
      _loading = true;

      rowsUpDown.Value = s.MaxRows;
      SelectByTag(fontCombo, s.FontSize);
      miniCheck.IsChecked = s.MiniBars;
      foreach (var column in MeterColumns.All)
      {
        _columnCheck[column.Bit].IsChecked = MeterColumns.Includes(s.Columns, column.Bit);
      }

      UiElementUtil.SetComboBoxTitle(columnCombo, "columns");
      hideCheck.IsChecked = s.HideOtherPlayers;
      streamerCheck.IsChecked = s.StreamerMode;
      critCombo.SelectedIndex = s.CritRateDisplay is >= 0 and <= 3 ? s.CritRateDisplay : 0;

      // Load() already refused anything that is not Damage, Tanking or Both, so this always lands on an item.
      SelectByTag(panelsCombo, (int) s.Panels);
      SelectByTag(resetCombo, s.DamageResetMode);

      var classes = (System.Collections.Generic.IList<string>)classCombo.ItemsSource;
      classCombo.SelectedItem = classes != null && classes.Contains(s.SelectedClass) ? s.SelectedClass : Resource.ANY_CLASS;

      barColor.Color = ParseColor(s.ProgressColor);
      highlightColor.Color = ParseColor(s.HighlightColor);

      _loading = false;
    }

    // The dropdown read back into the column set, through the same fold MeterColumns.Load() uses — so a tick that is previewed and a tick
    // that gets saved cannot come to mean different things, and a new column needs no change here.
    private MeterColumn ReadColumns() => MeterColumns.FromSwitches(c => _columnCheck[c.Bit].IsChecked);

    private static int TagOf(object item, int fallback)
    {
      return item is ComboBoxItem box && int.TryParse(box.Tag?.ToString(), out var v) ? v : fallback;
    }

    private static void SelectByTag(ComboBox combo, int value)
    {
      foreach (var item in combo.Items)
      {
        if (item is ComboBoxItem box && box.Tag is not null && box.Tag.ToString() == value.ToString())
        {
          combo.SelectedItem = box;
          return;
        }
      }

      combo.SelectedIndex = 0;
    }

    private static Color ParseColor(string raw)
    {
      return (Color)(ColorConverter.ConvertFromString(raw) ?? ColorConverter.ConvertFromString("#FF1D397E"));
    }

    private static string ToHex(Color color)
    {
      return color.ToString();
    }
  }
}
