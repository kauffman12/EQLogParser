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

    private static SolidColorBrush TEXT_BRUSH = new SolidColorBrush(Colors.White);
    private static SolidColorBrush UP_BRUSH = new SolidColorBrush(Colors.White);
    private static SolidColorBrush DOWN_BRUSH = new SolidColorBrush(Colors.Red);
    private static SolidColorBrush TITLE_BRUSH = new SolidColorBrush(Color.FromRgb(254, 156, 30));
    private static object StatsLock = new object();
    private const int DEFAULT_TEXT_FONT_SIZE = 13;
    private const int MAX_ROWS = 5;
    private const double OPACITY = 0.40;
    private const double DATA_OPACITY = 0.80;

    private OverlayDamageStats Stats = null;
    private DispatcherTimer UpdateTimer;
    private double CalculatedRowHeight = 0;
    private bool Active = false;
    private bool ProcessDirection = false;

    private Popup ButtonPopup;
    private StackPanel ButtonsPanel;
    private TextBlock TitleBlock;
    private StackPanel TitlePanel;
    private TextBlock TitleDamageBlock;
    private StackPanel TitleDamagePanel;
    private Rectangle TitleRectangle;
    private List<StackPanel> NamePanels = new List<StackPanel>();
    private List<TextBlock> NameBlockList = new List<TextBlock>();
    private List<StackPanel> DamagePanels = new List<StackPanel>();
    private List<TextBlock> DamageBlockList = new List<TextBlock>();
    private List<ImageAwesome> DamageRateList = new List<ImageAwesome>();
    private List<Rectangle> RectangleList = new List<Rectangle>();
    private Dictionary<int, double> PrevList = null;

    private List<Color> TitleColorList = new List<Color> { Color.FromRgb(50, 50, 50), Color.FromRgb(30, 30, 30), Color.FromRgb(10, 10, 10) };
    private List<List<Color>> ColorList = new List<List<Color>>()
    {
      new List<Color> { Color.FromRgb(60, 134, 80), Color.FromRgb(54, 129, 27), Color.FromRgb(39, 69, 27) },
      new List<Color> { Color.FromRgb(43, 111, 102), Color.FromRgb(38, 101, 78), Color.FromRgb(33, 62, 54) },
      new List<Color> { Color.FromRgb(67, 91, 133), Color.FromRgb(50, 76, 121), Color.FromRgb(36, 49, 70) },
      new List<Color> { Color.FromRgb(137, 141, 41), Color.FromRgb(130, 129, 42), Color.FromRgb(85, 86, 11) },
      new List<Color> { Color.FromRgb(149, 94, 31), Color.FromRgb(128, 86, 25), Color.FromRgb(78, 53, 21) }
    };

    public OverlayWindow(bool configure = false)
    {
      InitializeComponent();

      string width = ConfigUtil.GetApplicationSetting("OverlayWidth");
      string height = ConfigUtil.GetApplicationSetting("OverlayHeight");
      string top = ConfigUtil.GetApplicationSetting("OverlayTop");
      string left = ConfigUtil.GetApplicationSetting("OverlayLeft");

      var margin = SystemParameters.WindowNonClientFrameThickness;
      bool offsetSize = configure || width == null || height == null || top == null || left == null;

      if (!offsetSize)
      {
        CreateRows();
        Title = "Overlay";
        MinHeight = 0;
        UpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1500) };
        UpdateTimer.Tick += UpdateTimerTick;
        AllowsTransparency = true;
        Style = null;
        WindowStyle = WindowStyle.None;
        SetVisible(false);
        ShowActivated = false;
      }
      else
      {
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

      string value = ConfigUtil.GetApplicationSetting("OverlayFontSize");
      bool fontHasBeenSet = false;
      int currentFontSize = DEFAULT_TEXT_FONT_SIZE;
      if (value != null && int.TryParse(value, out currentFontSize) && currentFontSize >= 0 && currentFontSize <= 64)
      {
        foreach (var item in fontSizeSelection.Items)
        {
          if ((item as ComboBoxItem).Content as string == value)
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
        settingsButton.Click += (object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow)?.OpenOverlay(true, false);
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
        refreshButton.Click += (object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow)?.ResetOverlay();

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

    private void LoadTestData()
    {
#pragma warning disable CA1303 // Do not pass literals as localized parameters
      TitleBlock.Text = "Example NPC Name That is Kinda Long";
      TitleDamageBlock.Text = "[3s @250K] 250M";

      for (int i = 0; i < MAX_ROWS - 1; i++)
      {
        NameBlockList[i].Text = i + ". Example Player Name";
        DamageBlockList[i].Text = "[3s @50K] 50M";
        DamageRateList[i].Icon = FontAwesomeIcon.LongArrowUp;
        DamageRateList[i].Opacity = DATA_OPACITY;
      }
#pragma warning restore CA1303 // Do not pass literals as localized parameters
    }

    private void NpcDamageManager_EventsPlayerAttackProcessed(object sender, DamageProcessedEvent e)
    {
      lock (StatsLock)
      {
        Stats = DamageStatsManager.Instance.ComputeOverlayDamageStats(e.Record, e.BeginTime, Stats);
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
        if (Stats != null && Stats.UniqueNpcs.ContainsKey(e.Name))
        {
          Stats.UniqueNpcs.Remove(e.Name);
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

          if (Stats == null || (DateTime.Now - DateTime.MinValue.AddSeconds(Stats.RaidStats.LastTime)).TotalSeconds > DataManager.FIGHT_TIMEOUT)
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
              TitleDamageBlock.Text = string.Format("{0} [{1}s @{2}]", StatsUtil.FormatTotals(Stats.RaidStats.Total), Stats.RaidStats.TotalSeconds, StatsUtil.FormatTotals(Stats.RaidStats.DPS));
              //TitleDamageBlock.Text = string.Format("{0}/s   {1} [{2}s @{3}]", HitRate, StatsUtil.FormatTotals(Stats.RaidStats.Total), Stats.RaidStats.TotalSeconds, StatsUtil.FormatTotals(Stats.RaidStats.DPS));

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
                    RectangleList[i].Width = this.Width;
                  }
                  else
                  {
                    RectangleList[i].Visibility = Visibility.Hidden; // maybe it calculates width better
                    RectangleList[i].Width = Convert.ToDouble(list[i].Total) / total * this.Width;
                  }

                  string playerName = ConfigUtil.PlayerName;
                  var isMe = !string.IsNullOrEmpty(playerName) && list[i].Name.StartsWith(playerName, StringComparison.OrdinalIgnoreCase) &&
                    (playerName.Length >= list[i].Name.Length || list[i].Name[playerName.Length] == ' ');
                  if (MainWindow.IsHideOverlayOtherPlayersEnabled && !isMe)
                  {
                    NameBlockList[i].Text = list[i].Rank + ". " + "Hidden Player";
                  }
                  else
                  {
                    NameBlockList[i].Text = list[i].Rank + ". " + list[i].Name;
                  }

                  if (i <= 3 && !isMe && list[i].Total > 0)
                  {
                    topList[i] = list[i].Total;
                  }
                  else if (isMe)
                  {
                    me = list[i].Total;
                  }

                  var damage = StatsUtil.FormatTotals(list[i].Total) + " [" + list[i].TotalSeconds + "s @" + StatsUtil.FormatTotals(list[i].DPS) + "]";
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
                          DamageRateList[i].Foreground = DOWN_BRUSH;
                          DamageRateList[i].Opacity = DATA_OPACITY;
                        }
                        else if (PrevList[i] < diff)
                        {
                          DamageRateList[i].Icon = FontAwesomeIcon.LongArrowUp;
                          DamageRateList[i].Foreground = UP_BRUSH;
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
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
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
      configPanel.SetValue(Canvas.TopProperty, rowHeight * MAX_ROWS);

      if (!Active)
      {
        for (int i = 0; i < MAX_ROWS; i++)
        {
          SetSize(RectangleList[i], rowHeight, width);
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
      configPanel.SetValue(Canvas.RightProperty, 10.0);

      TitleRectangle = CreateRectangle(configure, TitleColorList);
      overlayCanvas.Children.Add(TitleRectangle);

      TitlePanel = CreateNameStackPanel();
      TitleBlock = CreateTextBlock();
      TitleBlock.Foreground = TITLE_BRUSH;
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
        var rectangle = CreateRectangle(configure, ColorList[i]);
        RectangleList.Add(rectangle);
        overlayCanvas.Children.Add(rectangle);

        var nameStack = CreateNameStackPanel();
        NamePanels.Add(nameStack);

        var nameBlock = CreateTextBlock();
        nameBlock.SetValue(Canvas.LeftProperty, 5.0);
        NameBlockList.Add(nameBlock);
        nameStack.Children.Add(nameBlock);
        overlayCanvas.Children.Add(nameStack);

        var damageStack = CreateDamageStackPanel();
        DamagePanels.Add(damageStack);

        var damageRate = CreateImageAwesome();
        DamageRateList.Add(damageRate);
        damageStack.Children.Add(damageRate);

        var damageBlock = CreateTextBlock();
        DamageBlockList.Add(damageBlock);
        damageStack.Children.Add(damageBlock);
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
      fontSizeLabel.FontSize = size - 1;
      fontSizeSelection.FontSize = size - 1;
      saveButton.FontSize = size - 1;

      TitleBlock.FontSize = size;
      TitleDamageBlock.FontSize = size;

      for (int i = 0; i < MAX_ROWS; i++)
      {
        NameBlockList[i].FontSize = size;
        DamageRateList[i].Height = size;
        DamageRateList[i].Width = size;
        DamageRateList[i].MaxHeight = size;
        DamageRateList[i].MaxWidth = size;
        DamageBlockList[i].FontSize = size;
      }
    }

    private void PanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
      Resize(e.NewSize.Height, e.NewSize.Width);
    }

    private void FontSizeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (TitleBlock != null && int.TryParse((fontSizeSelection.SelectedValue as ComboBoxItem).Content as string, out int size))
      {
        SetFont(size);
      }
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
      if (!double.IsNaN(overlayCanvas.ActualHeight) && overlayCanvas.ActualHeight > 0)
      {
        ConfigUtil.SetApplicationSetting("OverlayHeight", overlayCanvas.ActualHeight.ToString(CultureInfo.CurrentCulture));
      }

      if (!double.IsNaN(overlayCanvas.ActualWidth) && overlayCanvas.ActualWidth > 0)
      {
        ConfigUtil.SetApplicationSetting("OverlayWidth", overlayCanvas.ActualWidth.ToString(CultureInfo.CurrentCulture));
      }

      var margin = SystemParameters.WindowNonClientFrameThickness;
      if (Top + margin.Top >= SystemParameters.VirtualScreenTop && (Left + margin.Left) >= SystemParameters.VirtualScreenLeft)
      {
        ConfigUtil.SetApplicationSetting("OverlayTop", (Top + margin.Top).ToString(CultureInfo.CurrentCulture));
        ConfigUtil.SetApplicationSetting("OverlayLeft", (Left + margin.Left).ToString(CultureInfo.CurrentCulture));
      }

      if (TitleBlock != null && int.TryParse((fontSizeSelection.SelectedValue as ComboBoxItem).Content as string, out int size))
      {
        ConfigUtil.SetApplicationSetting("OverlayFontSize", size.ToString(CultureInfo.CurrentCulture));
      }

      (Application.Current.MainWindow as MainWindow)?.OpenOverlay(false, true);
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

    private static LinearGradientBrush CreateBrush(List<Color> colors)
    {
      var brush = new LinearGradientBrush
      {
        StartPoint = new Point(0.5, 0),
        EndPoint = new Point(0.5, 1)
      };

      brush.GradientStops.Add(new GradientStop(colors[0], 0.0));
      brush.GradientStops.Add(new GradientStop(colors[1], 0.5));
      brush.GradientStops.Add(new GradientStop(colors[2], 0.75));
      return brush;
    }

    private static Button CreateButton(string tooltip, string content, double size)
    {
      var button = new Button() { Background = null, BorderBrush = null, IsEnabled = true };
      button.SetValue(Panel.ZIndexProperty, 3);
      button.Foreground = TEXT_BRUSH;
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

    private static Rectangle CreateRectangle(bool configure, List<Color> colors)
    {
      var rectangle = new Rectangle
      {
        Fill = CreateBrush(colors)
      };

      rectangle.SetValue(Panel.ZIndexProperty, 1);
      rectangle.Effect = new BlurEffect { Radius = 5, RenderingBias = 0 };
      rectangle.Opacity = configure ? 1.0 : DATA_OPACITY;
      return rectangle;
    }

    private static TextBlock CreateTextBlock()
    {
      var textBlock = new TextBlock { Foreground = TEXT_BRUSH };
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
      stack.SetValue(Canvas.RightProperty, 5.0);
      return stack;
    }

    private static StackPanel CreateNameStackPanel()
    {
      var stack = new StackPanel { Orientation = Orientation.Horizontal };
      stack.SetValue(Panel.ZIndexProperty, 3);
      stack.SetValue(Canvas.LeftProperty, 5.0);
      return stack;
    }

    private static ImageAwesome CreateImageAwesome()
    {
      var image = new ImageAwesome { Margin = new Thickness { Bottom = 1, Right = 2 }, Opacity = 0.0, Foreground = UP_BRUSH, Icon = FontAwesomeIcon.None };
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
  }
}
