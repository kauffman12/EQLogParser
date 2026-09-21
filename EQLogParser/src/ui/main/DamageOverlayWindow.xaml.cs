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
    private static DamageOverlayStatsBuilder _statsBuilder = new();
    private static DamageOverlayStats _stats;
    private readonly DispatcherTimer _updateTimer;
    private readonly bool _preview;
    private Task _lastUpdateTask = Task.CompletedTask;
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
    private bool _savedShowDamagePercent;
    private bool _savedStreamerMode;
    private bool _currentShowDps;
    private string _currentSelectedClass;
    private string _savedSelectedClass;
    private string _savedProgressColor;
    private string _savedHighlightColor;
    private DamageOverlayToolbarWindow _toolbarWindow;

    /*
     * This window's two spans on the heartbeat (PerfCounters, UiBeatMonitor). They are reported separately because they run on different
     * threads and fail for different reasons: "build" is the stats rebuild under StatsLock on a pool thread, which goes long when the log
     * reader is holding that lock, while "loadstats" is this window rewriting every bar on the UI thread — the one of the two that can
     * stop the combat numbers, and the reason the meter is instrumented at all.
     */
    private static readonly int BuildId = PerfCounters.Register("meter.build", uiThread: false);
    private static readonly int LoadStatsId = PerfCounters.Register("meter.loadstats");

    internal DamageOverlayWindow(bool preview = false, bool reset = false)
    {
      ThemeConfig.SetCurrentTheme(this);
      InitializeComponent();

      /* Every stall line says which windows were open, because the meter's once-a-second rebuild is a standing suspect for an overlay
         that froze and recovered by itself — and it keeps rebuilding whether or not anyone can see it. */
      IsVisibleChanged += (_, e) => UiBeatMonitor.NoteSurface("meter", (bool)e.NewValue);

      _preview = preview;

      if (reset)
      {
        _stats = null;
        _statsBuilder = new();
      }

      // dimensions
      var width = ConfigUtil.GetSettingAsDouble("OverlayWidth", 400);
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

      var list = EQDataStore.Instance.GetClassList();
      list.Insert(0, Resource.ANY_CLASS);

      // selected class
      var selectedClass = ConfigUtil.GetSetting("OverlaySelectedClass");
      if (selectedClass == null || !list.Contains(selectedClass))
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

      // Damage Percent
      _savedShowDamagePercent = ConfigUtil.IfSet("OverlayShowDamagePercent");
      UpdateShowDamagePercent(_savedShowDamagePercent);

      UpdateMaxRows(_savedMaxRows);

      // Streamer Mode
      _savedStreamerMode = ConfigUtil.IfSet("OverlayStreamerMode");
      _currentMaxRows = _savedMaxRows;
      _currentStreamerMode = _savedStreamerMode;

      _currentShowDps = ConfigUtil.IfSetOrElse("OverlayShowingDps", true);

      _updateTimer = UiUtil.CreateTimer(UpdateTimerTick, 1000, false, DispatcherPriority.DataBind);

      if (preview)
      {
        _updateTimer.Stop();
        ResizeMode = ResizeMode.CanResizeWithGrip;
        lineGrid.Visibility = Visibility.Visible;
        SetResourceReference(BorderBrushProperty, "PreviewBackgroundBrush");
        SetResourceReference(BackgroundProperty, "PreviewBackgroundBrush");
        border.Background = null;
        damageContent.Visibility = Visibility.Visible;
        controlPanel.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
        MinHeight = 40;
      }
      else
      {
        MinHeight = 0;
        ResizeMode = ResizeMode.NoResize;
        lineGrid.Visibility = Visibility.Collapsed;
        controlPanel.Visibility = Visibility.Collapsed;
        BorderBrush = null;
        Background = null;
        border.SetResourceReference(Border.BackgroundProperty, "DamageOverlayBackgroundBrush");
        _updateTimer.Start();
      }
    }

    // staged-config working set (mirrors _saved* until the setup window restages it) and its companion window
    private DamageMeterSettingsWindow _meterSettings;
    private int _currentFontSize = 12;
    private bool _currentMiniBars;
    private bool _currentShowDamagePercent;
    private bool _currentStreamerMode;
    private int _currentMaxRows = 5;
    private string _currentProgressColor = "#FF1D397E";
    private string _currentHighlightColor = "Gold";

    // Staged configuration API for DamageMeterSettingsWindow: preview applies a whole state without touching the ini,
    // commit previews then persists (the same keys the inline panel used to write), discard simply ends configure --
    // MainWindow reopens a live meter that reads the untouched values back off disk, so discarding restores itself.
    // Geometry is not staged: dragging and resizing the stage is the geometry editor, and commit captures whatever
    // rectangle the stage ended on.

    internal void PreviewMeterState(DamageMeterConfigState s)
    {
      // Only these three move the bottom edge; re-fitting on a color-picker stop would snap back a stage somebody
      // resized by hand in between.
      var refit = s.FontSize != _currentFontSize || s.MiniBars != _currentMiniBars || s.MaxRows != _currentMaxRows;

      UpdateFontSize(s.FontSize);
      UpdateDamageMode(s.DamageResetMode);
      UpdateSelectedClass(s.SelectedClass);
      UpdateHideOthers(s.HideOtherPlayers);
      UpdateShowCritRate(s.CritRateDisplay);
      _currentStreamerMode = s.StreamerMode;
      UpdateShowDamagePercent(s.ShowDamagePercent);
      UpdateMiniBars(s.MiniBars);
      UpdateProgressBrush(s.ProgressColor);
      UpdateHighlightBrush(s.HighlightColor);

      // The bar pool rebuilds only when the row count actually moved: a color-picker drag would otherwise recreate
      // and reseed every DamageBar on each intermediate stop.
      if (s.MaxRows != _currentMaxRows)
      {
        UpdateMaxRows(s.MaxRows);
      }

      if (refit)
      {
        AdjustHeight();
      }
    }

    // The stage hugs its content while configuring: rows, font size and thin bars all move the bottom edge, so each
    // preview re-fits the window (dispatched behind layout, so it measures the NEW bar heights). Live meters never
    // hear from it — their height is a saved setting and stays exactly where the player left it.
    private void AdjustHeight()
    {
      if (!_preview)
      {
        return;
      }

      Dispatcher.InvokeAsync(() =>
      {
        var needed = damageContent.ActualHeight + 8;
        if (!needed.Equals(Height))
        {
          Height = needed;
        }
      }, DispatcherPriority.Background);
    }

    internal void CommitMeterState(DamageMeterConfigState s)
    {
      PreviewMeterState(s);

      var calcHeight = GetOverlayHeight();
      ConfigUtil.SetSetting("OverlayHeight", calcHeight);
      ConfigUtil.SetSetting("OverlayWidth", Width);

      ConfigUtil.SetSetting("OverlayTop", Top);
      ConfigUtil.SetSetting("OverlayLeft", Left);

      ConfigUtil.SetSetting("OverlayFontSize", (double)s.FontSize);
      _savedFontSize = s.FontSize;

      ConfigUtil.SetSetting("OverlayDamageMode", s.DamageResetMode);
      _savedDamageMode = s.DamageResetMode;

      ConfigUtil.SetSetting("OverlaySelectedClass", s.SelectedClass);
      _savedSelectedClass = s.SelectedClass;

      ConfigUtil.SetSetting("OverlayHideOtherPlayers", s.HideOtherPlayers);
      _savedHideOthers = s.HideOtherPlayers;

      ConfigUtil.SetSetting("OverlayEnableCritRate", s.CritRateDisplay);
      _savedShowCritRate = s.CritRateDisplay;

      ConfigUtil.SetSetting("OverlayMiniBars", s.MiniBars);
      _savedMiniBars = s.MiniBars;

      ConfigUtil.SetSetting("OverlayShowDamagePercent", s.ShowDamagePercent);
      _savedShowDamagePercent = s.ShowDamagePercent;

      ConfigUtil.SetSetting("OverlayStreamerMode", s.StreamerMode);
      _savedStreamerMode = s.StreamerMode;

      ConfigUtil.SetSetting("OverlayMaxRows", s.MaxRows);
      _savedMaxRows = s.MaxRows;
      _currentMaxRows = s.MaxRows;

      ConfigUtil.SetSetting("OverlayRankColor", s.ProgressColor);
      _savedProgressColor = s.ProgressColor;

      ConfigUtil.SetSetting("OverlayHighlightColor", s.HighlightColor);
      _savedHighlightColor = s.HighlightColor;

      MainActions.CloseDamageOverlay(false);
    }

    internal void DiscardMeterSettings() => MainActions.CloseDamageOverlay(false);

    private async void UpdateTimerTick(object sender, EventArgs e)
    {
      // if turned off
      if (!ConfigUtil.IfSet("IsDamageOverlayEnabled"))
      {
        Close();
        return;
      }

      if (!_lastUpdateTask.IsCompleted) return;

      var maxRows = _currentMaxRows;
      DamageOverlayStats damageOverlayStats = null;

      _lastUpdateTask = Task.Run(() =>
      {
        var buildMark = PerfCounters.Begin(BuildId);

        try
        {
          lock (StatsLock)
          {
            damageOverlayStats = _stats;
            var update = _statsBuilder.Build(_stats == null, _currentDamageMode, maxRows, _currentSelectedClass);

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
        finally
        {
          PerfCounters.End(buildMark);
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

        /* One span over both lists: the player sees "the meter redrew", and the two calls are the same work on two panels. */
        var loadMark = PerfCounters.Begin(LoadStatsId);

        try
        {
          if (damageOverlayStats.DamageStats != null)
          {
            LoadStats(damageContent.Children, damageOverlayStats.DamageStats);
          }

          if (damageOverlayStats.TankStats != null)
          {
            LoadStats(tankContent.Children, damageOverlayStats.TankStats);
          }
        }
        finally
        {
          PerfCounters.End(loadMark);
        }

        if (Visibility != Visibility.Visible)
        {
          Visibility = Visibility.Visible;
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
        HideToolbarWindow();
        Visibility = Visibility.Collapsed;

        if (!FightManager.Instance.HasOverlayFights())
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
          string className;
          if (_currentHideOthers && !isMe)
          {
            name = $"{stat.Rank}. Hidden Player";
            className = "";
          }
          else
          {
            name = $"{stat.Rank}. {stat.Name}";
            className = stat.ClassName;
          }

          var overrideColor = isMe ? "DamageOverlayHighlightBrush" : null;

          if (_currentShowCritRate > 0)
          {
            var critMods = new List<string>();
            if (_currentShowCritRate is 1 or 3 && isMe && AdpsTracker.Instance.MyDoTCritRateMod is var doTCritRate and > 0)
            {
              critMods.Add($"DoT +{doTCritRate}");
            }

            if (_currentShowCritRate is 2 or 3 && isMe && AdpsTracker.Instance.MyNukeCritRateMod is var nukeCritRate and > 0)
            {
              critMods.Add($"Nuke +{nukeCritRate}");
            }

            if (critMods.Count > 0)
            {
              name = $"{name}  [{string.Join(", ", critMods)}]";
            }
          }

          var percent = (float)Math.Round((float)stat.Total / localStats.RaidStats.Total * 100, 1);
          damageBar?.Update(name, className, $"{percent}%", StatsUtil.FormatTotals(stat.Total),
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
            damageBar.Update("", "", "", "", "", "", 0);
            damageBar.Visibility = Visibility.Collapsed;
          }
        }
      }

      var titleBar = children[^1] as DamageBar;
      titleBar?.Update(localStats.TargetTitle, "", "", StatsUtil.FormatTotals(localStats.RaidStats.Total),
        StatsUtil.FormatTotals(localStats.RaidStats.Dps, 1), localStats.RaidStats.TotalSeconds.ToString(CultureInfo.InvariantCulture), 0);

      if (titleBar?.Visibility == Visibility.Collapsed)
      {
        titleBar.Visibility = Visibility.Visible;
      }

      if (controlPanel.Visibility != Visibility.Visible)
      {
        controlPanel.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(ShowToolbarWindow, DispatcherPriority.Loaded);
      }
    }

    private void LoadTestData()
    {
      var classList = EQDataStore.Instance.GetClassList();
      for (var i = 0; i < damageContent.Children.Count - 1; i++)
      {
        if (damageContent.Children[i] is DamageBar { } bar)
        {
          if (i == 0)
          {
            bar.Update("Your Player Name", classList[i], "5.2%", "120.5M", "100.1K", "123", 120 - (i * 10), "DamageOverlayHighlightBrush");
          }
          else
          {
            bar.Update(i + 1 + ". Example Player " + i, classList[i], "3.1%", "120.5M", "100.1K", "123", 120 - (i * 10));
          }
        }
      }

      (damageContent.Children[^1] as DamageBar)?.Update("Example NPC", "", "", "500.2M", "490.5K", "456", 0);
    }

    private void CloseClick(object sender, RoutedEventArgs e) => MainActions.CloseDamageOverlay(false);

    private double GetOverlayHeight()
    {
      var pos = heightRectangle.TransformToAncestor(this).Transform(new Point(0, 0));
      return pos.Y - 2;
    }

    private void OverlayMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
      DragMove();
    }

    private void WindowContentRendered(object sender, EventArgs e)
    {
      // the stage is laid out and parked where it will stay: only now does its companion panel exist, because docking
      // reads the measured rectangle
      if (_preview && _meterSettings is null)
      {
        _meterSettings = new DamageMeterSettingsWindow(this) { Owner = this };
        _meterSettings.Show();
      }
    }

    private void SetWindowSizes(double height, double width, double top, double left)
    {
      // Size guards (keep your existing rules)
      if (width > 0 && width <= SystemParameters.VirtualScreenWidth)
        Width = width;

      if (height > 0 && height <= SystemParameters.VirtualScreenHeight)
        Height = height;

      // Virtual desktop bounds (multi-monitor aware)
      var vLeft = SystemParameters.VirtualScreenLeft;
      var vTop = SystemParameters.VirtualScreenTop;
      var overlapsH = Overlaps(left, width, vLeft, SystemParameters.VirtualScreenWidth);
      var overlapsV = Overlaps(top, height, vTop, SystemParameters.VirtualScreenHeight);

      // Apply positions:
      // - Allow negative (or > right/bottom) if there's any overlap.
      // - If completely offscreen on that axis, snap to 0 (your preference).
      Left = overlapsH ? left : 0;
      Top = overlapsV ? top : 0;

      // Helper: does the proposed rect overlap the virtual screen at all?
      static bool Overlaps(double aStart, double aLen, double bStart, double bLen)
      {
        var aEnd = aStart + aLen;
        var bEnd = bStart + bLen;
        return aLen > 0 && bLen > 0 && aStart < bEnd && aEnd > bStart; // strict overlap
      }
    }

    private void UpdateSelectedClass(string selectedClass)
    {
      _currentSelectedClass = selectedClass;
    }

    private void UpdateShowDamagePercent(bool isChecked)
    {
      foreach (var child in damageContent.Children)
      {
        if (child is DamageBar damageBar)
        {
          damageBar.SetShowDamagePercent(isChecked);
        }
      }

      foreach (var child in tankContent.Children)
      {
        if (child is DamageBar damageBar)
        {
          damageBar.SetShowDamagePercent(isChecked);
        }
      }

      _currentShowDamagePercent = isChecked;
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
        newHeight = _currentFontSize switch
        {
          10 => 19.0,
          12 => 21.0,
          14 => 22.0,
          _ => 24.0
        };
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

      _currentMiniBars = isChecked;
    }

    private void UpdateHideOthers(bool isHideOthers)
    {
      _currentHideOthers = isHideOthers;
    }

    private void UpdateShowCritRate(int show)
    {
      _currentShowCritRate = show;
    }

    private void UpdateDamageMode(int damageMode)
    {
      _currentDamageMode = damageMode;
    }

    private void UpdateProgressBrush(string colorString)
    {
      _currentProgressColor = colorString;
      Application.Current.Resources["DamageOverlayProgressBrush"] = UiUtil.GetBrush(colorString);
    }

    private void UpdateHighlightBrush(string colorString)
    {
      _currentHighlightColor = colorString;
      Application.Current.Resources["DamageOverlayHighlightBrush"] = UiUtil.GetBrush(colorString);
    }

    private void UpdateMaxRows(int maxRows)
    {
      List<UIElement> damage = [];
      List<UIElement> tank = [];

      // damage bars
      for (var i = 0; i < maxRows; i++)
      {
        damage.Add(new DamageBar("DamageOverlayDamageBrush", "DamageOverlayProgressBrush", true));
        tank.Add(new DamageBar("DamageOverlayDamageBrush", "DamageOverlayProgressBrush", true));
      }

      // title bar
      damage.Add(new DamageBar("DamageOverlayDamageBrush", "DamageOverlayProgressBrush", false));
      tank.Add(new DamageBar("DamageOverlayDamageBrush", "DamageOverlayProgressBrush", false));

      using (Dispatcher.CurrentDispatcher.DisableProcessing())
      {
        damageContent.Children.Clear();
        tankContent.Children.Clear();

        foreach (var element in damage)
        {
          damageContent.Children.Add(element);
        }

        foreach (var element in tank)
        {
          tankContent.Children.Add(element);
        }
      }

      _currentMaxRows = maxRows;

      UpdateShowDamagePercent(_currentShowDamagePercent);
      UpdateMiniBars(_currentMiniBars);

      if (_preview)
      {
        LoadTestData();
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
        _currentFontSize = fontSize;
        Application.Current.Resources["DamageOverlayFontSize"] = (double)fontSize;
        UpdateColumnSizes(fontSize);
      }

      UpdateMiniBars(_currentMiniBars);

      _toolbarWindow?.UpdateFontSize(fontSize);
      ScheduleToolbarPosition();
    }

    private void UpdateColumnSizes(int fontSize)
    {
      switch (fontSize)
      {
        case 10:
          Application.Current.Resources["DamageOverlayImageSize"] = 13.0;
          Application.Current.Resources["DamageOverlayDamageColDef1"] = new GridLength(50.0);
          Application.Current.Resources["DamageOverlayDamageColDef2"] = new GridLength(40.0);
          titlePercent.Margin = new Thickness(0, 5, 20, 0);
          titleDamage.Margin = new Thickness(0, 5, 18, 0);
          titleDPS.Margin = new Thickness(0, 5, 19, 0);
          titleTime.Margin = new Thickness(0, 5, 6, 0);
          titlePercent.FontSize = 11;
          titleDamage.FontSize = 11;
          titleDPS.FontSize = 11;
          titleTime.FontSize = 11;
          controlPanel.Height = 27;
          break;
        case 12:
          Application.Current.Resources["DamageOverlayImageSize"] = 14.0;
          Application.Current.Resources["DamageOverlayDamageColDef1"] = new GridLength(60.0);
          Application.Current.Resources["DamageOverlayDamageColDef2"] = new GridLength(45.0);
          titlePercent.Margin = new Thickness(0, 5, 22, 0);
          titleDamage.Margin = new Thickness(0, 5, 25, 0);
          titleDPS.Margin = new Thickness(0, 5, 21, 0);
          titleTime.Margin = new Thickness(0, 5, 6, 0);
          titlePercent.FontSize = 13;
          titleDamage.FontSize = 13;
          titleDPS.FontSize = 13;
          titleTime.FontSize = 13;
          controlPanel.Height = 27;
          break;
        case 14:
          Application.Current.Resources["DamageOverlayImageSize"] = 15.0;
          Application.Current.Resources["DamageOverlayDamageColDef1"] = new GridLength(70.0);
          Application.Current.Resources["DamageOverlayDamageColDef2"] = new GridLength(50.0);
          titlePercent.Margin = new Thickness(0, 5, 28, 0);
          titleDamage.Margin = new Thickness(0, 5, 33, 0);
          titleDPS.Margin = new Thickness(0, 5, 21, 0);
          titleTime.Margin = new Thickness(0, 5, 6, 0);
          titlePercent.FontSize = 15;
          titleDamage.FontSize = 15;
          titleDPS.FontSize = 15;
          titleTime.FontSize = 15;
          controlPanel.Height = 29;
          break;
        case 16:
          Application.Current.Resources["DamageOverlayImageSize"] = 16.0;
          Application.Current.Resources["DamageOverlayDamageColDef1"] = new GridLength(75.0);
          Application.Current.Resources["DamageOverlayDamageColDef2"] = new GridLength(55.0);
          titlePercent.Margin = new Thickness(0, 5, 28, 0);
          titleDamage.Margin = new Thickness(0, 5, 32, 0);
          titleDPS.Margin = new Thickness(0, 5, 25, 0);
          titleTime.Margin = new Thickness(0, 5, 6, 0);
          titlePercent.FontSize = 17;
          titleDamage.FontSize = 17;
          titleDPS.FontSize = 17;
          titleTime.FontSize = 17;
          controlPanel.Height = 31;
          break;
      }
    }

    private void ConfigureClick(object sender, RoutedEventArgs e)
    {
      lock (StatsLock)
      {
        _updateTimer.Stop();
      }

      HideToolbarWindow();
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
      _currentShowDps = true;
      _toolbarWindow?.SetShowingDps(true);
      ConfigUtil.SetSetting("OverlayShowingDps", _currentShowDps);
      UpdateTimerTick(null, null);
    }

    private void TankClick(object sender, RoutedEventArgs e)
    {
      _currentShowDps = false;
      _toolbarWindow?.SetShowingDps(false);
      ConfigUtil.SetSetting("OverlayShowingDps", _currentShowDps);
      UpdateTimerTick(null, null);
    }

    private void FullResetClick(object sender, RoutedEventArgs e)
    {
      lock (StatsLock)
      {
        _stats = null;
        _statsBuilder = new();
        FightManager.Instance.ResetOverlayFights();
      }
    }

    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
      ScheduleToolbarPosition();
    }

    private void WindowClosing(object sender, CancelEventArgs e)
    {
      _updateTimer?.Stop();
      damageContent.Children.Clear();
      tankContent.Children.Clear();

      if (_toolbarWindow is not null)
      {
        _toolbarWindow.Owner = null;
        _toolbarWindow.Close();
        _toolbarWindow = null;
      }
    }

    private void EnsureToolbarWindow()
    {
      if (_preview || _toolbarWindow is not null)
      {
        return;
      }

      _toolbarWindow = new DamageOverlayToolbarWindow { Owner = this };

      _toolbarWindow.ConfigureRequested += (_, _) => ConfigureClick(null, null);
      _toolbarWindow.CopyRequested += (_, _) => CopyClick(null, null);
      _toolbarWindow.ResetRequested += (_, _) => FullResetClick(null, null);
      _toolbarWindow.CloseRequested += (_, _) => CloseClick(null, null);
      _toolbarWindow.DpsRequested += (_, _) => DpsClick(null, null);
      _toolbarWindow.TankRequested += (_, _) => TankClick(null, null);

      _toolbarWindow.SetShowingDps(_currentShowDps);
      _toolbarWindow.UpdateFontSize(_savedFontSize);
    }

    private void ShowToolbarWindow()
    {
      if (_preview || controlPanel.Visibility != Visibility.Visible)
      {
        return;
      }

      EnsureToolbarWindow();

      if (_toolbarWindow is null)
      {
        return;
      }

      if (!_toolbarWindow.IsVisible)
      {
        _toolbarWindow.Show();
      }

      ScheduleToolbarPosition();
    }

    private void HideToolbarWindow()
    {
      if (_toolbarWindow?.IsVisible == true)
      {
        _toolbarWindow.Hide();
      }
    }

    private void PositionToolbarWindow()
    {
      if (_preview ||
          _toolbarWindow is null ||
          controlPanel.Visibility != Visibility.Visible ||
          !IsVisible)
      {
        return;
      }

      if (PresentationSource.FromVisual(_toolbarWindow) is not HwndSource toolbarSource ||
          toolbarSource.CompositionTarget == null)
      {
        return;
      }

      var screenPixels = controlPanel.PointToScreen(new Point(0, 0));

      var screenDips =
          toolbarSource.CompositionTarget.TransformFromDevice.Transform(screenPixels);

      _toolbarWindow.Left = screenDips.X;
      _toolbarWindow.Top = screenDips.Y;
    }

    private void ScheduleToolbarPosition()
    {
      if (_preview)
      {
        return;
      }

      // Background fires after layout + render are complete,
      // ensuring both windows have settled to their final positions.
      Dispatcher.BeginInvoke(PositionToolbarWindow, DispatcherPriority.Background);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
      base.OnLocationChanged(e);
      ScheduleToolbarPosition();
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

        // set to layered and topmost by xaml
        var exStyle = (int)NativeMethods.GetWindowLongPtr(source.Handle, (int)NativeMethods.GetWindowLongFields.GwlExstyle);

        if (!_preview)
        {
          // Add transparency and layered styles
          exStyle |= (int)NativeMethods.ExtendedWindowStyles.WsExLayered | (int)NativeMethods.ExtendedWindowStyles.WsExTransparent;

          if (!_savedStreamerMode)
          {
            // tool window to not show up in alt-tab
            exStyle |= (int)NativeMethods.ExtendedWindowStyles.WsExToolwindow;
          }
        }
        else
        {
          exStyle |= (int)NativeMethods.ExtendedWindowStyles.WsExToolwindow | (int)NativeMethods.ExtendedWindowStyles.WsExNoActive;
        }

        NativeMethods.SetWindowLong(source.Handle, (int)NativeMethods.GetWindowLongFields.GwlExstyle, exStyle);
      }
    }
  }
}
