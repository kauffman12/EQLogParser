using ActiproSoftware.Windows.Controls.Docking;
using ActiproSoftware.Windows.Themes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EQLogParser
{
  public partial class MainWindow : Window
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    public static SolidColorBrush WARNING_BRUSH = new SolidColorBrush(Color.FromRgb(241, 109, 29));
    public static SolidColorBrush BRIGHT_TEXT_BRUSH = new SolidColorBrush(Colors.White);
    public static SolidColorBrush LIGHTER_BRUSH = new SolidColorBrush(Color.FromRgb(90, 90, 90));
    public static SolidColorBrush GOOD_BRUSH = new SolidColorBrush(Colors.LightGreen);
    public static BitmapImage COLLAPSE_BITMAP = new BitmapImage(new Uri(@"pack://application:,,,/icons/Collapse_16x.png"));
    public static BitmapImage EXPAND_BITMAP = new BitmapImage(new Uri(@"pack://application:,,,/icons/Expand_16x.png"));

    private const string APP_NAME = "EQLogParser";
    private const string VERSION = "v1.3.5";
    private const string VERIFIED_PETS = "Verified Pets";
    private const string PLAYER_TABLE_LABEL = " No NPCs Selected";
    private const string SHARE_DPS_LABEL = "No Players Selected";
    private const string SHARE_DPS_TOO_BIG_LABEL = "Exceeded Copy/Paste Limit for EQ";
    private const int MIN_LINE_LENGTH = 33;
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

    // workaround for adjusting column withs of player datagrid
    private List<DataGrid> PlayerChildGrids = new List<DataGrid>();

    // stats
    private static bool UpdatingStats = false;
    private static CombinedDamageStats CurrentDamageStats = null;
    private static CombinedHealStats CurrentHealStats = null;
    private static StatsSummary CurrentDamageSummary = null;
    private static StatsSummary CurrentHealSummary = null;
    private static StatsSummary SelectedSummary = null;

    private DocumentWindow DamageChartWindow;
    private DocumentWindow HealingChartWindow;

    // progress window
    private static DateTime StartLoadTime; // millis
    private static bool MonitorOnly;

    private static NpcDamageManager NpcDamageManager = new NpcDamageManager();
    private LogReader EQLogReader = null;

    // binding property
    public ObservableCollection<SortableName> VerifiedPlayersProperty { get; set; }

    public MainWindow()
    {
      try
      {
        InitializeComponent();
        LOG.Info("Initialized Components");

        // update titles
        Title = APP_NAME + " " + VERSION;
        dpsTitle.Content = PLAYER_TABLE_LABEL;
        healTitle.Content = PLAYER_TABLE_LABEL;

        // Clear/Reset
        DataManager.Instance.EventsClearedActiveData += (sender, cleared) =>
        {
          CurrentDamageStats = null;
          playerDataGrid.ItemsSource = null;
          PlayerChildGrids.Clear();
          healDataGrid.ItemsSource = null;
          dpsTitle.Content = PLAYER_TABLE_LABEL;
          healTitle.Content = PLAYER_TABLE_LABEL;
          (DamageChartWindow?.Content as LineChart)?.Clear();
          (HealingChartWindow?.Content as LineChart)?.Clear();
        };

        // pet -> players
        petMappingGrid.ItemsSource = PetPlayersView;
        DataManager.Instance.EventsUpdatePetMapping += (sender, mapping) => Dispatcher.InvokeAsync(() =>
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
          UpdateStats();
        });

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

        // fix player DPS table sorting
        playerDataGrid.Sorting += (s, e) =>
        {
          if (e.Column.Header != null && e.Column.Header.ToString() != "Name")
          {
            e.Column.SortDirection = e.Column.SortDirection ?? ListSortDirection.Ascending;
          }
        };

        PropertyDescriptor pd = DependencyPropertyDescriptor.FromProperty(DataGridColumn.ActualWidthProperty, typeof(DataGridColumn));
        foreach (var column in playerDataGrid.Columns)
        {
          pd.AddValueChanged(column, new EventHandler(ColumnWidthPropertyChanged));
        }

        CastLineParser.EventsLineProcessed += (sender, data) => CastLinesProcessed++;
        DamageLineParser.EventsLineProcessed += (sender, data) => DamageLinesProcessed++;
        HealLineParser.EventsLineProcessed += (sender, data) => HealLinesProcessed++;

        HealLineParser.EventsHealProcessed += (sender, data) => DataManager.Instance.AddHealRecord(data.Record);
        DamageLineParser.EventsDamageProcessed += (sender, data) => DataManager.Instance.AddDamageRecord(data.Record);
        DamageLineParser.EventsResistProcessed += (sender, data) => DataManager.Instance.AddResistRecord(data.Record);

        // Setup themes
        ThemeManager.BeginUpdate();
        ThemeManager.AreNativeThemesEnabled = true;
        SharedThemeCatalogRegistrar.Register();
        DockingThemeCatalogRegistrar.Register();
        ThemeManager.CurrentTheme = ThemeName.MetroDark.ToString();

        // after everything else is done
        DataManager.Instance.LoadState();

        var npcTable = npcWindow.Content as NpcTable;
        npcTable.EventsSelectionChange += (sender, data) => UpdateStats();

        DamageStatsBuilder.EventsUpdateDataPoint += (object sender, DataPointEvent e) =>
        {
          var groups = sender as List<List<TimedAction>>;
          if (LoadChart(groups, DamageChartWindow, new DamageGroupIterator(groups, e.NpcNames)))
          {
            // cleanup memory if its closed
            DamageChartWindow = null;
          }
        };

        HealStatsBuilder.EventsUpdateDataPoint += (object sender, DataPointEvent e) =>
        {
          var groups = sender as List<List<TimedAction>>;
          if (LoadChart(groups, HealingChartWindow, new HealGroupIterator(groups)))
          {
            // cleanup memory if its closed
            HealingChartWindow = null;
          }
        };

        OpenHealingChart();
        OpenDamageChart();
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

    private bool LoadChart(List<List<TimedAction>> recordGroups, DocumentWindow chartWindow, RecordGroupIterator rgIterator, List<PlayerStats> selected = null)
    {
      bool windowClosed = false;

      Dispatcher.InvokeAsync(() =>
      {
        if (chartWindow != null && recordGroups != null && chartWindow.IsOpen)
        {
          (chartWindow.Content as LineChart).AddDataPoints(rgIterator, selected);
        }
        else
        {
          windowClosed = true;
        }
      });

      return windowClosed;
    }

    private void ColumnWidthPropertyChanged(object sender, EventArgs e)
    {
      var column = sender as DataGridColumn;
      foreach (var grid in PlayerChildGrids)
      {
        grid.Columns[column.DisplayIndex].Width = column.ActualWidth;
      }
    }

    public void Busy(bool state)
    {
      busyIcon.Visibility = state ? Visibility.Visible : Visibility.Hidden;
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

      if (CurrentDamageStats != null)
      {
        List<PlayerStats> damageList = playerDataGrid.SelectedItems.Cast<PlayerStats>().ToList();
        LoadChart(DamageStatsBuilder.DamageGroups, DamageChartWindow, new DamageGroupIterator(DamageStatsBuilder.DamageGroups, DamageStatsBuilder.NpcNames), damageList);
      }
    }

    private void OpenHealingChart()
    {
      var host = DamageChartWindow?.DockHost;
      HealingChartWindow = Helpers.OpenChart(dockSite, HealingChartWindow, host, LineChart.HEALING_CHOICES, "Healing Chart");

      if (CurrentHealStats != null)
      {
        List<PlayerStats> healList = healDataGrid.SelectedItems.Cast<PlayerStats>().ToList();
        LoadChart(HealStatsBuilder.HealGroups, HealingChartWindow, new HealGroupIterator(HealStatsBuilder.HealGroups), healList);
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

    private void DataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
      e.Row.Header = (e.Row.GetIndex() + 1).ToString();
    }

    // Player HPS Data Grid
    private void HealDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      UpdateHealDataGridMenuItems();
      UpdateHealParseText();

      List<PlayerStats> healList = healDataGrid.SelectedItems.Cast<PlayerStats>().ToList();
      var lineChart = HealingChartWindow?.Content as LineChart;
      lineChart?.Plot(healList);
    }

    // Player DPS Data Grid
    private void PlayerDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      UpdatePlayerDataGridMenuItems();
      UpdateDamageParseText();

      List<PlayerStats> damageList = playerDataGrid.SelectedItems.Cast<PlayerStats>().ToList();
      var lineChart = DamageChartWindow?.Content as LineChart;
      lineChart?.Plot(damageList);
    }

    private void PlayerDataGridExpander_Loaded(object sender, RoutedEventArgs e)
    {
      Image image = (sender as Image);
      PlayerStats stats = image.DataContext as PlayerStats;
      if (stats != null && CurrentDamageStats.Children.ContainsKey(stats.Name))
      {
        var list = CurrentDamageStats.Children[stats.Name];
        if (list.Count > 1 || stats.Name == DataManager.UNASSIGNED_PET_OWNER || (list.Count == 1 && !list[0].Name.StartsWith(stats.Name)))
        {
          var container = playerDataGrid.ItemContainerGenerator.ContainerFromItem(stats) as DataGridRow;
          if (container != null)
          {
            if (container.DetailsVisibility != Visibility.Visible)
            {
              image.Source = EXPAND_BITMAP;
            }
            else
            {
              image.Source = COLLAPSE_BITMAP;
            }
          }
        }
      }
    }

    private void PlayerDataGridExpander_MouseDown(object sender, MouseButtonEventArgs e)
    {
      Image image = (sender as Image);
      PlayerStats stats = image.DataContext as PlayerStats;
      var container = playerDataGrid.ItemContainerGenerator.ContainerFromItem(stats) as DataGridRow;

      if (image != null && container != null)
      {
        if (image.Source == COLLAPSE_BITMAP)
        {
          image.Source = EXPAND_BITMAP;
          container.DetailsVisibility = Visibility.Collapsed;
        }
        else if (image.Source == EXPAND_BITMAP)
        {
          image.Source = COLLAPSE_BITMAP;
          container.DetailsVisibility = Visibility.Visible;
        }
      }
    }

    private void DataGridSelectAll_Click(object sender, RoutedEventArgs e)
    {
      Helpers.DataGridSelectAll(sender);
    }

    private void DataGridUnselectAll_Click(object sender, RoutedEventArgs e)
    {
      Helpers.DataGridUnselectAll(sender);
    }

    private void PlayerDataGridHitFreq_Click(object sender, RoutedEventArgs e)
    {
      if (playerDataGrid.SelectedItems.Count == 1)
      {
        var chart = new HitFreqChart();
        var results = DamageStatsBuilder.GetHitFreqValues(CurrentDamageStats, playerDataGrid.SelectedItems.Cast<PlayerStats>().First());

        var hitFreqWindow = Helpers.OpenNewTab(dockSite, "freqChart", "Hit Frequency", chart, 400, 300);

        chart.Update(results);
        hitFreqWindow.CanFloat = true;
        hitFreqWindow.CanClose = true;
      }
    }

    private void PlayerDataGridSpellCastsByClass_Click(object sender, RoutedEventArgs e)
    {
      MenuItem menuItem = (sender as MenuItem);
      ShowSpellCasts(StatsBuilder.GetSelectedPlayerStatsByClass(menuItem.Tag as string, playerDataGrid.Items), CurrentDamageSummary);
    }

    private void PlayerDataGridShowSpellCasts_Click(object sender, RoutedEventArgs e)
    {
      if (playerDataGrid.SelectedItems.Count > 0)
      {
        ShowSpellCasts(playerDataGrid.SelectedItems.Cast<PlayerStats>().ToList(), CurrentDamageSummary);
      }
    }

    private void HealDataGridSpellCastsByClass_Click(object sender, RoutedEventArgs e)
    {
      MenuItem menuItem = (sender as MenuItem);
      ShowSpellCasts(StatsBuilder.GetSelectedPlayerStatsByClass(menuItem.Tag as string, healDataGrid.Items), CurrentHealSummary);
    }

    private void HealDataGridShowSpellCasts_Click(object sender, RoutedEventArgs e)
    {
      if (healDataGrid.SelectedItems.Count > 0)
      {
        ShowSpellCasts(healDataGrid.SelectedItems.Cast<PlayerStats>().ToList(), CurrentHealSummary);
      }
    }

    private void ShowSpellCasts(List<PlayerStats> selectedStats, StatsSummary summary)
    {
      var spellTable = new SpellCountTable(this, summary.ShortTitle);
      if (summary == CurrentDamageSummary)
      {
        spellTable.ShowSpells(selectedStats, CurrentDamageStats);
      }
      else if (summary == CurrentHealSummary)
      {
        spellTable.ShowSpells(selectedStats, CurrentHealStats);
      }

      Helpers.OpenNewTab(dockSite, "spellCastsWindow", "Spell Counts", spellTable);
    }

    private void HealDataGridShowBreakdownByClass_Click(object sender, RoutedEventArgs e)
    {
      MenuItem menuItem = (sender as MenuItem);
      ShowHealing(StatsBuilder.GetSelectedPlayerStatsByClass(menuItem.Tag as string, healDataGrid.Items));
    }

    private void HealDataGridShowBreakdown_Click(object sender, RoutedEventArgs e)
    {
      if (healDataGrid.SelectedItems.Count > 0)
      {
        ShowHealing(healDataGrid.SelectedItems.Cast<PlayerStats>().ToList());
      }
    }

    private void ShowHealing(List<PlayerStats> selectedStats)
    {
      var healTable = new HealTable(this, healTitle.Content.ToString());
      healTable.Show(selectedStats, CurrentHealStats);
      Helpers.OpenNewTab(dockSite, "healWindow", "Healing Breakdown", healTable);
    }

    private void PlayerDataGridShowDamageByClass_Click(object sender, RoutedEventArgs e)
    {
      MenuItem menuItem = (sender as MenuItem);
      ShowDamage(StatsBuilder.GetSelectedPlayerStatsByClass(menuItem.Tag as string, playerDataGrid.Items));
    }

    private void PlayerDataGridShowDamage_Click(object sender, RoutedEventArgs e)
    {
      if (playerDataGrid.SelectedItems.Count > 0)
      {
        ShowDamage(playerDataGrid.SelectedItems.Cast<PlayerStats>().ToList());
      }
    }

    private void ShowDamage(List<PlayerStats> selectedStats)
    {
      var damageTable = new DamageTable(this, CurrentDamageSummary.ShortTitle);
      damageTable.ShowDamage(selectedStats, CurrentDamageStats);
      Helpers.OpenNewTab(dockSite, "damageWindow", "Damage Breakdown", damageTable);
    }

    // Player DPS Child Grid
    private void PlayerChildrenDataGrid_PrevMouseWheel(object sender, System.Windows.Input.MouseEventArgs e)
    {
      if (!e.Handled)
      {
        e.Handled = true;
        MouseWheelEventArgs wheelArgs = e as MouseWheelEventArgs;
        var newEvent = new MouseWheelEventArgs(wheelArgs.MouseDevice, wheelArgs.Timestamp, wheelArgs.Delta);
        newEvent.RoutedEvent = MouseWheelEvent;
        var container = playerDataGrid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow;
        container.RaiseEvent(newEvent);
      }
    }

    private void PlayerChildrenGrid_RowDetailsVis(object sender, DataGridRowDetailsEventArgs e)
    {
      PlayerStats stats = e.Row.Item as PlayerStats;
      var childrenDataGrid = e.DetailsElement as DataGrid;
      if (stats != null && childrenDataGrid != null && CurrentDamageStats != null && CurrentDamageStats.Children.ContainsKey(stats.Name))
      {
        if (childrenDataGrid.ItemsSource != CurrentDamageStats.Children[stats.Name])
        {
          childrenDataGrid.ItemsSource = CurrentDamageStats.Children[stats.Name];
          PlayerChildGrids.Add(childrenDataGrid);

          // show bane column if needed
          if (playerDataGrid.Columns[4].Visibility == Visibility.Visible)
          {
            childrenDataGrid.Columns[4].Visibility = Visibility.Visible;
          }

          // fix column widths
          foreach (var column in playerDataGrid.Columns)
          {
            childrenDataGrid.Columns[column.DisplayIndex].Width = column.ActualWidth;
          }
        }
      }
    }

    // Player DPS Text/Send to EQ Window
    private void CopyToEQ_Click(object sender, RoutedEventArgs e)
    {
      Clipboard.SetDataObject(playerParseTextBox.Text);
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
            Busy(false);

            if (npcWindow.IsOpen)
            {
              (npcWindow.Content as NpcTable).SelectLastRow();
            }

            DataManager.Instance.SaveState();
            LOG.Info("Finished Loading Log File");
          }
          else
          {
            Task.Delay(300).ContinueWith(task => UpdateLoadingProgress());
          }
        }
      });
    }

    private void PlayerParseTextCheckChange(object sender, RoutedEventArgs e)
    {
      if (SelectedSummary == CurrentDamageSummary)
      {
        UpdateDamageParseText();
      }
      else if (SelectedSummary == CurrentHealSummary)
      {
        UpdateHealParseText();
      }
    }

    private void UpdateDamageParseText()
    {
      if (CurrentDamageStats != null)
      {
        List<PlayerStats> list = playerDataGrid?.SelectedItems.Count > 0 ? playerDataGrid.SelectedItems.Cast<PlayerStats>().ToList() : null;
        SelectedSummary = CurrentDamageSummary = DamageStatsBuilder.BuildSummary(CurrentDamageStats, list, playerParseTextDoTotals.IsChecked.Value, playerParseTextDoRank.IsChecked.Value);
        playerParseTextBox.Text = CurrentDamageSummary.Title + CurrentDamageSummary.RankedPlayers;
        playerParseTextBox.SelectAll();
      }
    }

    private void UpdateHealParseText()
    {
      if (CurrentHealStats != null)
      {
        List<PlayerStats> list = healDataGrid?.SelectedItems.Count > 0 ? healDataGrid.SelectedItems.Cast<PlayerStats>().ToList() : null;
        SelectedSummary = CurrentHealSummary = HealStatsBuilder.BuildSummary(CurrentHealStats, list, playerParseTextDoTotals.IsChecked.Value, playerParseTextDoRank.IsChecked.Value);
        playerParseTextBox.Text = CurrentHealSummary.Title + CurrentHealSummary.RankedPlayers;
        playerParseTextBox.SelectAll();
      }
    }

    private void UpdateStats()
    {
      if (!UpdatingStats)
      {
        bool taskStarted = false;
        UpdatingStats = true;

        if (npcWindow.IsOpen)
        {
          var realItems = (npcWindow.Content as NpcTable).GetSelectedItems();
          if (realItems.Count > 0)
          {
            dpsTitle.Content = "Calculating DPS...";
            healTitle.Content = "Calculating HPS...";
            playerDataGrid.ItemsSource = null;
            healDataGrid.ItemsSource = null;
            PlayerChildGrids.Clear();

            if (realItems.Count > 0)
            {
              taskStarted = true;
              Busy(true);

              bool damageStatsComplete = false;
              bool healStatsComplete = false;

              realItems = realItems.OrderBy(item => item.ID).ToList();
              string title = realItems.First().Name;

              new Task(() =>
              {
                CurrentDamageStats = DamageStatsBuilder.BuildTotalStats(title, realItems);

                Dispatcher.InvokeAsync((() =>
                {
                  dpsTitle.Content = StatsBuilder.BuildTitle(CurrentDamageStats);
                  playerDataGrid.ItemsSource = new ObservableCollection<PlayerStats>(CurrentDamageStats.StatsList);
                  UpdatePlayerDataGridMenuItems();
                  damageStatsComplete = true;

                  if (damageWindow.IsVisible)
                  {
                    UpdateDamageParseText();
                  }

                  if (healStatsComplete)
                  {
                    Busy(false);
                    UpdatingStats = false;
                  }
                }));
              }).Start();

              new Task(() =>
              {
                CurrentHealStats = HealStatsBuilder.BuildTotalStats(title, realItems);

                Dispatcher.InvokeAsync((() =>
                {
                  healTitle.Content = StatsBuilder.BuildTitle(CurrentHealStats);
                  healDataGrid.ItemsSource = new ObservableCollection<PlayerStats>(CurrentHealStats.StatsList);
                  UpdateHealDataGridMenuItems();
                  healStatsComplete = true;

                  if (healWindow.IsVisible)
                  {
                    UpdateHealParseText();
                  }

                  if (damageStatsComplete)
                  {
                    Busy(false);
                    UpdatingStats = false;
                  }
                }));
              }).Start();
            }
          }
        }

        if (!taskStarted)
        {
          if (playerDataGrid.ItemsSource is ObservableCollection<PlayerStats> damageList)
          {
            CurrentDamageStats = null;
            dpsTitle.Content = PLAYER_TABLE_LABEL;
            damageList.Clear();
          }

          if (healDataGrid.ItemsSource is ObservableCollection<PlayerStats> healList)
          {
            CurrentHealStats = null;
            healTitle.Content = PLAYER_TABLE_LABEL;
            healList.Clear();
          }

          (DamageChartWindow?.Content as LineChart)?.Clear();
          (HealingChartWindow?.Content as LineChart)?.Clear();

          playerParseTextBox.Text = "";
          UpdatePlayerDataGridMenuItems();
          UpdateHealDataGridMenuItems();
          UpdatingStats = false;
        }
      }
    }

    private void UpdatePlayerDataGridMenuItems()
    {
      if (CurrentDamageStats != null && CurrentDamageStats.StatsList?.Count > 0)
      {
        pdgMenuItemSelectAll.IsEnabled = playerDataGrid.SelectedItems.Count < playerDataGrid.Items.Count;
        pdgMenuItemUnselectAll.IsEnabled = playerDataGrid.SelectedItems.Count > 0;
        pdgMenuItemShowDamage.IsEnabled = pdgMenuItemShowSpellCasts.IsEnabled = true;
        pdgMenuItemShowHitFreq.IsEnabled = playerDataGrid.SelectedItems.Count == 1;
        UpdateClassMenuItems(pdgMenuItemShowDamage, playerDataGrid, CurrentDamageStats.UniqueClasses);
        UpdateClassMenuItems(pdgMenuItemShowSpellCasts, playerDataGrid, CurrentDamageStats.UniqueClasses);
      }
      else
      {
        pdgMenuItemUnselectAll.IsEnabled = pdgMenuItemSelectAll.IsEnabled = pdgMenuItemShowDamage.IsEnabled = pdgMenuItemShowSpellCasts.IsEnabled = pdgMenuItemShowHitFreq.IsEnabled = false;
      }
    }

    private void UpdateHealDataGridMenuItems()
    {
      if (CurrentHealStats != null && CurrentHealStats.StatsList.Count > 0)
      {
        hdgMenuItemSelectAll.IsEnabled = healDataGrid.SelectedItems.Count < healDataGrid.Items.Count;
        hdgMenuItemUnselectAll.IsEnabled = healDataGrid.SelectedItems.Count > 0;
        hdgMenuItemShowBreakdown.IsEnabled = hdgMenuItemShowSpellCasts.IsEnabled = true;
        UpdateClassMenuItems(hdgMenuItemShowBreakdown, healDataGrid, CurrentHealStats.UniqueClasses);
        UpdateClassMenuItems(hdgMenuItemShowSpellCasts, healDataGrid, CurrentHealStats.UniqueClasses);
      }
      else
      {
        hdgMenuItemUnselectAll.IsEnabled = hdgMenuItemSelectAll.IsEnabled = hdgMenuItemShowBreakdown.IsEnabled = hdgMenuItemShowSpellCasts.IsEnabled = false;
      }
    }

    private void UpdateClassMenuItems(MenuItem menu, DataGrid dataGrid, Dictionary<string, byte> uniqueClasses)
    {
      foreach (var item in menu.Items)
      {
        MenuItem menuItem = item as MenuItem;
        if (menuItem.Header as string == "Selected")
        {
          menuItem.IsEnabled = dataGrid.SelectedItems.Count > 0;
        }
        else
        {
          menuItem.IsEnabled = uniqueClasses.ContainsKey(menuItem.Header as string);
        }
      }
    }

    private void ShowColumn(int index, bool show)
    {
      if (playerDataGrid.Columns[index].Visibility == Visibility.Hidden && show || playerDataGrid.Columns[index].Visibility == Visibility.Visible && !show)
      {
        playerDataGrid.Columns[index].Visibility = show ? Visibility.Visible : Visibility.Hidden;
        foreach (var grid in PlayerChildGrids)
        {
          grid.Columns[index].Visibility = show ? Visibility.Visible : Visibility.Hidden;
        }
      }
    }

    private void PlayerParseTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
      if (playerParseTextBox.Text == "" || playerParseTextBox.Text == SHARE_DPS_LABEL)
      {
        copyToEQButton.IsEnabled = copyDamageParseToEQClick.IsEnabled = copyHealParseToEQClick.IsEnabled = false;
        copyToEQButton.Foreground = LIGHTER_BRUSH;
        sharePlayerParseLabel.Text = SHARE_DPS_LABEL;
        sharePlayerParseLabel.Foreground = BRIGHT_TEXT_BRUSH;
        sharePlayerParseWarningLabel.Text = playerParseTextBox.Text.Length + "/" + 509;
        sharePlayerParseWarningLabel.Visibility = Visibility.Hidden;
      }
      else if (playerParseTextBox.Text.Length > 509)
      {
        copyToEQButton.IsEnabled = copyDamageParseToEQClick.IsEnabled = copyHealParseToEQClick.IsEnabled = false;
        copyToEQButton.Foreground = LIGHTER_BRUSH;
        sharePlayerParseLabel.Text = SHARE_DPS_TOO_BIG_LABEL;
        sharePlayerParseLabel.Foreground = WARNING_BRUSH;
        sharePlayerParseWarningLabel.Text = playerParseTextBox.Text.Length + "/" + 509;
        sharePlayerParseWarningLabel.Foreground = WARNING_BRUSH;
        sharePlayerParseWarningLabel.Visibility = Visibility.Visible;
      }
      else if (playerParseTextBox.Text.Length > 0 && playerParseTextBox.Text != SHARE_DPS_LABEL)
      {
        copyToEQButton.IsEnabled = copyDamageParseToEQClick.IsEnabled = copyHealParseToEQClick.IsEnabled = true;
        copyToEQButton.Foreground = BRIGHT_TEXT_BRUSH;
        var count = SelectedSummary == CurrentDamageSummary ? playerDataGrid.SelectedItems.Count : healDataGrid.SelectedItems.Count;
        string players = count == 1 ? "Player" : "Players";
        sharePlayerParseLabel.Text = string.Format("{0} {1} Selected", count, players);
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

      if (line.Length > MIN_LINE_LENGTH)
      {
        CastLineCount++;
        CastProcessor.Add(line);

        DamageLineCount++;
        DamageProcessor.Add(line);

        HealLineCount++;
        HealProcessor.Add(line);
      }

      if (DamageProcessor.Size() > 25000 || HealProcessor.Size() > 25000 || CastProcessor.Size() > 25000)
      {
        Thread.Sleep(10);
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

  public class ZeroConverter : IValueConverter
  {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
      if (value.GetType() == typeof(decimal))
      {
        return (decimal) value > 0 ? value.ToString() : "-";
      }
      return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
      if (value is string)
      {
        decimal decValue;
        if (!decimal.TryParse((string) value, out decValue))
        {
          decValue = 0;
        }
        return decValue;
      }
      return 0;
    }
  }
}
