
using ActiproSoftware.Windows.Controls.Docking;
using ActiproSoftware.Windows.Themes;
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
using System.Windows.Media;
using System.Windows.Threading;

namespace EQLogParser
{
  public partial class MainWindow : Window
  {
    private const string APP_NAME = "EQLogParser";
    private const string VERSION = "v1.0.7";
    private const string DPS_LABEL = " No NPCs Selected";
    private const string SHARE_DPS_LABEL = "No Players Selected";
    private const string SHARE_DPS_TOO_BIG_LABEL = "Exceeded Copy/Paste Limit for EQ";
    private const int DISPATCHER_DELAY = 250; // millis

    private static SolidColorBrush NORMAL_BRUSH = new SolidColorBrush(Color.FromRgb(37, 37, 38));
    private static SolidColorBrush BREAK_TIME_BRUSH = new SolidColorBrush(Color.FromRgb(150, 65, 13));
    private static SolidColorBrush WARNING_BRUSH = new SolidColorBrush(Color.FromRgb(241, 109, 29));
    private static SolidColorBrush BRIGHT_TEXT_BRUSH = new SolidColorBrush(Colors.White);
    private static SolidColorBrush LIGHTER_BRUSH = new SolidColorBrush(Color.FromRgb(90, 90, 90));
    private static SolidColorBrush GOOD_BRUSH = new SolidColorBrush(Colors.LightGreen);

    private static ActionProcessor NpcDamageProcessor;

    private ObservableCollection<string> VerifiedPetsView = new ObservableCollection<string>();
    private ObservableCollection<string> VerifiedPlayersView = new ObservableCollection<string>();
    private ObservableCollection<NonPlayer> NonPlayersView = new ObservableCollection<NonPlayer>();
    private ObservableCollection<PetMapping> PetPlayersView = new ObservableCollection<PetMapping>();

    // stats
    private static bool NeedStatsUpdate = false;
    private static bool NeedDPSTextUpdate = false;
    private static CombinedStats CurrentStats = null;
    private DispatcherTimer NonPlayerSelectionTimer;
    private int CurrentFightID = 0;

    // progress window
    private static bool UpdatingProgress = false;
    private static long ProcessedBytes = 0; // EOF
    private static DateTime StartLoadTime; // millis

    private NpcDamageManager NpcDamageManager = null;
    private LogReader EQLogReader = null;
    private bool NeedScrollIntoView = false;

    public MainWindow()
    {
      InitializeComponent();

      // update titles
      Title = APP_NAME + " " + VERSION;
      dpsTitle.Content = DPS_LABEL;

      // pet -> players
      petMappingGrid.ItemsSource = PetPlayersView;
      DataManager.Instance.EventsNewPetMapping += (sender, mapping) => Dispatcher.InvokeAsync(() =>
      {
        PetPlayersView.Add(mapping);
        NeedStatsUpdate = true;
      });

      // verified pets table
      verifiedPetsGrid.ItemsSource = VerifiedPetsView;
      DataManager.Instance.EventsNewVerifiedPet += (sender, name) => Dispatcher.InvokeAsync(() => VerifiedPetsView.Add(name));

      // verified player table
      verifiedPlayersGrid.ItemsSource = VerifiedPlayersView;
      DataManager.Instance.EventsNewVerifiedPlayer += (sender, name) => Dispatcher.InvokeAsync(() => VerifiedPlayersView.Add(name));

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

      DispatcherTimer dispatcherTimer = new DispatcherTimer();
      dispatcherTimer.Tick += new EventHandler(DispatcherTimer_Tick);
      dispatcherTimer.Interval = new TimeSpan(0, 0, 0, 0, DISPATCHER_DELAY);
      dispatcherTimer.Start();

      NonPlayerSelectionTimer = new DispatcherTimer();
      NonPlayerSelectionTimer.Tick += NonPlayerSelectionTimer_Tick;
      NonPlayerSelectionTimer.Interval = new TimeSpan(0, 0, 0, 0, DISPATCHER_DELAY);

      ThemeManager.BeginUpdate();
      try
      {
        // Use the Actipro styles for native WPF controls that look great with Actipro's control products
        ThemeManager.AreNativeThemesEnabled = true;

        SharedThemeCatalogRegistrar.Register();
        DockingThemeCatalogRegistrar.Register();

        // Default the theme to Metro Light
        ThemeManager.CurrentTheme = ThemeName.MetroDark.ToString();
      }
      finally
      {
        ThemeManager.EndUpdate();
      }
    }

