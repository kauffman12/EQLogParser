using FontAwesome.WPF;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
    private static SolidColorBrush TEXT_BRUSH = new SolidColorBrush(Colors.White);
    private static SolidColorBrush UP_BRUSH = new SolidColorBrush(Colors.White);
    private static SolidColorBrush DOWN_BRUSH = new SolidColorBrush(Colors.Red);
    private const int DEFAULT_TEXT_FONT_SIZE = 13;
    private const int MAX_ROWS = 5;
    private const double OPACITY = 0.45;
    private const double DATA_OPACITY = 0.85;

    private OverlayDamageStats Stats = null;
    private DispatcherTimer UpdateTimer;
    private double LastUpdate = 0;
    private int RowCount = 0;
    private double CalculatedRowHeight = 0;
    private bool Active = false;
    private bool ProcessDirection = false;

    private Button CopyButton;
    private Button RefreshButton;
    private Button SettingsButton;
    private StackPanel TitlePanel;
    private TextBlock TitleBlock;
    private TextBlock TitleDamageBlock;
    private Rectangle TitleRectangle;
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
        Top = offsetSize ? dvalue - margin.Top : dvalue;
      }

      if (left != null && double.TryParse(left, out dvalue) && !double.IsNaN(dvalue))
      {
        Left = offsetSize ? dvalue - margin.Left : dvalue;
      }

      string value = ConfigUtil.GetApplicationSetting("OverlayFontSize");
      bool fontHasBeenSet = false;
      if (value != null && int.TryParse(value, out int ivalue) && ivalue >= 0 && ivalue <= 64)
      {
        foreach (var item in fontSizeSelection.Items)
        {
          if ((item as ComboBoxItem).Content as string == value)
          {
            fontSizeSelection.SelectedItem = item;
            SetFont(ivalue);
            fontHasBeenSet = true;
          }
        }
      }

      if (!fontHasBeenSet)
      {
        SetFont(DEFAULT_TEXT_FONT_SIZE);
      }

      if (!offsetSize)
      {
        DamageLineParser.EventsDamageProcessed += DamageLineParser_EventsDamageProcessed;
        Active = true;
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

    private void DamageLineParser_EventsDamageProcessed(object sender, DamageProcessedEvent e)
    {
      Stats = DamageStatsManager.Instance.ComputeOverlayDamageStats(e.Record, e.BeginTime, Stats);
      if (UpdateTimer != null && !UpdateTimer.IsEnabled)
      {
        UpdateTimer.Start();
      }
    }

    private void UpdateTimerTick(object sender, EventArgs e)
    {
      // people wanted shorter delays for damage updates but I don't want the indicator to change constantly
      // so this limits it to 1/2 the current time value
      ProcessDirection = !ProcessDirection;

      if (Stats == null || (DateTime.Now - DateTime.MinValue.AddSeconds(Stats.RaidStats.LastTime)).TotalSeconds > NpcDamageManager.NPC_DEATH_TIME)
      {
        windowBrush.Opacity = 0.0;
        SetVisible(false);
        this.Height = 0;
        Stats = null;
        PrevList = null;
        UpdateTimer.Stop();
      }
      else if (Active && Stats != null && Stats.RaidStats.LastTime > LastUpdate)
      {
        var list = Stats.StatsList.Take(MAX_ROWS).ToList();
        if (list.Count > 0)
        {
          TitleBlock.Text = Stats.TargetTitle;
          TitleDamageBlock.Text = StatsUtil.FormatTotals(Stats.RaidStats.Total) + " [" + Stats.RaidStats.TotalSeconds + "s @" +
            StatsUtil.FormatTotals(Stats.RaidStats.DPS) + "]";

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
          if (this.ActualHeight != requested)
          {
            this.Height = requested;
          }

          if (overlayCanvas.Visibility != Visibility.Visible)
          {
            overlayCanvas.Visibility = Visibility.Hidden;
            TitlePanel.Visibility = Visibility.Hidden;
            TitleRectangle.Visibility = Visibility.Hidden;
            TitleBlock.Visibility = Visibility.Hidden;
            TitleDamageBlock.Visibility = Visibility.Hidden;
            TitlePanel.Height = CalculatedRowHeight;
            TitleRectangle.Height = CalculatedRowHeight;
            TitleDamageBlock.Height = CalculatedRowHeight;
            TitleBlock.Height = CalculatedRowHeight;
            overlayCanvas.Visibility = Visibility.Visible;
            TitlePanel.Visibility = Visibility.Visible;
            TitleRectangle.Visibility = Visibility.Visible;
            TitleBlock.Visibility = Visibility.Visible;
            TitleDamageBlock.Visibility = Visibility.Visible;
            windowBrush.Opacity = OPACITY;
          }

          for (int i = 0; i < MAX_ROWS; i++)
          {
            SetRowVisible(i < goodRowCount, i);
          }
        }
      }
    }

    private void SetRowVisible(bool visible, int index)
    {
      if (visible)
      {
        DamagePanels[index].Height = CalculatedRowHeight;
        NameBlockList[index].Height = CalculatedRowHeight;
        RectangleList[index].Height = CalculatedRowHeight;
        DamagePanels[index].SetValue(Canvas.TopProperty, CalculatedRowHeight * (index + 1));
        NameBlockList[index].SetValue(Canvas.TopProperty, CalculatedRowHeight * (index + 1));
        RectangleList[index].SetValue(Canvas.TopProperty, CalculatedRowHeight * (index + 1));
      }

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
      SetSize(TitleDamageBlock, rowHeight, double.NaN);

      SetSize(configPanel, rowHeight, double.NaN);
      configPanel.SetValue(Canvas.TopProperty, rowHeight * MAX_ROWS);

      if (!Active)
      {
        for (int i = 0; i < MAX_ROWS; i++)
        {
          SetSize(RectangleList[i], rowHeight, width);
          SetSize(NameBlockList[i], rowHeight, double.NaN);
          SetSize(DamagePanels[i], rowHeight, double.NaN);
          SetSize(DamageRateList[i], rowHeight, double.NaN);
          SetSize(DamageBlockList[i], rowHeight, double.NaN);

          double pos = rowHeight * (i + 1);
          RectangleList[i].SetValue(Canvas.TopProperty, pos);
          NameBlockList[i].SetValue(Canvas.TopProperty, pos);
          DamagePanels[i].SetValue(Canvas.TopProperty, pos);
          DamageRateList[i].SetValue(Canvas.TopProperty, pos);
          DamageBlockList[i].SetValue(Canvas.TopProperty, pos);
        }
      }
    }

    private void CreateRows(bool configure = false)
    {
      configPanel.SetValue(Panel.ZIndexProperty, 3);
      configPanel.SetValue(Canvas.RightProperty, 10.0);

      TitleRectangle = CreateRectangle(configure, TitleColorList);
      overlayCanvas.Children.Add(TitleRectangle);

      TitlePanel = new StackPanel { Orientation = Orientation.Horizontal };
      TitlePanel.SetValue(Canvas.LeftProperty, 5.0);
      TitlePanel.SetValue(Panel.ZIndexProperty, 2);

      SettingsButton = CreateButton();
      SettingsButton.ToolTip = new ToolTip { Content = "Change Settings" };
      SettingsButton.Margin = new Thickness(8, 1, 0, 0);
      SettingsButton.Content = "\xE713";
      SettingsButton.Visibility = configure ? Visibility.Collapsed : Visibility.Visible;
      SettingsButton.Click += (object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow)?.OpenOverlay(true, false);

      CopyButton = CreateButton();
      CopyButton.ToolTip = new ToolTip { Content = "Copy To EQ" };
      CopyButton.Margin = new Thickness(4, 1, 0, 0);
      CopyButton.Content = "\xE8C8";
      CopyButton.Visibility = configure ? Visibility.Collapsed : Visibility.Visible;
      CopyButton.Click += (object sender, RoutedEventArgs e) =>
      {
        (Application.Current.MainWindow as MainWindow)?.AddAndCopyDamageParse(Stats, Stats.StatsList);
      };

      RefreshButton = CreateButton();
      RefreshButton.ToolTip = new ToolTip { Content = "Cancel Current Parse" };
      RefreshButton.Margin = new Thickness(4, 1, 0, 0);
      RefreshButton.Content = "\xE8BB";
      RefreshButton.Visibility = configure ? Visibility.Collapsed : Visibility.Visible;
      RefreshButton.Click += (object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow)?.ResetOverlay();

      TitleBlock = CreateTextBlock();
      TitlePanel.Children.Add(TitleBlock);
      TitlePanel.Children.Add(SettingsButton);
      TitlePanel.Children.Add(CopyButton);
      TitlePanel.Children.Add(RefreshButton);
      overlayCanvas.Children.Add(TitlePanel);

      TitleDamageBlock = CreateTextBlock();
      TitleDamageBlock.SetValue(Canvas.RightProperty, 5.0);
      overlayCanvas.Children.Add(TitleDamageBlock);
      RowCount++;

      for (int i = 0; i < MAX_ROWS; i++)
      {
        var rectangle = CreateRectangle(configure, ColorList[i]);
        RectangleList.Add(rectangle);
        overlayCanvas.Children.Add(rectangle);

        var nameBlock = CreateTextBlock();
        nameBlock.SetValue(Canvas.LeftProperty, 5.0);
        NameBlockList.Add(nameBlock);
        overlayCanvas.Children.Add(nameBlock);

        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.SetValue(Panel.ZIndexProperty, 3);
        stack.SetValue(Canvas.RightProperty, 5.0);
        DamagePanels.Add(stack);

        var damageRate = CreateImageAwesome();
        DamageRateList.Add(damageRate);
        stack.Children.Add(damageRate);

        var damageBlock = CreateTextBlock();
        damageBlock.SetValue(Canvas.RightProperty, 5.0);
        DamageBlockList.Add(damageBlock);
        stack.Children.Add(damageBlock);
        overlayCanvas.Children.Add(stack);

        RowCount++;
      }
    }

    private void SetFont(int size)
    {
      fontSizeLabel.FontSize = size - 1;
      fontSizeSelection.FontSize = size - 1;
      saveButton.FontSize = size - 1;

      TitleBlock.FontSize = size;
      TitleDamageBlock.FontSize = size;
      SettingsButton.FontSize = size - 1;
      CopyButton.FontSize = size - 1;
      RefreshButton.FontSize = size - 2;

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
      if (this.Top + margin.Top > 0 && (this.Left + margin.Left) > 0)
      {
        ConfigUtil.SetApplicationSetting("OverlayTop", (this.Top + margin.Top).ToString(CultureInfo.CurrentCulture));
        ConfigUtil.SetApplicationSetting("OverlayLeft", (this.Left + margin.Left).ToString(CultureInfo.CurrentCulture));
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
        DamageLineParser.EventsDamageProcessed -= DamageLineParser_EventsDamageProcessed;

        if (UpdateTimer?.IsEnabled == true)
        {
          UpdateTimer.Stop();
        }
      }
    }

    void WindowLoaded(object sender, RoutedEventArgs e)
    {
      WindowInteropHelper wndHelper = new WindowInteropHelper(this);
      int exStyle = (int)NativeMethods.GetWindowLongPtr(wndHelper.Handle, (int)NativeMethods.GetWindowLongFields.GWL_EXSTYLE);
      exStyle |= (int)NativeMethods.ExtendedWindowStyles.WS_EX_TOOLWINDOW;
      NativeMethods.SetWindowLong(wndHelper.Handle, (int)NativeMethods.GetWindowLongFields.GWL_EXSTYLE, (IntPtr)exStyle);
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

    private static Button CreateButton()
    {
      var button = new Button();
      button.SetValue(Panel.ZIndexProperty, 3);
      button.Background = null;
      button.Foreground = TEXT_BRUSH;
      button.BorderBrush = null;
      button.VerticalAlignment = VerticalAlignment.Top;
      button.Padding = new Thickness(0, 0, 0, 0);
      button.FontFamily = new FontFamily("Segoe MDL2 Assets");
      button.IsEnabled = true;
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
      return textBlock;
    }

    private static ImageAwesome CreateImageAwesome()
    {
      var image = new ImageAwesome { Margin = new Thickness(0, 0, 2, 8), Opacity = 0.0, Foreground = UP_BRUSH, Icon = FontAwesomeIcon.None };
      image.SetValue(Panel.ZIndexProperty, 3);
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
