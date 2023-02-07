using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DamageOverlayWindow.xaml
  /// </summary>
  public partial class DamageOverlayWindow : Window
  {
    private static object StatsLock = new object();
    private readonly DispatcherTimer UpdateTimer;
    private CombinedStats Stats = null;
    private bool Preview = false;
    private double SavedHeight;
    private double SavedWidth;
    private double SavedTop = double.NaN;
    private double SavedLeft;
    private int SavedFontSize;
    private int SavedMaxRows;
    private string CurrentSelectedClass;
    private string SavedSelectedClass;
    private int CurrentDamageMode;
    private int SavedDamageMode;
    private bool CurrentHideOthers;
    private bool SavedHideOthers;
    private bool CurrentShowCritRate;
    private bool SavedShowCritRate;
    private bool SavedMiniBars;
    private string SavedProgressColor;

    internal DamageOverlayWindow(bool preview = false)
    {
      InitializeComponent();
      MainActions.SetTheme(this, MainWindow.CurrentTheme);
      Preview = preview;

      // dimensions
      var width = ConfigUtil.GetSetting("OverlayWidth");
      var height = ConfigUtil.GetSetting("OverlayHeight");
      var top = ConfigUtil.GetSetting("OverlayTop");
      var left = ConfigUtil.GetSetting("OverlayLeft");
      SetWindowSizes(height, width, top, left);

      // fonts
      var fontSizeString = ConfigUtil.GetSetting("OverlayFontSize");
      if (fontSizeString == null || !int.TryParse(fontSizeString, out SavedFontSize) || (SavedFontSize != 12 && SavedFontSize != 14 && SavedFontSize != 16))
      {
        SavedFontSize = 14;
      }
      UpdateFontSize(SavedFontSize);

      // color
      SavedProgressColor = ConfigUtil.GetSetting("OverlayRankColor");
      if (SavedProgressColor == null || ColorConverter.ConvertFromString(SavedProgressColor) == null)
      {
        SavedProgressColor = "#FF1D397E";
      }

      UpdateProgressBrush(SavedProgressColor);

      // Max Rows
      var maxRowsString = ConfigUtil.GetSetting("OverlayMaxRows");
      if (maxRowsString == null || !int.TryParse(maxRowsString, out SavedMaxRows) || (SavedMaxRows < 5 && SavedMaxRows > 10))
      {
        SavedMaxRows = 5;
      }
      UpdateMaxRows(SavedMaxRows);

      // damage mode
      SavedDamageMode = ConfigUtil.GetSettingAsInteger("OverlayDamageMode");
      if (SavedDamageMode != 0 && SavedDamageMode != 30 && SavedDamageMode != 40 && SavedDamageMode != 50 && SavedDamageMode != 60)
      {
        SavedDamageMode = 0;
      }
      UpdateDamageMode(SavedDamageMode);

      var list = PlayerManager.Instance.GetClassList();
      list.Insert(0, EQLogParser.Resource.ANY_CLASS);
      classList.ItemsSource = list;    
      
      // selected class
      string selectedClass = ConfigUtil.GetSetting("OverlaySelectedClass");
      if (selectedClass == null || !PlayerManager.Instance.GetClassList().Contains(selectedClass))
      {
        selectedClass = EQLogParser.Resource.ANY_CLASS;
      }

      UpdateSelectedClass(selectedClass);
      SavedSelectedClass = CurrentSelectedClass;

      // Hide other player names on overlay
      SavedHideOthers = ConfigUtil.IfSet("OverlayHideOtherPlayers");
      UpdateHideOthers(SavedHideOthers);

      // Hide/Show crit rate
      SavedShowCritRate = ConfigUtil.IfSet("OverlayShowCritRate");
      UpdateShowCritRate(SavedShowCritRate);

      // Mini bars
      SavedMiniBars = ConfigUtil.IfSet("OverlayMiniBars");
      UpdateMiniBars(SavedMiniBars);

      UpdateTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = new TimeSpan(0, 0, 0, 0, 1000) };
      UpdateTimer.Tick += UpdateTimerTick;

      if (preview)
      {
        UpdateTimer.Stop();
        ResizeMode = ResizeMode.CanResizeWithGrip;
        buttonsPanel.Visibility = Visibility.Visible;
        SetResourceReference(Window.BorderBrushProperty, "PreviewBackgroundBrush");
        SetResourceReference(Window.BackgroundProperty, "PreviewBackgroundBrush");
        border.Background = null;
        LoadTestData(true);
      }
      else
      {
        ResizeMode = ResizeMode.NoResize;
        buttonsPanel.Visibility = Visibility.Collapsed;
        this.BorderBrush = null;
        this.Background = null;
        border.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "DamageOverlayBackgroundBrush");
        UpdateTimer.Start();
      }
    }

    private void UpdateTimerTick(object sender, EventArgs e)
    {
      // if turned off
      if (!ConfigUtil.IfSet("IsDamageOverlayEnabled"))
      {
        Close();
        return;
      }

      int maxRows = maxRowsList.SelectedIndex + 5;

      CombinedStats localStats;
      lock (StatsLock)
      {
        localStats = Stats = DamageStatsManager.ComputeOverlayStats(Stats == null, CurrentDamageMode, maxRows, CurrentSelectedClass);
      }

      if (localStats != null)
      {
        for (int i = 0; i < content.Children.Count; i++)
        {
          var statIndex = i;
          var damageBar = content.Children[i] as DamageBar;
          if (localStats.StatsList.Count > statIndex)
          {
            var stat = localStats.StatsList[statIndex];
            var time = new TimeSpan(0, 0, (int)stat.TotalSeconds);
            var timeString = string.Format("{0:D2}:{1:D2}", time.Minutes, time.Seconds);
            var barPercent = (statIndex == 0) ? 100.0 : (stat.Total / (double)localStats.StatsList[0].Total) * 100.0;

            string playerName = ConfigUtil.PlayerName;
            var isMe = !string.IsNullOrEmpty(playerName) && stat.Name.StartsWith(playerName, StringComparison.OrdinalIgnoreCase) &&
              (playerName.Length >= stat.Name.Length || stat.Name[playerName.Length] == ' ');

            string name;
            string origName;
            if (CurrentHideOthers && !isMe)
            {
              name = string.Format("{0}. Hidden Player", stat.Rank);
              origName ="";
            }
            else
            {
              name = string.Format("{0}. {1}", stat.Rank, stat.Name);
              origName = stat.OrigName;
            }

            if (CurrentShowCritRate)
            {
              var critMods = new List<string>();

              if (isMe && PlayerManager.Instance.IsDoTClass(stat.ClassName) && DataManager.Instance.MyDoTCritRateMod is uint doTCritRate && doTCritRate > 0)
              {
                critMods.Add(string.Format("DoT +{0}", doTCritRate));
              }

              if (isMe && DataManager.Instance.MyNukeCritRateMod is uint nukeCritRate && nukeCritRate > 0)
              {
                critMods.Add(string.Format("Nuke +{0}", nukeCritRate));
              }

              if (critMods.Count > 0)
              {
                name = string.Format("{0}  [{1}]", name, string.Join(", ", critMods));
              }
            }

            damageBar.Update(origName, name, StatsUtil.FormatTotals(stat.Total, 2),
            StatsUtil.FormatTotals(stat.DPS, 2), timeString, barPercent);

            if (damageBar.Visibility == Visibility.Collapsed)
            {
              damageBar.Visibility = Visibility.Visible;
            }
          }
          else
          {
            if (damageBar.Visibility == Visibility.Visible)
            {
              damageBar.Update("", "", "", "", "", 0);
              damageBar.Visibility = Visibility.Collapsed;
            }
          }
        }

        var titleBar = content.Children[content.Children.Count - 1] as DamageBar;
        var titleTime = new TimeSpan(0, 0, (int)localStats.RaidStats.TotalSeconds);
        var titleTimeString = string.Format("{0:D2}:{1:D2}", titleTime.Minutes, titleTime.Seconds);
        titleBar.Update("", localStats.TargetTitle, StatsUtil.FormatTotals(localStats.RaidStats.Total, 2),
          StatsUtil.FormatTotals(localStats.RaidStats.DPS, 2), titleTimeString, 0);

        if (titleBar.Visibility == Visibility.Collapsed)
        {
          titleBar.Visibility = Visibility.Visible;
        }

        if (controlPanel.Visibility != Visibility.Visible)
        {
          controlPanel.Visibility = Visibility.Visible;
          thePopup.IsOpen = true;
        }
      }
      else
      {
        foreach (var child in content.Children)
        {
          if (child is DamageBar damageBar)
          {
            damageBar.Visibility = Visibility.Collapsed;
          }
        }

        controlPanel.Visibility = Visibility.Collapsed;
        thePopup.IsOpen = false;

        if (!DataManager.Instance.HasOverlayFights())
        {
          ((MainWindow)Application.Current.MainWindow).CloseDamageOverlay();
        }
      }
    }

    private void LoadTestData(bool load)
    {
      for (int i = 0; i < content.Children.Count - 1; i++)
      {
        if (load)
        {
          (content.Children[i] as DamageBar).Update(ConfigUtil.PlayerName, i + 1 + ". Example Player " + i, "120.50M", "100.10K", "01:02", 120 - (i * 10));
        }
        else
        {
          (content.Children[i] as DamageBar).Update("", "", "", "", "", 0);
          (content.Children[i] as DamageBar).Visibility = Visibility.Collapsed;
        }
      }

      if (load)
      {
        (content.Children[content.Children.Count - 1] as DamageBar).Update("", "Example NPC", "500.20M", "490.50K", "03:02", 0);
      }
      else
      {
        (content.Children[content.Children.Count - 1] as DamageBar).Update("", "", "", "", "", 0);
        (content.Children[content.Children.Count - 1] as DamageBar).Visibility = Visibility.Collapsed;
      }
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
      this.Hide();
      ((MainWindow)Application.Current.MainWindow).CloseDamageOverlay();
      ((MainWindow)Application.Current.MainWindow).OpenDamageOverlayIfEnabled();
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
      ConfigUtil.SetSetting("OverlayHeight", this.Height.ToString());
      ConfigUtil.SetSetting("OverlayWidth", this.Width.ToString());
      SavedHeight = this.Height;
      SavedWidth = this.Width;

      ConfigUtil.SetSetting("OverlayTop", this.Top.ToString());
      ConfigUtil.SetSetting("OverlayLeft", this.Left.ToString());
      SavedTop = this.Top;
      SavedLeft = this.Left;

      if (Application.Current.Resources["DamageOverlayFontSize"] is double fontSize)
      {
        ConfigUtil.SetSetting("OverlayFontSize", fontSize.ToString());
        SavedFontSize = (int)fontSize;
      }

      ConfigUtil.SetSetting("OverlayDamageMode", CurrentDamageMode.ToString());
      SavedDamageMode = CurrentDamageMode;

      ConfigUtil.SetSetting("OverlaySelectedClass", CurrentSelectedClass);
      SavedSelectedClass = CurrentSelectedClass;

      ConfigUtil.SetSetting("OverlayHideOtherPlayers", CurrentHideOthers.ToString());
      SavedHideOthers = CurrentHideOthers;

      ConfigUtil.SetSetting("OverlayShowCritRate", CurrentShowCritRate.ToString());
      SavedShowCritRate = CurrentShowCritRate;

      ConfigUtil.SetSetting("OverlayMiniBars", miniBars.IsChecked.Value.ToString());
      SavedMiniBars = miniBars.IsChecked.Value;

      ConfigUtil.SetSetting("OverlayMaxRows", (maxRowsList.SelectedIndex + 5).ToString());
      SavedMaxRows = (maxRowsList.SelectedIndex + 5);

      ConfigUtil.SetSetting("OverlayRankColor", progressBrush.Color.ToString());
      SavedProgressColor = progressBrush.Color.ToString();

      saveButton.IsEnabled = false;
      cancelButton.IsEnabled = false;
      closeButton.IsEnabled = true;
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
      this.Height = SavedHeight;
      this.Width = SavedWidth;
      this.Top = SavedTop;
      this.Left = SavedLeft;

      if ((maxRowsList.SelectedIndex + 5) != SavedMaxRows)
      {
        UpdateMaxRows(SavedMaxRows);
      }

      CurrentShowCritRate = SavedShowCritRate;
      UpdateShowCritRate(CurrentShowCritRate);

      CurrentHideOthers = SavedHideOthers;
      UpdateHideOthers(CurrentHideOthers);

      CurrentDamageMode = SavedDamageMode;
      UpdateDamageMode(CurrentDamageMode);

      CurrentSelectedClass = SavedSelectedClass;
      UpdateSelectedClass(CurrentSelectedClass);

      UpdateFontSize(SavedFontSize);
      UpdateMiniBars(SavedMiniBars);
      UpdateProgressBrush(SavedProgressColor);

      saveButton.IsEnabled = false;
      cancelButton.IsEnabled = false;
      closeButton.IsEnabled = true;
    }

    private void OverlayMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
      this.DragMove();

      if (Preview)
      {
        DataChanged();
      }
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
      // delay to avoid WindowSize event from saving new values
      SavedHeight = this.Height;
      SavedWidth = this.Width;
      SavedTop = this.Top;
      SavedLeft = this.Left;
    }

    private void SetWindowSizes(string height, string width, string top, string left)
    {
      if (width != null && double.TryParse(width, out double dvalue) && !double.IsNaN(dvalue))
      {
        Width = dvalue;
      }

      if (height != null && double.TryParse(height, out dvalue) && !double.IsNaN(dvalue))
      {
         Height = dvalue;
      }

      if (top != null && double.TryParse(top, out dvalue) && !double.IsNaN(dvalue))
      {
        Top = dvalue;
      }

      if (left != null && double.TryParse(left, out dvalue) && !double.IsNaN(dvalue))
      {
        var test = dvalue;
        if (test >= SystemParameters.VirtualScreenLeft && test < SystemParameters.VirtualScreenWidth)
        {
          Left = test;
        }
        else
        {
          Left = dvalue;
        }
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
      CurrentSelectedClass = selectedClass;
      if (classList.SelectedItem?.ToString() != selectedClass)
      {
        classList.SelectedItem = selectedClass;
      }
    }

    private void MiniBarsChecked(object sender, RoutedEventArgs e)
    {
      if (miniBars.IsChecked != null)
      {
        if ((Application.Current.Resources["DamageOverlayBarHeight"].ToString() == "3" && miniBars.IsChecked == false) ||
          (Application.Current.Resources["DamageOverlayBarHeight"].ToString() != "3" && miniBars.IsChecked == true))
        {
          UpdateMiniBars(miniBars.IsChecked.Value);
          DataChanged();
        }
      }
    }

    private void UpdateMiniBars(bool isChecked)
    {
      double newHeight = 0.0;
      if (isChecked)
      {
        newHeight = 3.0;
      }
      else
      {
        if (fontList.SelectedValue is ComboBoxItem item && int.TryParse(item.Tag.ToString(), out int value))
        {
          switch (value)
          {
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

      foreach (var child in content.Children)
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

    private void ShowNamesChanged(object sender, SelectionChangedEventArgs e)
    {
      if (showNames.SelectedIndex != -1 && e.RemovedItems.Count > 0)
      {
        UpdateHideOthers(showNames.SelectedIndex == 1);
        DataChanged();
      }
    }

    private void UpdateHideOthers(bool hideOthers)
    {
      CurrentHideOthers = hideOthers;

      int selectedIndex = CurrentHideOthers ? 1 : 0;
      if (showNames.SelectedIndex != selectedIndex)
      {
        showNames.SelectedIndex = selectedIndex;
      }
    }

    private void ShowCritRateChanged(object sender, SelectionChangedEventArgs e)
    {
      if (showCritRate.SelectedIndex != -1 && e.RemovedItems.Count > 0)
      {
        UpdateShowCritRate(showCritRate.SelectedIndex == 1);
        DataChanged();
      }
    }

    private void UpdateShowCritRate(bool show)
    {
      CurrentShowCritRate = show;

      int selectedIndex = CurrentShowCritRate ? 1 : 0;
      if (showCritRate.SelectedIndex != selectedIndex)
      {
        showCritRate.SelectedIndex = selectedIndex;
      }
    }

    private void DamageModeChanged(object sender, SelectionChangedEventArgs e)
    {
      if (damageModeList.SelectedIndex != -1 && e.RemovedItems.Count > 0 && 
        damageModeList.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag.ToString(), out int value))
      {
        UpdateDamageMode(value);
        DataChanged();
      }
    }

    private void UpdateDamageMode(int damageMode)
    {
      CurrentDamageMode = damageMode;
      if (damageModeList.SelectedItem == null || (damageModeList.SelectedItem is ComboBoxItem selected && !selected.Tag.Equals(damageMode.ToString())))
      {
        foreach (var item in damageModeList.Items)
        {
          if (item is ComboBoxItem comboBoxItem  && comboBoxItem.Tag.Equals(damageMode.ToString()))
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
        UpdateMaxRows(maxRowsList.SelectedIndex + 5);
        DataChanged();
      }
    }

    private void UpdateProgressBrush(string colorString)
    {
      if (progressBrush.Color.ToString() != colorString)
      {
        progressBrush.Brush = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString(colorString) };
        progressBrush.Color = (Color)ColorConverter.ConvertFromString(colorString);
      }

      Application.Current.Resources["DamageOverlayProgressBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString(colorString) };
    }

    private void SelectedProgressBrush(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      if (progressBrush.Brush.ToString() != progressBrush.Color.ToString())
      {
        UpdateProgressBrush(progressBrush.Color.ToString());
        DataChanged();
      }
    }

    private void UpdateMaxRows(int maxRows)
    {
      content.Children.Clear();

      // damage bars
      for (int i = 0; i < maxRows; i++)
      {
        content.Children.Add(new DamageBar("DamageOverlayDamageBrush", "DamageOverlayProgressBrush", true));
      }

      // title bar
      content.Children.Add(new DamageBar("DamageOverlayTitleBrush", "DamageOverlayTitleBrush", false));

      int selectedIndex = maxRows - 5;
      if (maxRowsList.SelectedIndex != selectedIndex)
      {
        maxRowsList.SelectedIndex = selectedIndex;
      }

      UpdateMiniBars(miniBars.IsChecked == true);

      if (Preview)
      {
        LoadTestData(Preview);
      }
    }

    private void FontSizeChanged(object sender, SelectionChangedEventArgs e)
    {
      if (fontList.SelectedValue is ComboBoxItem item && e.RemovedItems.Count > 0 && int.TryParse(item.Tag.ToString(), out int value))
      {
        UpdateFontSize(value);
        DataChanged();
      }
    }

    private void UpdateFontSize(int fontSize)
    {
      int selectedIndex = -1;
      switch (fontSize)
      {
        case 12:
          selectedIndex = 0;
          break;
        case 14:
          selectedIndex = 1;
          break;
        case 16:
          selectedIndex = 2;
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

    private void UpdateColumnSizes(int fontSize)
    {
      switch (fontSize)
      {
        case 12:
          Application.Current.Resources["DamageOverlayDamageColDef1"] = new GridLength(60.0);
          Application.Current.Resources["DamageOverlayDamageColDef2"] = new GridLength(45.0);
          titleDamage.Margin = new System.Windows.Thickness(0, 5, 42, 0);
          titleDPS.Margin = new System.Windows.Thickness(0, 5, 20, 0);
          titleTime.Margin = new System.Windows.Thickness(0, 5, 9, 0);
          break;
        case 14:
          Application.Current.Resources["DamageOverlayDamageColDef1"] = new GridLength(70.0);
          Application.Current.Resources["DamageOverlayDamageColDef2"] = new GridLength(50.0);
          titleDamage.Margin = new System.Windows.Thickness(0, 5, 52, 0);
          titleDPS.Margin = new System.Windows.Thickness(0, 5, 24, 0);
          titleTime.Margin = new System.Windows.Thickness(0, 5, 9, 0);
          break;
        case 16:
          Application.Current.Resources["DamageOverlayDamageColDef1"] = new GridLength(75.0);
          Application.Current.Resources["DamageOverlayDamageColDef2"] = new GridLength(55.0);
          titleDamage.Margin = new System.Windows.Thickness(0, 5, 58, 0);
          titleDPS.Margin = new System.Windows.Thickness(0, 5, 30, 0);
          titleTime.Margin = new System.Windows.Thickness(0, 5, 9, 0);
          break;
      }
    }

    private void ConfigureClick(object sender, RoutedEventArgs e)
    {
      lock (StatsLock)
      {
        UpdateTimer.Stop();
        Stats = null;
      }

      this.Hide();
      ((MainWindow)Application.Current.MainWindow).CloseDamageOverlay();
      ((MainWindow)Application.Current.MainWindow).OpenDamageOverlayIfEnabled(true);
    }

    private void CopyClick(object sender, RoutedEventArgs e)
    {
      lock (StatsLock)
      {
        (Application.Current.MainWindow as MainWindow)?.AddAndCopyDamageParse(Stats, Stats.StatsList);
      }
    }

    private void ResetClick(object sender, RoutedEventArgs e)
    {
      lock (StatsLock)
      {
        Stats = null;
        DataManager.Instance.ResetOverlayFights();
      }
    }

    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
      if (Preview)
      {
        if (!double.IsNaN(SavedTop))
        {
          DataChanged();
        }
      }
    }

    private void WindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
      UpdateTimer?.Stop();
      content.Children.Clear();
    }

    private void BorderSizeChanged(object sender, SizeChangedEventArgs e)
    {
      thePopup.VerticalOffset += 1;
      thePopup.VerticalOffset -= 1;
    }

    private void DataChanged()
    {
      // not initialized
      if (saveButton != null)
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

    protected override void OnSourceInitialized(EventArgs e)
    {
      base.OnSourceInitialized(e);

      if (!Preview)
      {
        var source = (HwndSource)PresentationSource.FromVisual(this);
        // set to layered and topmost by xaml
        int exStyle = (int)NativeMethods.GetWindowLongPtr(source.Handle, (int)NativeMethods.GetWindowLongFields.GWL_EXSTYLE);
        exStyle |= (int)NativeMethods.ExtendedWindowStyles.WS_EX_TOOLWINDOW | (int)NativeMethods.ExtendedWindowStyles.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(source.Handle, (int)NativeMethods.GetWindowLongFields.GWL_EXSTYLE, (IntPtr)exStyle);
      }
    }
  }
}
