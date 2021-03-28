using FontAwesome.WPF;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for OverlayWindow.xaml
  /// </summary>
  public partial class OverlayWindow : Window
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private static readonly SolidColorBrush TEXTBRUSH = new SolidColorBrush(Colors.White);
    private static readonly SolidColorBrush UPBRUSH = new SolidColorBrush(Colors.White);
    private static readonly SolidColorBrush DOWNBRUSH = new SolidColorBrush(Colors.Red);
    private static readonly SolidColorBrush TITLEBRUSH = new SolidColorBrush(Color.FromRgb(254, 156, 30));
    private static readonly object StatsLock = new object();
    private static readonly Color TITLECOLOR = Color.FromRgb(30, 30, 30);

    private const int DEFAULT_TEXT_FONT_SIZE = 13;
    private const int MAX_ROWS = 5;
    private const double OPACITY = 0.40;
    private const double DATA_OPACITY = 0.70;

    private readonly List<ColorComboBox> ColorComboBoxList = new List<ColorComboBox>();
    private readonly List<StackPanel> NamePanels = new List<StackPanel>();
    private readonly List<TextBlock> NameBlockList = new List<TextBlock>();
    private readonly List<StackPanel> DamagePanels = new List<StackPanel>();
    private readonly List<TextBlock> DamageBlockList = new List<TextBlock>();
    private readonly List<ImageAwesome> DamageRateList = new List<ImageAwesome>();
    private readonly List<Rectangle> RectangleList = new List<Rectangle>();
    private readonly List<Color> ColorList = new List<Color>();
    private readonly DispatcherTimer UpdateTimer;
    private readonly double CalculatedRowHeight;
    private readonly int CurrentDamageSelectionMode;
    private readonly bool Active = false;
    private readonly Popup ButtonPopup;
    private readonly StackPanel ButtonsPanel;

    private OverlayDamageStats Stats = null;
    private bool ProcessDirection = false;
    private TextBlock TitleBlock;
    private StackPanel TitlePanel;
    private TextBlock TitleDamageBlock;
    private StackPanel TitleDamagePanel;
    private Rectangle TitleRectangle;
    private Dictionary<int, double> PrevList = null;
    private bool IsHideOverlayOtherPlayersEnabled = false;
    private bool IsShowOverlayCritRateEnabled = false;

    public OverlayWindow(bool configure = false)
    {
      InitializeComponent();
      LoadColorSettings();

      string width = ConfigUtil.GetSetting("OverlayWidth");
      string height = ConfigUtil.GetSetting("OverlayHeight");
      string top = ConfigUtil.GetSetting("OverlayTop");
      string left = ConfigUtil.GetSetting("OverlayLeft");

      // Hide other player names on overlay
      IsHideOverlayOtherPlayersEnabled = ConfigUtil.IfSet("HideOverlayOtherPlayers");
      showNameSelection.SelectedIndex = IsHideOverlayOtherPlayersEnabled ? 1 : 0;

      // Hide/Show crit rate
      IsShowOverlayCritRateEnabled = ConfigUtil.IfSet("ShowOverlayCritRate");
      showCritRateSelection.SelectedIndex = IsShowOverlayCritRateEnabled ? 1 : 0;

      var margin = SystemParameters.WindowNonClientFrameThickness;
      bool offsetSize = configure || width == null || height == null || top == null || left == null;

      if (!offsetSize)
      {
        CreateRows();
        Title = "Overlay";
        MinHeight = 0;
        UpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1000) };
        UpdateTimer.Tick += UpdateTimerTick;
        AllowsTransparency = true;
        Style = null;
        WindowStyle = WindowStyle.None;
        SetVisible(false);
        ShowActivated = false;
      }
      else
      {
        overlayCanvas.Background = new SolidColorBrush(Color.FromRgb(45, 45, 48));
        CreateRows(true);
        MinHeight = 130;
        AllowsTransparency = false;
        WindowStyle = WindowStyle.SingleBorderWindow;
        SetVisible(true);
        LoadTestData();
      }

      if (width != null && double.TryParse(width, out double dvalue) && !double.IsNaN(dvalue))
      {
        Width = offsetSize ? dvalue + margin.Left + margin.Right : dvalue;
      }

      if (height != null && double.TryParse(height, out dvalue) && !double.IsNaN(dvalue))
      {
        Height = offsetSize ? dvalue + margin.Top + margin.Bottom : 0;
        if (!offsetSize)
        {
          CalculatedRowHeight = dvalue / (MAX_ROWS + 1);
        }
      }

      if (top != null && double.TryParse(top, out dvalue) && !double.IsNaN(dvalue))
      {
        var test = offsetSize ? dvalue - margin.Top : dvalue;
        if (test >= SystemParameters.VirtualScreenTop && test < SystemParameters.VirtualScreenHeight)
        {
          Top = test;
        }
      }

      if (left != null && double.TryParse(left, out dvalue) && !double.IsNaN(dvalue))
      {
        var test = offsetSize ? dvalue - margin.Left : dvalue;
        if (test >= SystemParameters.VirtualScreenLeft && test < SystemParameters.VirtualScreenWidth)
        {
          Left = test;
        }
      }

      int damageMode = ConfigUtil.GetSettingAsInteger("OverlayDamageMode");
      foreach (var item in damageModeSelection.Items.Cast<ComboBoxItem>())
      {
        if ((string)item.Tag == damageMode.ToString(CultureInfo.CurrentCulture))
        {
          damageModeSelection.SelectedItem = item;
          CurrentDamageSelectionMode = damageMode;
        }
      }

      string fontSize = ConfigUtil.GetSetting("OverlayFontSize");
      bool fontHasBeenSet = false;
      int currentFontSize = DEFAULT_TEXT_FONT_SIZE;
      if (fontSize != null && int.TryParse(fontSize, out currentFontSize) && currentFontSize >= 0 && currentFontSize <= 64)
      {
        foreach (var item in fontSizeSelection.Items)
        {
          if ((item as ComboBoxItem).Tag as string == fontSize)
          {
            fontSizeSelection.SelectedItem = item;
            SetFont(currentFontSize);
            fontHasBeenSet = true;
          }
        }
      }

      if (!fontHasBeenSet)
      {
        SetFont(currentFontSize);
      }

      if (!offsetSize)
      {
        NpcDamageManager.EventsPlayerAttackProcessed += NpcDamageManager_EventsPlayerAttackProcessed;
        DataManager.Instance.EventsNewInactiveFight += Instance_EventsNewInactiveFight;
        Active = true;
      }
      else
      {
        // remove when configuring
        NpcDamageManager.EventsPlayerAttackProcessed -= NpcDamageManager_EventsPlayerAttackProcessed;
        DataManager.Instance.EventsNewInactiveFight -= Instance_EventsNewInactiveFight;
      }

      if (!configure)
      {
        var settingsButton = CreateButton("Change Settings", "\xE713", currentFontSize - 1);
        settingsButton.Click += (object sender, RoutedEventArgs e) => OverlayUtil.OpenOverlay(Dispatcher, true, false);
        settingsButton.Margin = new Thickness(4, 0, 0, 0);

        var copyButton = CreateButton("Copy Parse", "\xE8C8", currentFontSize - 1);
        copyButton.Click += (object sender, RoutedEventArgs e) =>
        {
          lock (Stats)
          {
            (Application.Current.MainWindow as MainWindow)?.AddAndCopyDamageParse(Stats, Stats.StatsList);
          }
        };

        var refreshButton = CreateButton("Cancel Current Parse", "\xE8BB", currentFontSize - 1);
        refreshButton.Click += (object sender, RoutedEventArgs e) => OverlayUtil.ResetOverlay(Dispatcher);

        ButtonPopup = new Popup();
        ButtonsPanel = CreateNameStackPanel();
        ButtonsPanel.Children.Add(settingsButton);
        ButtonsPanel.Children.Add(copyButton);
        ButtonsPanel.Children.Add(refreshButton);
        ButtonPopup.Child = ButtonsPanel;
        ButtonPopup.AllowsTransparency = true;
        ButtonPopup.Opacity = 0.3;
        ButtonPopup.Placement = PlacementMode.Relative;
        ButtonPopup.PlacementTarget = this;
        ButtonPopup.VerticalOffset = -1;

        ButtonsPanel.SizeChanged += (object sender, SizeChangedEventArgs e) =>
        {
          if (TitlePanel.Margin.Left != e.NewSize.Width + 2)
          {
            TitlePanel.Margin = new Thickness(e.NewSize.Width + 2, TitlePanel.Margin.Top, 0, TitlePanel.Margin.Bottom);
          }

          if (ButtonsPanel != null && ButtonsPanel.ActualHeight != TitlePanel.ActualHeight)
          {
            ButtonsPanel.Height = TitlePanel.ActualHeight;
          }
        };
      }
    }

    private void LoadColorSettings()
    {
      // load defaults
      ColorList.Add((Color)ColorConverter.ConvertFromString("#2e7d32"));
      ColorList.Add((Color)ColorConverter.ConvertFromString("#01579b"));
      ColorList.Add((Color)ColorConverter.ConvertFromString("#006064"));
      ColorList.Add((Color)ColorConverter.ConvertFromString("#673ab7"));
      ColorList.Add((Color)ColorConverter.ConvertFromString("#37474f"));

      for (int i = 0; i < ColorList.Count; i++)
      {
        try
        {
          string name = ConfigUtil.GetSetting(string.Format(CultureInfo.CurrentCulture, "OverlayRankColor{0}", i + 1));
          if (!string.IsNullOrEmpty(name) && ColorConverter.ConvertFromString(name) is Color color)
          {
            ColorList[i] = color; // override
          }
        }
        catch (FormatException ex)
        {
          LOG.Error("Invalid Overlay Color", ex);
        }
      }
    }

    private void LoadTestData()
    {
      for (int i = 0; i < MAX_ROWS - 1; i++)
      {
        NameBlockList[i].Text = i + 1 + ". Example Player Name";
        NameBlockList[i].FontStyle = FontStyles.Italic;
        NameBlockList[i].FontWeight = FontWeights.Light;
      }
    }

    private void NpcDamageManager_EventsPlayerAttackProcessed(object sender, DamageProcessedEvent e)
    {
      lock (StatsLock)
      {
        var activeFights = DataManager.Instance.GetActiveFights();

        // reset if stats if first time or first new damage is received
        if (Stats == null || (activeFights.Count == 1 && activeFights[0].DamageBlocks.Count == 1 &&
          activeFights[0].DamageBlocks[0].Actions.Count == 1 && CurrentDamageSelectionMode == 0))
        {
          Stats = new OverlayDamageStats { BeginTime = e.BeginTime, RaidStats = new PlayerStats() };
        }

        Stats.ActiveFights = activeFights;
        var timeout = CurrentDamageSelectionMode == 0 ? DataManager.FIGHTTIMEOUT : CurrentDamageSelectionMode;
        DamageStatsManager.Instance.ComputeOverlayDamageStats(e.Record, e.BeginTime, timeout, Stats);

        if (UpdateTimer != null && !UpdateTimer.IsEnabled)
        {
          UpdateTimer.Start();
        }
      }
    }

    private void Instance_EventsNewInactiveFight(object sender, Fight e)
    {
      lock(StatsLock)
      {
        if (Stats != null && e.LastDamageTime >= Stats.BeginTime)
        {
          Stats.InactiveFights.Add(e);
        }
      }
    }

    private void UpdateTimerTick(object sender, EventArgs e)
    {
      lock(StatsLock)
      {
        try
        {
          Topmost = true; // possible workaround

          // people wanted shorter delays for damage updates but I don't want the indicator to change constantly
          // so this limits it to 1/2 the current time value
          ProcessDirection = !ProcessDirection;

          var timeout = CurrentDamageSelectionMode == 0 ? DataManager.FIGHTTIMEOUT : CurrentDamageSelectionMode;
          if (Stats == null || (DateTime.Now - DateTime.MinValue.AddSeconds(Stats.LastTime)).TotalSeconds > timeout)
          {
            windowBrush.Opacity = 0.0;
            ButtonPopup.IsOpen = false;
            SetVisible(false);
            Height = 0;
            Stats = null;
            PrevList = null;
            UpdateTimer.Stop();
          }
          else if (Active && Stats != null)
          {
            var list = Stats.StatsList.Take(MAX_ROWS).ToList();
            if (list.Count > 0)
            {
              TitleBlock.Text = Stats.TargetTitle;
              TitleDamageBlock.Text = string.Format(CultureInfo.CurrentCulture, "{0} [{1}s @{2}]", 
                StatsUtil.FormatTotals(Stats.RaidStats.Total), Stats.RaidStats.TotalSeconds, StatsUtil.FormatTotals(Stats.RaidStats.DPS));

              long total = 0;
              int goodRowCount = 0;
              long me = 0;
              var topList = new Dictionary<int, long>();
              for (int i = 0; i < MAX_ROWS; i++)
              {
                if (list.Count > i)
                {
                  if (ProcessDirection)
                  {
                    DamageRateList[i].Opacity = 0.0;
                  }

                  if (i == 0)
                  {
                    total = list[i].Total;
                    RectangleList[i].Width = Width;
                  }
                  else
                  {
                    RectangleList[i].Visibility = Visibility.Hidden; // maybe it calculates width better
                    RectangleList[i].Width = Convert.ToDouble(list[i].Total) / total * Width;
                  }

                  string playerName = ConfigUtil.PlayerName;
                  var isMe = !string.IsNullOrEmpty(playerName) && list[i].Name.StartsWith(playerName, StringComparison.OrdinalIgnoreCase) &&
                    (playerName.Length >= list[i].Name.Length || list[i].Name[playerName.Length] == ' ');

                  string updateText;
                  if (IsHideOverlayOtherPlayersEnabled && !isMe)
                  {
                    updateText = string.Format(CultureInfo.CurrentCulture, "{0}. Hidden Player", list[i].Rank);
                  }
                  else
                  {
                    updateText = string.Format(CultureInfo.CurrentCulture, "{0}. {1}", list[i].Rank, list[i].Name);
                  }

                  if (IsShowOverlayCritRateEnabled)
                  {
                    List<string> critMods = new List<string>();

                    if (isMe && PlayerManager.Instance.IsDoTClass(list[i].ClassName) && DataManager.Instance.MyDoTCritRateMod is uint doTCritRate && doTCritRate > 0)
                    {
                      critMods.Add(string.Format(CultureInfo.CurrentCulture, "DoT CR +{0}", doTCritRate));
                    }

                    if (isMe && DataManager.Instance.MyNukeCritRateMod is uint nukeCritRate && nukeCritRate > 0)
                    {
                      critMods.Add(string.Format(CultureInfo.CurrentCulture, "Nuke CR +{0}", nukeCritRate));
                    }

                    if (critMods.Count > 0)
                    {
                      updateText = string.Format(CultureInfo.CurrentCulture, "{0} [{1}]", updateText, string.Join(", ", critMods));
                    }
                  }

                  NameBlockList[i].Text = updateText;

                  if (i <= 4 && !isMe && list[i].Total > 0)
                  {
                    topList[i] = list[i].Total;
                  }
                  else if (isMe)
                  {
                    me = list[i].Total;
                  }

                  var damage = StatsUtil.FormatTotals(list[i].Total) + " [" + list[i].TotalSeconds.ToString(CultureInfo.CurrentCulture) + "s @" + StatsUtil.FormatTotals(list[i].DPS) + "]";
                  DamageBlockList[i].Text = damage;
                  goodRowCount++;
                }
              }

              if (ProcessDirection)
              {
                if (me > 0 && topList.Count > 0)
                {
                  var updatedList = new Dictionary<int, double>();
                  foreach (int i in topList.Keys)
                  {
                    if (i != me)
                    {
                      var diff = topList[i] / (double)me;
                      updatedList[i] = diff;
                      if (PrevList != null && PrevList.ContainsKey(i))
                      {
                        if (PrevList[i] > diff)
                        {
                          DamageRateList[i].Icon = FontAwesomeIcon.LongArrowDown;
                          DamageRateList[i].Foreground = DOWNBRUSH;
                          DamageRateList[i].Opacity = DATA_OPACITY;
                        }
                        else if (PrevList[i] < diff)
                        {
                          DamageRateList[i].Icon = FontAwesomeIcon.LongArrowUp;
                          DamageRateList[i].Foreground = UPBRUSH;
                          DamageRateList[i].Opacity = DATA_OPACITY;
                        }
                      }
                    }
                  }

                  PrevList = updatedList;
                }
                else
                {
                  PrevList = null;
                }
              }

              var requested = (goodRowCount + 1) * CalculatedRowHeight;
              if (ActualHeight != requested)
              {
                Height = requested;
              }

              if (overlayCanvas.Visibility != Visibility.Visible)
              {
                overlayCanvas.Visibility = Visibility.Hidden;
                TitleRectangle.Visibility = Visibility.Hidden;
                TitlePanel.Visibility = Visibility.Hidden;
                TitleDamagePanel.Visibility = Visibility.Hidden;
                TitleRectangle.Height = CalculatedRowHeight;
                TitleDamagePanel.Height = CalculatedRowHeight;
                TitlePanel.Height = CalculatedRowHeight;
                overlayCanvas.Visibility = Visibility.Visible;
                TitleRectangle.Visibility = Visibility.Visible;
                TitlePanel.Visibility = Visibility.Visible;
                TitleDamagePanel.Visibility = Visibility.Visible;
                windowBrush.Opacity = OPACITY;
                ButtonPopup.IsOpen = true;
              }

              for (int i = 0; i < MAX_ROWS; i++)
              {
                SetRowVisible(i < goodRowCount, i);
              }
            }
          }
        }
        catch (Exception ex)
        {
          LOG.Error("Overlay Error", ex);
        }
      }
    }

    private void SetRowVisible(bool visible, int index)
    {
      if (visible)
      {
        DamagePanels[index].Height = CalculatedRowHeight;
        NamePanels[index].Height = CalculatedRowHeight;
        RectangleList[index].Height = CalculatedRowHeight;
        DamagePanels[index].SetValue(Canvas.TopProperty, CalculatedRowHeight * (index + 1));
        NamePanels[index].SetValue(Canvas.TopProperty, CalculatedRowHeight * (index + 1));
        RectangleList[index].SetValue(Canvas.TopProperty, CalculatedRowHeight * (index + 1));
      }

      NamePanels[index].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
      NameBlockList[index].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
      DamagePanels[index].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
      DamageRateList[index].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
      DamageBlockList[index].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
      RectangleList[index].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetVisible(bool visible)
    {
      overlayCanvas.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

      foreach (var child in overlayCanvas.Children)
      {
        var element = child as FrameworkElement;
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
      }
    }

    private void Resize(double height, double width)
    {
      double rowHeight = CalculatedRowHeight > 0 ? CalculatedRowHeight : height / (MAX_ROWS + 1);

      SetSize(TitleRectangle, rowHeight, width);
      SetSize(TitlePanel, rowHeight, double.NaN);
      SetSize(TitleDamagePanel, rowHeight, double.NaN);

      SetSize(configPanel, rowHeight, double.NaN);
      SetSize(savePanel, rowHeight, width);

      if (!Active)
      {
        for (int i = 0; i < MAX_ROWS; i++)
        {
          // should only effect test data
          var percent = Convert.ToDouble(65 - 10 * i) / 100;
          SetSize(RectangleList[i], rowHeight, width * percent);
          SetSize(NamePanels[i], rowHeight, double.NaN);
          SetSize(DamagePanels[i], rowHeight, double.NaN);

          double pos = rowHeight * (i + 1);
          RectangleList[i].SetValue(Canvas.TopProperty, pos);
          NamePanels[i].SetValue(Canvas.TopProperty, pos);
          DamagePanels[i].SetValue(Canvas.TopProperty, pos);
        }
      }
    }

    private void CreateRows(bool configure = false)
    {
      configPanel.SetValue(Panel.ZIndexProperty, 3);
      configPanel.SetValue(Canvas.RightProperty, 4.0);
      savePanel.SetValue(Panel.ZIndexProperty, 3);
      savePanel.SetValue(Canvas.BottomProperty, 1.0);

      TitleRectangle = CreateRectangle(TITLECOLOR);
      overlayCanvas.Children.Add(TitleRectangle);

      TitlePanel = CreateNameStackPanel();
      TitleBlock = CreateTextBlock();
      TitleBlock.Foreground = configure ? TEXTBRUSH : TITLEBRUSH;
      TitlePanel.Children.Add(TitleBlock);
      overlayCanvas.Children.Add(TitlePanel);

      TitleDamagePanel = CreateDamageStackPanel();
      TitleDamageBlock = CreateTextBlock();
      TitleDamagePanel.Children.Add(TitleDamageBlock);
      overlayCanvas.Children.Add(TitleDamagePanel);

      if (!configure)
      {
        TitlePanel.SizeChanged += TitleResizing;
        TitleDamagePanel.SizeChanged += TitleResizing;
      }

      for (int i = 0; i < MAX_ROWS; i++)
      {
        var rectangle = CreateRectangle(ColorList[i]);
        RectangleList.Add(rectangle);
        overlayCanvas.Children.Add(rectangle);

        var nameStack = CreateNameStackPanel();
        NamePanels.Add(nameStack);

        var nameBlock = CreateTextBlock();
        nameBlock.SetValue(Canvas.LeftProperty, 4.0);
        NameBlockList.Add(nameBlock);
        nameStack.Children.Add(nameBlock);
        overlayCanvas.Children.Add(nameStack);

        var damageStack = CreateDamageStackPanel();
        DamagePanels.Add(damageStack);

        if (configure)
        {
          var colorChoice = new ColorComboBox(ColorComboBox.Theme.Dark) { Tag = string.Format(CultureInfo.CurrentCulture, "OverlayRankColor{0}", i + 1) };
          colorChoice.SelectionChanged += (object sender, SelectionChangedEventArgs e) =>
          {
            rectangle.Fill = CreateBrush((colorChoice.SelectedValue as ColorItem).Brush.Color);
          };

          if (colorChoice.ItemsSource is List<ColorItem> colors)
          {
            colorChoice.SelectedItem = colors.Find(item => item.Brush.Color == ColorList[i]);
          }

          ColorComboBoxList.Add(colorChoice);
          damageStack.Children.Add(colorChoice);
        }
        else
        {
          var damageRate = CreateImageAwesome();
          DamageRateList.Add(damageRate);
          damageStack.Children.Add(damageRate);

          var damageBlock = CreateTextBlock();
          DamageBlockList.Add(damageBlock);
          damageStack.Children.Add(damageBlock);
        }

        overlayCanvas.Children.Add(damageStack);
      }
    }

    private void TitleResizing(object sender, SizeChangedEventArgs e)
    {
      if (ButtonPopup != null)
      {
        // trigger placement event for popup
        ButtonPopup.HorizontalOffset += 1;
        ButtonPopup.HorizontalOffset -= 1;
        TitlePanel.MaxWidth = ActualWidth - TitleDamagePanel.ActualWidth - ButtonsPanel.ActualWidth - 24;
      }
      else
      {
        TitlePanel.MaxWidth = ActualWidth - TitleDamagePanel.ActualWidth - 24;
      }
    }

    private void SetFont(int size)
    {
      TitleBlock.FontSize = size;
      TitleDamageBlock.FontSize = size;

      for (int i = 0; i < MAX_ROWS; i++)
      {
        NameBlockList[i].FontSize = size;

        // empty during configure
        if (DamageRateList.Count > 0)
        {
          DamageRateList[i].Height = size;
          DamageRateList[i].Width = size;
          DamageRateList[i].MaxHeight = size;
          DamageRateList[i].MaxWidth = size;
          DamageBlockList[i].FontSize = size;
        }
      }
    }

    private void PanelSizeChanged(object sender, SizeChangedEventArgs e) => Resize(e.NewSize.Height, e.NewSize.Width);
    private void ShowNamesSelectionChanged(object sender, SelectionChangedEventArgs e) => IsHideOverlayOtherPlayersEnabled = showNameSelection.SelectedIndex == 1;
    private void ShowCritRateSelectionChanged(object sender, SelectionChangedEventArgs e) => IsShowOverlayCritRateEnabled = showCritRateSelection.SelectedIndex == 1;

    private void FontSizeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (TitleBlock != null && int.TryParse((fontSizeSelection.SelectedValue as ComboBoxItem).Tag as string, out int size))
      {
        SetFont(size);
      }
    }
   
    private void SaveClick(object sender, RoutedEventArgs e)
    {
      if (!double.IsNaN(overlayCanvas.ActualHeight) && overlayCanvas.ActualHeight > 0)
      {
        ConfigUtil.SetSetting("OverlayHeight", overlayCanvas.ActualHeight.ToString(CultureInfo.CurrentCulture));
      }

      if (!double.IsNaN(overlayCanvas.ActualWidth) && overlayCanvas.ActualWidth > 0)
      {
        ConfigUtil.SetSetting("OverlayWidth", overlayCanvas.ActualWidth.ToString(CultureInfo.CurrentCulture));
      }

      var margin = SystemParameters.WindowNonClientFrameThickness;
      if (Top + margin.Top >= SystemParameters.VirtualScreenTop && (Left + margin.Left) >= SystemParameters.VirtualScreenLeft)
      {
        ConfigUtil.SetSetting("OverlayTop", (Top + margin.Top).ToString(CultureInfo.CurrentCulture));
        ConfigUtil.SetSetting("OverlayLeft", (Left + margin.Left).ToString(CultureInfo.CurrentCulture));
      }

      if (TitleBlock != null)
      {
        if (int.TryParse((fontSizeSelection.SelectedValue as ComboBoxItem).Tag as string, out int size))
        {
          ConfigUtil.SetSetting("OverlayFontSize", size.ToString(CultureInfo.CurrentCulture));
        }

        ConfigUtil.SetSetting("OverlayDamageMode", (string)(damageModeSelection.SelectedItem as ComboBoxItem).Tag);
        ConfigUtil.SetSetting("HideOverlayOtherPlayers", IsHideOverlayOtherPlayersEnabled.ToString(CultureInfo.CurrentCulture));
        ConfigUtil.SetSetting("ShowOverlayCritRate", IsShowOverlayCritRateEnabled.ToString(CultureInfo.CurrentCulture));

        ColorComboBoxList.ForEach(colorChoice =>
        {
          ConfigUtil.SetSetting(colorChoice.Tag as string, (colorChoice.SelectedValue as ColorItem).Name);
        });
      }

      OverlayUtil.OpenOverlay(Dispatcher, false, true);
    }

    private void WindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
      if (Active)
      {
        NpcDamageManager.EventsPlayerAttackProcessed -= NpcDamageManager_EventsPlayerAttackProcessed;
        DataManager.Instance.EventsNewInactiveFight -= Instance_EventsNewInactiveFight;
        ButtonPopup.IsOpen = false;

        if (UpdateTimer?.IsEnabled == true)
        {
          UpdateTimer.Stop();
        }
      }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
      base.OnSourceInitialized(e);
      var source = (HwndSource)PresentationSource.FromVisual(this);
      int exStyle = (int)NativeMethods.GetWindowLongPtr(source.Handle, (int)NativeMethods.GetWindowLongFields.GWL_EXSTYLE);
      exStyle |= (int)NativeMethods.ExtendedWindowStyles.WS_EX_TOOLWINDOW | (int)NativeMethods.ExtendedWindowStyles.WS_EX_TRANSPARENT;
      NativeMethods.SetWindowLong(source.Handle, (int)NativeMethods.GetWindowLongFields.GWL_EXSTYLE, (IntPtr)exStyle);
    }

    private static Button CreateButton(string tooltip, string content, double size)
    {
      var button = new Button() { Background = null, BorderBrush = null, IsEnabled = true };
      button.SetValue(Panel.ZIndexProperty, 3);
      button.Foreground = TEXTBRUSH;
      button.VerticalAlignment = VerticalAlignment.Top;
      button.Padding = new Thickness(0, 0, 0, 0);
      button.FontFamily = new FontFamily("Segoe MDL2 Assets");
      button.VerticalAlignment = VerticalAlignment.Center;
      button.Margin = new Thickness(2, 0, 0, 0);
      button.ToolTip = new ToolTip { Content = tooltip };
      button.Content = content;
      button.FontSize = size;
      return button;
    }

    private static Rectangle CreateRectangle(Color color)
    {
      var rectangle = new Rectangle();
      rectangle.SetValue(Panel.ZIndexProperty, 1);
      rectangle.Effect = new BlurEffect { Radius = 5, RenderingBias = 0 };
      rectangle.Opacity = DATA_OPACITY;
      rectangle.Fill = CreateBrush(color);
      return rectangle;
    }

    private static TextBlock CreateTextBlock()
    {
      var textBlock = new TextBlock { Foreground = TEXTBRUSH };
      textBlock.SetValue(Panel.ZIndexProperty, 3);
      textBlock.UseLayoutRounding = true;
      textBlock.Effect = new DropShadowEffect { ShadowDepth = 2, BlurRadius = 2, Opacity = 0.6 };
      textBlock.FontFamily = new FontFamily("Lucidia Console");
      textBlock.Margin = new Thickness() { };
      textBlock.VerticalAlignment = VerticalAlignment.Center;
      return textBlock;
    }

    private static StackPanel CreateDamageStackPanel()
    {
      var stack = new StackPanel { Orientation = Orientation.Horizontal };
      stack.SetValue(Panel.ZIndexProperty, 3);
      stack.SetValue(Canvas.RightProperty, 4.0);
      return stack;
    }

    private static StackPanel CreateNameStackPanel()
    {
      var stack = new StackPanel { Orientation = Orientation.Horizontal };
      stack.SetValue(Panel.ZIndexProperty, 3);
      stack.SetValue(Canvas.LeftProperty, 4.0);
      return stack;
    }

    private static ImageAwesome CreateImageAwesome()
    {
      var image = new ImageAwesome { Margin = new Thickness { Bottom = 1, Right = 2 }, Opacity = 0.0, Foreground = UPBRUSH, Icon = FontAwesomeIcon.None };
      image.SetValue(Panel.ZIndexProperty, 3);
      image.VerticalAlignment = VerticalAlignment.Center;
      return image;
    }

    private static void SetSize(FrameworkElement element, double height, double width)
    {
      if (!double.IsNaN(height) && element.Height != height)
      {
        element.Height = height;
      }

      if (!double.IsNaN(width) && element.Width != width)
      {
        element.Width = width;
      }
    }

    private static Brush CreateBrush(Color color)
    {
      var brush = new LinearGradientBrush
      {
        StartPoint = new Point(0.5, 0),
        EndPoint = new Point(0.5, 1)
      };

      brush.GradientStops.Add(new GradientStop(ChangeColorBrightness(color, 0.15f), 0.0));
      brush.GradientStops.Add(new GradientStop(color, 0.5));
      brush.GradientStops.Add(new GradientStop(ChangeColorBrightness(color, -0.4f), 0.75));
      return brush;
    }

    // From: https://gist.github.com/zihotki/09fc41d52981fb6f93a81ebf20b35cd5
    public static Color ChangeColorBrightness(Color color, float correctionFactor)
    {
      float red = color.R;
      float green = color.G;
      float blue = color.B;

      if (correctionFactor < 0)
      {
        correctionFactor = 1 + correctionFactor;
        red *= correctionFactor;
        green *= correctionFactor;
        blue *= correctionFactor;
      }
      else
      {
        red = (255 - red) * correctionFactor + red;
        green = (255 - green) * correctionFactor + green;
        blue = (255 - blue) * correctionFactor + blue;
      }

      return Color.FromArgb(color.A, (byte)red, (byte)green, (byte)blue);
    }
  }
}
