using ActiproSoftware.Windows.Controls.Docking;
using ActiproSoftware.Windows.Themes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
    public ObservableCollection<SortableName> VerifiedPlayersProperty { get; set; }

    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private static SolidColorBrush WARNING_BRUSH = new SolidColorBrush(Color.FromRgb(241, 109, 29));
    private static SolidColorBrush BRIGHT_TEXT_BRUSH = new SolidColorBrush(Colors.White);
    private static SolidColorBrush LIGHTER_BRUSH = new SolidColorBrush(Color.FromRgb(90, 90, 90));
    private static SolidColorBrush GOOD_BRUSH = new SolidColorBrush(Colors.LightGreen);

    private const string APP_NAME = "EQLogParser";
    private const string VERSION = "v1.3.18";
    private const string VERIFIED_PETS = "Verified Pets";
    private const string PLAYER_TABLE_LABEL = " No NPCs Selected";
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
    private static bool MonitorOnly;

    private ObservableCollection<SortableName> VerifiedPetsView = new ObservableCollection<SortableName>();
    private ObservableCollection<SortableName> VerifiedPlayersView = new ObservableCollection<SortableName>();
    private ObservableCollection<PetMapping> PetPlayersView = new ObservableCollection<PetMapping>();

    private NpcDamageManager NpcDamageManager = new NpcDamageManager();
    private Dictionary<string, ParseData> Parses = new Dictionary<string, ParseData>();

    private DocumentWindow DamageWindow = null;
    private DocumentWindow HealingWindow = null;
    private DocumentWindow DamageChartWindow = null;
    private DocumentWindow HealingChartWindow = null;

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
        verifiedPlayersGrid.ItemsSource = VerifiedPlayersView;
        DataManager.Instance.EventsNewVerifiedPlayer += (sender, name) => Dispatcher.InvokeAsync(() =>
        {
          Helpers.InsertNameIntoSortedList(name, VerifiedPlayersView);
          verifiedPlayersWindow.Title = "Players (" + VerifiedPlayersView.Count + ")";
        });

        VerifiedPlayersProperty = VerifiedPlayersView;

        parseList.ItemsSource = new List<string>() { Labels.DAMAGE_PARSE, Labels.HEAL_PARSE };
        parseList.SelectedIndex = 0;

        CastLineParser.EventsLineProcessed += (sender, data) => CastLinesProcessed++;
        DamageLineParser.EventsLineProcessed += (sender, data) => DamageLinesProcessed++;
        HealingLineParser.EventsLineProcessed += (sender, data) => HealLinesProcessed++;

        HealingLineParser.EventsHealProcessed += (sender, data) => DataManager.Instance.AddHealRecord(data.Record, data.BeginTime);
        DamageLineParser.EventsDamageProcessed += (sender, data) => DataManager.Instance.AddDamageRecord(data.Record, data.BeginTime);
        DamageLineParser.EventsResistProcessed += (sender, data) => DataManager.Instance.AddResistRecord(data.Record, data.BeginTime);

        (npcWindow.Content as NpcTable).EventsSelectionChange += (sender, data) => ComputeStats();

        DamageStatsManager.Instance.EventsUpdateDataPoint += (sender, data) => HandleChartUpdateEvent(DamageChartWindow, sender, data);
        HealingStatsManager.Instance.EventsUpdateDataPoint += (sender, data) => HandleChartUpdateEvent(HealingChartWindow, sender, data);

        DamageStatsManager.Instance.EventsGenerationStatus += Instance_EventsGenerationStatus;
        HealingStatsManager.Instance.EventsGenerationStatus += Instance_EventsGenerationStatus;

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
      (DamageChartWindow?.Content as LineChart)?.Clear();
      (HealingChartWindow?.Content as LineChart)?.Clear();
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
        Overlay = new OverlayWindow(this, configure);
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

    internal void UpdateDamageParse(CombinedStats combined, List<PlayerStats> selected)
    {
      UpdateParse("Damage", DamageStatsManager.Instance, combined, selected);
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
      var damageSummary = DamageWindow?.Content as DamageSummary;
      if (damageSummary != null && DamageWindow?.IsOpen == true)
      {
        damageOptions.IsBaneEanbled = damageSummary.IsBaneEnabled();
        damageOptions.RequestSummaryData = true;
      }

      var healingOptions = new HealingStatsOptions() { Name = name, Npcs = filtered, RequestChartData = HealingChartWindow?.IsOpen == true };
      var healingSummary = HealingWindow?.Content as HealingSummary;
      if (healingSummary != null && HealingWindow?.IsOpen == true)
      {
        healingOptions.IsAEHealingEanbled = healingSummary.IsAEHealingEnabled();
        healingOptions.RequestSummaryData = true;
      }

      Task.Run(() => DamageStatsManager.Instance.BuildTotalStats(damageOptions));
      Task.Run(() => HealingStatsManager.Instance.BuildTotalStats(healingOptions));
    }

    private void Window_Closed(object sender, System.EventArgs e)
    {
      StopProcessing();
      DataManager.Instance.SaveState();
      Application.Current.Shutdown();
    }

    private void PlayerParseTextWindow_Loaded(object sender, RoutedEventArgs e)
    {
      playerParseTextWindow.State = DockingWindowState.AutoHide;
    }

    // Main Menu
    private void MenuItemWindow_Click(object sender, RoutedEventArgs e)
    {
      if (e.Source == npcWindowMenuitem)
      {
        Helpers.OpenWindow(npcWindow);
      }
      else if (e.Source == fileProgressWindowMenuItem)
      {
        Helpers.OpenWindow(progressWindow);
      }
      else if (e.Source == petMappingWindowMenuItem)
      {
        Helpers.OpenWindow(petMappingWindow);
      }
      else if (e.Source == verifiedPlayersWindowMenuItem)
      {
        Helpers.OpenWindow(verifiedPlayersWindow);
      }
      else if (e.Source == verifiedPetsWindowMenuItem)
      {
        Helpers.OpenWindow(verifiedPetsWindow);
      }
      else if (e.Source == playerParseTextWindowMenuItem)
      {
        Helpers.OpenWindow(playerParseTextWindow);
      }
      else if (e.Source == damageChartMenuItem)
      {
        OpenDamageChart();
      }
      else if (e.Source == healingChartMenuItem)
      {
        OpenHealingChart();
      }
      else if (e.Source == damageSummaryMenuItem)
      {
        OpenDamageSummary();
      }
      else if (e.Source == healingSummaryMenuItem)
      {
        OpenHealingSummary();
      }
    }

    private bool OpenChart(DocumentWindow window, DocumentWindow other, string title, List<string> choices, out DocumentWindow newWindow)
    {
      bool updated = false;
      newWindow = window;

      if (newWindow?.IsOpen == true)
      {
        Helpers.OpenWindow(newWindow);
      }
      else
      {
        updated = true;
        var chart = new LineChart(choices);
        newWindow = new DocumentWindow(dockSite, title, title, null, chart);
        Helpers.OpenWindow(newWindow);
        newWindow.CanFloat = true;
        newWindow.CanClose = true;

        if (other?.IsOpen == true)
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
      if (OpenChart(DamageChartWindow, HealingChartWindow, "Damage Chart", LineChart.DAMAGE_CHOICES, out DamageChartWindow))
      {
        List<PlayerStats> selected = null;
        bool isBaneEnabled = false;
        var summary = DamageWindow?.Content as DamageSummary;
        if (summary != null)
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
      if (OpenChart(HealingChartWindow, DamageChartWindow, "Healing Chart", LineChart.HEALING_CHOICES, out HealingChartWindow))
      {
        List<PlayerStats> selected = null;
        bool isAEHealingEnabled = false;
        var summary = HealingWindow?.Content as HealingSummary;
        if (summary != null)
        {
          selected = summary.GetSelectedStats();
          isAEHealingEnabled = summary.IsAEHealingEnabled();
        }

        var options = new HealingStatsOptions() { IsAEHealingEanbled = isAEHealingEnabled, RequestChartData = true };
        HealingStatsManager.Instance.FireUpdateEvent(options, selected);
      }
    }

    private void OpenDamageSummary()
    {
      if (DamageWindow?.IsOpen == true)
      {
        Helpers.OpenWindow(DamageWindow);
      }
      else
      {
        var damageSummary = new DamageSummary();
        var site = (HealingWindow?.IsOpen == true) ? HealingWindow.DockSite : dockSite;
        DamageWindow = new DocumentWindow(site, "damageSummary", "Damage Summary", null, damageSummary);

        Helpers.OpenWindow(DamageWindow);
        (DamageWindow.Content as DamageSummary).EventsSelectionChange += (sender, data) =>
        {
          var table = sender as DamageSummary;
          var options = new DamageStatsOptions() { IsBaneEanbled = table.IsBaneEnabled(), RequestChartData = true };
          DamageStatsManager.Instance.FireSelectionEvent(options, data.Selected);
          UpdateParse(Labels.DAMAGE_PARSE, data.Selected);
        };

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
        Helpers.OpenWindow(HealingWindow);
      }
      else
      {
        var healingSummary = new HealingSummary();
        var site = (DamageWindow?.IsOpen == true) ? DamageWindow.DockSite : dockSite;
        HealingWindow = new DocumentWindow(site, "healingSummary", "Healing Summary", null, healingSummary);

        Helpers.OpenWindow(HealingWindow);
        (HealingWindow.Content as HealingSummary).EventsSelectionChange += (sender, data) =>
        {
          var table = sender as HealingSummary;
          var options = new HealingStatsOptions() { IsAEHealingEanbled = table.IsAEHealingEnabled(), RequestChartData = true };
          HealingStatsManager.Instance.FireSelectionEvent(options, data.Selected);
          UpdateParse(Labels.HEAL_PARSE, data.Selected);
        };

        RepositionCharts(HealingWindow);

        if (HealingStatsManager.Instance.HealGroups.Count > 0)
        {
          // keep chart request until resize issue is fixed. resetting the series fixes it at a minimum
          var healingOptions = new HealingStatsOptions() { IsAEHealingEanbled = healingSummary.IsAEHealingEnabled(), RequestSummaryData = true };
          Task.Run(() => HealingStatsManager.Instance.RebuildTotalStats(healingOptions));
        }
      }
    }

    private void RepositionCharts(DocumentWindow window)
    {
      var tabControl = window.ParentContainer as TabbedMdiContainer;
   
      if (tabControl != null)
      {
        bool moved = false;
        foreach (var child in tabControl.Windows.Reverse().ToList())
        {
          if (child == DamageChartWindow || child == HealingChartWindow)
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
      OpenLogFile(true);
    }

    private void MenuItemSelectLogFile_Click(object sender, RoutedEventArgs e)
    {
      MenuItem item = sender as MenuItem;
      int lastMins = -1;
      if (item != null && item.Tag != null && item.Tag.ToString() != "")
      {
        lastMins = Convert.ToInt32(item.Tag.ToString()) * 60;
      }

      OpenLogFile(false, lastMins);
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
          double filePercent = EQLogReader.FileSize > 0 ? Math.Min(Convert.ToInt32((double) FilePosition / EQLogReader.FileSize * 100), 100) : 100;
          double castPercent = CastLineCount > 0 ? Math.Round((double) CastLinesProcessed / CastLineCount * 100, 1) : 0;
          double damagePercent = DamageLineCount > 0 ? Math.Round((double) DamageLinesProcessed / DamageLineCount * 100, 1) : 0;
          double healPercent = HealLineCount > 0 ? Math.Round((double) HealLinesProcessed / HealLineCount * 100, 1) : 0;
          bytesReadLabel.Content = filePercent + "%";

          if ((filePercent >= 100 || MonitorOnly) && EQLogReader.FileLoadComplete)
          {
            bytesReadTitle.Content = "Monitoring:";
            bytesReadLabel.Content = "Active";
            bytesReadLabel.Foreground = GOOD_BRUSH;
          }

          if (((filePercent >= 100 && castPercent >= 100 && damagePercent >= 100 && healPercent >= 100) || MonitorOnly) && EQLogReader.FileLoadComplete)
          {
            bytesReadTitle.Content = "Monitoring";

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
            Task.Delay(300).ContinueWith(task => UpdateLoadingProgress());
          }

          Busy(false);
        }
      });
    }

    private void Instance_EventsGenerationStatus(object sender, StatsGenerationEvent e)
    {
      switch(e.State)
      {
        case "COMPLETED":
        case "NONPC":
          UpdateParse(e.Name, sender as SummaryBuilder, e.CombinedStats);
          break;
      }
    }

    private void UpdateParse(string name, SummaryBuilder builder, CombinedStats combined, List<PlayerStats> selected = null)
    {
      Dispatcher.InvokeAsync(() =>
      {
        var summary = builder?.BuildSummary(combined, selected, playerParseTextDoTotals.IsChecked.Value, playerParseTextDoRank.IsChecked.Value);
        Parses[name] = new ParseData() { Builder = builder, CombinedStats = combined, Selected = selected };
        playerParseTextBox.Text = summary.Title + summary.RankedPlayers;
        playerParseTextBox.SelectAll();
      });
    }

    private void UpdateParse(string name, List<PlayerStats> selected)
    {
      if (Parses.ContainsKey(name))
      {
        UpdateParse(name, Parses[name].Builder, Parses[name].CombinedStats, selected);
      }
    }

    private void PlayerParseTextCheckChange(object sender, RoutedEventArgs e)
    {
      if (parseList.SelectedIndex > -1)
      {
        var value = parseList.SelectedItem as string;
        UpdateParse(value, Parses[value]?.Selected);
      }
    }

    private void ParseList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      var value = parseList.SelectedItem as string;
      if (value != null && Parses.ContainsKey(value))
      {
        UpdateParse(value, Parses[value]?.Selected);
      }
    }

    private void PlayerParseTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
      if (playerParseTextBox.Text == "" || playerParseTextBox.Text == SHARE_DPS_LABEL)
      {
        copyToEQButton.IsEnabled =  false;
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

        ParseData data;
        if (Parses.TryGetValue(parseList.SelectedItem as string, out data))
        {
          var count = data.Selected?.Count > 0 ? data.Selected?.Count : 0;
          string players = count == 1 ? "Player" : "Players";
          sharePlayerParseLabel.Text = string.Format("{0} {1} Selected", count, players);
        }

        sharePlayerParseLabel.Foreground = BRIGHT_TEXT_BRUSH;
        sharePlayerParseWarningLabel.Text = playerParseTextBox.Text.Length + " / " + 509;
        sharePlayerParseWarningLabel.Foreground = GOOD_BRUSH;
        sharePlayerParseWarningLabel.Visibility = Visibility.Visible;
      }
    }

    private void PetMapping_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      var comboBox = sender as ComboBox;
      if (comboBox != null)
      {
        var selected = comboBox.SelectedItem as SortableName;
        var mapping = comboBox.DataContext as PetMapping;
        if (mapping != null && selected != null && selected.Name != mapping.Owner)
        {
          DataManager.Instance.UpdatePetToPlayer(mapping.Pet, selected.Name);
          petMappingGrid.CommitEdit();
        }
      }
    }

    private void OpenLogFile(bool monitorOnly = false, int lastMins = -1)
    {
      try
      {
        MonitorOnly = monitorOnly;

        // WPF doesn't have its own file chooser so use Win32 Version
        Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog();

        // filter to txt files
        dialog.DefaultExt = ".txt";
        dialog.Filter = "eqlog_Player_server (.txt .txt.gz)|*.txt;*.txt.gz";

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
          if (dialog.FileName.Length > 0)
          {
            LOG.Info("Selected Log File: " + dialog.FileName);
            string fileName = dialog.FileName.Substring(dialog.FileName.LastIndexOf("\\") + 1);
            string[] parts = fileName.Split('_');

            if (parts.Length > 1)
            {
              name = parts[1];
            }
          }

          DataManager.Instance.SetPlayerName(name);
          DataManager.Instance.Clear();
          NpcDamageManager.LastUpdateTime = double.NaN;
          progressWindow.IsOpen = true;
          EQLogReader = new LogReader(dialog.FileName, FileLoadingCallback, monitorOnly, lastMins);
          EQLogReader.Start();
          UpdateLoadingProgress();
        }
      }
      catch (Exception e)
      {
        LOG.Error(e);
      }
    }

    private void FileLoadingCallback(string line, long position)
    {
      Interlocked.Exchange(ref FilePosition, position);

      if (PreProcessor.NeedProcessing(line))
      {
        CastLineCount++;
        CastProcessor.Add(line);

        DamageLineCount++;
        DamageProcessor.Add(line);

        HealLineCount++;
        HealingProcessor.Add(line);

        if (DamageProcessor.Size() > 50000 || HealingProcessor.Size() > 50000 || CastProcessor.Size() > 50000)
        {
          Thread.Sleep(15);
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
  }
}