    private void AddNonPlayer(NonPlayer npc)
    {
      Dispatcher.InvokeAsync(() =>
      {
        npc.FightID = CurrentFightID;
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

    private void Window_Closed(object sender, System.EventArgs e)
    {
      if (EQLogReader != null)
      {
        EQLogReader.Stop();
      }

      if (NpcDamageProcessor != null)
      {
        NpcDamageProcessor.Stop();
      }

      Application.Current.Shutdown();
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

    private void NonPlayerSelectionTimer_Tick(object sender, EventArgs e)
    {
      NeedStatsUpdate = true;
      NonPlayerSelectionTimer.Stop();
    }

    private void UpdateLoadingProgress()
    {
      if (EQLogReader != null && UpdatingProgress)
      {
        double percentComplete = Convert.ToInt32((double)(ProcessedBytes + 2) / EQLogReader.FileSize * 100);
        fileSizeLabel.Content = Math.Ceiling(EQLogReader.FileSize / 1024.0) + " KB";
        bytesProcessedLabel.Content = Math.Ceiling(EQLogReader.BytesRead / 1024.0) + " KB";
        completeLabel.Content = percentComplete + "%";
        processedTimeLabel.Content = Math.Round((DateTime.Now - StartLoadTime).TotalSeconds, 1) + " sec";

        if (percentComplete >= 100.0)
        {
          percentComplete = 100;
          UpdatingProgress = false;
          completeLabel.Foreground = GOOD_BRUSH;
        }
      }
    }

    private void UpdateDPSText()
    {
      if (NeedDPSTextUpdate)
      {
        DataGrid grid;
        Label label;
        bool selectedOnly = false;
        if (dpsWindow.IsVisible)
        {
          grid = playerDataGrid;
          label = dpsTitle;
        }
        else
        {
          grid = playerDamageDataGrid;
          label = damageTitle;
          selectedOnly = true;
        }

        var selected = grid.SelectedItems;
        if (selected != null && selected.Count > 0)
        {
          Tuple<string, string> result = StatsBuilder.GetSummary(CurrentStats, selected.Cast<PlayerStats>().ToList(), selectedOnly);
          playerDPSTextBox.Text = result.Item1 + result.Item2;
          playerDPSTextBox.SelectAll();
        }
        else
        {
          playerDPSTextBox.Text = label.Content.ToString();
        }

        NeedDPSTextUpdate = false;
      }
    }

    private void UpdateStats()
    {
      if (NeedStatsUpdate)
      {
        var selected = npcDataGrid.SelectedItems;
        if (selected.Count > 0)
        {
          new Task(() =>
          {
            CurrentStats = StatsBuilder.BuildTotalStats(selected.Cast<NonPlayer>().ToList());
            Dispatcher.InvokeAsync((() =>
            {
              if (NeedStatsUpdate)
              {
                dpsTitle.Content = CurrentStats.TargetTitle + CurrentStats.DamageTitle;
                playerDPSTextBox.Text = dpsTitle.Content.ToString();
                playerDataGrid.ItemsSource = new ObservableCollection<PlayerStats>(CurrentStats.StatsList);
                NeedStatsUpdate = false;
              }
            }));
          }).Start();
        }
        else
        {
          if (playerDataGrid.ItemsSource is ObservableCollection<PlayerStats> list)
          {
            dpsTitle.Content = DPS_LABEL;
            playerDPSTextBox.Text = "";
            list.Clear();
            NeedStatsUpdate = false;
          }
        }
      }
    }

    private void PlayerDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      NeedDPSTextUpdate = true;
    }

    private void PlayerDamageDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      var list = playerDamageDataGrid.SelectedItems.Cast<PlayerStats>().ToList();
      if (list.Count == 0)
      {
        list = playerDamageDataGrid.ItemsSource as List<PlayerStats>;
      }
      damageTitle.Content = "Selected Players " + StatsBuilder.GetSummary(CurrentStats, list, true).Item1;
      NeedDPSTextUpdate = true;
    }

    private void NpcDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      // adds a delay where a drag-select doesn't keep sending events
      NonPlayerSelectionTimer.Stop();
      NonPlayerSelectionTimer.Start();
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

    }

    private void PlayerDPSText_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
      if (!playerDPSTextBox.IsFocused)
      {
        playerDPSTextBox.Focus();
      }
    }

