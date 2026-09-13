// The damage meter's setup companion: it stages every option in a DamageMeterConfigState, previews each change on the
// sample-data stage live, and writes nothing until Save. Opened by the meter overlay itself when configure mode
// starts; owned by the stage so it closes with it by the same gesture that ends configure (Save or Cancel). ✕ and Esc
// deliberately do not exist here: the way out is two named decisions, exactly like the FCT panel.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Syncfusion.Windows.Shared;

namespace EQLogParser
{
  public sealed partial class DamageMeterSettingsWindow : Window
  {
    private readonly DamageOverlayWindow _stage;
    private bool _loading = true;
    private double _lastStageLeft;
    private double _lastStageTop;

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
      _lastStageLeft = stage.Left;
      _lastStageTop = stage.Top;

      var list = EQDataStore.Instance.GetClassList();
      list.Insert(0, Resource.ANY_CLASS);
      classCombo.ItemsSource = list;

      FillFrom(DamageMeterConfigState.Load());

      Loaded += OnLoaded;
    }

    // Dock beside the stage: right when the work area has room there, left otherwise, and clamped into the work area
    // vertically so a meter parked at a screen edge never pushes this panel off it.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
      var wa = SystemParameters.WorkArea;
      var gap = 12.0;

      Left = _stage.Left + _stage.ActualWidth + gap;
      if (Left + ActualWidth > wa.Right)
      {
        Left = _stage.Left - ActualWidth - gap;
      }

      Top = _stage.Top;
      if (Top + ActualHeight > wa.Bottom)
      {
        Top = wa.Bottom - ActualHeight;
      }

      if (Top < wa.Top)
      {
        Top = wa.Top;
      }

      _stage.LocationChanged += (_, _) => FollowStage();
    }

    // The stage moves by dragging; move this window by the same delta so the pairing the user chose (which side, how
    // far apart) survives the drag rather than snapping to a fixed dock.
    private void FollowStage()
    {
      var dLeft = _stage.Left - _lastStageLeft;
      var dTop = _stage.Top - _lastStageTop;

      _lastStageLeft = _stage.Left;
      _lastStageTop = _stage.Top;

      Left += dLeft;
      Top += dTop;
    }

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

    private void ColourPicked(object sender, SelectedColorChangedEventArgs e)
    {
      if (_loading || barColor is null)
      {
        return;
      }

      _stage.PreviewMeterState(Snapshot());
    }

    private void SaveClick(object sender, RoutedEventArgs e) => _stage.CommitMeterState(Snapshot());

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
        ShowDamagePercent = percentCheck.IsChecked == true,
        HideOtherPlayers = hideCheck.IsChecked == true,
        StreamerMode = streamerCheck.IsChecked == true,
        CritRateDisplay = critCombo.SelectedIndex is >= 0 ? critCombo.SelectedIndex : 0,
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
      percentCheck.IsChecked = s.ShowDamagePercent;
      hideCheck.IsChecked = s.HideOtherPlayers;
      streamerCheck.IsChecked = s.StreamerMode;
      critCombo.SelectedIndex = s.CritRateDisplay is >= 0 and <= 3 ? s.CritRateDisplay : 0;
      SelectByTag(resetCombo, s.DamageResetMode);

      var classes = (System.Collections.Generic.IList<string>)classCombo.ItemsSource;
      classCombo.SelectedItem = classes != null && classes.Contains(s.SelectedClass) ? s.SelectedClass : Resource.ANY_CLASS;

      barColor.Color = ParseColor(s.ProgressColor);
      highlightColor.Color = ParseColor(s.HighlightColor);

      _loading = false;
    }

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
