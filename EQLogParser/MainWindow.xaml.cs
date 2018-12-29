
using ActiproSoftware.Windows.Controls.DataGrid;
using ActiproSoftware.Windows.Controls.Docking;
using ActiproSoftware.Windows.Themes;
using LiveCharts;
using LiveCharts.Wpf;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
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
    public static SolidColorBrush NORMAL_BRUSH = new SolidColorBrush(Color.FromRgb(37, 37, 38));
    public static SolidColorBrush BREAK_TIME_BRUSH = new SolidColorBrush(Color.FromRgb(150, 65, 13));
    public static SolidColorBrush WARNING_BRUSH = new SolidColorBrush(Color.FromRgb(241, 109, 29));
    public static SolidColorBrush BRIGHT_TEXT_BRUSH = new SolidColorBrush(Colors.White);
    public static SolidColorBrush LIGHTER_BRUSH = new SolidColorBrush(Color.FromRgb(90, 90, 90));
    public static SolidColorBrush GOOD_BRUSH = new SolidColorBrush(Colors.LightGreen);
    public static BitmapImage COLLAPSE_BITMAP = new BitmapImage(new Uri(@"pack://application:,,,/icons/Collapse_16x.png"));
    public static BitmapImage EXPAND_BITMAP = new BitmapImage(new Uri(@"pack://application:,,,/icons/Expand_16x.png"));

    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private const string APP_NAME = "EQLogParser";
    private const string VERSION = "v1.1.3";
    private const string VERIFIED_PETS = "Verified Pets";
    private const string DPS_LABEL = " No NPCs Selected";
    private const string SHARE_DPS_LABEL = "No Players Selected";
    private const string SHARE_DPS_TOO_BIG_LABEL = "Exceeded Copy/Paste Limit for EQ";
    private const int MIN_LINE_LENGTH = 33;
    private const int DISPATCHER_DELAY = 150; // millis
    private static long CastLineCount = 0;
    private static long DamageLineCount = 0;
    private static long CastLinesProcessed = 0;
    private static long DamageLinesProcessed = 0;
    private static long FilePosition = 0;

    private static ActionProcessor<string> CastProcessor = null;
    private static ActionProcessor<string> DamageProcessor = null;
    private ObservableCollection<SortableName> VerifiedPetsView = new ObservableCollection<SortableName>();
    private ObservableCollection<SortableName> VerifiedPlayersView = new ObservableCollection<SortableName>();
    private ObservableCollection<NonPlayer> NonPlayersView = new ObservableCollection<NonPlayer>();
    private ObservableCollection<PetMapping> PetPlayersView = new ObservableCollection<PetMapping>();

    // stats
    private static bool NeedStatsUpdate = false;
    private static bool UpdatingStats = false;
    private static bool NeedDPSTextUpdate = false;
    private static CombinedStats CurrentStats = null;
    private DispatcherTimer NonPlayerSelectionTimer;

    // progress window
    private static bool UpdatingProgress = false;
    private static DateTime StartLoadTime; // millis
    private static bool MonitorOnly;

    // tab counts
    private static DocumentWindow spellCastsWindow = null;

    private static NpcDamageManager NpcDamageManager = null;
    private LogReader EQLogReader = null;
    private bool NeedScrollIntoView = false;

    public MainWindow()
    {
      try
      {
        InitializeComponent();
        LOG.Info("Initialized Components");

        // update titles
        Title = APP_NAME + " " + VERSION;
        dpsTitle.Content = DPS_LABEL;

        // Clear/Reset
        DataManager.Instance.EventsClearedActiveData += (sender, cleared) =>
        {
          CurrentStats = null;
          NonPlayersView.Clear();
          ResetDPSChart();
          playerDataGrid.ItemsSource = null;
          npcMenuItemClear.IsEnabled = npcMenuItemSelectAll.IsEnabled = npcMenuItemUnselectAll.IsEnabled = npcMenuItemSelectFight.IsEnabled = false;
          dpsTitle.Content = DPS_LABEL;
          NeedStatsUpdate = NeedDPSTextUpdate = true;
        };

        // pet -> players
        petMappingGrid.ItemsSource = PetPlayersView;
        DataManager.Instance.EventsNewPetMapping += (sender, mapping) => Dispatcher.InvokeAsync(() =>
        {
          PetPlayersView.Add(mapping);
          petMappingWindow.Title = "Pet Owners (" + PetPlayersView.Count + ")";
          NeedStatsUpdate = true;
        });

        // verified pets table
        verifiedPetsGrid.ItemsSource = VerifiedPetsView;
        DataManager.Instance.EventsNewVerifiedPet += (sender, name) => Dispatcher.InvokeAsync(() =>
        {
          VerifiedPetsView.Add(new SortableName() { Name = name });
          verifiedPetsWindow.Title = "Pets (" + VerifiedPetsView.Count + ")";
        });

        // verified player table
        verifiedPlayersGrid.ItemsSource = VerifiedPlayersView;
        DataManager.Instance.EventsNewVerifiedPlayer += (sender, name) => Dispatcher.InvokeAsync(() =>
        {
          VerifiedPlayersView.Add(new SortableName() { Name = name });
          verifiedPlayersWindow.Title = "Players (" + VerifiedPlayersView.Count + ")";
        });

        // List of NPCs to select from, damage is saved in the NonPlayer object
        npcDataGrid.ItemsSource = NonPlayersView;
        DataManager.Instance.EventsUpdatedNonPlayer += (sender, npc) => NeedStatsUpdate = (CurrentStats != null && CurrentStats.NpcIDs.Contains(npc.ID));
        DataManager.Instance.EventsRemovedNonPlayer += (sender, name) => RemoveNonPlayer(name);
        DataManager.Instance.EventsNewNonPlayer += (sender, npc) => AddNonPlayer(npc);
        DataManager.Instance.EventsNewUnverifiedPetOrPlayer += (sender, name) => RemoveNonPlayer(name);

        // fix player DPS table sorting
        playerDataGrid.Sorting += (s, e) =>
        {
          if (e.Column.Header != null && (e.Column.Header.ToString() != "Name" && e.Column.Header.ToString() != "Additional Details"))
          {
            e.Column.SortDirection = e.Column.SortDirection ?? ListSortDirection.Ascending;
          }
        };

        DamageLineParser.EventsLineProcessed += (sender, data) => DamageLinesProcessed++;
        CastLineParser.EventsLineProcessed += (sender, data) => CastLinesProcessed++;
        NpcDamageManager = new NpcDamageManager();

        DispatcherTimer dispatcherTimer = new DispatcherTimer();
        dispatcherTimer.Tick += new EventHandler(DispatcherTimer_Tick);
        dispatcherTimer.Interval = new TimeSpan(0, 0, 0, 0, DISPATCHER_DELAY);
        dispatcherTimer.Start();

        NonPlayerSelectionTimer = new DispatcherTimer();
        NonPlayerSelectionTimer.Tick += NonPlayerSelectionTimer_Tick;
        NonPlayerSelectionTimer.Interval = new TimeSpan(0, 0, 0, 0, DISPATCHER_DELAY);

        // Setup themes
        ThemeManager.BeginUpdate();
        ThemeManager.AreNativeThemesEnabled = true;
        SharedThemeCatalogRegistrar.Register();
        DockingThemeCatalogRegistrar.Register();
        ThemeManager.CurrentTheme = ThemeName.MetroDark.ToString();
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

    private void Window_Closed(object sender, System.EventArgs e)
    {
      StopProcessing();
      Application.Current.Shutdown();
    }

    private void PlayerDPSTextWindow_Loaded(object sender, RoutedEventArgs e)
    {
      playerDPSTextWindow.State = DockingWindowState.AutoHide;
    }

    private void DispatcherTimer_Tick(object sender, EventArgs e)
    {
      UpdateLoadingProgress();
      UpdateStats();
      UpdateDPSText();

      if (NeedScrollIntoView)
      {
        npcDataGrid.ScrollIntoView(npcDataGrid.Items.CurrentItem);
        NeedScrollIntoView = false;
      }
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
      else if (e.Source == playerDPSTextWindowMenuItem)
      {
        Helpers.OpenWindow(playerDPSTextWindow);
      }
      else if (e.Source == dpsChartMenuItem)
      {
        if (chartWindow.IsOpen)
        {
          // just focus
          Helpers.OpenWindow(chartWindow);
        }
        else
        {
          chartWindow = new DocumentWindow(dockSite, "dpsChart", "DPS Over Time", null, new DPSChart());
          chartWindow.ContainerDockedSize = new Size(400, 300);
          Helpers.OpenWindow(chartWindow);
          ResetDPSChart();
          chartWindow.CanFloat = true;
          chartWindow.CanClose = true;
          chartWindow.MoveToNewHorizontalContainer();
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

    // NonPlayer Window
    private void AddNonPlayer(NonPlayer npc)
    {
      Dispatcher.InvokeAsync(() =>
      {
        NonPlayersView.Add(npc);
        npcDataGrid.Items.MoveCurrentToLast();
        if (!npcDataGrid.IsMouseOver)
        {
          NeedScrollIntoView = true;
        }
      });
    }

    private void RemoveNonPlayer(string name)
    {
      Dispatcher.InvokeAsync(() =>
      {
        int i = 0;
        foreach (NonPlayer item in NonPlayersView.Reverse())
        {
          i++;
          if (name == item.Name)
          {
            NonPlayersView.Remove(item);
            npcDataGrid.Items.Refresh(); // re-numbers
          }
        }
      });
    }

    private void NonPlayerSelectionTimer_Tick(object sender, EventArgs e)
    {
      NeedStatsUpdate = true;
      NonPlayerSelectionTimer.Stop();
    }

    private void NonPlayerDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      // adds a delay where a drag-select doesn't keep sending events
      NonPlayerSelectionTimer.Stop();
      NonPlayerSelectionTimer.Start();

      ThemedDataGrid callingDataGrid = sender as ThemedDataGrid;
      npcMenuItemSelectAll.IsEnabled = (callingDataGrid.SelectedItems.Count < callingDataGrid.Items.Count) && callingDataGrid.Items.Count > 0;
      npcMenuItemUnselectAll.IsEnabled = callingDataGrid.SelectedItems.Count > 0 && callingDataGrid.Items.Count > 0;
      npcMenuItemClear.IsEnabled = callingDataGrid.Items.Count > 0;
    }

    private void NonPlayerDataGridSelectFight_Click(object sender, RoutedEventArgs e)
    {
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      ThemedDataGrid callingDataGrid = menu.PlacementTarget as ThemedDataGrid;
      NonPlayer npc = callingDataGrid.SelectedItem as NonPlayer;
      if (npc != null && npc.FightID > -1)
      {
        Parallel.ForEach(NonPlayersView, (one) =>
        {
          if (one.FightID == npc.FightID)
          {
            Dispatcher.InvokeAsync(() => callingDataGrid.SelectedItems.Add(one));
          }
        });
      }
    }

    private void NonPlayerDataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
      DataGrid_LoadingRow(sender, e);

      NonPlayer npc = e.Row.Item as NonPlayer;
      if (npc != null && npc.BeginTimeString == NonPlayer.BREAK_TIME)
      {
        if (e.Row.Background != BREAK_TIME_BRUSH)
        {
          e.Row.Background = BREAK_TIME_BRUSH;
        }
      }
      else if (e.Row.Background != NORMAL_BRUSH)
      {
        e.Row.Background = NORMAL_BRUSH;
      }

      if (npcMenuItemSelectFight.IsEnabled == false)
      {
        npcMenuItemSelectFight.IsEnabled = true;
      }
    }

    private void DataGridClear_Click(object sender, RoutedEventArgs e)
    {
      DataManager.Instance.Clear();
    }

    private void DataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
      e.Row.Header = (e.Row.GetIndex() + 1).ToString();
    }

    // Player DPS Data Grid
    private void PlayerDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      NeedDPSTextUpdate = true;
      UpdatePlayerDataGridMenuItems();
    }

    private void PlayerDataGridExpander_Loaded(object sender, RoutedEventArgs e)
    {
      Image image = (sender as Image);
      PlayerStats stats = image.DataContext as PlayerStats;
      if (stats != null && CurrentStats.Children.ContainsKey(stats.Name) && (CurrentStats.Children[stats.Name].Count > 1 || stats.Name == DataManager.UNASSIGNED_PET_OWNER))
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
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      ThemedDataGrid callingDataGrid = menu.PlacementTarget as ThemedDataGrid;
      callingDataGrid.SelectAll();
    }

    private void DataGridUnselectAll_Click(object sender, RoutedEventArgs e)
    {
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      ThemedDataGrid callingDataGrid = menu.PlacementTarget as ThemedDataGrid;
      callingDataGrid.UnselectAll();
    }

    private void PlayerDataGridSpellCastsByClass_Click(object sender, RoutedEventArgs e)
    {
      MenuItem menuItem = (sender as MenuItem);
      ShowSpellCasts(StatsBuilder.GetSelectedPlayerStatsByClass(menuItem.Tag as string, playerDataGrid.Items));
    }

    private void PlayerDataGridShowSpellCasts_Click(object sender, RoutedEventArgs e)
    {
      if (playerDataGrid.SelectedItems.Count > 0)
      {
        ShowSpellCasts(playerDataGrid.SelectedItems.Cast<PlayerStats>().ToList());
      }
    }

    private void ShowSpellCasts(List<PlayerStats> selectedStats)
    {
      ThemedDataGrid dataGrid = new ThemedDataGrid();
      dataGrid.AlternatingRowBackground = null;
      dataGrid.AutoGenerateColumns = false;
      dataGrid.RowHeaderWidth = 0;
      dataGrid.IsReadOnly = true;

      dataGrid.Columns.Add(new DataGridTextColumn()
      {
        Header = "",
        Binding = new Binding("Spell"),
        CellStyle = Application.Current.Resources["SpellGridCellStyle"] as Style
      });

      dataGrid.Sorting += (s, e2) =>
      {
        if (e2.Column.Header != null && (e2.Column.Header.ToString() != ""))
        {
          e2.Column.SortDirection = e2.Column.SortDirection ?? ListSortDirection.Ascending;
        }
      };

      List<string> playerList = new List<string>();
      foreach (var stats in selectedStats)
      {
        string name = stats.Name;
        if (CurrentStats.Children.ContainsKey(stats.Name) && CurrentStats.Children[stats.Name].Count > 1)
        {
          name = CurrentStats.Children[stats.Name].First().Name;
        }

        playerList.Add(name);
      }

      ObservableCollection<SpellCountRow> rows = new ObservableCollection<SpellCountRow>();
      dataGrid.ItemsSource = rows;

      busyIcon.Visibility = Visibility.Visible;
      Task.Delay(20).ContinueWith(task =>
      {
        try
        {
          var raidStats = CurrentStats.RaidStats;
          if (raidStats.FirstFightID < int.MaxValue && raidStats.LastFightID > int.MinValue && raidStats.BeginTimes.ContainsKey(raidStats.FirstFightID)
            && raidStats.LastTimes.ContainsKey(raidStats.LastFightID))
          {
            DateTime start = raidStats.BeginTimes[CurrentStats.RaidStats.FirstFightID];
            DateTime end = raidStats.LastTimes[CurrentStats.RaidStats.LastFightID];
            SpellCounts counts = SpellCountBuilder.GetSpellCounts(playerList, start.AddSeconds(-10), end);

            int colCount = 0;
            foreach (string name in counts.SortedPlayers)
            {
              string colBinding = "Values[" + colCount + "]"; // dont use colCount directory since it will change during Dispatch
              int total = counts.TotalCountMap.ContainsKey(name) ? counts.TotalCountMap[name] : 0;

              Dispatcher.InvokeAsync(() =>
              {
                DataGridTextColumn col = new DataGridTextColumn() { Header = name + " = " + total, Binding = new Binding(colBinding) };
                col.CellStyle = Application.Current.Resources["RightAlignGridCellStyle"] as Style;
                col.HeaderStyle = Application.Current.Resources["BrightCenterGridHeaderStyle"] as Style;
                dataGrid.Columns.Add(col);
              });

              Thread.Sleep(5);
              colCount++;
            }

            Dispatcher.InvokeAsync(() =>
            {
              int total = counts.UniqueSpellCounts.Values.Sum();
              DataGridTextColumn col = new DataGridTextColumn() { Header = "Total Count = " + total, Binding = new Binding("Values[" + colCount + "]") };
              col.CellStyle = Application.Current.Resources["RightAlignGridCellStyle"] as Style;
              col.HeaderStyle = Application.Current.Resources["BrightCenterGridHeaderStyle"] as Style;
              dataGrid.Columns.Add(col);
            });

            foreach (var spell in counts.SpellList)
            {
              SpellCountRow row = new SpellCountRow() { Spell = spell, Values = new int[counts.SortedPlayers.Count + 1] };
              row.IsReceived = spell.StartsWith("Received");

              int i;
              for (i = 0; i < counts.SortedPlayers.Count; i++)
              {
                if (counts.PlayerCountMap.ContainsKey(counts.SortedPlayers[i]))
                {
                  row.Values[i] = counts.PlayerCountMap[counts.SortedPlayers[i]].ContainsKey(spell) ? counts.PlayerCountMap[counts.SortedPlayers[i]][spell] : 0;
                }
              }

              row.Values[i] = counts.UniqueSpellCounts[spell];
              Dispatcher.InvokeAsync(() => rows.Add(row));
              Thread.Sleep(5);
            }
          }

          Dispatcher.InvokeAsync(() => busyIcon.Visibility = Visibility.Hidden);
        }
        catch (Exception err)
        {
          LOG.Error(err);
        }
      });

      if (spellCastsWindow == null || !spellCastsWindow.IsOpen)
      {
        spellCastsWindow = new DocumentWindow(dockSite, "spellCastsWindow", "Spell Counts", null, dataGrid);
      }
      else
      {
        spellCastsWindow.Content = dataGrid;
      }

      Helpers.OpenWindow(spellCastsWindow);
      spellCastsWindow.MoveToLast();
    }

    private void PlayerDataGridShowDamagByClass_Click(object sender, RoutedEventArgs e)
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

    private void ShowDamage(List<PlayerStats> selected)
    {
      ObservableCollection<PlayerStats> list = new ObservableCollection<PlayerStats>();
      playerDamageDataGrid.ItemsSource = list; busyIcon.Visibility = Visibility.Visible;

      Task.Delay(20).ContinueWith(task =>
      {
        foreach (var playerStat in selected)
        {
          if (CurrentStats.Children.ContainsKey(playerStat.Name))
          {
            foreach (var childStat in CurrentStats.Children[playerStat.Name])
            {
              Dispatcher.InvokeAsync(() => list.Add(childStat));
            }
          }
          else
          {
            Dispatcher.InvokeAsync(() => list.Add(playerStat));
          }

          Thread.Sleep(120);
        }

        Dispatcher.InvokeAsync(() => busyIcon.Visibility = Visibility.Hidden);
      });

      if (!damageWindow.IsOpen)
      {
        damageWindow = new DocumentWindow(dockSite, "damageWindow", "Damage Breakdown", null, playerDamageParent);
      }

      Helpers.OpenWindow(damageWindow);
      damageWindow.MoveToLast();
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
      if (stats != null && childrenDataGrid != null && CurrentStats != null && CurrentStats.Children.ContainsKey(stats.Name))
      {
        if (childrenDataGrid.ItemsSource != CurrentStats.Children[stats.Name])
        {
          childrenDataGrid.ItemsSource = CurrentStats.Children[stats.Name];
        }
      }
    }

    private void PlayerDamageGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
      e.Row.IsHitTestVisible = false;
    }

    private void PlayerSubGrid_RowDetailsVis(object sender, DataGridRowDetailsEventArgs e)
    {
      PlayerStats stats = e.Row.Item as PlayerStats;
      var subStatsDataGrid = e.DetailsElement as DataGrid;
      if (stats != null && subStatsDataGrid != null && CurrentStats != null && CurrentStats.SubStats.ContainsKey(stats.Name))
      {
        if (subStatsDataGrid.ItemsSource != CurrentStats.SubStats[stats.Name])
        {
          subStatsDataGrid.ItemsSource = CurrentStats.SubStats[stats.Name];
        }
      }
    }

    // Player DPS Text/Send to EQ Window
    private void CopyToEQ_Click(object sender, RoutedEventArgs e)
    {
      Clipboard.SetText(playerDPSTextBox.Text);
    }

    private void PlayerDPSText_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
      if (!playerDPSTextBox.IsFocused)
      {
        playerDPSTextBox.Focus();
      }
    }

    private void UpdateLoadingProgress()
    {
      if (EQLogReader != null && UpdatingProgress)
      {
        busyIcon.Visibility = Visibility.Visible;
        bytesReadTitle.Content = "Reading:";
        processedTimeLabel.Content = Math.Round((DateTime.Now - StartLoadTime).TotalSeconds, 1) + " sec";
        double filePercent = EQLogReader.FileSize > 0 ? Math.Min(Convert.ToInt32((double)FilePosition / EQLogReader.FileSize * 100), 100) : 100;
        double castPercent = CastLineCount > 0 ? Math.Round((double)CastLinesProcessed / CastLineCount * 10, 1) : 0;
        double damagePercent = DamageLineCount > 0 ? Math.Round((double)DamageLinesProcessed / DamageLineCount * 10, 1) : 0;
        bytesReadLabel.Content = filePercent + "%";
        processedCastsLabel.Content = castPercent;
        processedDamageLabel.Content = damagePercent;

        if ((filePercent >= 100 || MonitorOnly) && EQLogReader.FileLoadComplete)
        {
          bytesReadTitle.Content = "Monitoring:";
          bytesReadLabel.Content = "Active";
          bytesReadLabel.Foreground = GOOD_BRUSH;
        }

        if (MonitorOnly || (filePercent >= 100 && castPercent >= 10 && damagePercent >= 10 && EQLogReader.FileLoadComplete))
        {
          UpdatingProgress = false;
          bytesReadTitle.Content = "Monitoring";
          busyIcon.Visibility = Visibility.Hidden;
          processedCastsLabel.Content = "-";
          processedDamageLabel.Content = "-";
          LOG.Info("Finished Loading Log File");
        }
      }
    }

    private void PlayerDPSTextCheckChange(object sender, RoutedEventArgs e)
    {
      NeedDPSTextUpdate = true;
    }

    private void UpdateDPSText()
    {
      if (NeedDPSTextUpdate)
      {
        busyIcon.Visibility = Visibility.Visible;
        DataGrid grid = playerDataGrid;
        Label label = dpsTitle;

        var selected = grid.SelectedItems;
        if (selected != null && CurrentStats != null && selected.Count > 0)
        {
          List<PlayerStats> list = selected.Cast<PlayerStats>().ToList();
          StatsSummary summary = StatsBuilder.BuildSummary(CurrentStats, list, playerDPSTextDoTotals.IsChecked ?? false, playerDPSTextDoRank.IsChecked ?? false);
          playerDPSTextBox.Text = summary.Title + summary.RankedPlayers;
          playerDPSTextBox.SelectAll();
          UpdateDPSChart("Selected Players DPS Over Time", list);
        }
        else
        {
          playerDPSTextBox.Text = SHARE_DPS_LABEL;

          if (CurrentStats != null)
          {
            var list = CurrentStats.StatsList.Take(5).ToList();
            UpdateDPSChart("Top " + list.Count + " DPS Over Time", CurrentStats.StatsList.Take(5).ToList());
          }
        }

        busyIcon.Visibility = Visibility.Hidden;
        NeedDPSTextUpdate = false;
      }
    }

    private void ResetDPSChart()
    {
      if (chartWindow != null && chartWindow.IsOpen)
      {
        chartWindow.Title = "DPS Over Time";
        (chartWindow.Content as DPSChart).Reset();
      }
    }

    private void UpdateDPSChart(string title, List<PlayerStats> list)
    {
      if (chartWindow != null && chartWindow.IsOpen)
      {
        var chartData = StatsBuilder.GetDPSValues(CurrentStats, list, NpcDamageManager);
        (chartWindow.Content as DPSChart).Update(chartData);
        chartWindow.Title = title;
      }
    }

    private void UpdateStats()
    {
      if (NeedStatsUpdate && !UpdatingStats)
      {
        bool taskStarted = false;
        UpdatingStats = true;
        var selected = npcDataGrid.SelectedItems;
        if (selected.Count > 0)
        {
          dpsTitle.Content = "Calculating DPS...";
          playerDataGrid.ItemsSource = null;

          var realItems = selected.Cast<NonPlayer>().Where(item => !item.Name.Contains("Inactivity >")).ToList();
          if (realItems.Count > 0)
          {
            taskStarted = true;
            busyIcon.Visibility = Visibility.Visible;
            new Task(() =>
            {
              CurrentStats = StatsBuilder.BuildTotalStats(realItems);
              Dispatcher.InvokeAsync((() =>
              {
                if (NeedStatsUpdate)
                {
                  dpsTitle.Content = StatsBuilder.BuildTitle(CurrentStats);
                  playerDPSTextBox.Text = dpsTitle.Content.ToString();
                  playerDataGrid.ItemsSource = new ObservableCollection<PlayerStats>(CurrentStats.StatsList);

                  var list = CurrentStats.StatsList.Take(5).ToList();
                  UpdateDPSChart("Top " + list.Count + " DPS Over Time", CurrentStats.StatsList.Take(5).ToList());
                  NeedStatsUpdate = false;
                  UpdatingStats = false;
                  UpdatePlayerDataGridMenuItems();
                }
                Dispatcher.InvokeAsync(() => busyIcon.Visibility = Visibility.Hidden);
              }));
            }).Start();
          }
        }

        if (!taskStarted)
        {
          if (playerDataGrid.ItemsSource is ObservableCollection<PlayerStats> list)
          {
            CurrentStats = null;
            dpsTitle.Content = DPS_LABEL;
            playerDPSTextBox.Text = "";
            list.Clear();
            ResetDPSChart();
          }

          UpdatePlayerDataGridMenuItems();
          NeedStatsUpdate = false;
          UpdatingStats = false;
        }
      }
    }

    private void UpdatePlayerDataGridMenuItems()
    {
      if (CurrentStats != null && CurrentStats.StatsList.Count > 0)
      {
        pdgMenuItemSelectAll.IsEnabled = playerDataGrid.SelectedItems.Count < playerDataGrid.Items.Count;
        pdgMenuItemUnselectAll.IsEnabled = playerDataGrid.SelectedItems.Count > 0;
        pdgMenuItemShowDamage.IsEnabled = pdgMenuItemShowSpellCasts.IsEnabled = true;

        foreach (var item in pdgMenuItemShowDamage.Items)
        {
          MenuItem menuItem = item as MenuItem;
          if (menuItem.Header as string == "Selected")
          {
            menuItem.IsEnabled = playerDataGrid.SelectedItems.Count > 0;
          }
          else
          {
            menuItem.IsEnabled = CurrentStats.UniqueClasses.ContainsKey(menuItem.Header as string);
          }
        }

        foreach (var item in pdgMenuItemShowSpellCasts.Items)
        {
          MenuItem menuItem = item as MenuItem;
          if (menuItem.Header as string == "Selected")
          {
            menuItem.IsEnabled = playerDataGrid.SelectedItems.Count > 0;
          }
          else
          {
            menuItem.IsEnabled = CurrentStats.UniqueClasses.ContainsKey(menuItem.Header as string);
          }
        }
      }
      else
      {
        pdgMenuItemUnselectAll.IsEnabled = pdgMenuItemSelectAll.IsEnabled = pdgMenuItemShowDamage.IsEnabled = pdgMenuItemShowSpellCasts.IsEnabled = false;
      }
    }


    private void PlayerDPSTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
      if (playerDPSTextBox.Text == "" || playerDPSTextBox.Text == SHARE_DPS_LABEL)
      {
        copyToEQButton.IsEnabled = copyToEQRightClick.IsEnabled = false;
        copyToEQButton.Foreground = LIGHTER_BRUSH;
        sharePlayerDPSLabel.Text = SHARE_DPS_LABEL;
        sharePlayerDPSLabel.Foreground = BRIGHT_TEXT_BRUSH;
        sharePlayerDPSWarningLabel.Text = playerDPSTextBox.Text.Length + "/" + 509;
        sharePlayerDPSWarningLabel.Visibility = Visibility.Hidden;
      }
      else if (playerDPSTextBox.Text.Length > 509)
      {
        copyToEQButton.IsEnabled = copyToEQRightClick.IsEnabled = false;
        copyToEQButton.Foreground = LIGHTER_BRUSH;
        sharePlayerDPSLabel.Text = SHARE_DPS_TOO_BIG_LABEL;
        sharePlayerDPSLabel.Foreground = WARNING_BRUSH;
        sharePlayerDPSWarningLabel.Text = playerDPSTextBox.Text.Length + "/" + 509;
        sharePlayerDPSWarningLabel.Foreground = WARNING_BRUSH;
        sharePlayerDPSWarningLabel.Visibility = Visibility.Visible;
      }
      else if (playerDPSTextBox.Text.Length > 0 && playerDPSTextBox.Text != SHARE_DPS_LABEL)
      {
        copyToEQButton.IsEnabled = copyToEQRightClick.IsEnabled = true;
        copyToEQButton.Foreground = BRIGHT_TEXT_BRUSH;
        var count = playerDataGrid.SelectedItems.Count;
        string players = count == 1 ? "Player" : "Players";
        sharePlayerDPSLabel.Text = String.Format("{0} {1} Selected", count, players);
        sharePlayerDPSLabel.Foreground = BRIGHT_TEXT_BRUSH;
        sharePlayerDPSWarningLabel.Text = playerDPSTextBox.Text.Length + " / " + 509;
        sharePlayerDPSWarningLabel.Foreground = GOOD_BRUSH;
        sharePlayerDPSWarningLabel.Visibility = Visibility.Visible;
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
        dialog.Filter = "eqlog_player_server (.txt)|*.txt";

        // show dialog and read result
        // if null result then dialog was probably canceled
        bool? result = dialog.ShowDialog();
        if (result == true)
        {
          StopProcessing();
          CastProcessor = new ActionProcessor<string>("CastProcessor", CastLineParser.Process);
          DamageProcessor = new ActionProcessor<string>("DamageProcessor", DamageLineParser.Process);

          bytesReadLabel.Foreground = BRIGHT_TEXT_BRUSH;
          processedCastsLabel.Foreground = BRIGHT_TEXT_BRUSH;
          processedDamageLabel.Foreground = BRIGHT_TEXT_BRUSH;
          Title = APP_NAME + " " + VERSION + " -- (" + dialog.FileName + ")";
          StartLoadTime = DateTime.Now;
          CastLineCount = DamageLineCount = CastLinesProcessed = DamageLinesProcessed = FilePosition = 0;

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
          progressWindow.IsOpen = UpdatingProgress = true;
          EQLogReader = new LogReader(dialog.FileName, FileLoadingCallback, monitorOnly, lastMins);
          EQLogReader.Start();
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
      }

      if (DamageProcessor.Size() > 100000 || CastProcessor.Size() > 100000)
      {
        Thread.Sleep(20);
      }
    }

    private void StopProcessing()
    {
      if (EQLogReader != null)
      {
        EQLogReader.Stop();
      }

      if (CastProcessor != null)
      {
        CastProcessor.Stop();
      }

      if (DamageProcessor != null)
      {
        DamageProcessor.Stop();
      }
    }
  }
}
