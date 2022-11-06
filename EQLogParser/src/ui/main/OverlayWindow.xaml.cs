using FontAwesome5;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
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

    private const int MINDELAY = 10000;
    private static readonly object StatsLock = new object();
    private readonly List<StackPanel> NamePanels = new List<StackPanel>();
    private readonly List<Image> NameIconList = new List<Image>();
    private readonly List<TextBlock> NameBlockList = new List<TextBlock>();
    private readonly List<StackPanel> DamagePanels = new List<StackPanel>();
    private readonly List<TextBlock> DamageBlockList = new List<TextBlock>();
    private readonly List<ImageAwesome> DamageRateList = new List<ImageAwesome>();
    private readonly List<Rectangle> EmptyList = new List<Rectangle>();
    private readonly List<Rectangle> RectangleList = new List<Rectangle>();
    private readonly List<Color> ColorList = new List<Color>();
    private readonly Popup ButtonPopup;
    private readonly StackPanel ButtonsPanel;
    private readonly DispatcherTimer UpdateTimer;
    private readonly Button SettingsButton;
    private readonly Button CopyButton;
    private readonly Button RefreshButton;

    private double CalculatedRowHeight;
    private int CurrentDamageSelectionMode;
    private CombinedStats Stats = null;
    private int ProcessDirection = 0;
    private TextBlock TitleBlock;
    private StackPanel TitlePanel;
    private TextBlock TitleDamageBlock;
    private StackPanel TitleDamagePanel;
    private Rectangle TitleRectangle;
    private Dictionary<int, double> PrevList = null;
    private bool IsHideOverlayOtherPlayersEnabled = false;
    private bool IsShowOverlayCritRateEnabled = false;
    private string SelectedClass;
    private int CurrentMaxRows = 5;
    private int CurrentFontSize = 13;
    private CancellationTokenSource CancelToken = null;
    private bool Active = false;

    public OverlayWindow()
    {
      InitializeComponent();
      Application.Current.Resources["OverlayCurrentBrush"] = Application.Current.Resources["OverlayActiveBrush"];

      UpdateSettings();
      OverlayUtil.SetVisible(overlayCanvas, false);

      OverlayUtil.CreateTitleRow(OverlayUtil.TITLEBRUSH, overlayCanvas, out TitleRectangle, out TitlePanel, out TitleBlock,
        out TitleDamagePanel, out TitleDamageBlock);

      MinHeight = 0;
      UpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1000) };
      UpdateTimer.Tick += UpdateTimerTick;

      DataManager.Instance.EventsNewOverlayFight += NewOverlayFight;

      RefreshButton = OverlayUtil.CreateButton("Cancel Current Parse", "\xE8BB");
      RefreshButton.Click += ClickRefresh;

      SettingsButton = OverlayUtil.CreateButton("Change Settings", "\xE713");
      SettingsButton.Click += ClickSettings;

      CopyButton = OverlayUtil.CreateButton("Copy Parse", "\xE8C8");
      CopyButton.Click += ClickCopy;

      ButtonPopup = new Popup();
      ButtonsPanel = OverlayUtil.CreateNameStackPanel();
      ButtonsPanel.Children.Add(SettingsButton);
      ButtonsPanel.Children.Add(CopyButton);
      ButtonsPanel.Children.Add(RefreshButton);
      ButtonPopup.Child = ButtonsPanel;
      ButtonPopup.AllowsTransparency = true;
      ButtonPopup.Opacity = OverlayUtil.OPACITY;
      ButtonPopup.Placement = PlacementMode.Left;
      ButtonPopup.PlacementTarget = TitlePanel;
      ButtonPopup.VerticalOffset = -2;
      ButtonPopup.HorizontalOffset = -5;
      ButtonsPanel.SizeChanged += ChangeButtonPanelSize;

      TitlePanel.SizeChanged += TitleResizing;
      TitleDamagePanel.SizeChanged += TitleResizing;

      CreateRows();
      SetFont();

      if (DataManager.Instance.HasOverlayFights())
      {
        UpdateTimer.Start();
      }

      Active = true;
    }

    public void Pause()
    {
      if (Active)
      {
        DataManager.Instance.EventsNewOverlayFight -= NewOverlayFight;
        Active = false;
        Reset(false);
      }
    }

    public void Resume()
    {
      if (!Active)
      {
        UpdateSettings();
        SetFont();
        DataManager.Instance.EventsNewOverlayFight += NewOverlayFight;
        Active = true;

        if (Stats != null)
        {
          ShowData();
          UpdateTimer.Start();
        }
      }
    }

    private void ClickRefresh(object sender, RoutedEventArgs e) => Reset(true);
    private void ClickSettings(object sender, RoutedEventArgs e) => OverlayUtil.OpenOverlay(true, false);

    private void ClickCopy(object sender, RoutedEventArgs e)
    {
      lock (StatsLock)
      {
        (Application.Current.MainWindow as MainWindow)?.AddAndCopyDamageParse(Stats, Stats.StatsList);
      }
    }

    private void ChangeButtonPanelSize(object sender, SizeChangedEventArgs e)
    {
      if (TitlePanel.Margin.Left != e.NewSize.Width + 2)
      {
        TitlePanel.Margin = new Thickness(e.NewSize.Width + 2, TitlePanel.Margin.Top, 0, TitlePanel.Margin.Bottom);
      }

      if (ButtonsPanel.ActualHeight != TitlePanel.ActualHeight)
      {
        ButtonsPanel.Height = TitlePanel.ActualHeight;
      }

      Dispatcher.InvokeAsync(() =>
      {
        // trigger placement event for popup
        ButtonPopup.HorizontalOffset += 1;
        ButtonPopup.HorizontalOffset -= 1;
      });
    }

    private void UpdateSettings()
    {
      var prevMaxRows = CurrentMaxRows;
      OverlayUtil.LoadSettings(ColorList, out IsHideOverlayOtherPlayersEnabled, out IsShowOverlayCritRateEnabled, out SelectedClass,
        out CurrentDamageSelectionMode, out CurrentFontSize, out CurrentMaxRows, out string width, out string height, out string top, out string left);

      if (CurrentMaxRows != prevMaxRows)
      {
        RectangleList.ForEach(rectangle => overlayCanvas.Children.Remove(rectangle));
        EmptyList.ForEach(empty => overlayCanvas.Children.Remove(empty));
        NamePanels.ForEach(nameStack => overlayCanvas.Children.Remove(nameStack));
        DamagePanels.ForEach(damageStack => overlayCanvas.Children.Remove(damageStack));
        RectangleList.Clear();
        EmptyList.Clear();
        NamePanels.Clear();
        DamagePanels.Clear();
        DamageBlockList.Clear();
        DamageRateList.Clear();
        NameBlockList.Clear();
        NameIconList.Clear();
        CreateRows();
      }

      var margin = SystemParameters.WindowNonClientFrameThickness;
      if (width != null && double.TryParse(width, out double dvalue) && !double.IsNaN(dvalue))
      {
        Width = dvalue;
      }

      if (height != null && double.TryParse(height, out dvalue) && !double.IsNaN(dvalue))
      {
        CalculatedRowHeight = dvalue / (CurrentMaxRows + 1);
      }

      if (top != null && double.TryParse(top, out dvalue) && !double.IsNaN(dvalue))
      {
        var test = dvalue - margin.Top;
        if (test >= SystemParameters.VirtualScreenTop && test < SystemParameters.VirtualScreenHeight)
        {
          Top = test;
        }
      }

      if (left != null && double.TryParse(left, out dvalue) && !double.IsNaN(dvalue))
      {
        var test = dvalue;
        if (test >= SystemParameters.VirtualScreenLeft && test < SystemParameters.VirtualScreenWidth)
        {
          Left = test;
        }
      }
    }

    private void NewOverlayFight(object sender, Fight fight)
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
          ProcessDirection = ProcessDirection >= 3 ? 0 : ProcessDirection + 1;

          Stats = DamageStatsManager.ComputeOverlayStats(Stats == null, CurrentDamageSelectionMode, CurrentMaxRows, SelectedClass);

          if (Stats == null)
          {
            Reset(true);
          }
          else
          {
            ShowData();
          }
        }
        catch (Exception ex)
        {
          LOG.Error("Overlay Error", ex);
        }
      }
    }

    private void ShowData()
    {
      TitleBlock.Text = Stats.TargetTitle;
      TitleDamageBlock.Text = string.Format(CultureInfo.CurrentCulture, "{0} [{1}s @{2}]",
        StatsUtil.FormatTotals(Stats.RaidStats.Total), Stats.RaidStats.TotalSeconds, StatsUtil.FormatTotals(Stats.RaidStats.DPS));

      long total = 0;
      int goodRowCount = 0;
      int meIndex = 0;
      long meDamage = 0;
      var topList = new Dictionary<int, long>();
      for (int i = 0; i < CurrentMaxRows; i++)
      {
        if (Stats.StatsList.Count > i)
        {
          if (ProcessDirection == 3)
          {
            DamageRateList[i].Opacity = 0.0;
          }

          if (i == 0)
          {
            total = Stats.StatsList[i].Total;
            RectangleList[i].Width = Width;
            EmptyList[i].Width = 0;
          }
          else
          {
            RectangleList[i].Visibility = Visibility.Hidden; // maybe it calculates width better
            RectangleList[i].Width = Convert.ToDouble(Stats.StatsList[i].Total) / total * Width;
            EmptyList[i].Width = (Width - RectangleList[i].Width);
            EmptyList[i].SetValue(Canvas.LeftProperty, RectangleList[i].Width);
          }

          string playerName = ConfigUtil.PlayerName;
          var isMe = !string.IsNullOrEmpty(playerName) && Stats.StatsList[i].Name.StartsWith(playerName, StringComparison.OrdinalIgnoreCase) &&
            (playerName.Length >= Stats.StatsList[i].Name.Length || Stats.StatsList[i].Name[playerName.Length] == ' ');

          string updateText;
          if (IsHideOverlayOtherPlayersEnabled && !isMe)
          {
            updateText = string.Format(CultureInfo.CurrentCulture, "{0}. Hidden Player", Stats.StatsList[i].Rank);
            NameIconList[i].Source = PlayerManager.UNK_ICON;
          }
          else
          {
            updateText = string.Format(CultureInfo.CurrentCulture, "{0}. {1}", Stats.StatsList[i].Rank, Stats.StatsList[i].Name);
            NameIconList[i].Source = PlayerManager.Instance.GetPlayerIcon(Stats.StatsList[i].OrigName);
          }

          if (IsShowOverlayCritRateEnabled)
          {
            List<string> critMods = new List<string>();

            if (isMe && PlayerManager.Instance.IsDoTClass(Stats.StatsList[i].ClassName) && DataManager.Instance.MyDoTCritRateMod is uint doTCritRate && doTCritRate > 0)
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

          if (i <= (CurrentMaxRows - 1) && !isMe && Stats.StatsList[i].Total > 0)
          {
            topList[i] = Stats.StatsList[i].Total;
          }
          else if (isMe)
          {
            meIndex = i;
            meDamage = Stats.StatsList[i].Total;
          }

          var damage = StatsUtil.FormatTotals(Stats.StatsList[i].Total) + " [" + Stats.StatsList[i].TotalSeconds.ToString(CultureInfo.CurrentCulture)
            + "s @" + StatsUtil.FormatTotals(Stats.StatsList[i].DPS) + "]";
          DamageBlockList[i].Text = damage;
          goodRowCount++;
        }
      }

      if (ProcessDirection == 3)
      {
        if (meIndex >= 0 && topList.Count > 0)
        {
          var updatedList = new Dictionary<int, double>();
          foreach (int i in topList.Keys)
          {
            if (i != meIndex)
            {
              var diff = topList[i] / (double)meDamage;
              updatedList[i] = diff;
              if (PrevList != null && PrevList.ContainsKey(i))
              {
                if (PrevList[i] > diff)
                {
                  DamageRateList[i].Icon = EFontAwesomeIcon.Solid_LongArrowAltDown;
                  DamageRateList[i].Foreground = OverlayUtil.DOWNBRUSH;
                  DamageRateList[i].Opacity = OverlayUtil.DATA_OPACITY;
                }
                else if (PrevList[i] < diff)
                {
                  DamageRateList[i].Icon = EFontAwesomeIcon.Solid_LongArrowAltUp;
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
      if (ActualHeight != requested && Height != requested)
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
        ButtonPopup.IsOpen = true;
      }

      for (int i = 0; i < CurrentMaxRows; i++)
      {
        SetRowVisible(i < goodRowCount, i);
      }
    }

    private void Reset(bool clearFights = false)
    {
      UpdateTimer.Stop();

      if (clearFights)
      {
        Stats = null;
        DataManager.Instance.ResetOverlayFights();
      }

      PrevList = null;

      if (CurrentDamageSelectionMode > 0)
      {
        ButtonPopup.IsOpen = false;
        OverlayUtil.SetVisible(overlayCanvas, false);
        Height = 0;
      }
      else
      {
        if (CancelToken != null)
        {
          CancelToken.Cancel();
          CancelToken.Dispose();
          CancelToken = null;
        }

        CancelToken = new CancellationTokenSource();
        Task.Delay(MINDELAY).ContinueWith(task =>
        {
          Dispatcher.BeginInvoke(() =>
          {
            lock (StatsLock)
            {
              if (!UpdateTimer.IsEnabled && Height > 0)
              {
                ButtonPopup.IsOpen = false;
                OverlayUtil.SetVisible(overlayCanvas, false);
                Height = 0;
              }

              if (CancelToken != null)
              {
                CancelToken.Cancel();
                CancelToken.Dispose();
                CancelToken = null;
              }
            }
          });
        }, CancelToken.Token);
      }
    }

    private void SetRowVisible(bool visible, int index)
    {
      if (visible)
      {
        DamagePanels[index].Height = CalculatedRowHeight;
        NamePanels[index].Height = CalculatedRowHeight;
        RectangleList[index].Height = CalculatedRowHeight;
        EmptyList[index].Height = CalculatedRowHeight;
        DamagePanels[index].SetValue(Canvas.TopProperty, CalculatedRowHeight * (index + 1));
        NamePanels[index].SetValue(Canvas.TopProperty, CalculatedRowHeight * (index + 1));
        RectangleList[index].SetValue(Canvas.TopProperty, CalculatedRowHeight * (index + 1));
        EmptyList[index].SetValue(Canvas.TopProperty, CalculatedRowHeight * (index + 1));
      }

      NamePanels[index].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
      NameBlockList[index].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
      NameIconList[index].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
      DamagePanels[index].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
      DamageRateList[index].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
      DamageBlockList[index].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
      RectangleList[index].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
      EmptyList[index].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Resize(double height)
    {
      double rowHeight = CalculatedRowHeight > 0 ? CalculatedRowHeight : height / (CurrentMaxRows + 1);

      UIElementUtil.SetSize(TitlePanel, rowHeight, double.NaN);
      UIElementUtil.SetSize(TitleDamagePanel, rowHeight, double.NaN);
      // when active title needs to be as long as normal rows
      UIElementUtil.SetSize(TitleRectangle, rowHeight, Width);
    }

    private void CreateRows()
    {
      for (int i = 0; i < CurrentMaxRows; i++)
      {
        var rectangle = OverlayUtil.CreateRectangle(ColorList[i], OverlayUtil.DATA_OPACITY);
        RectangleList.Add(rectangle);
        overlayCanvas.Children.Add(rectangle);

        var empty = OverlayUtil.CreateRectangle("OverlayCurrentBrush", OverlayUtil.OPACITY);
        EmptyList.Add(empty);
        overlayCanvas.Children.Add(empty);

        var nameStack = OverlayUtil.CreateNameStackPanel();
        NamePanels.Add(nameStack);

        var classImage = OverlayUtil.CreateImage();
        NameIconList.Add(classImage);
        nameStack.Children.Add(classImage);

        var nameBlock = OverlayUtil.CreateTextBlock();
        NameBlockList.Add(nameBlock);
        nameStack.Children.Add(nameBlock);
        overlayCanvas.Children.Add(nameStack);

        var damageStack = OverlayUtil.CreateDamageStackPanel();
        DamagePanels.Add(damageStack);
        var damageRate = OverlayUtil.CreateImageAwesome();
        DamageRateList.Add(damageRate);
        damageStack.Children.Add(damageRate);

        var damageBlock = OverlayUtil.CreateTextBlock();
        DamageBlockList.Add(damageBlock);
        damageStack.Children.Add(damageBlock);
        overlayCanvas.Children.Add(damageStack);
      }
    }

    private void TitleResizing(object sender, SizeChangedEventArgs e)
    {
      TitlePanel.MaxWidth = ActualWidth - TitleDamagePanel.ActualWidth - ButtonsPanel.ActualWidth - 24;

      if (ButtonsPanel.ActualHeight != TitlePanel.ActualHeight)
      {
        ButtonsPanel.Height = TitlePanel.ActualHeight;
      }

      Dispatcher.InvokeAsync(() =>
      {
        // trigger placement event for popup
        ButtonPopup.HorizontalOffset += 1;
        ButtonPopup.HorizontalOffset -= 1;
      });
    }

    private void SetFont()
    {
      int size = CurrentFontSize;
      TitleBlock.FontSize = size;
      TitleDamageBlock.FontSize = size;

      SettingsButton.FontSize = size;
      CopyButton.FontSize = size - 1;
      RefreshButton.FontSize = size - 2;

      for (int i = 0; i < CurrentMaxRows; i++)
      {
        NameBlockList[i].FontSize = size;
        NameIconList[i].Height = size + 4;
        NameIconList[i].MaxHeight = size + 4;

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

    private void PanelSizeChanged(object sender, SizeChangedEventArgs e) => Resize(e.NewSize.Height);

    private void WindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
      if (UpdateTimer != null)
      {
        UpdateTimer.Stop();
        UpdateTimer.Tick -= UpdateTimerTick;
      }

      RefreshButton.Click -= ClickRefresh;
      SettingsButton.Click -= ClickSettings;
      CopyButton.Click -= ClickCopy;
      TitlePanel.SizeChanged -= TitleResizing;
      TitleDamagePanel.SizeChanged -= TitleResizing;
      ButtonsPanel.SizeChanged -= ChangeButtonPanelSize;
      ButtonPopup.IsOpen = false;

      if (Active)
      {
        DataManager.Instance.EventsNewOverlayFight -= NewOverlayFight;
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
