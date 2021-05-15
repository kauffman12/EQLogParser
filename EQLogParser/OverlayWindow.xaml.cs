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

    private static readonly object StatsLock = new object();
    private static readonly Color TITLECOLOR = Color.FromRgb(30, 30, 30);
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

    private CombinedStats Stats = null;
    private bool ProcessDirection = false;
    private TextBlock TitleBlock;
    private StackPanel TitlePanel;
    private TextBlock TitleDamageBlock;
    private StackPanel TitleDamagePanel;
    private Rectangle TitleRectangle;
    private Dictionary<int, double> PrevList = null;
    private bool IsHideOverlayOtherPlayersEnabled = false;
    private bool IsShowOverlayCritRateEnabled = false;
    private string SelectedClass = Properties.Resources.ANY_CLASS;
    private int CurrentMaxRows = 5;
    private int CurrentFontSize = 13;

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

      // Max Rows
      string maxRows = ConfigUtil.GetSetting("MaxOverlayRows");
      if (!string.IsNullOrEmpty(maxRows) && int.TryParse(maxRows, out int max) && max >= 5 && max <= 10)
      {
        CurrentMaxRows = max;
      }

      // selected class
      string savedClass = ConfigUtil.GetSetting("SelectedOverlayClass");
      if (!string.IsNullOrEmpty(savedClass) && PlayerManager.Instance.GetClassList().Contains(savedClass))
      {
        SelectedClass = savedClass;
      }

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
        var list = PlayerManager.Instance.GetClassList();
        list.Insert(0, Properties.Resources.ANY_CLASS);
        classesList.ItemsSource = list;
        classesList.SelectedItem = SelectedClass;
        maxRowsSelection.SelectedItem = maxRowsSelection.Items[CurrentMaxRows - 5];
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
          CalculatedRowHeight = dvalue / (CurrentMaxRows + 1);
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

      if (fontSize != null && int.TryParse(fontSize, out CurrentFontSize) && CurrentFontSize >= 0 && CurrentFontSize <= 64)
      {
        foreach (var item in fontSizeSelection.Items)
        {
          if ((item as ComboBoxItem).Tag as string == fontSize)
          {
            fontSizeSelection.SelectedItem = item;
            SetFont();
            fontHasBeenSet = true;
          }
        }
      }

      if (!fontHasBeenSet)
      {
        SetFont();
      }

      if (!offsetSize)
      {
        DataManager.Instance.EventsNewOverlayFight += Instance_NewOverlayFight;
        Active = true;
      }
      else
      {
        // remove when configuring
        DataManager.Instance.EventsNewOverlayFight -= Instance_NewOverlayFight;
      }

      if (!configure)
      {
        var settingsButton = OverlayUtil.CreateButton("Change Settings", "\xE713", CurrentFontSize - 1);
        settingsButton.Click += (object sender, RoutedEventArgs e) => OverlayUtil.OpenOverlay(Dispatcher, true, false);
        settingsButton.Margin = new Thickness(4, 0, 0, 0);

        var copyButton = OverlayUtil.CreateButton("Copy Parse", "\xE8C8", CurrentFontSize - 1);
        copyButton.Click += (object sender, RoutedEventArgs e) =>
        {
          lock (StatsLock)
          {
            (Application.Current.MainWindow as MainWindow)?.AddAndCopyDamageParse(Stats, Stats.StatsList);
          }
        };

        var refreshButton = OverlayUtil.CreateButton("Cancel Current Parse", "\xE8BB", CurrentFontSize - 1);
        refreshButton.Click += (object sender, RoutedEventArgs e) => OverlayUtil.ResetOverlay(Dispatcher);

        ButtonPopup = new Popup();
        ButtonsPanel = OverlayUtil.CreateNameStackPanel();
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

        if (DataManager.Instance.GetOverlayFights().Count > 0)
        {
          UpdateTimer.Start();
        }
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
      ColorList.Add((Color)ColorConverter.ConvertFromString("#37474f"));
      ColorList.Add((Color)ColorConverter.ConvertFromString("#37474f"));
      ColorList.Add((Color)ColorConverter.ConvertFromString("#37474f"));
      ColorList.Add((Color)ColorConverter.ConvertFromString("#37474f"));
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
      for (int i = 0; i < CurrentMaxRows - 1; i++)
      {
        NameBlockList[i].Text = i + 1 + ". Example Player Name";
        NameBlockList[i].FontStyle = FontStyles.Italic;
        NameBlockList[i].FontWeight = FontWeights.Light;
      }

      NameBlockList[CurrentMaxRows - 1].Text = CurrentMaxRows + ". ...";
      NameBlockList[CurrentMaxRows - 1].FontStyle = FontStyles.Italic;
      NameBlockList[CurrentMaxRows - 1].FontWeight = FontWeights.Light;
    }

    private void Instance_NewOverlayFight(object sender, Fight fight)
    {
      if (UpdateTimer != null && !UpdateTimer.IsEnabled)
      {
        UpdateTimer.Start();
      }
    }

    private void UpdateTimerTick(object sender, EventArgs e)
    {
      lock (StatsLock)
      {
        try
        {
          Topmost = true; // possible workaround

          // people wanted shorter delays for damage updates but I don't want the indicator to change constantly
          // so this limits it to 1/2 the current time value
          ProcessDirection = !ProcessDirection;

          Stats = DamageStatsManager.ComputeOverlayStats(CurrentDamageSelectionMode, CurrentMaxRows, SelectedClass);

          if (Stats == null)
          {
            windowBrush.Opacity = 0.0;
            ButtonPopup.IsOpen = false;
            SetVisible(false);
            Height = 0;
            PrevList = null;
            UpdateTimer.Stop();
            DataManager.Instance.ResetOverlayFights();
          }
          else if (Active)
          {
            TitleBlock.Text = Stats.TargetTitle;
            TitleDamageBlock.Text = string.Format(CultureInfo.CurrentCulture, "{0} [{1}s @{2}]",
              StatsUtil.FormatTotals(Stats.RaidStats.Total), Stats.RaidStats.TotalSeconds, StatsUtil.FormatTotals(Stats.RaidStats.DPS));

            long total = 0;
            int goodRowCount = 0;
            long me = 0;
            var topList = new Dictionary<int, long>();
            for (int i = 0; i < CurrentMaxRows; i++)
            {
              if (Stats.StatsList.Count > i)
              {
                if (ProcessDirection)
                {
                  DamageRateList[i].Opacity = 0.0;
                }

                if (i == 0)
                {
                  total = Stats.StatsList[i].Total;
                  RectangleList[i].Width = Width;
                }
                else
                {
                  RectangleList[i].Visibility = Visibility.Hidden; // maybe it calculates width better
                  RectangleList[i].Width = Convert.ToDouble(Stats.StatsList[i].Total) / total * Width;
                }

                string playerName = ConfigUtil.PlayerName;
                var isMe = !string.IsNullOrEmpty(playerName) && Stats.StatsList[i].Name.StartsWith(playerName, StringComparison.OrdinalIgnoreCase) &&
                  (playerName.Length >= Stats.StatsList[i].Name.Length || Stats.StatsList[i].Name[playerName.Length] == ' ');

                string updateText;
                if (IsHideOverlayOtherPlayersEnabled && !isMe)
                {
                  updateText = string.Format(CultureInfo.CurrentCulture, "{0}. Hidden Player", Stats.StatsList[i].Rank);
                }
                else
                {
                  updateText = string.Format(CultureInfo.CurrentCulture, "{0}. {1}", Stats.StatsList[i].Rank, Stats.StatsList[i].Name);
                }

                if (IsShowOverlayCritRateEnabled)
                {
                  List<string> critMods = new List<string>();

                  if (isMe && PlayerManager.Instance.IsDoTClass(Stats.StatsList[i].ClassName) && DataManager.Instance.MyDoTCritRateMod is uint doTCritRate && doTCritRate > 0)
                  {
                    critMods.Add(string.Format("DoT CR +{0}", doTCritRate));
                  }

                  if (isMe && DataManager.Instance.MyNukeCritRateMod is uint nukeCritRate && nukeCritRate > 0)
                  {
                    critMods.Add(string.Format("Nuke CR +{0}", nukeCritRate));
                  }

                  if (critMods.Count > 0)
                  {
                    updateText = string.Format("{0} [{1}]", updateText, string.Join(", ", critMods));
                  }
                }

                NameBlockList[i].Text = updateText;

                if (i <= 4 && !isMe && Stats.StatsList[i].Total > 0)
                {
                  topList[i] = Stats.StatsList[i].Total;
                }
                else if (isMe)
                {
                  me = Stats.StatsList[i].Total;
                }

                var damage = StatsUtil.FormatTotals(Stats.StatsList[i].Total) + " [" + Stats.StatsList[i].TotalSeconds.ToString(CultureInfo.CurrentCulture)
                  + "s @" + StatsUtil.FormatTotals(Stats.StatsList[i].DPS) + "]";
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
                        DamageRateList[i].Foreground = OverlayUtil.DOWNBRUSH;
                        DamageRateList[i].Opacity = OverlayUtil.DATA_OPACITY;
                      }
                      else if (PrevList[i] < diff)
                      {
                        DamageRateList[i].Icon = FontAwesomeIcon.LongArrowUp;
                        DamageRateList[i].Foreground = OverlayUtil.UPBRUSH;
                        DamageRateList[i].Opacity = OverlayUtil.DATA_OPACITY;
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
              windowBrush.Opacity = OverlayUtil.OPACITY;
              ButtonPopup.IsOpen = true;
            }

            for (int i = 0; i < CurrentMaxRows; i++)
            {
              SetRowVisible(i < goodRowCount, i);
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
      double rowHeight = CalculatedRowHeight > 0 ? CalculatedRowHeight : height / (CurrentMaxRows + 1);

      UIElementUtil.SetSize(TitleRectangle, rowHeight, width);
      UIElementUtil.SetSize(TitlePanel, rowHeight, double.NaN);
      UIElementUtil.SetSize(TitleDamagePanel, rowHeight, double.NaN);

      UIElementUtil.SetSize(configPanel, rowHeight, double.NaN);
      UIElementUtil.SetSize(savePanel, rowHeight, width);

      if (!Active)
      {
        for (int i = 0; i < CurrentMaxRows; i++)
        {
          // should only effect test data
          var percent = Convert.ToDouble(70 - 10 * i) / 100;
          percent = percent > 0.0 ? percent: 0.0;
          UIElementUtil.SetSize(RectangleList[i], rowHeight, width * percent);
          UIElementUtil.SetSize(NamePanels[i], rowHeight, double.NaN);
          UIElementUtil.SetSize(DamagePanels[i], rowHeight, double.NaN);

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

      TitleRectangle = OverlayUtil.CreateRectangle(TITLECOLOR);
      overlayCanvas.Children.Add(TitleRectangle);

      TitlePanel = OverlayUtil.CreateNameStackPanel();
      TitleBlock = OverlayUtil.CreateTextBlock();
      TitleBlock.Foreground = configure ? OverlayUtil.TEXTBRUSH : OverlayUtil.TITLEBRUSH;
      TitlePanel.Children.Add(TitleBlock);
      overlayCanvas.Children.Add(TitlePanel);

      TitleDamagePanel = OverlayUtil.CreateDamageStackPanel();
      TitleDamageBlock = OverlayUtil.CreateTextBlock();
      TitleDamagePanel.Children.Add(TitleDamageBlock);
      overlayCanvas.Children.Add(TitleDamagePanel);

      if (!configure)
      {
        TitlePanel.SizeChanged += TitleResizing;
        TitleDamagePanel.SizeChanged += TitleResizing;
      }

      for (int i = 0; i < CurrentMaxRows; i++)
      {
        var rectangle = OverlayUtil.CreateRectangle(ColorList[i]);
        RectangleList.Add(rectangle);
        overlayCanvas.Children.Add(rectangle);

        var nameStack = OverlayUtil.CreateNameStackPanel();
        NamePanels.Add(nameStack);

        var nameBlock = OverlayUtil.CreateTextBlock();
        nameBlock.SetValue(Canvas.LeftProperty, 4.0);
        NameBlockList.Add(nameBlock);
        nameStack.Children.Add(nameBlock);
        overlayCanvas.Children.Add(nameStack);

        var damageStack = OverlayUtil.CreateDamageStackPanel();
        DamagePanels.Add(damageStack);

        if (configure)
        {
          var colorChoice = new ColorComboBox(ColorComboBox.Theme.Dark) { Tag = string.Format(CultureInfo.CurrentCulture, "OverlayRankColor{0}", i + 1) };
          colorChoice.SelectionChanged += (object sender, SelectionChangedEventArgs e) =>
          {
            rectangle.Fill = OverlayUtil.CreateBrush((colorChoice.SelectedValue as ColorItem).Brush.Color);
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
          var damageRate = OverlayUtil.CreateImageAwesome();
          DamageRateList.Add(damageRate);
          damageStack.Children.Add(damageRate);

          var damageBlock = OverlayUtil.CreateTextBlock();
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

    private void SetFont()
    {
      int size = CurrentFontSize;
      TitleBlock.FontSize = size;
      TitleDamageBlock.FontSize = size;

      for (int i = 0; i < CurrentMaxRows; i++)
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
    private void SelectPlayerClassChanged(object sender, SelectionChangedEventArgs e) => SelectedClass = classesList.SelectedValue.ToString();

    private void FontSizeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (TitleBlock != null && int.TryParse((fontSizeSelection.SelectedValue as ComboBoxItem).Tag as string, out int size))
      {
        CurrentFontSize = size;
        SetFont();
      }
    }

    private void MaxRowsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (TitleBlock != null && int.TryParse((maxRowsSelection.SelectedValue as ComboBoxItem).Tag as string, out int maxRows))
      {
        // clear out old rows
        overlayCanvas.Children.Remove(TitleRectangle);
        overlayCanvas.Children.Remove(TitlePanel);
        overlayCanvas.Children.Remove(TitleDamagePanel);
        RectangleList.ForEach(rectangle => overlayCanvas.Children.Remove(rectangle));
        NamePanels.ForEach(nameStack => overlayCanvas.Children.Remove(nameStack));
        DamagePanels.ForEach(damageStack => overlayCanvas.Children.Remove(damageStack));
        RectangleList.Clear();
        NamePanels.Clear();
        DamagePanels.Clear();
        NameBlockList.Clear();

        CurrentMaxRows = maxRows;
        ConfigUtil.SetSetting("MaxOverlayRows", CurrentMaxRows.ToString(CultureInfo.CurrentCulture));
        CreateRows(true);
        SetVisible(true);
        SetFont();
        LoadTestData();
        Resize(overlayCanvas.ActualHeight, overlayCanvas.ActualWidth);
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
        ConfigUtil.SetSetting("SelectedOverlayClass", SelectedClass);

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
        DataManager.Instance.EventsNewOverlayFight -= Instance_NewOverlayFight;
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
  }
}
