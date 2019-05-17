using ActiproSoftware.Windows.Controls.Docking;
using ActiproSoftware.Windows.Themes;
using FontAwesome.WPF;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace EQLogParser
{
  public partial class MainWindow : Window
  {
    // binding property
    public ObservableCollection<SortableName> VerifiedPlayersProperty { get; } = new ObservableCollection<SortableName>();

    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private enum LogOption { OPEN, MONITOR, ARCHIVE };

    private static SolidColorBrush WARNING_BRUSH = new SolidColorBrush(Color.FromRgb(241, 109, 29));
    private static SolidColorBrush BRIGHT_TEXT_BRUSH = new SolidColorBrush(Colors.White);
    private static SolidColorBrush LIGHTER_BRUSH = new SolidColorBrush(Color.FromRgb(90, 90, 90));
    private static SolidColorBrush GOOD_BRUSH = new SolidColorBrush(Colors.LightGreen);

    private static readonly Regex ParseFileName = new Regex(@"^eqlog_([a-zA-Z]+)_([a-zA-Z]+).*\.txt", RegexOptions.Singleline | RegexOptions.Compiled); 
    private static List<string> DAMAGE_CHOICES = new List<string>() { "DPS", "Damage", "Av Hit", "% Crit" };
    private static List<string> HEALING_CHOICES = new List<string>() { "HPS", "Healing", "Av Heal", "% Crit" };
    private static List<string> TANKING_CHOICES = new List<string>() { "DPS", "Damaged", "Av Hit" };

    private const string APP_NAME = "EQ Log Parser";
    private const string VERSION = "v1.5.9";
    private const string SHARE_DPS_LABEL = "No Players Selected";
    private const string SHARE_DPS_TOO_BIG_LABEL = "Exceeded Copy/Paste Limit for EQ";

    private static long CastLineCount = 0;
    private static long DamageLineCount = 0;
    private static long HealLineCount = 0;
    private static long CastLinesProcessed = 0;
    private static long DamageLinesProcessed = 0;
    private static long HealLinesProcessed = 0;
    private static long FilePosition = 0;

    private static ActionProcessor<string> CastProcessor = null;
    private static ActionProcessor<string> DamageProcessor = null;
    private static ActionProcessor<string> HealingProcessor = null;

    // progress window
    private static DateTime StartLoadTime;
    private static LogOption CurrentLogOption;

    private ObservableCollection<SortableName> VerifiedPetsView = new ObservableCollection<SortableName>();
    private ObservableCollection<PetMapping> PetPlayersView = new ObservableCollection<PetMapping>();
    private ObservableCollection<string> AvailableParses = new ObservableCollection<string>();

    private ChatManager PlayerChatManager;
    private NpcDamageManager NpcDamageManager = new NpcDamageManager();
    private Dictionary<string, ParseData> Parses = new Dictionary<string, ParseData>();

    Dictionary<string, DockingWindow> IconToWindow;
    private DocumentWindow ChatWindow = null;
    private DocumentWindow DamageWindow = null;
    private DocumentWindow HealingWindow = null;
    private DocumentWindow TankingWindow = null;
    private DocumentWindow DamageChartWindow = null;
    private DocumentWindow HealingChartWindow = null;
    private DocumentWindow TankingChartWindow = null;

    private LogReader EQLogReader = null;
    private OverlayWindow Overlay = null;
    private int BusyCount = 0;

    public MainWindow()
    {
      try
      {
        InitializeComponent();

        // update titles
        Title = APP_NAME + " " + VERSION;

        // used for setting menu icons based on open windows
        IconToWindow = new Dictionary<string, DockingWindow>()
        {
          { npcIcon.Name, npcWindow }, { verifiedPlayersIcon.Name, verifiedPlayersWindow },
          { verifiedPetsIcon.Name, verifiedPetsWindow }, { petMappingIcon.Name, petMappingWindow },
          { playerParseIcon.Name, playerParseTextWindow }, { fileProgessIcon.Name, progressWindow }
        };

        // Clear/Reset
        DataManager.Instance.EventsClearedActiveData += Instance_EventsClearedActiveData;

        // pet -> players
        petMappingGrid.ItemsSource = PetPlayersView;
        DataManager.Instance.EventsUpdatePetMapping += Instance_EventsUpdatePetMapping;

        // verified pets table
        verifiedPetsGrid.ItemsSource = VerifiedPetsView;
        DataManager.Instance.EventsNewVerifiedPet += (sender, name) => Dispatcher.InvokeAsync(() =>
        {
          Helpers.InsertNameIntoSortedList(name, VerifiedPetsView);
          verifiedPetsWindow.Title = "Pets (" + VerifiedPetsView.Count + ")";
        });

        // verified player table
        verifiedPlayersGrid.ItemsSource = VerifiedPlayersProperty;
        DataManager.Instance.EventsNewVerifiedPlayer += (sender, name) => Dispatcher.InvokeAsync(() =>
        {
          Helpers.InsertNameIntoSortedList(name, VerifiedPlayersProperty);
          verifiedPlayersWindow.Title = "Players (" + VerifiedPlayersProperty.Count + ")";
        });

        parseList.ItemsSource = AvailableParses;
        parseList.SelectedIndex = -1;

        CastLineParser.EventsLineProcessed += (sender, data) => CastLinesProcessed++;
        DamageLineParser.EventsLineProcessed += (sender, data) => DamageLinesProcessed++;
        HealingLineParser.EventsLineProcessed += (sender, data) => HealLinesProcessed++;

        HealingLineParser.EventsHealProcessed += (sender, data) => DataManager.Instance.AddHealRecord(data.Record, data.BeginTime);
        DamageLineParser.EventsDamageProcessed += (sender, data) => DataManager.Instance.AddDamageRecord(data.Record, data.IsPlayerDamage, data.BeginTime);
        DamageLineParser.EventsResistProcessed += (sender, data) => DataManager.Instance.AddResistRecord(data.Record, data.BeginTime);

        (npcWindow.Content as NpcTable).EventsSelectionChange += (sender, data) => ComputeStats();

        DamageStatsManager.Instance.EventsUpdateDataPoint += (sender, data) => HandleChartUpdateEvent(DamageChartWindow, sender, data);
        HealingStatsManager.Instance.EventsUpdateDataPoint += (sender, data) => HandleChartUpdateEvent(HealingChartWindow, sender, data);
        TankingStatsManager.Instance.EventsUpdateDataPoint += (sender, data) => HandleChartUpdateEvent(TankingChartWindow, sender, data);

        DamageStatsManager.Instance.EventsGenerationStatus += Instance_EventsGenerationStatus;
        HealingStatsManager.Instance.EventsGenerationStatus += Instance_EventsGenerationStatus;
        TankingStatsManager.Instance.EventsGenerationStatus += Instance_EventsGenerationStatus;

        // Setup themes
        ThemeManager.BeginUpdate();
        ThemeManager.AreNativeThemesEnabled = true;
        SharedThemeCatalogRegistrar.Register();
        DockingThemeCatalogRegistrar.Register();
        ThemeManager.CurrentTheme = ThemeName.MetroDark.ToString();

        OpenDamageSummary();

        // application data state last
        DataManager.Instance.LoadState();
        LOG.Info("Initialized Components");
      }
      catch (Exception e)
      {
        LOG.Error(e);
        throw;
      }
      finally
      {
        ThemeManager.EndUpdate();
      }
    }

    private void Instance_EventsUpdatePetMapping(object sender, PetMapping mapping)
    {
      Dispatcher.InvokeAsync(() =>
      {
        var existing = PetPlayersView.FirstOrDefault(item => mapping.Pet == item.Pet);
        if (existing != null && existing.Owner != mapping.Owner)
        {
          existing.Owner = mapping.Owner;
        }
        else
        {
          PetPlayersView.Add(mapping);
        }

        petMappingWindow.Title = "Pet Owners (" + PetPlayersView.Count + ")";

        if ((npcWindow?.Content as NpcTable)?.GetSelectedItems()?.Count > 0)
        {
          ComputeStats();
        }
      });
    }

    private void Instance_EventsClearedActiveData(object sender, bool e)
    {
      (DamageWindow?.Content as DamageSummary)?.Clear();
      (HealingWindow?.Content as HealingSummary)?.Clear();
      (TankingWindow?.Content as TankingSummary)?.Clear();
      (DamageChartWindow?.Content as LineChart)?.Clear();
      (HealingChartWindow?.Content as LineChart)?.Clear();
      (TankingChartWindow?.Content as LineChart)?.Clear();
    }

    internal void Busy(bool state)
    {
      BusyCount += state ? 1 : -1;
      BusyCount = BusyCount < 0 ? 0 : BusyCount;
      busyIcon.Visibility = BusyCount == 0 ? Visibility.Hidden : Visibility.Visible;
    }

    internal void CopyToEQ_Click(object sender = null, RoutedEventArgs e = null)
    {
      Clipboard.SetDataObject(playerParseTextBox.Text);
    }

    internal void OpenOverlay(bool configure = false, bool saveFirst = false)
    {
      if (saveFirst)
      {
        DataManager.Instance.SaveState();
      }

      Dispatcher.InvokeAsync(() =>
      {
        Overlay?.Close();
        Overlay = new OverlayWindow(configure);
        Overlay.Show();
      });
    }

    internal void ResetOverlay()
    {
      Overlay?.Close();
      if ((DamageWindow?.Content as DamageSummary)?.IsOverlayEnabled() == true)
      {
        OpenOverlay();
      }
    }

    internal void CloseOverlay()
    {
      Overlay?.Close();
    }

    internal void AddDamageParse(CombinedStats combined, List<PlayerStats> selected)
    {
      AddParse(Labels.DAMAGEPARSE, DamageStatsManager.Instance, combined, selected);
    }

    private void Window_Closed(object sender, EventArgs e)
    {
      StopProcessing();
      taskBarIcon.Dispose();
      DataManager.Instance.SaveState();
      Application.Current.Shutdown();
    }

    private void HandleChartUpdateEvent(DocumentWindow window, object sender, DataPointEvent e)
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (window?.IsOpen == true)
        {
          (window.Content as LineChart).HandleUpdateEvent(sender, e);
        }
      });
    }

    private void ComputeStats()
    {
      var npcList = (npcWindow?.Content as NpcTable)?.GetSelectedItems();
      var filtered = npcList?.AsParallel().Where(npc => npc.GroupID != -1).OrderBy(npc => npc.ID).ToList();
      string name = filtered?.FirstOrDefault()?.Name;

      var damageOptions = new DamageStatsOptions() { Name = name, Npcs = filtered, RequestChartData = DamageChartWindow?.IsOpen == true };
      if (DamageWindow?.Content is DamageSummary damageSummary && DamageWindow?.IsOpen == true)
      {
        damageOptions.IsBaneEanbled = damageSummary.IsBaneEnabled();
        damageOptions.RequestSummaryData = true;
      }

      var healingOptions = new HealingStatsOptions() { Name = name, Npcs = filtered, RequestChartData = HealingChartWindow?.IsOpen == true };
      if (HealingWindow?.Content is HealingSummary healingSummary && HealingWindow?.IsOpen == true)
      {
        healingOptions.IsAEHealingEanbled = healingSummary.IsAEHealingEnabled();
        healingOptions.RequestSummaryData = true;
      }

      var tankingOptions = new TankingStatsOptions() { Name = name, Npcs = filtered, RequestChartData = TankingChartWindow?.IsOpen == true };
      if (TankingWindow?.Content is TankingSummary tankingSummary && TankingWindow?.IsOpen == true)
      {
        tankingOptions.RequestSummaryData = true;
      }

      Task.Run(() => DamageStatsManager.Instance.BuildTotalStats(damageOptions));
      Task.Run(() => HealingStatsManager.Instance.BuildTotalStats(healingOptions));
      Task.Run(() => TankingStatsManager.Instance.BuildTotalStats(tankingOptions));
    }

    private void PlayerParseTextWindow_Loaded(object sender, RoutedEventArgs e)
    {
      playerParseTextWindow.State = DockingWindowState.AutoHide;
    }

    // Main Menu
    private void MenuItemWindow_Click(object sender, RoutedEventArgs e)
    {
      if (e.Source == damageChartMenuItem)
      {
        OpenDamageChart();
      }
      else if (e.Source == healingChartMenuItem)
      {
        OpenHealingChart();
      }
      else if (e.Source == tankingChartMenuItem)
      {
        OpenTankingChart();
      }
      else if (e.Source == damageSummaryMenuItem)
      {
        OpenDamageSummary();
      }
      else if (e.Source == healingSummaryMenuItem)
      {
        OpenHealingSummary();
      }
      else if (e.Source == tankingSummaryMenuItem)
      {
        OpenTankingSummary();
      }
      else if (e.Source == chatMenuItem)
      {
        OpenChat();
      }
      else
      {
        if ((sender as MenuItem)?.Icon is ImageAwesome icon && IconToWindow.ContainsKey(icon.Name))
        {
          Helpers.OpenWindow(IconToWindow[icon.Name]);
        }
      }
    }

    private bool OpenChart(DocumentWindow window, DocumentWindow other1, DocumentWindow other2, FrameworkElement icon, string title, List<string> choices, out DocumentWindow newWindow)
    {
      bool updated = false;
      newWindow = window;

      if (newWindow?.IsOpen == true)
      {
        newWindow.Close();
        newWindow = null;
      }
      else
      {
        updated = true;
        var chart = new LineChart(choices);
        newWindow = new DocumentWindow(dockSite, title, title, null, chart);
        IconToWindow[icon.Name] = newWindow;

        Helpers.OpenWindow(newWindow);
        newWindow.CanFloat = true;
        newWindow.CanClose = true;

        if (other1?.IsOpen == true || other2?.IsOpen == true)
        {
          newWindow.MoveToNextContainer();
        }
        else
        {
          newWindow.MoveToNewHorizontalContainer();
        }
      }

      return updated;
    }

    private void OpenDamageChart()
    {
      if (OpenChart(DamageChartWindow, HealingChartWindow, TankingChartWindow, damageChartIcon, "Damage Chart", DAMAGE_CHOICES, out DamageChartWindow))
      {
        List<PlayerStats> selected = null;
        bool isBaneEnabled = false;
        if (DamageWindow?.Content is DamageSummary summary)
        {
          selected = summary.GetSelectedStats();
          isBaneEnabled = summary.IsBaneEnabled();
        }

        var options = new DamageStatsOptions() { IsBaneEanbled = isBaneEnabled, RequestChartData = true };
        DamageStatsManager.Instance.FireUpdateEvent(options, selected);
      }
    }

    private void OpenHealingChart()
    {
      if (OpenChart(HealingChartWindow, DamageChartWindow, TankingChartWindow, healingChartIcon, "Healing Chart", HEALING_CHOICES, out HealingChartWindow))
      {
        List<PlayerStats> selected = null;
        bool isAEHealingEnabled = false;
        if (HealingWindow?.Content is HealingSummary summary)
        {
          selected = summary.GetSelectedStats();
          isAEHealingEnabled = summary.IsAEHealingEnabled();
        }

        var options = new HealingStatsOptions() { IsAEHealingEanbled = isAEHealingEnabled, RequestChartData = true };
        HealingStatsManager.Instance.FireUpdateEvent(options, selected);
      }
    }

    private void OpenTankingChart()
    {
      if (OpenChart(TankingChartWindow, DamageChartWindow, HealingChartWindow, tankingChartIcon, "Tanking Chart", TANKING_CHOICES, out TankingChartWindow))
      {
        List<PlayerStats> selected = (TankingWindow?.Content is TankingSummary summary) ? summary.GetSelectedStats() : null;
        var options = new TankingStatsOptions() { RequestChartData = true };
        TankingStatsManager.Instance.FireUpdateEvent(options, selected);
      }
    }

    private void OpenDamageSummary()
    {
      if (DamageWindow?.IsOpen == true)
      {
        DamageWindow.Close();
      }
      else
      {
        var damageSummary = new DamageSummary();
        damageSummary.EventsSelectionChange += DamageSummary_SelectionChanged;
        DamageWindow = new DocumentWindow(dockSite, "damageSummary", "Damage Summary", null, damageSummary);
        IconToWindow[damageSummaryIcon.Name] = DamageWindow;

        Helpers.OpenWindow(DamageWindow);
        if (HealingWindow?.IsOpen == true || TankingWindow?.IsOpen == true)
        {
          DamageWindow.MoveToPreviousContainer();
        }

        RepositionCharts(DamageWindow);

        if (DamageStatsManager.Instance.DamageGroups.Count > 0)
        {
          // keep chart request until resize issue is fixed. resetting the series fixes it at a minimum
          var damageOptions = new DamageStatsOptions() { IsBaneEanbled = damageSummary.IsBaneEnabled(), RequestSummaryData = true };
          Task.Run(() => DamageStatsManager.Instance.RebuildTotalStats(damageOptions));
        }
      }
    }

    private void OpenHealingSummary()
    {
      if (HealingWindow?.IsOpen == true)
      {
        HealingWindow.Close();
      }
      else
      {
        var healingSummary = new HealingSummary();
        healingSummary.EventsSelectionChange += HealingSummary_SelectionChanged;
        HealingWindow = new DocumentWindow(dockSite, "healingSummary", "Healing Summary", null, healingSummary);
        IconToWindow[healingSummaryIcon.Name] = HealingWindow;

        Helpers.OpenWindow(HealingWindow);
        if (DamageWindow?.IsOpen == true || TankingWindow?.IsOpen == true)
        {
          HealingWindow.MoveToPreviousContainer();
        }

        RepositionCharts(HealingWindow);

        if (HealingStatsManager.Instance.HealingGroups.Count > 0)
        {
          // keep chart request until resize issue is fixed. resetting the series fixes it at a minimum
          var healingOptions = new HealingStatsOptions() { IsAEHealingEanbled = healingSummary.IsAEHealingEnabled(), RequestSummaryData = true };
          Task.Run(() => HealingStatsManager.Instance.RebuildTotalStats(healingOptions));
        }
      }
    }

    private void OpenTankingSummary()
    {
      if (TankingWindow?.IsOpen == true)
      {
        TankingWindow.Close();
      }
      else
      {
        var tankingSummary = new TankingSummary();
        tankingSummary.EventsSelectionChange += TankingSummary_SelectionChanged;
        TankingWindow = new DocumentWindow(dockSite, "tankingSummary", "Tanking Summary", null, tankingSummary);
        IconToWindow[tankingSummaryIcon.Name] = TankingWindow;

        Helpers.OpenWindow(TankingWindow);
        if (DamageWindow?.IsOpen == true || HealingWindow?.IsOpen == true)
        {
          TankingWindow.MoveToPreviousContainer();
        }

        RepositionCharts(TankingWindow);

        if (TankingStatsManager.Instance.TankingGroups.Count > 0)
        {
          // keep chart request until resize issue is fixed. resetting the series fixes it at a minimum
          var tankingOptions = new TankingStatsOptions() { RequestSummaryData = true };
          Task.Run(() => TankingStatsManager.Instance.RebuildTotalStats(tankingOptions));
        }
      }
    }

    private void OpenChat()
    {
      if (ChatWindow?.IsOpen == true)
      {
        ChatWindow.Close();
      }
      else
      {
        ChatWindow = new DocumentWindow(dockSite, "chatWindow", "Chat Search", null, new ChatViewer());
        IconToWindow[chatIcon.Name] = ChatWindow;
        Helpers.OpenWindow(ChatWindow);
      }
    }

    private void DamageSummary_SelectionChanged(object sender, PlayerStatsSelectionChangedEvent data)
    {
      var table = sender as DamageSummary;
      var options = new DamageStatsOptions() { IsBaneEanbled = table.IsBaneEnabled(), RequestChartData = true };
      DamageStatsManager.Instance.FireSelectionEvent(options, data.Selected);
      UpdateParse(Labels.DAMAGEPARSE, data.Selected);
    }

    private void HealingSummary_SelectionChanged(object sender, PlayerStatsSelectionChangedEvent data)
    {
      var table = sender as HealingSummary;
      var options = new HealingStatsOptions() { IsAEHealingEanbled = table.IsAEHealingEnabled(), RequestChartData = true };
      HealingStatsManager.Instance.FireSelectionEvent(options, data.Selected);
      UpdateParse(Labels.HEALPARSE, data.Selected);
    }

    private void TankingSummary_SelectionChanged(object sender, PlayerStatsSelectionChangedEvent data)
    {
      var options = new TankingStatsOptions() { RequestChartData = true };
      TankingStatsManager.Instance.FireSelectionEvent(options, data.Selected);
      UpdateParse(Labels.TANKPARSE, data.Selected);
    }

    private void RepositionCharts(DocumentWindow window)
    {
      if (window.ParentContainer is TabbedMdiContainer tabControl)
      {
        bool moved = false;
        foreach (var child in tabControl.Windows.Reverse().ToList())
        {
          if (child == DamageChartWindow || child == HealingChartWindow || child == TankingChartWindow)
          {
            if (child.IsOpen && !moved)
            {
              moved = true;
              child.MoveToNewHorizontalContainer();
            }
            else if (child.IsOpen && moved)
            {
              child.MoveToNextContainer();
            }

            (child.Content as LineChart)?.FixSize();
          }
        }
      }
    }

    // Main Menu Op File
    private void MenuItemSelectMonitorLogFile_Click(object sender, RoutedEventArgs e)
    {
      CurrentLogOption = LogOption.MONITOR;
      OpenLogFile();
    }

    private void MenuItemSelectArchiveChat_Click(object sender, RoutedEventArgs e)
    {
      CurrentLogOption = LogOption.ARCHIVE;
      OpenLogFile();
    }

    private void MenuItemSelectLogFile_Click(object sender, RoutedEventArgs e)
    {
      int lastMins = -1;
      if (sender is MenuItem item && !string.IsNullOrEmpty(item.Tag as string))
      {
        lastMins = Convert.ToInt32(item.Tag.ToString(), CultureInfo.CurrentCulture) * 60;
      }

      CurrentLogOption = LogOption.OPEN;
      OpenLogFile(lastMins);
    }

    private void PlayerParseText_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
      if (!playerParseTextBox.IsFocused)
      {
        playerParseTextBox.Focus();
      }
    }

    private void UpdateLoadingProgress()
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (EQLogReader != null)
        {
          Busy(true);
          Overlay?.Close();

          bytesReadTitle.Content = "Reading:";
          processedTimeLabel.Content = Math.Round((DateTime.Now - StartLoadTime).TotalSeconds, 1) + " sec";
          double filePercent = EQLogReader.FileSize > 0 ? Math.Min(Convert.ToInt32((double)FilePosition / EQLogReader.FileSize * 100), 100) : 100;
          double castPercent = CastLineCount > 0 ? Math.Round((double)CastLinesProcessed / CastLineCount * 100, 1) : 100;
          double damagePercent = DamageLineCount > 0 ? Math.Round((double)DamageLinesProcessed / DamageLineCount * 100, 1) : 100;
          double healPercent = HealLineCount > 0 ? Math.Round((double)HealLinesProcessed / HealLineCount * 100, 1) : 100;
          bytesReadLabel.Content = filePercent + "%";

          if (EQLogReader.FileLoadComplete)
          {
            if ((filePercent >= 100 && CurrentLogOption != LogOption.ARCHIVE) || CurrentLogOption == LogOption.MONITOR)
            {
              bytesReadTitle.Content = "Monitoring:";
              bytesReadLabel.Content = "Active";
              bytesReadLabel.Foreground = GOOD_BRUSH;
            }
            else if (filePercent >= 100 && CurrentLogOption == LogOption.ARCHIVE)
            {
              bytesReadTitle.Content = "Archiving:";
              bytesReadLabel.Content = "Complete";
              bytesReadLabel.Foreground = GOOD_BRUSH;
            }
          }

          if (((filePercent >= 100 && castPercent >= 100 && damagePercent >= 100 && healPercent >= 100) || 
          CurrentLogOption == LogOption.MONITOR || CurrentLogOption == LogOption.ARCHIVE) && EQLogReader.FileLoadComplete)
          {
            if (npcWindow.IsOpen)
            {
              (npcWindow.Content as NpcTable).SelectLastRow();
            }

            DataManager.Instance.SaveState();

            if ((DamageWindow?.Content as DamageSummary)?.IsOverlayEnabled() == true)
            {
              OpenOverlay();
            }

            Helpers.SpellAbbrvCache.Clear(); // only really needed during big parse
            LOG.Info("Finished Loading Log File");
          }
          else
          {
            _ = Task.Delay(500).ContinueWith(task => UpdateLoadingProgress(), TaskScheduler.Default);
          }

          Busy(false);
        }
      });
    }

    private void Instance_EventsGenerationStatus(object sender, StatsGenerationEvent e)
    {
      switch (e.State)
      {
        case "COMPLETED":
        case "NONPC":
          AddParse(e.Type, sender as ISummaryBuilder, e.CombinedStats);
          break;
      }
    }

    private void AddParse(string type, ISummaryBuilder builder, CombinedStats combined, List<PlayerStats> selected = null)
    {
      Parses[type] = new ParseData() { Builder = builder, CombinedStats = combined, Selected = selected };

      if (!AvailableParses.Contains(type))
      {
        Dispatcher.InvokeAsync(() => AvailableParses.Add(type));
      }

      TriggerParseUpdate(type);
    }

    private void UpdateParse(string type, List<PlayerStats> selected)
    {
      if (Parses.ContainsKey(type))
      {
        Parses[type].Selected = selected;
        TriggerParseUpdate(type);
      }
    }

    private void TriggerParseUpdate(string type)
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (parseList.SelectedItem?.ToString() == type)
        {
          SetParseTextByType(type);
        }
        else
        {
          parseList.SelectedItem = type;
        }
      });
    }

    private void SetParseTextByType(string type)
    {
      if (Parses.ContainsKey(type))
      {
        var selected = Parses[type].Selected;
        var combined = Parses[type].CombinedStats;
        var summary = Parses[type].Builder?.BuildSummary(combined, selected, playerParseTextDoTotals.IsChecked.Value, playerParseTextDoRank.IsChecked.Value);
        playerParseTextBox.Text = summary.Title + summary.RankedPlayers;
        playerParseTextBox.SelectAll();
      }
    }

    private void ParseList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (parseList.SelectedIndex > -1)
      {
        SetParseTextByType(parseList.SelectedItem as string);
      }
    }

    private void PlayerParseTextCheckChange(object sender, RoutedEventArgs e)
    {
      if (parseList.SelectedIndex > -1)
      {
        SetParseTextByType(parseList.SelectedItem as string);
      }
    }

    private void PlayerParseTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
      if (string.IsNullOrEmpty(playerParseTextBox.Text) || playerParseTextBox.Text == SHARE_DPS_LABEL)
      {
        copyToEQButton.IsEnabled = false;
        copyToEQButton.Foreground = LIGHTER_BRUSH;
        sharePlayerParseLabel.Text = SHARE_DPS_LABEL;
        sharePlayerParseLabel.Foreground = BRIGHT_TEXT_BRUSH;
        sharePlayerParseWarningLabel.Text = playerParseTextBox.Text.Length + "/" + 509;
        sharePlayerParseWarningLabel.Visibility = Visibility.Hidden;
      }
      else if (playerParseTextBox.Text.Length > 509)
      {
        copyToEQButton.IsEnabled = false;
        copyToEQButton.Foreground = LIGHTER_BRUSH;
        sharePlayerParseLabel.Text = SHARE_DPS_TOO_BIG_LABEL;
        sharePlayerParseLabel.Foreground = WARNING_BRUSH;
        sharePlayerParseWarningLabel.Text = playerParseTextBox.Text.Length + "/" + 509;
        sharePlayerParseWarningLabel.Foreground = WARNING_BRUSH;
        sharePlayerParseWarningLabel.Visibility = Visibility.Visible;
      }
      else if (playerParseTextBox.Text.Length > 0 && playerParseTextBox.Text != SHARE_DPS_LABEL)
      {
        copyToEQButton.IsEnabled = true;
        copyToEQButton.Foreground = BRIGHT_TEXT_BRUSH;

        if (parseList.SelectedItem != null && Parses.TryGetValue(parseList.SelectedItem as string, out ParseData data))
        {
          var count = data.Selected?.Count > 0 ? data.Selected?.Count : 0;
          string players = count == 1 ? "Player" : "Players";
          sharePlayerParseLabel.Text = string.Format(CultureInfo.CurrentCulture, "{0} {1} Selected", count, players);
        }

        sharePlayerParseLabel.Foreground = BRIGHT_TEXT_BRUSH;
        sharePlayerParseWarningLabel.Text = playerParseTextBox.Text.Length + " / " + 509;
        sharePlayerParseWarningLabel.Foreground = GOOD_BRUSH;
        sharePlayerParseWarningLabel.Visibility = Visibility.Visible;
      }
    }

    private void PetMapping_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (sender is ComboBox comboBox)
      {
        if (comboBox.DataContext is PetMapping mapping && comboBox.SelectedItem is SortableName selected && selected.Name != mapping.Owner)
        {
          DataManager.Instance.UpdatePetToPlayer(mapping.Pet, selected.Name);
          petMappingGrid.CommitEdit();
        }
      }
    }

    private void OpenLogFile(int lastMins = -1)
    {
      try
      {
        // WPF doesn't have its own file chooser so use Win32 Version
        Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
        {
          // filter to txt files
          DefaultExt = ".txt",
          Filter = "eqlog_Player_server (.txt .txt.gz)|*.txt;*.txt.gz"
        };

        // show dialog and read result
        if (dialog.ShowDialog().Value)
        {
          StopProcessing();
          CastProcessor = new ActionProcessor<string>("CastProcessor", CastLineParser.Process);
          DamageProcessor = new ActionProcessor<string>("DamageProcessor", DamageLineParser.Process);
          HealingProcessor = new ActionProcessor<string>("HealProcessor", HealingLineParser.Process);

          bytesReadLabel.Foreground = BRIGHT_TEXT_BRUSH;
          Title = APP_NAME + " " + VERSION + " -- (" + dialog.FileName + ")";
          StartLoadTime = DateTime.Now;
          CastLineCount = DamageLineCount = HealLineCount = CastLinesProcessed = DamageLinesProcessed = HealLinesProcessed = FilePosition = 0;

          string name = "Uknown";
          string server = "Uknown";
          if (dialog.FileName.Length > 0)
          {
            LOG.Info("Selected Log File: " + dialog.FileName);

            string file = Path.GetFileName(dialog.FileName);
            MatchCollection matches = ParseFileName.Matches(file);
            if (matches.Count == 1)
            {
              if (matches[0].Groups.Count > 1)
              {
                name = matches[0].Groups[1].Value;
              }

              if (matches[0].Groups.Count > 2)
              {
                server = matches[0].Groups[2].Value;
              }
            }
          }

          PlayerChatManager = new ChatManager(name + "." + server);
          DataManager.Instance.SetPlayerName(name);
          DataManager.Instance.SetServerName(server);
          DataManager.Instance.Clear();

          NpcDamageManager.LastUpdateTime = double.NaN;
          progressWindow.IsOpen = true;
          EQLogReader = new LogReader(dialog.FileName, FileLoadingCallback, CurrentLogOption == LogOption.MONITOR, lastMins);
          EQLogReader.Start();
          UpdateLoadingProgress();
        }
      }
      catch (Exception e)
      {
        LOG.Error(e);
        throw;
      }
    }

    private void FileLoadingCallback(string line, long position)
    {
      int sleep = (int)((DamageProcessor.Size() + HealingProcessor.Size() + CastProcessor.Size()) / 5000);
      if (sleep > 10)
      {
        Thread.Sleep(4 * (sleep - 10));
      }

      Interlocked.Exchange(ref FilePosition, position);

      if (PreLineParser.NeedProcessing(line))
      {
        // avoid having other things parse chat by accident
        var chatType = ChatLineParser.Process(line);
        if (chatType != null)
        {
          PlayerChatManager.Add(chatType);
        }
        else if (CurrentLogOption != LogOption.ARCHIVE)
        {
          CastLineCount += 1;
          CastProcessor.Add(line);
          DamageLineCount += 1;
          DamageProcessor.Add(line);
          HealLineCount += 1;
          HealingProcessor.Add(line);
        }
      }
    }

    private void StopProcessing()
    {
      EQLogReader?.Stop();
      CastProcessor?.Stop();
      DamageProcessor?.Stop();
      HealingProcessor?.Stop();
    }

    private void WindowIcon_Loaded(object sender, RoutedEventArgs e)
    {
      if (sender is FrameworkElement icon && IconToWindow.ContainsKey(icon.Name))
      {
        icon.Visibility = IconToWindow[icon.Name]?.IsOpen == true ? Visibility.Visible : Visibility.Hidden;
      }
    }

    private void NPCWindow_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
      (npcWindow?.Content as NpcTable).NPCSearchBox_KeyDown(sender, e);
    }

    private void DockSite_WindowUnreg(object sender, DockingWindowEventArgs e)
    {
      (e.Window.Content as IDisposable)?.Dispose();
    }

    private void TrayIcon_MouseUp(object sender, RoutedEventArgs e)
    {
      if (Visibility == Visibility.Hidden)
      {
        Show();
      }

      Activate();

      if (WindowState == WindowState.Minimized)
      {
        WindowState = WindowState.Normal;
      }
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
      if (WindowState != WindowState.Minimized)
      {
        ShowInTaskbar = true;
      }
      else
      {
        ShowInTaskbar = false;
        Hide();
      }
    }
  }
}
