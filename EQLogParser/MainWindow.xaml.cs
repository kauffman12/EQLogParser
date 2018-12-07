
using ActiproSoftware.Windows.Controls.Docking;
using ActiproSoftware.Windows.Themes;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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
    public static string PlayerName = "Unknown";

    private const string APP_NAME = "EQLogParser";
    private const string VERSION = "v1.0.3";
    private const string DPS_LABEL = " No NPCs Selected";
    private const string DPS_SUMMARY_LABEL = "No Players Selected";
    private const int DISPATCHER_DELAY = 200; // millis

    private static SolidColorBrush NORMAL_BRUSH = new SolidColorBrush(Color.FromRgb(37, 37, 38));
    private static SolidColorBrush BREAK_TIME_BRUSH = new SolidColorBrush(Color.FromRgb(150, 65, 13));

    // line queues
    private static ActionProcessor NpcDamageProcessor;

    // stats
    private static bool NeedStatsUpdate = false;
    private static bool NeedDPSTextUpdate = false;
    private static CombinedStats CurrentStats = null;
    private DispatcherTimer NonPlayerSelectionTimer;

    // progress window
    private static bool UpdatingProgress = false;
    private static long ProcessedBytes = 0; // EOF
    private static DateTime StartLoadTime; // millis

    private NpcDamageManager NpcDamageManager = null;
    private LogReader EQLogReader = null;

    private bool NeedScrollIntoView = false;
    private string LogFile = null;
    private ObservableCollection<NonPlayer> NpcList = new ObservableCollection<NonPlayer>();
    private ObservableCollection<PetMapping> PetMappingList = new ObservableCollection<PetMapping>();
    private ObservableCollection<Player> VerifiedPetsList = new ObservableCollection<Player>();
    private ObservableCollection<Player> VerifiedPlayersList = new ObservableCollection<Player>();

    public MainWindow()
    {
      InitializeComponent();
      npcDataGrid.ItemsSource = NpcList;
      petMappingGrid.ItemsSource = PetMappingList;
      verifiedPetsGrid.ItemsSource = VerifiedPetsList;
      verifiedPlayersGrid.ItemsSource = VerifiedPlayersList;
      debugDataGrid.ItemsSource = new ObservableCollection<string>();
      UpdateWindowTitle();
      dpsTitle.Content = DPS_LABEL;
      playerDPSTextBox.Text = DPS_SUMMARY_LABEL;

      // fix player DPS table sorting
      playerDataGrid.Sorting += (s, e) =>
      {
        if (e.Column.Header != null && (e.Column.Header.ToString() != "Name" && e.Column.Header.ToString() != "Additional Details"))
        {
          e.Column.SortDirection = e.Column.SortDirection ?? ListSortDirection.Ascending;
        }
      };

      NpcDamageManager = new NpcDamageManager();
      NpcDamageProcessor = new ActionProcessor(ProcessNPCDamage);
      LineParser.NpcDamageManagerInstance = NpcDamageManager;

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

    private void reset()
    {
      dpsTitle.Content = DPS_LABEL;
      playerDPSTextBox.Text = DPS_SUMMARY_LABEL;
      completeLabel.Foreground = new SolidColorBrush(Colors.White);

      if (EQLogReader != null)
      {
        EQLogReader.Stop();
      }

      UpdateWindowTitle();
      NpcList.Clear();
      NpcDamageManager = new NpcDamageManager();
      LineParser.NpcDamageManagerInstance = NpcDamageManager;

      NeedStatsUpdate = true;
      UpdatingProgress = true;
      ProcessedBytes = 0;
      StartLoadTime = DateTime.Now;
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
          completeLabel.Foreground = new SolidColorBrush(Colors.LightGreen);
        }
      }
    }

    private void UpdatePlayerName()
    {
      if (LogFile.Length > 0)
      {
        string fileName = LogFile.Substring(LogFile.LastIndexOf("\\") + 1);
        string[] parts = fileName.Split('_');

        if (parts.Length > 1)
        {
          PlayerName = parts[1];
          LineParser.AttackerReplacement.TryAdd("you", PlayerName);
          LineParser.AttackerReplacement.TryAdd("You", PlayerName);

          if (LineParser.VerifiedPlayers.TryAdd(PlayerName, true))
          {
            VerifiedPlayersList.Add(new Player() { Name = PlayerName });
          }
        }
      }
    }

    private void UpdateDPSText()
    {
      if (NeedDPSTextUpdate)
      {
        var selected = playerDataGrid.SelectedItems;
        if (selected != null && selected.Count > 0)
        {
          playerDPSTextBox.Text = StatsBuilder.GetSummary(CurrentStats, selected.Cast<PlayerStats>().ToList());
          playerDPSTextBox.SelectAll();
        }
        else
        {
          playerDPSTextBox.Text = playerDPSTextBox.Text = DPS_SUMMARY_LABEL;
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
            Dispatcher.InvokeAsync((Action)(() =>
            {
              if (NeedStatsUpdate)
              {
                dpsTitle.Content = CurrentStats.Title;
                playerDataGrid.ItemsSource = new ObservableCollection<PlayerStats>(CurrentStats.StatsList);
                NeedStatsUpdate = false;
                playerDPSTextBox.Text = playerDPSTextBox.Text = DPS_SUMMARY_LABEL;
              }
            }));
          }).Start();
        }
        else
        {
          if (playerDataGrid.ItemsSource is ObservableCollection<PlayerStats> list)
          {
            dpsTitle.Content = DPS_LABEL;
            list.Clear();
            NeedStatsUpdate = false;
            playerDPSTextBox.Text = DPS_SUMMARY_LABEL;
          }
        }
      }
    }

    private void UpdateWindowTitle()
    {
      Title = APP_NAME + " " + VERSION;

      if (LogFile != null)
      {
        Title += " -- (" + LogFile + ")";
      }
    }

    private void RemovePlayers(List<string> players)
    {
      int i = 0;
      foreach (NonPlayer npc in NpcList.Reverse())
      {
        i++;
        if (players.Contains(npc.Name))
        {
          NpcList.Remove(npc);
          npcDataGrid.Items.Refresh(); // re-numbers
        }
      }
    }

    private void PlayerDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      NeedDPSTextUpdate = true;
    }

    private void NpcDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      // adds a delay where a drag-select doesn't keep sending events
      NonPlayerSelectionTimer.Stop();
      NonPlayerSelectionTimer.Start();
    }

    private void DataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
      e.Row.Header = (e.Row.GetIndex() + 1).ToString();
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

    private void MenuItemTest_Click(object sender, RoutedEventArgs e)
    {
      Regex CheckDirectDamage = new Regex(@"^(?:(\w+)(?:`s (pet|ward|warder))?) (?:" + string.Join("|", DataTypes.DAMAGE_LIST) + @")[es]{0,2} (.+) for (\d+) points of (?:non-melee )?damage\.", RegexOptions.Singleline | RegexOptions.Compiled);
      List<string> list = new List<string>();

      DateTime start = DateTime.Now;
      for (int i = 0; i < 20000000; i++)
      {
        DateTime obj;
        DateTime.TryParseExact("Fri Nov 16 23:15:27 2018", "ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out obj);
      }


      list.ForEach(item => CheckDirectDamage.Matches(item));
      Console.WriteLine((DateTime.Now - start).TotalSeconds);
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
      else if (e.Source == debugWindowMenuItem)
      {
        if (!debugWindow.IsOpen)
        {
          debugWindow = new DocumentWindow(docSite, "debugWindow", "Debug", null, debugDataGrid);
        }

        Utils.OpenWindow(debugWindow);
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
      System.Nullable<bool> result = dialog.ShowDialog();
      if (result == true)
      {
        LogFile = dialog.FileName;
        UpdatePlayerName();
        reset();

        progressWindow.IsOpen = true;
        EQLogReader = new LogReader(dialog.FileName, FileLoadingCallback);
        EQLogReader.Start();
      }
    }

    private void FileLoadingCallback(string line)
    {
      ProcessLine pline = LineParser.KeepForProcessingState(line);
      FileLoadingContinue(pline);

      if (NpcDamageProcessor.QueueSize() > 100000)
      {
        Thread.Sleep(50);
      }
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
        // check for lines to verify player names
        string name;
        bool needRemove;

        switch (pline.State)
        {
          case 0:
            // check for damage
            DateTime lastUpdateTime = NpcDamageManager.GetLastUpdateTime();
            List<string> newPlayers;
            List<string> newPets;
            bool needRemoved;
            DamageRecord record = LineParser.ParseDamage(pline.ActionPart, out newPlayers, out newPets, out needRemoved);

            if (newPlayers.Count > 0 || newPets.Count > 0)
            {
              if (needRemoved)
              {
                Dispatcher.InvokeAsync(() => RemovePlayers(newPlayers.Concat(newPets).ToList()));
              }

              newPlayers.ForEach(player =>
              {
                Dispatcher.InvokeAsync(() => VerifiedPlayersList.Add(new Player() { Name = player }));
              });
              newPets.ForEach(pet =>
              {
                Dispatcher.InvokeAsync(() => VerifiedPetsList.Add(new Player() { Name = pet }));
              });
            }

            if (record != null)
            {
              bool newEntry;
              NonPlayer npc = NpcDamageManager.AddOrUpdateNpc(record, pline.CurrentTime, pline.TimeString.Substring(4, 15), out newEntry);
              if (newEntry)
              {
                TimeSpan diff = pline.CurrentTime.Subtract(lastUpdateTime);
                if (lastUpdateTime != DateTime.MinValue && diff.TotalSeconds >= 60)
                {
                  NonPlayer divider = new NonPlayer() { BeginTimeString = NonPlayer.BREAK_TIME, Name = Utils.FormatTimeSpan(diff) };
                  Dispatcher.InvokeAsync(() => NpcList.Add(divider));
                }

                Dispatcher.InvokeAsync(() =>
                {
                  NpcList.Add(npc);
                  if (npcDataGrid.Items.Count > 0)
                  {
                    npcDataGrid.Items.MoveCurrentToLast();
                    if (!npcDataGrid.IsMouseOver)
                    {
                      NeedScrollIntoView = true;
                    }
                  }
                });
              }
              else
              {
                if (CurrentStats != null && CurrentStats.NpcIDs.Contains(npc.ID))
                {
                  NeedStatsUpdate = true;
                }
              }
            }
            else
            {
              Dispatcher.InvokeAsync(() =>
              {
                if (debugWindow.IsOpen && debugWindow.IsActive)
                {
                  ObservableCollection<string> collection = debugDataGrid.ItemsSource as ObservableCollection<string>;
                  if (collection != null)
                  {
                    collection.Add(pline.ActionPart);
                  }
                }
              });
            }
            break;
          case 1:
            // check slain
            LineParser.CheckForSlain(pline.ActionPart);
            break;
          case 2:
          case 3:
          case 4:
            if (LineParser.CheckForPlayers(pline.ActionPart, out name, out needRemove))
            {
              if (needRemove)
              {
                Dispatcher.InvokeAsync(() => RemovePlayers(new List<string>() { name }));
              }

              Dispatcher.InvokeAsync(() => VerifiedPlayersList.Add(new Player() { Name = name }));
            }
            break;
          case 5:
            // map pet to player
            if (LineParser.CheckForPetLeader(pline.ActionPart, out name))
            {
              if (name.Length > 0)
              {
                PetMapping mapping = new PetMapping() { Pet = name, Owner = LineParser.PetToPlayers[name] };

                NeedStatsUpdate = true;
                Dispatcher.InvokeAsync(() =>
                {
                  PetMappingList.Add(mapping);
                  VerifiedPetsList.Add(new Player() { Name = name });
                });
              }
            }
            break;
        }
      }
      catch (Exception e)
      {
        Console.WriteLine(e.StackTrace);
      }

      Interlocked.Add(ref ProcessedBytes, pline.Line.Length + 2);
    }
  }
}
