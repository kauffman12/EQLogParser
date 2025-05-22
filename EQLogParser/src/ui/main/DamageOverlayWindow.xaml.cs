using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace EQLogParser
{
  public partial class DamageOverlayWindow
  {
    private const double DamageModeZeroTimeout = TimeSpan.TicksPerSecond * 7; // with 3 second slain queue delay
    private const long TopTimeout = TimeSpan.TicksPerSecond * 2;
    private static readonly object StatsLock = new();
    private static readonly SolidColorBrush ActiveBrush = new(Color.FromRgb(254, 156, 30));
    private static readonly SolidColorBrush InActiveBrush = new(Colors.White);
    private static DamageOverlayStats _stats;
    private readonly DispatcherTimer _updateTimer;
    private readonly bool _preview;
    private readonly bool _ready;
    private Task _lastUpdateTask = Task.CompletedTask;
    private double _savedHeight;
    private double _savedWidth;
    private double _savedTop = double.NaN;
    private double _savedLeft;
    private long _lastTopTicks = long.MinValue;
    private int _savedFontSize;
    private int _savedMaxRows;
    private int _currentDamageMode;
    private int _savedDamageMode;
    private int _currentShowCritRate;
    private int _savedShowCritRate;
    private bool _currentHideOthers;
    private bool _savedHideOthers;
    private bool _savedMiniBars;
    private bool _savedStreamerMode;
    private bool _currentShowDps;
    private string _currentSelectedClass;
    private string _savedSelectedClass;
    private string _savedProgressColor;
    private string _savedHighlightColor;

    internal DamageOverlayWindow(bool preview = false, bool reset = false)
    {
      MainActions.SetCurrentTheme(this);
      InitializeComponent();
      dpsButton.Foreground = ActiveBrush;
      tankButton.Foreground = InActiveBrush;
      _preview = preview;

      if (reset)
      {
        lock (StatsLock)
        {
          _stats = null;
        }
      }

      // dimensions
      var width = ConfigUtil.GetSettingAsDouble("OverlayWidth", SystemParameters.VirtualScreenWidth / 4);
      var height = ConfigUtil.GetSettingAsDouble("OverlayHeight", int.MaxValue);
      var top = ConfigUtil.GetSettingAsDouble("OverlayTop", 20);
      var left = ConfigUtil.GetSettingAsDouble("OverlayLeft", 100);
      SetWindowSizes(height, width, top, left);

      // fonts
      var fontSizeString = ConfigUtil.GetSetting("OverlayFontSize");
      if (fontSizeString == null || !int.TryParse(fontSizeString, out _savedFontSize) || (_savedFontSize != 10 &&
        _savedFontSize != 12 && _savedFontSize != 14 && _savedFontSize != 16))
      {
        _savedFontSize = 12;
      }

      UpdateFontSize(_savedFontSize);

      // color
      _savedProgressColor = ConfigUtil.GetSetting("OverlayRankColor");
      if (_savedProgressColor == null || ColorConverter.ConvertFromString(_savedProgressColor) == null)
      {
        _savedProgressColor = "#FF1D397E";
      }

      UpdateProgressBrush(_savedProgressColor);

      // highlight color
      _savedHighlightColor = ConfigUtil.GetSetting("OverlayHighlightColor");
      if (_savedHighlightColor == null || ColorConverter.ConvertFromString(_savedHighlightColor) == null)
      {
        _savedHighlightColor = _savedProgressColor;
      }

      UpdateHighlightBrush(_savedHighlightColor);

      // Max Rows
      var maxRowsString = ConfigUtil.GetSetting("OverlayMaxRows");
      if (maxRowsString == null || !int.TryParse(maxRowsString, out _savedMaxRows) || _savedMaxRows < 1 || _savedMaxRows > 10)
      {
        _savedMaxRows = 5;
      }

      // damage mode
      _savedDamageMode = ConfigUtil.GetSettingAsInteger("OverlayDamageMode");
      if (_savedDamageMode < 0 || _savedDamageMode > 100)
      {
        _savedDamageMode = 0;
      }

      UpdateDamageMode(_savedDamageMode);

      var list = PlayerManager.Instance.GetClassList();
      list.Insert(0, Resource.ANY_CLASS);
      classList.ItemsSource = list;

      // selected class
      var selectedClass = ConfigUtil.GetSetting("OverlaySelectedClass");
      if (selectedClass == null || !PlayerManager.Instance.GetClassList().Contains(selectedClass))
      {
        selectedClass = Resource.ANY_CLASS;
      }

      UpdateSelectedClass(selectedClass);
      _savedSelectedClass = _currentSelectedClass;

      // Hide other player names on overlay
      _savedHideOthers = ConfigUtil.IfSet("OverlayHideOtherPlayers");
      UpdateHideOthers(_savedHideOthers);

      // Hide/Show crit rate
      _savedShowCritRate = ConfigUtil.GetSettingAsInteger("OverlayEnableCritRate");
      UpdateShowCritRate(_savedShowCritRate);

      // Mini bars
      _savedMiniBars = ConfigUtil.IfSet("OverlayMiniBars");
      UpdateMiniBars(_savedMiniBars);

      UpdateMaxRows(_savedMaxRows);

      // Streamer Mode
      _savedStreamerMode = ConfigUtil.IfSet("OverlayStreamerMode");
      streamer.IsChecked = _savedStreamerMode;

      _currentShowDps = ConfigUtil.IfSet("OverlayShowingDps");
      dpsButton.Foreground = _currentShowDps ? ActiveBrush : InActiveBrush;
      tankButton.Foreground = !_currentShowDps ? ActiveBrush : InActiveBrush;

      _updateTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = new TimeSpan(0, 0, 0, 0, 1000) };
      _updateTimer.Tick += UpdateTimerTick;
      _ready = true;

      if (preview)
      {
        _updateTimer.Stop();
        ResizeMode = ResizeMode.CanResizeWithGrip;
        buttonsPanel.Visibility = Visibility.Visible;
        lineGrid.Visibility = Visibility.Visible;
        SetResourceReference(BorderBrushProperty, "PreviewBackgroundBrush");
        SetResourceReference(BackgroundProperty, "PreviewBackgroundBrush");
        border.Background = null;
        damageContent.Visibility = Visibility.Visible;
        controlPanel.Visibility = Visibility.Visible;
        SetMinHeight(true);
      }
      else
      {
        SetMinHeight(false);
        ResizeMode = ResizeMode.NoResize;
        buttonsPanel.Visibility = Visibility.Collapsed;
        lineGrid.Visibility = Visibility.Collapsed;
        controlPanel.Visibility = Visibility.Collapsed;
        BorderBrush = null;
        Background = null;
        border.SetResourceReference(Border.BackgroundProperty, "DamageOverlayBackgroundBrush");
        _updateTimer.Start();
      }
    }

    private async void UpdateTimerTick(object sender, EventArgs e)
    {
      // if turned off
      if (!ConfigUtil.IfSet("IsDamageOverlayEnabled"))
      {
        Close();
        return;
      }

      if (!_lastUpdateTask.IsCompleted) return;

      var maxRows = maxRowsList.SelectedIndex + 1;
      DamageOverlayStats damageOverlayStats = null;

      _lastUpdateTask = Task.Run(() =>
      {
        try
        {
          lock (StatsLock)
          {
            damageOverlayStats = _stats;
            var update = DamageStatsManager.ComputeOverlayStats(_stats == null, _currentDamageMode, maxRows, _currentSelectedClass);

            if (update == null)
            {
              if (_stats != null && (_currentDamageMode != 0 || (DateTime.Now.Ticks - _stats.LastUpdateTicks) >= DamageModeZeroTimeout))
              {
                damageOverlayStats = _stats = null;
              }
            }
            else
            {
              update.LastUpdateTicks = DateTime.Now.Ticks;
              damageOverlayStats = _stats = update;
            }
          }
        }
        catch (Exception)
        {
          // ignore for now
        }
      });

      await _lastUpdateTask;

      if (damageOverlayStats != null)
      {
        var currentTicks = DateTime.UtcNow.Ticks;
        if (_lastTopTicks == long.MinValue || (currentTicks - _lastTopTicks) > TopTimeout)
        {
          Topmost = true;
          _lastTopTicks = currentTicks;
        }

        if (damageOverlayStats.DamageStats != null)
        {
          LoadStats(damageContent.Children, damageOverlayStats.DamageStats);
        }

        if (damageOverlayStats.TankStats != null)
        {
          LoadStats(tankContent.Children, damageOverlayStats.TankStats);
        }

        if (_currentShowDps)
        {
          if (tankContent.Visibility != Visibility.Collapsed)
          {
            tankContent.Visibility = Visibility.Collapsed;
          }

          if (damageContent.Visibility != Visibility.Visible)
          {
            damageContent.Visibility = Visibility.Visible;
          }
        }
        else
        {
          if (tankContent.Visibility != Visibility.Visible)
          {
            tankContent.Visibility = Visibility.Visible;
          }

          if (damageContent.Visibility != Visibility.Collapsed)
          {
            damageContent.Visibility = Visibility.Collapsed;
          }
        }
      }
      else
      {
        foreach (var child in damageContent.Children)
        {
          if (child is DamageBar damageBar)
          {
            damageBar.Visibility = Visibility.Collapsed;
          }
        }

        foreach (var child in tankContent.Children)
        {
          if (child is DamageBar damageBar)
          {
            damageBar.Visibility = Visibility.Collapsed;
          }
        }

        damageContent.Visibility = Visibility.Collapsed;
        tankContent.Visibility = Visibility.Collapsed;
        controlPanel.Visibility = Visibility.Collapsed;
        thePopup.IsOpen = false;

        if (!DataManager.Instance.HasOverlayFights())
        {
          MainActions.CloseDamageOverlay(false);
        }
      }
    }

    private void LoadStats(UIElementCollection children, CombinedStats localStats)
    {
      for (var i = 0; i < children.Count; i++)
      {
        var statIndex = i;
        var damageBar = children[i] as DamageBar;
        if (localStats.StatsList.Count > statIndex)
        {
          var stat = localStats.StatsList[statIndex];
          var barPercent = (statIndex == 0) ? 100.0 : stat.Total / (double)localStats.StatsList[0].Total * 100.0;

          var playerName = ConfigUtil.PlayerName;
          var isMe = !string.IsNullOrEmpty(playerName) && stat.Name.StartsWith(playerName, StringComparison.OrdinalIgnoreCase) &&
            (playerName.Length >= stat.Name.Length || stat.Name[playerName.Length] == ' ');

          string name;
          string origName;
          if (_currentHideOthers && !isMe)
          {
            name = $"{stat.Rank}. Hidden Player";
            origName = "";
          }
          else
          {
            name = $"{stat.Rank}. {stat.Name}";
            origName = stat.OrigName;
          }

          var overrideColor = isMe ? "DamageOverlayHighlightBrush" : null;

          if (_currentShowCritRate > 0)
          {
            var critMods = new List<string>();
            if (_currentShowCritRate is 1 or 3 && isMe && DataManager.Instance.MyDoTCritRateMod is var doTCritRate and > 0)
            {
              critMods.Add($"DoT +{doTCritRate}");
            }

            if (_currentShowCritRate is 2 or 3 && isMe && DataManager.Instance.MyNukeCritRateMod is var nukeCritRate and > 0)
            {
              critMods.Add($"Nuke +{nukeCritRate}");
            }

            if (critMods.Count > 0)
            {
              name = $"{name}  [{string.Join(", ", critMods)}]";
            }
          }

          damageBar?.Update(origName, name, StatsUtil.FormatTotals(stat.Total),
          StatsUtil.FormatTotals(stat.Dps, 1), stat.TotalSeconds.ToString(CultureInfo.InvariantCulture), barPercent, overrideColor);

          if (damageBar?.Visibility == Visibility.Collapsed)
          {
            damageBar.Visibility = Visibility.Visible;
          }
        }
        else
        {
          if (damageBar?.Visibility == Visibility.Visible)
          {
            damageBar.Update("", "", "", "", "", 0);
            damageBar.Visibility = Visibility.Collapsed;
          }
        }
      }

      var titleBar = children[^1] as DamageBar;
      titleBar?.Update("", localStats.TargetTitle, StatsUtil.FormatTotals(localStats.RaidStats.Total),
        StatsUtil.FormatTotals(localStats.RaidStats.Dps, 1), localStats.RaidStats.TotalSeconds.ToString(CultureInfo.InvariantCulture), 0);

      if (titleBar?.Visibility == Visibility.Collapsed)
      {
        titleBar.Visibility = Visibility.Visible;
      }

      if (controlPanel.Visibility != Visibility.Visible)
      {
        controlPanel.Visibility = Visibility.Visible;
        thePopup.IsOpen = true;
      }
    }

    private void LoadTestData()
    {
      for (var i = 0; i < damageContent.Children.Count - 1; i++)
      {
        if (damageContent.Children[i] is DamageBar { } bar)
        {
          if (i == 0)
          {
            bar.Update(ConfigUtil.PlayerName, "Your Player Name", "120.5M", "100.1K", "123", 120 - (i * 10), "DamageOverlayHighlightBrush");
          }
          else
          {
            bar.Update(ConfigUtil.PlayerName, i + 1 + ". Example Player " + i, "120.5M", "100.1K", "123", 120 - (i * 10));
          }
        }
      }

      (damageContent.Children[^1] as DamageBar)?.Update("", "Example NPC", "500.2M", "490.5K", "456", 0);
    }

    private void CloseClick(object sender, RoutedEventArgs e) => MainActions.CloseDamageOverlay(false);

    private void SaveClick(object sender, RoutedEventArgs e)
    {
      var calcHeight = GetOverlayHeight();
      ConfigUtil.SetSetting("OverlayHeight", calcHeight);
      ConfigUtil.SetSetting("OverlayWidth", Width);
      _savedHeight = calcHeight;
      _savedWidth = Width;

      ConfigUtil.SetSetting("OverlayTop", Top);
      ConfigUtil.SetSetting("OverlayLeft", Left);
      _savedTop = Top;
      _savedLeft = Left;

      if (Application.Current.Resources["DamageOverlayFontSize"] is double fontSize)
      {
        ConfigUtil.SetSetting("OverlayFontSize", fontSize);
        _savedFontSize = (int)fontSize;
      }

      ConfigUtil.SetSetting("OverlayDamageMode", _currentDamageMode);
      _savedDamageMode = _currentDamageMode;

      ConfigUtil.SetSetting("OverlaySelectedClass", _currentSelectedClass);
      _savedSelectedClass = _currentSelectedClass;

      ConfigUtil.SetSetting("OverlayHideOtherPlayers", _currentHideOthers);
      _savedHideOthers = _currentHideOthers;

      ConfigUtil.SetSetting("OverlayEnableCritRate", _currentShowCritRate);
      _savedShowCritRate = _currentShowCritRate;

      ConfigUtil.SetSetting("OverlayMiniBars", miniBars.IsChecked == true);
      _savedMiniBars = miniBars.IsChecked == true;

      ConfigUtil.SetSetting("OverlayStreamerMode", streamer.IsChecked == true);
      _savedStreamerMode = streamer.IsChecked == true;

      ConfigUtil.SetSetting("OverlayMaxRows", maxRowsList.SelectedIndex + 1);
      _savedMaxRows = maxRowsList.SelectedIndex + 1;

      ConfigUtil.SetSetting("OverlayRankColor", progressBrush.Color.ToString(CultureInfo.CurrentCulture));
      _savedProgressColor = progressBrush.Color.ToString(CultureInfo.CurrentCulture);

      ConfigUtil.SetSetting("OverlayHighlightColor", highlightBrush.Color.ToString(CultureInfo.CurrentCulture));
      _savedHighlightColor = highlightBrush.Color.ToString(CultureInfo.CurrentCulture);

      saveButton.IsEnabled = false;
      cancelButton.IsEnabled = false;
      closeButton.IsEnabled = true;
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
      Height = _savedHeight;
      Width = _savedWidth;
      Top = _savedTop;
      Left = _savedLeft;

      if ((maxRowsList.SelectedIndex + 1) != _savedMaxRows)
      {
        UpdateMaxRows(_savedMaxRows);
      }

      _currentShowCritRate = _savedShowCritRate;
      UpdateShowCritRate(_currentShowCritRate);

      _currentHideOthers = _savedHideOthers;
      UpdateHideOthers(_currentHideOthers);

      _currentDamageMode = _savedDamageMode;
      UpdateDamageMode(_currentDamageMode);

      _currentSelectedClass = _savedSelectedClass;
      UpdateSelectedClass(_currentSelectedClass);

      UpdateFontSize(_savedFontSize);
      UpdateMiniBars(_savedMiniBars);
      streamer.IsChecked = _savedStreamerMode;

      UpdateProgressBrush(_savedProgressColor);
      UpdateHighlightBrush(_savedHighlightColor);

      saveButton.IsEnabled = false;
      cancelButton.IsEnabled = false;
      closeButton.IsEnabled = true;
    }

    private void SetMinHeight(bool isFixed)
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (isFixed)
        {
          var pos = cancelButton.TransformToAncestor(this).Transform(new Point(0, 0));
          Height = MinHeight = pos.Y + cancelButton.ActualHeight + 10;
        }
        else
        {
          MinHeight = 0;
        }
      }, DispatcherPriority.Background);
    }

    private double GetOverlayHeight()
    {
      var barHeight = (double)Application.Current.Resources["DamageOverlayBarHeight"]!;
      var pos = heightRectangle.TransformToAncestor(this).Transform(new Point(0, 0));
      return pos.Y - 2;
    }

    private void OverlayMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
      DragMove();

      if (_preview)
      {
        DataChanged();
      }
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
      // delay to avoid WindowSize event from saving new values
      _savedHeight = Height;
      _savedWidth = Width;
      _savedTop = Top;
      _savedLeft = Left;
    }

    private void SetWindowSizes(double height, double width, double top, double left)
    {
      if (width >= 0 && width <= SystemParameters.VirtualScreenWidth)
      {
        Width = width;
      }

      if (height >= 0 && height <= SystemParameters.VirtualScreenHeight)
      {
        Height = height;
      }
      else
      {
        // workaround for GetOverlayHeight needing to be called after init
        _ = Dispatcher.InvokeAsync(() => Height = _savedHeight = GetOverlayHeight());
      }

      if (top < int.MaxValue && top < SystemParameters.VirtualScreenHeight)
      {
        Top = top;
      }

      if (left < int.MaxValue && left >= SystemParameters.VirtualScreenLeft && left < SystemParameters.VirtualScreenWidth)
      {
        Left = left;
      }
    }

    private void SelectedClassChanged(object sender, SelectionChangedEventArgs e)
    {
      if (classList.SelectedIndex != -1 && e.RemovedItems.Count > 0)
      {
        UpdateSelectedClass(classList.SelectedItem.ToString());
        DataChanged();
      }
    }

    private void UpdateSelectedClass(string selectedClass)
    {
      _currentSelectedClass = selectedClass;
      if (classList.SelectedItem?.ToString() != selectedClass)
      {
        classList.SelectedItem = selectedClass;
      }
    }

    private void StreamerChecked(object sender, RoutedEventArgs e)
    {
      DataChanged();
    }

    private void MiniBarsChecked(object sender, RoutedEventArgs e)
    {
      if (miniBars.IsChecked != null)
      {
        if ((Application.Current.Resources["DamageOverlayBarHeight"]?.ToString() == "3" && miniBars.IsChecked == false) ||
          (Application.Current.Resources["DamageOverlayBarHeight"]?.ToString() != "3" && miniBars.IsChecked == true))
        {
          UpdateMiniBars(miniBars.IsChecked.Value);
          DataChanged();
          AdjustHeight();
        }
      }
    }

    private void UpdateMiniBars(bool isChecked)
    {
      var newHeight = 0.0;
      if (isChecked)
      {
        newHeight = 3.0;
      }
      else
      {
        if (fontList.SelectedValue is ComboBoxItem item && int.TryParse(item.Tag.ToString(), out var value))
        {
          switch (value)
          {
            case 10:
              newHeight = 19.0;
              break;
            case 12:
              newHeight = 21.0;
              break;
            case 14:
              newHeight = 22.0;
              break;
            case 16:
              newHeight = 24.0;
              break;
          }
        }
      }

      Application.Current.Resources["DamageOverlayBarHeight"] = newHeight;

      foreach (var child in damageContent.Children)
      {
        if (child is DamageBar damageBar)
        {
          damageBar.SetMiniBars(isChecked);
        }
      }

      foreach (var child in tankContent.Children)
      {
        if (child is DamageBar damageBar)
        {
          damageBar.SetMiniBars(isChecked);
        }
      }

      if (miniBars.IsChecked != isChecked)
      {
        miniBars.IsChecked = isChecked;
      }
    }

    private void HideOthersChecked(object sender, RoutedEventArgs e)
    {
      if (hideOthers != null)
      {
        UpdateHideOthers(hideOthers.IsChecked == true);
        DataChanged();
      }
    }

    private void UpdateHideOthers(bool isHideOthers)
    {
      _currentHideOthers = isHideOthers;

      var selectedIndex = _currentHideOthers ? 1 : 0;
      if (_currentHideOthers != hideOthers.IsChecked)
      {
        hideOthers.IsChecked = _currentHideOthers;
      }
    }

    private void ShowCritRateChanged(object sender, SelectionChangedEventArgs e)
    {
      if (showCritRate.SelectedIndex != -1 && e.RemovedItems.Count > 0)
      {
        UpdateShowCritRate(showCritRate.SelectedIndex);
        DataChanged();
      }
    }

    private void UpdateShowCritRate(int show)
    {
      _currentShowCritRate = show;

      var selectedIndex = show;
      if (showCritRate.SelectedIndex != selectedIndex)
      {
        showCritRate.SelectedIndex = selectedIndex;
      }
    }

    private void DamageModeChanged(object sender, SelectionChangedEventArgs e)
    {
      if (damageModeList.SelectedIndex != -1 && e.RemovedItems.Count > 0 &&
        damageModeList.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag.ToString(), out var value))
      {
        UpdateDamageMode(value);
        DataChanged();
      }
    }

    private void UpdateDamageMode(int damageMode)
    {
      _currentDamageMode = damageMode;
      if (damageModeList.SelectedItem == null || (damageModeList.SelectedItem is ComboBoxItem selected && !selected.Tag.Equals(damageMode.ToString())))
      {
        foreach (var item in damageModeList.Items)
        {
          if (item is ComboBoxItem comboBoxItem && comboBoxItem.Tag.Equals(damageMode.ToString()))
          {
            damageModeList.SelectedItem = comboBoxItem;
          }
        }
      }
    }

    private void MaxRowsChanged(object sender, SelectionChangedEventArgs e)
    {
      if (maxRowsList.SelectedIndex != -1 && e.RemovedItems.Count > 0)
      {
        UpdateMaxRows(maxRowsList.SelectedIndex + 1);
        DataChanged();
        AdjustHeight();
      }
    }

    private void UpdateProgressBrush(string colorString)
    {
      if (progressBrush.Color.ToString(CultureInfo.InvariantCulture) != colorString)
      {
        progressBrush.Brush = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString(colorString)! };
        progressBrush.Color = (Color)ColorConverter.ConvertFromString(colorString)!;
      }

      Application.Current.Resources["DamageOverlayProgressBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString(colorString)! };
    }

    private void SelectedProgressBrush(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      if (progressBrush.Brush.ToString(CultureInfo.InvariantCulture) != progressBrush.Color.ToString(CultureInfo.InvariantCulture))
      {
        UpdateProgressBrush(progressBrush.Color.ToString(CultureInfo.InvariantCulture));
        DataChanged();
      }
    }

    private void UpdateHighlightBrush(string colorString)
    {
      if (highlightBrush.Color.ToString(CultureInfo.InvariantCulture) != colorString)
      {
        highlightBrush.Brush = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString(colorString)! };
        highlightBrush.Color = (Color)ColorConverter.ConvertFromString(colorString)!;
      }

      Application.Current.Resources["DamageOverlayHighlightBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString(colorString)! };
    }

    private void SelectedHighlightBrush(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      if (highlightBrush.Brush.ToString(CultureInfo.InvariantCulture) != highlightBrush.Color.ToString(CultureInfo.InvariantCulture))
      {
        UpdateHighlightBrush(highlightBrush.Color.ToString(CultureInfo.InvariantCulture));
        DataChanged();
      }
    }

    private void UpdateMaxRows(int maxRows)
    {
      damageContent.Children.Clear();
      tankContent.Children.Clear();

      // damage bars
      for (var i = 0; i < maxRows; i++)
      {
        damageContent.Children.Add(new DamageBar("DamageOverlayDamageBrush", "DamageOverlayProgressBrush", true));
        tankContent.Children.Add(new DamageBar("DamageOverlayDamageBrush", "DamageOverlayProgressBrush", true));
      }

      // title bar
      damageContent.Children.Add(new DamageBar("DamageOverlayDamageBrush", "DamageOverlayProgressBrush", false));
      tankContent.Children.Add(new DamageBar("DamageOverlayDamageBrush", "DamageOverlayProgressBrush", false));

      var selectedIndex = maxRows - 1;
      if (maxRowsList.SelectedIndex != selectedIndex)
      {
        maxRowsList.SelectedIndex = selectedIndex;
      }

      UpdateMiniBars(miniBars.IsChecked == true);

      if (_preview)
      {
        LoadTestData();
      }
    }

    private void FontSizeChanged(object sender, SelectionChangedEventArgs e)
    {
      if (fontList.SelectedValue is ComboBoxItem item && e.RemovedItems.Count > 0 && int.TryParse(item.Tag.ToString(), out var value))
      {
        UpdateFontSize(value);
        DataChanged();
        AdjustHeight();
      }
    }

    private void UpdateFontSize(int fontSize)
    {
      var selectedIndex = -1;
      switch (fontSize)
      {
        case 10:
          selectedIndex = 0;
          break;
        case 12:
          selectedIndex = 1;
          break;
        case 14:
          selectedIndex = 2;
          break;
        case 16:
          selectedIndex = 3;
          break;
      }

      if (selectedIndex != -1)
      {
        Application.Current.Resources["DamageOverlayFontSize"] = (double)fontSize;
        UpdateColumnSizes(fontSize);

        if (fontList.SelectedIndex != selectedIndex)
        {
          fontList.SelectedIndex = selectedIndex;
        }
      }

      UpdateMiniBars(miniBars.IsChecked == true);
    }

    private void AdjustHeight()
    {
      Dispatcher.InvokeAsync(() =>
      {
        var needed = damageContent.ActualHeight + buttonsPanel.ActualHeight + 8;
        if (!needed.Equals(Height))
        {
          Height = needed;
          SetMinHeight(true);
        }
      }, DispatcherPriority.Background);
    }

    private void UpdateColumnSizes(int fontSize)
    {
      switch (fontSize)
      {
        case 10:
          Application.Current.Resources["DamageOverlayImageSize"] = 12.0;
          Application.Current.Resources["DamageOverlayDamageColDef1"] = new GridLength(50.0);
          Application.Current.Resources["DamageOverlayDamageColDef2"] = new GridLength(40.0);
          titleDamage.Margin = new Thickness(0, 5, 20, 0);
          titleDPS.Margin = new Thickness(0, 5, 19, 0);
          titleTime.Margin = new Thickness(0, 5, 6, 0);
          titleDamage.FontSize = 11;
          titleDPS.FontSize = 11;
          titleTime.FontSize = 11;
          dpsButton.FontSize = 11;
          tankButton.FontSize = 11;
          configButton.FontSize = 9;
          copyButton.FontSize = 9;
          resetButton.FontSize = 11;
          exitButton.FontSize = 9;
          controlPanel.Height = 27;
          thePopup.Height = 25;
          rect1.Height = 12;
          rect2.Height = 12;
          break;
        case 12:
          Application.Current.Resources["DamageOverlayImageSize"] = 15.0;
          Application.Current.Resources["DamageOverlayDamageColDef1"] = new GridLength(60.0);
          Application.Current.Resources["DamageOverlayDamageColDef2"] = new GridLength(45.0);
          titleDamage.Margin = new Thickness(0, 5, 27, 0);
          titleDPS.Margin = new Thickness(0, 5, 21, 0);
          titleTime.Margin = new Thickness(0, 5, 6, 0);
          titleDamage.FontSize = 13;
          titleDPS.FontSize = 13;
          titleTime.FontSize = 13;
          dpsButton.FontSize = 13;
          tankButton.FontSize = 13;
          configButton.FontSize = 11;
          copyButton.FontSize = 11;
          resetButton.FontSize = 13;
          exitButton.FontSize = 10;
          controlPanel.Height = 27;
          thePopup.Height = 27;
          rect1.Height = 14;
          rect2.Height = 14;
          break;
        case 14:
          Application.Current.Resources["DamageOverlayImageSize"] = 15.0;
          Application.Current.Resources["DamageOverlayDamageColDef1"] = new GridLength(70.0);
          Application.Current.Resources["DamageOverlayDamageColDef2"] = new GridLength(50.0);
          titleDamage.Margin = new Thickness(0, 5, 35, 0);
          titleDPS.Margin = new Thickness(0, 5, 21, 0);
          titleTime.Margin = new Thickness(0, 5, 6, 0);
          titleDamage.FontSize = 15;
          titleDPS.FontSize = 15;
          titleTime.FontSize = 15;
          dpsButton.FontSize = 15;
          tankButton.FontSize = 15;
          configButton.FontSize = 13;
          copyButton.FontSize = 13;
          resetButton.FontSize = 15;
          exitButton.FontSize = 11;
          controlPanel.Height = 29;
          thePopup.Height = 29;
          rect1.Height = 16;
          rect2.Height = 16;
          break;
        case 16:
          Application.Current.Resources["DamageOverlayImageSize"] = 17.0;
          Application.Current.Resources["DamageOverlayDamageColDef1"] = new GridLength(75.0);
          Application.Current.Resources["DamageOverlayDamageColDef2"] = new GridLength(55.0);
          titleDamage.Margin = new Thickness(0, 5, 34, 0);
          titleDPS.Margin = new Thickness(0, 5, 25, 0);
          titleTime.Margin = new Thickness(0, 5, 6, 0);
          titleDamage.FontSize = 17;
          titleDPS.FontSize = 17;
          titleTime.FontSize = 17;
          dpsButton.FontSize = 17;
          tankButton.FontSize = 17;
          configButton.FontSize = 15;
          copyButton.FontSize = 15;
          resetButton.FontSize = 16;
          exitButton.FontSize = 13;
          controlPanel.Height = 31;
          thePopup.Height = 31;
          rect1.Height = 18;
          rect2.Height = 18;
          break;
      }
    }

    private void ConfigureClick(object sender, RoutedEventArgs e)
    {
      lock (StatsLock)
      {
        _updateTimer.Stop();
      }

      Hide();
      MainActions.CloseDamageOverlay(true);
    }

    private void CopyClick(object sender, RoutedEventArgs e)
    {
      lock (StatsLock)
      {
        if (_currentShowDps)
        {
          if (_stats.DamageStats != null)
          {
            MainActions.AddAndCopyDamageParse(_stats.DamageStats, _stats.DamageStats.StatsList);
          }
        }
        else
        {
          if (_stats.TankStats != null)
          {
            MainActions.AddAndCopyTankParse(_stats.TankStats, _stats.TankStats.StatsList);
          }
        }
      }
    }

    private void DpsClick(object sender, RoutedEventArgs e)
    {
      dpsButton.Foreground = ActiveBrush;
      tankButton.Foreground = InActiveBrush;
      _currentShowDps = true;
      ConfigUtil.SetSetting("OverlayShowingDps", _currentShowDps);
      UpdateTimerTick(null, null);
    }

    private void TankClick(object sender, RoutedEventArgs e)
    {
      dpsButton.Foreground = InActiveBrush;
      tankButton.Foreground = ActiveBrush;
      _currentShowDps = false;
      ConfigUtil.SetSetting("OverlayShowingDps", _currentShowDps);
      UpdateTimerTick(null, null);
    }

    private void ResetClick(object sender, RoutedEventArgs e)
    {
      lock (StatsLock)
      {
        _stats = null;
        DataManager.Instance.ResetOverlayFights();
      }
    }

    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
      if (_preview)
      {
        if ((!double.IsNaN(_savedTop) && _savedTop != Top) || (_savedWidth > 0 && _savedWidth != Width))
        {
          DataChanged();
        }
      }
    }

    private void WindowClosing(object sender, CancelEventArgs e)
    {
      _updateTimer?.Stop();
      damageContent.Children.Clear();
      tankContent.Children.Clear();
    }

    private void BorderSizeChanged(object sender, SizeChangedEventArgs e)
    {
      thePopup.VerticalOffset += 1;
      thePopup.VerticalOffset -= 1;
    }

    private void DataChanged()
    {
      // not initialized
      if (saveButton != null && _ready)
      {
        if (!saveButton.IsEnabled)
        {
          saveButton.IsEnabled = true;
          closeButton.IsEnabled = false;
        }

        if (!cancelButton.IsEnabled)
        {
          cancelButton.IsEnabled = true;
          closeButton.IsEnabled = false;
        }
      }
    }

    // Possible workaround for data area passed to system call is too small
    protected override void OnSourceInitialized(EventArgs e)
    {
      base.OnSourceInitialized(e);
      var source = (HwndSource)PresentationSource.FromVisual(this)!;
      if (source != null)
      {
        source.AddHook(NativeMethods.BandAidHook); // Make sure this is hooked first. That ensures it runs last
        source.AddHook(NativeMethods.ProblemHook);

        if (!_preview)
        {
          // set to layered and topmost by xaml
          var exStyle = (int)NativeMethods.GetWindowLongPtr(source.Handle, (int)NativeMethods.GetWindowLongFields.GwlExstyle);

          // Add transparency and layered styles
          exStyle |= (int)NativeMethods.ExtendedWindowStyles.WsExLayered | (int)NativeMethods.ExtendedWindowStyles.WsExTransparent;

          if (!_savedStreamerMode)
          {
            // tool window to not show up in alt-tab
            exStyle |= (int)NativeMethods.ExtendedWindowStyles.WsExToolwindow;
          }

          NativeMethods.SetWindowLong(source.Handle, (int)NativeMethods.GetWindowLongFields.GwlExstyle, exStyle);
        }
      }
    }
  }
}