    private void MenuItemWindow_Click(object sender, RoutedEventArgs e)
    {
      if (e.Source == npcWindowMenuitem)
      {
        Utils.OpenWindow(npcWindow);
      }
      else if (e.Source == fileProgressWindowMenuItem)
      {
        Utils.OpenWindow(progressWindow);
      }
      else if (e.Source == petMappingWindowMenuItem)
      {
        Utils.OpenWindow(petMappingWindow);
      }
      else if (e.Source == verifiedPlayersWindowMenuItem)
      {
        Utils.OpenWindow(verifiedPlayersWindow);
      }
      else if (e.Source == verifiedPetsWindowMenuItem)
      {
        Utils.OpenWindow(verifiedPetsWindow);
      }
      else if (e.Source == playerDPSTextWindowMenuItem)
      {
        Utils.OpenWindow(playerDPSTextWindow);
      }
    }

    private void MenuItemSelectLogFile_Click(object sender, RoutedEventArgs e)
    {
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
        dpsTitle.Content = DPS_LABEL;
        completeLabel.Foreground = BRIGHT_TEXT_BRUSH;
        Title = APP_NAME + " " + VERSION + " -- (" + dialog.FileName + ")";

        NeedStatsUpdate = true;
        UpdatingProgress = true;
        ProcessedBytes = 0;
        StartLoadTime = DateTime.Now;
        CurrentFightID = 0;

        if (NpcDamageProcessor != null)
        {
          NpcDamageProcessor.Stop();
        }

        if (EQLogReader != null)
        {
          EQLogReader.Stop();
        }

        NpcDamageProcessor = new ActionProcessor(ProcessNPCDamage);
        NpcDamageManager = new NpcDamageManager();

        string name = "Uknown";
        if (dialog.FileName.Length > 0)
        {
          string fileName = dialog.FileName.Substring(dialog.FileName.LastIndexOf("\\") + 1);
          string[] parts = fileName.Split('_');

          if (parts.Length > 1)
          {
            name = parts[1];
          }
        }

        DataManager.Instance.SetPlayerName(name);
        DataManager.Instance.Clear();

        NonPlayersView.Clear();

        progressWindow.IsOpen = true;
        EQLogReader = new LogReader(dialog.FileName, FileLoadingCallback, FileLoadingCompleteCallback);
        EQLogReader.Start();
      }
    }

    private void FileLoadingCallback(string line)
    {
      ProcessLine pline = LineParser.KeepForProcessingState(line);
      FileLoadingContinue(pline);

      if (NpcDamageProcessor.QueueSize() > 150000)
      {
        Thread.Sleep(25);
      }
    }

    private void FileLoadingCompleteCallback()
    {
      NpcDamageProcessor.LowerPriority();
    }

    private void FileLoadingContinue(ProcessLine pline)
    {
      if (pline != null && pline.State >= 0)
      {
        // prioritize checking for players
        if (pline.State >= 2)
        {
          NpcDamageProcessor.PrependToQueue(pline);
        }
        else
        {
          NpcDamageProcessor.AppendToQueue(pline);
        }
      }
      else
      {
        Interlocked.Add(ref ProcessedBytes, pline.Line.Length + 2);
      }
    }

