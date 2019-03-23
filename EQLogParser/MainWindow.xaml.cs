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
    private static ActionProcessor<string> HealProcessor = null;

    private ObservableCollection<SortableName> VerifiedPetsView = new ObservableCollection<SortableName>();
    private ObservableCollection<SortableName> VerifiedPlayersView = new ObservableCollection<SortableName>();
    private ObservableCollection<PetMapping> PetPlayersView = new ObservableCollection<PetMapping>();

    private DocumentWindow DamageChartWindow;
    private DocumentWindow HealingChartWindow;

    // progress window
    private static DateTime StartLoadTime; // millis
    private static bool MonitorOnly;

    private static NpcDamageManager NpcDamageManager = new NpcDamageManager();
    private LogReader EQLogReader = null;

    // binding property
    public ObservableCollection<SortableName> VerifiedPlayersProperty { get; set; }

    private OverlayWindow Overlay;
    private DamageSummary DamageSummaryTable;
    private HealSummary HealSummaryTable;

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

        CastLineParser.EventsLineProcessed += (sender, data) => CastLinesProcessed++;
        DamageLineParser.EventsLineProcessed += (sender, data) => DamageLinesProcessed++;
        HealLineParser.EventsLineProcessed += (sender, data) => HealLinesProcessed++;

        HealLineParser.EventsHealProcessed += (sender, data) => DataManager.Instance.AddHealRecord(data.Record, data.BeginTime);
        DamageLineParser.EventsDamageProcessed += (sender, data) => DataManager.Instance.AddDamageRecord(data.Record, data.BeginTime);
        DamageLineParser.EventsResistProcessed += (sender, data) => DataManager.Instance.AddResistRecord(data.Record, data.BeginTime);

        (npcWindow.Content as NpcTable).EventsSelectionChange += (sender, data) => ComputeStats();

        DamageSummaryTable = damageWindow.Content as DamageSummary;
        DamageSummaryTable.EventsSelectionChange += (sender, data) => UpdateDamageParseText();
        HealSummaryTable = healWindow.Content as HealSummary;
        HealSummaryTable.EventsSelectionChange += (sender, data) => UpdateDamageParseText();

        DamageStatsBuilder.EventsUpdateDataPoint += (sender, data) => HandleChartUpdateEvent(DamageChartWindow, sender, data);
        HealStatsBuilder.EventsUpdateDataPoint += (sender, data) => HandleChartUpdateEvent(HealingChartWindow, sender, data);

        // Setup themes
        ThemeManager.BeginUpdate();
        ThemeManager.AreNativeThemesEnabled = true;
        SharedThemeCatalogRegistrar.Register();
        DockingThemeCatalogRegistrar.Register();
        ThemeManager.CurrentTheme = ThemeName.MetroDark.ToString();

        OpenHealingChart();
        OpenDamageChart();

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
      (damageWindow?.Content as DamageSummary)?.Clear();
      (healWindow?.Content as HealSummary)?.Clear();
      (DamageChartWindow?.Content as LineChart)?.Clear();
      (HealingChartWindow?.Content as LineChart)?.Clear();
    }

    internal void Busy(bool state)
    {
      BusyCount += state ? 1 : -1;
      BusyCount = BusyCount < 0 ? 0 : BusyCount;
      busyIcon.Visibility = BusyCount == 0 ? Visibility.Hidden : Visibility.Visible;
    }

    internal StatsSummary BuildDamageSummary(CombinedStats combined, List<PlayerStats> selected)
    {
      var summary = DamageStatsBuilder.BuildSummary(combined, selected, playerParseTextDoTotals.IsChecked.Value, playerParseTextDoRank.IsChecked.Value);
      playerParseTextBox.Text = summary.Title + summary.RankedPlayers;
      playerParseTextBox.SelectAll();
      return summary;
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
      if (DamageSummaryTable?.IsOverlayEnabled() == true)
      {
        OpenOverlay();
      }
    }

    internal void CloseOverlay()
    {
      Overlay?.Close();
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

      DamageSummaryTable?.UpdateStats(filtered);
      HealSummaryTable?.UpdateStats(filtered);

      if (filtered?.Count == 0)
      {
        (DamageChartWindow?.Content as LineChart)?.Clear();
        (HealingChartWindow?.Content as LineChart)?.Clear();
      }
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
    }

    private void OpenDamageChart()
    {
      var host = HealingChartWindow?.DockHost;
      DamageChartWindow = Helpers.OpenChart(dockSite, DamageChartWindow, host, LineChart.DAMAGE_CHOICES, "Damage Chart");
      DamageStatsBuilder.FireUpdateEvent(DamageSummaryTable.IsBaneEnabled(), DamageSummaryTable.GetSelectedStats());
    }

    private void OpenHealingChart()
    {
      var host = DamageChartWindow?.DockHost;
      HealingChartWindow = Helpers.OpenChart(dockSite, HealingChartWindow, host, LineChart.HEALING_CHOICES, "Healing Chart");
      HealStatsBuilder.FireUpdateEvent(true, DamageSummaryTable.GetSelectedStats());
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
      LOG.Error("bbbb");
      if (!playerParseTextBox.IsFocused)
      {
        playerParseTextBox.Focus();
      }
      LOG.Error("bbbb2");
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

            if (DamageSummaryTable != null && DamageSummaryTable.IsOverlayEnabled())
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

    private void PlayerParseTextCheckChange(object sender, RoutedEventArgs e)
    {
      //if (SelectedSummary == CurrentDamageSummary)
      //{
      //  UpdateDamageParseText();
      //}

      //if (SelectedSummary == CurrentHealSummary)
      //{
      //  UpdateHealParseText();
      //}
    }

    private void UpdateDamageParseText()
    {
      /*
      if (CurrentDamageStats != null)
      {
        List<PlayerStats> list = playerDataGrid?.SelectedItems.Count > 0 ? playerDataGrid.SelectedItems.Cast<PlayerStats>().ToList() : null;
        SelectedSummary = CurrentDamageSummary = BuildDamageSummary(CurrentDamageStats, list);
      }
      */
    }

    private void UpdateHealParseText()
    {
      /*
      if (CurrentHealStats != null)
      {
        List<PlayerStats> list = healDataGrid?.SelectedItems.Count > 0 ? healDataGrid.SelectedItems.Cast<PlayerStats>().ToList() : null;
        SelectedSummary = CurrentHealSummary = HealStatsBuilder.BuildSummary(CurrentHealStats, list, playerParseTextDoTotals.IsChecked.Value, playerParseTextDoRank.IsChecked.Value);
        playerParseTextBox.Text = CurrentHealSummary.Title + CurrentHealSummary.RankedPlayers;
        playerParseTextBox.SelectAll();
      }
      */
    }

    private void PlayerParseTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
      if (playerParseTextBox.Text == "" || playerParseTextBox.Text == SHARE_DPS_LABEL)
      {
        //copyToEQButton.IsEnabled = copyDamageParseToEQClick.IsEnabled = copyHealParseToEQClick.IsEnabled = false;
        copyToEQButton.Foreground = LIGHTER_BRUSH;
        sharePlayerParseLabel.Text = SHARE_DPS_LABEL;
        sharePlayerParseLabel.Foreground = BRIGHT_TEXT_BRUSH;
        sharePlayerParseWarningLabel.Text = playerParseTextBox.Text.Length + "/" + 509;
        sharePlayerParseWarningLabel.Visibility = Visibility.Hidden;
      }
      else if (playerParseTextBox.Text.Length > 509)
      {
        //copyToEQButton.IsEnabled = copyDamageParseToEQClick.IsEnabled = copyHealParseToEQClick.IsEnabled = false;
        copyToEQButton.Foreground = LIGHTER_BRUSH;
        sharePlayerParseLabel.Text = SHARE_DPS_TOO_BIG_LABEL;
        sharePlayerParseLabel.Foreground = WARNING_BRUSH;
        sharePlayerParseWarningLabel.Text = playerParseTextBox.Text.Length + "/" + 509;
        sharePlayerParseWarningLabel.Foreground = WARNING_BRUSH;
        sharePlayerParseWarningLabel.Visibility = Visibility.Visible;
      }
      else if (playerParseTextBox.Text.Length > 0 && playerParseTextBox.Text != SHARE_DPS_LABEL)
      {
        //copyToEQButton.IsEnabled = copyDamageParseToEQClick.IsEnabled = copyHealParseToEQClick.IsEnabled = true;
        copyToEQButton.Foreground = BRIGHT_TEXT_BRUSH;
        //var count = SelectedSummary == CurrentDamageSummary ? playerDataGrid.SelectedItems.Count : healDataGrid.SelectedItems.Count;
        //string players = count == 1 ? "Player" : "Players";
        //sharePlayerParseLabel.Text = string.Format("{0} {1} Selected", count, players);
        sharePlayerParseLabel.Foreground = BRIGHT_TEXT_BRUSH;
        sharePlayerParseWarningLabel.Text = playerParseTextBox.Text.Length + " / " + 509;
        sharePlayerParseWarningLabel.Foreground = GOOD_BRUSH;
        sharePlayerParseWarningLabel.Visibility = Visibility.Visible;
      }
    }

    private void PetMapping_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      LOG.Error("aaaa");
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
      LOG.Error("aaaa2");
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
          HealProcessor = new ActionProcessor<string>("HealProcessor", HealLineParser.Process);

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
        HealProcessor.Add(line);

        if (DamageProcessor.Size() > 50000 || HealProcessor.Size() > 50000 || CastProcessor.Size() > 50000)
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
      HealProcessor?.Stop();
    }

    private void Window_VisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
      if (sender == damageWindow && damageWindow.IsVisible)
      {
        UpdateDamageParseText();
      }
      else if (sender == healWindow && healWindow.IsVisible)
      {
        UpdateHealParseText();
      }
    }
  }
}