    private void ProcessNPCDamage(object data)
    {
      ProcessLine pline = data as ProcessLine;

      if (pline.CurrentTime == DateTime.MinValue)
      {
        Interlocked.Add(ref ProcessedBytes, pline.Line.Length + 2);
        return; // abort
      }

      try
      {
        switch (pline.State)
        {
          case 0:
            // check for damage
            DateTime lastUpdateTime = NpcDamageManager.LastUpdateTime;
            DamageRecord record = LineParser.ParseDamage(pline.ActionPart);

            if (record != null)
            {
              TimeSpan diff = pline.CurrentTime.Subtract(lastUpdateTime);
              if (lastUpdateTime != DateTime.MinValue && diff.TotalSeconds >= 60)
              {
                NonPlayer divider = new NonPlayer() { BeginTimeString = NonPlayer.BREAK_TIME, Name = Utils.FormatTimeSpan(diff) };
                Dispatcher.InvokeAsync(() =>
                {
                  CurrentFightID++;
                  NonPlayersView.Add(divider);
                });
              }

              NpcDamageManager.AddOrUpdateNpc(record, pline.CurrentTime, pline.TimeString.Substring(4, 15));
            }
            break;
          case 1:
            // check slain
            LineParser.CheckForSlain(pline);
            break;
          case 2:
            LineParser.CheckForShrink(pline);
            break;
          case 3:
          case 4:
            LineParser.CheckForPlayers(pline);
            break;
          case 5:
            LineParser.CheckForPetLeader(pline);
            break;
          case 6:
            LineParser.CheckForHeal(pline);
            break;
        }
      }
      catch (Exception e)
      {
        Console.WriteLine(e.StackTrace);
      }

      Interlocked.Add(ref ProcessedBytes, pline.Line.Length + 2);
    }

    private void PlayerDPSTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
      if (playerDPSTextBox.Text == "")
      {
        copyToEQButton.IsEnabled = false;
        copyToEQButton.Foreground = LIGHTER_BRUSH;
        sharePlayerDPSLabel.Text = SHARE_DPS_LABEL;
        sharePlayerDPSLabel.Foreground = BRIGHT_TEXT_BRUSH;
        sharePlayerDPSWarningLabel.Text = playerDPSTextBox.Text.Length + "/" + 509;
        sharePlayerDPSWarningLabel.Visibility = Visibility.Hidden;
      }
      else if (playerDPSTextBox.Text.Length > 509)
      {
        copyToEQButton.IsEnabled = false;
        copyToEQButton.Foreground = LIGHTER_BRUSH;
        sharePlayerDPSLabel.Text = SHARE_DPS_TOO_BIG_LABEL;
        sharePlayerDPSLabel.Foreground = WARNING_BRUSH;
        sharePlayerDPSWarningLabel.Text = playerDPSTextBox.Text.Length + "/" + 509;
        sharePlayerDPSWarningLabel.Foreground = WARNING_BRUSH;
        sharePlayerDPSWarningLabel.Visibility = Visibility.Visible;
      }
      else if (playerDataGrid.SelectedItems.Count > 0)
      {
        copyToEQButton.IsEnabled = true;
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

    private void CopyToEQ_Click(object sender, RoutedEventArgs e)
    {
      Clipboard.SetText(playerDPSTextBox.Text);
    }

    private void DataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
      e.Row.Header = (e.Row.GetIndex() + 1).ToString();
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

    private void PlayerDataGridShowDamage_Click(object sender, RoutedEventArgs e)
    {
      if (playerDataGrid.SelectedItems.Count > 0)
      {
        var list = playerDataGrid.SelectedItems.Cast<PlayerStats>().ToList();
        playerDamageDataGrid.ItemsSource = list;
        damageTitle.Content = "Selected Players " + StatsBuilder.GetSummary(CurrentStats, list, true).Item1;
        if (!damageWindow.IsOpen)
        {
          damageWindow = new DocumentWindow(docSite, "damageWindow", "Damage Breakdown", null, playerDamageParent);
        }

        Utils.OpenWindow(damageWindow);
        damageWindow.MoveToLast();
      }
    }
  }
}
