using FontAwesome5;
using log4net;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;
using Syncfusion.Windows.Tools.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace EQLogParser
{
  internal static partial class MainActions
  {
    internal static event Action<string, bool> EventsLogLoadingComplete;
    internal static event Action<List<Fight>> EventsFightSelectionChanged;
    internal static event Action<string> EventsChartOpened;
    internal static event Action<string> EventsDamageSummaryOptionsChanged;
    internal static event Action<string> EventsHealingSummaryOptionsChanged;
    internal static event Action<WindowState> EventsWindowStateChanged;
    internal static event Action<PlayerStatsSelectionChangedEventArgs> EventsDamageSelectionChanged;
    internal static event Action<PlayerStatsSelectionChangedEventArgs> EventsHealingSelectionChanged;
    internal static event Action<PlayerStatsSelectionChangedEventArgs> EventsTankingSelectionChanged;
    internal static readonly HttpClient TheHttpClient = new();

    private const string PetsListTitle = "Verified Pets";
    private const string PlayerListTitle = "Verified Players";
    private const string PetOwnersTitle = "Pet Owners";
    public static readonly ObservableCollection<dynamic> VerifiedPlayersView = [];
    public static readonly ObservableCollection<dynamic> VerifiedPetsView = [];
    public static readonly List<string> ClassList = [];
    public static readonly ObservableCollection<PetMapping> PetPlayersView = [];
    private static readonly object VerifiedPlayersViewLock = new();
    private static readonly object VerifiedPetsViewLock = new();
    private static readonly object PetPlayersViewLock = new();
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly JsonSerializerOptions DiscordSerializationOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static MainWindow _mainWindow;

    static MainActions()
    {
      BindingOperations.EnableCollectionSynchronization(VerifiedPlayersView, VerifiedPlayersViewLock);
      BindingOperations.EnableCollectionSynchronization(VerifiedPetsView, VerifiedPetsViewLock);
      BindingOperations.EnableCollectionSynchronization(PetPlayersView, PetPlayersViewLock);
    }

    internal static void AddAndCopyDamageParse(CombinedStats combined, List<PlayerStats> selected) => _mainWindow?.AddAndCopyDamageParse(combined, selected);
    internal static void AddAndCopyTankParse(CombinedStats combined, List<PlayerStats> selected) => _mainWindow?.AddAndCopyTankParse(combined, selected);
    internal static void CopyToEqClick(string label) => _mainWindow?.CopyToEqClick(label);
    internal static void CloseDamageOverlay(bool reopen) => _mainWindow?.CloseDamageOverlay(reopen);
    internal static List<Fight> GetFights(bool selected) => _mainWindow?.GetFights(selected);
    internal static void FireChartOpened(string name) => EventsChartOpened?.Invoke(name);
    internal static void FireDamageSelectionChanged(PlayerStatsSelectionChangedEventArgs args) => EventsDamageSelectionChanged?.Invoke(args);
    internal static void FireTankingSelectionChanged(PlayerStatsSelectionChangedEventArgs args) => EventsTankingSelectionChanged?.Invoke(args);
    internal static void FireHealingSelectionChanged(PlayerStatsSelectionChangedEventArgs args) => EventsHealingSelectionChanged?.Invoke(args);
    internal static void FireWindowStateChanged(WindowState state) => EventsWindowStateChanged?.Invoke(state);
    internal static void FireLogLoadingEvent(string log, bool open) => EventsLogLoadingComplete?.Invoke(log, open);
    internal static void FireFightSelectionChanged(List<Fight> fights) => EventsFightSelectionChanged?.Invoke(fights);
    internal static void ShowTriggersEnabled(bool show) => _mainWindow?.ShowTriggersEnabled(show);
    internal static DockingManager GetDockSite() => _mainWindow.dockSite;
    internal static Window GetOwner() => _mainWindow;

    internal static void SetMainWindow(MainWindow main)
    {
      _mainWindow = main;

      // init theme and fonts through ThemeConfig
      ThemeConfig.Init(main);
    }

    internal static void UpdateStatus(string text)
    {
      ConfigUtil.InvokeEventsLoadingText(text);
      Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    internal static void AddDocumentWindows(DockingManager dockSite)
    {
      SyncFusionUtil.AddDocument(dockSite, typeof(TriggersTester), "triggerTestWindow", "Trigger Tester");
      SyncFusionUtil.AddDocument(dockSite, typeof(TriggersLogView), "triggerLogWindow", "Trigger Log");
      SyncFusionUtil.AddDocument(dockSite, typeof(HealingSummary), "healingSummaryWindow", "Healing Summary");
      SyncFusionUtil.AddDocument(dockSite, typeof(TankingSummary), "tankingSummaryWindow", "Tanking Summary");
      SyncFusionUtil.AddDocument(dockSite, typeof(DamageChart), "damageChartWindow", "DPS Trends");
      SyncFusionUtil.AddDocument(dockSite, typeof(DamageColumnChart), "damageBarChartWindow", "DPS Benchmark");
      SyncFusionUtil.AddDocument(dockSite, typeof(HealingChart), "healingChartWindow", "Healing Trends");
      SyncFusionUtil.AddDocument(dockSite, typeof(HealingColumnChart), "healingBarChartWindow", "HPS Benchmark");
      SyncFusionUtil.AddDocument(dockSite, typeof(TankingChart), "tankingChartWindow", "Tanking Trends");
      SyncFusionUtil.AddDocument(dockSite, typeof(ChatViewer), "chatWindow", "Chat Archive");
      SyncFusionUtil.AddDocument(dockSite, typeof(EventViewer), "specialEventsWindow", "Misc Events");
      SyncFusionUtil.AddDocument(dockSite, typeof(RandomViewer), "randomsWindow", "Random Rolls");
      SyncFusionUtil.AddDocument(dockSite, typeof(LootViewer), "lootWindow", "Looted Items");
      SyncFusionUtil.AddDocument(dockSite, typeof(TriggersView), "triggersWindow", "Trigger Manager");
      SyncFusionUtil.AddDocument(dockSite, typeof(NpcStatsViewer), "spellResistsWindow", "Spell Resists");
      SyncFusionUtil.AddDocument(dockSite, typeof(SpellDamageStatsViewer), "spellDamageStatsWindow", "Spell Damage");
      SyncFusionUtil.AddDocument(dockSite, typeof(TauntStatsViewer), "tauntStatsWindow", "Taunt Usage");
      SyncFusionUtil.AddDocument(dockSite, typeof(DamageSummary), "damageSummaryWindow", "DPS Summary", true);
    }

    internal static async Task SendDiscordMessage(string content, string webhookUrl)
    {
      try
      {
        var payload = new { content };
        var json = JsonSerializer.Serialize(payload, DiscordSerializationOptions);
        using var body = new StringContent(json, Encoding.UTF8, "application/json");

        // Reuses the same HttpClient instance & its connection pool
        var request = await TheHttpClient.PostAsync(webhookUrl, body);
        request.EnsureSuccessStatusCode();
      }
      catch (Exception ex)
      {
        Log.Error("Problem Sending to Discord", ex);
      }
    }

    internal static bool ToggleSetting(string setting, ImageAwesome icon)
    {
      var enabled = icon.Visibility == Visibility.Hidden;
      ConfigUtil.SetSetting(setting, enabled);
      icon.Visibility = enabled ? Visibility.Visible : Visibility.Hidden;
      return enabled;
    }

    internal static void CreateOpenLogMenuItems(MenuItem parent, RoutedEventHandler callback)
    {
      parent.Items.Add(CreateMenuItem("Now", "0", callback, EFontAwesomeIcon.Solid_CalendarDay));
      parent.Items.Add(CreateMenuItem("Last Hour", "1", callback, EFontAwesomeIcon.Solid_CalendarDay));
      parent.Items.Add(CreateMenuItem("Last  8 Hours", "8", callback, EFontAwesomeIcon.Solid_CalendarDay));
      parent.Items.Add(CreateMenuItem("Last 24 Hours", "24", callback, EFontAwesomeIcon.Solid_CalendarDay));
      parent.Items.Add(CreateMenuItem("Last 2 Days", "48", callback, EFontAwesomeIcon.Solid_CalendarAlt));
      parent.Items.Add(CreateMenuItem("Last  7 Days", "168", callback, EFontAwesomeIcon.Solid_CalendarAlt));
      parent.Items.Add(CreateMenuItem("Last 14 Days", "336", callback, EFontAwesomeIcon.Solid_CalendarAlt));
      parent.Items.Add(CreateMenuItem("Last 30 Days", "720", callback, EFontAwesomeIcon.Solid_CalendarAlt));
      parent.Items.Add(CreateMenuItem("Everything", null, callback, EFontAwesomeIcon.Solid_Infinity));

      static MenuItem CreateMenuItem(string name, string value, RoutedEventHandler handler, EFontAwesomeIcon awesome)
      {
        var imageAwesome = new ImageAwesome { Icon = awesome, Style = (Style)Application.Current.Resources["EQIconStyle"] };
        var menuItem = new MenuItem { Header = name, Tag = value };
        menuItem.Click += handler;
        menuItem.Icon = imageAwesome;
        return menuItem;
      }
    }

    // ---- Player / Pet UI helpers ----

    // should already run on the UI thread
    internal static void Clear(ContentControl petsWindow, ContentControl playersWindow, ContentControl petMappingWindow)
    {
      PetPlayersView.Clear();
      VerifiedPetsView.Clear();
      VerifiedPlayersView.Clear();

      var entry = new ExpandoObject() as dynamic;
      entry.Name = Labels.Unassigned;
      VerifiedPlayersView.Add(entry);
      DockingManager.SetHeader(petsWindow, $"{PetsListTitle} ({VerifiedPetsView.Count})");
      DockingManager.SetHeader(playersWindow, $"{PlayerListTitle} ({VerifiedPlayersView.Count})");
      DockingManager.SetHeader(petMappingWindow, $"{PetOwnersTitle} ({PetPlayersView.Count})");
    }

    // should already run on the UI thread
    internal static void InitPetOwners(MainWindow main, ContentControl petMappingWindow)
    {
      PlayerRegistry.Instance.EventsNewPetMapping += async (mapping) =>
      {
        await UiUtil.InvokeAsync(() =>
        {
          InsertPetMapping(mapping);
          DockingManager.SetHeader(petMappingWindow, $"{PetOwnersTitle} ({PetPlayersView.Count})");
        }, DispatcherPriority.DataBind);

        main.CheckComputeStats();
      };
    }

    // should already run on the UI thread
    internal static void InitVerifiedPlayers(ContentControl playersWindow, ContentControl petMappingWindow)
    {
      ClassList.Clear();
      ClassList.Add("");
      ClassList.AddRange(EQDataStore.Instance.GetClassList());
      PlayerRegistry.Instance.EventsNewVerifiedPlayer += async (name) =>
      {
        await UiUtil.InvokeAsync(() =>
        {
          UiUtil.InsertNameIntoSortedList(name, VerifiedPlayersView, true);
          DockingManager.SetHeader(playersWindow, $"{PlayerListTitle} ({VerifiedPlayersView.Count})");
        }, DispatcherPriority.DataBind);
      };

      PlayerRegistry.Instance.EventsUpdateDefaultPlayerClass += async (mapping) =>
      {
        await UiUtil.InvokeAsync(() =>
        {
          var entry = new ExpandoObject() as dynamic;
          entry.Name = mapping.Player;
          int index = VerifiedPlayersView.ToList().BinarySearch(entry, UiUtil.TheSortableNameComparer);
          if (index >= 0)
          {
            VerifiedPlayersView[index].PlayerClass = mapping.ClassName;
          }
        }, DispatcherPriority.DataBind);
      };

      PlayerRegistry.Instance.EventsRemoveVerifiedPlayer += async (name) =>
      {
        await UiUtil.InvokeAsync(() =>
        {
          var found = VerifiedPlayersView.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
          if (found != null)
          {
            VerifiedPlayersView.Remove(found);
            DockingManager.SetHeader(playersWindow, $"{PlayerListTitle} ({VerifiedPlayersView.Count})");

            var existing = PetPlayersView.FirstOrDefault(item => item.Owner.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
              PetPlayersView.Remove(existing);
              DockingManager.SetHeader(petMappingWindow, $"{PetOwnersTitle} ({PetPlayersView.Count})");
            }

            _mainWindow?.CheckComputeStats();
          }
        }, DispatcherPriority.DataBind);
      };
    }

    internal static void InitVerifiedPets(MainWindow main, ContentControl petsWindow, ContentControl petMappingWindow)
    {
      PlayerRegistry.Instance.EventsNewVerifiedPet += (name) => main.Dispatcher.InvokeAsync(async () =>
      {
        await UiUtil.InvokeAsync(() =>
        {
          UiUtil.InsertNameIntoSortedList(name, VerifiedPetsView);
          DockingManager.SetHeader(petsWindow, $"{PetsListTitle} ({VerifiedPetsView.Count})");
        }, DispatcherPriority.DataBind);
      });

      PlayerRegistry.Instance.EventsRemoveVerifiedPet += async (name) =>
      {
        await UiUtil.InvokeAsync(() =>
        {
          var found = VerifiedPetsView.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
          if (found != null)
          {
            VerifiedPetsView.Remove(found);
            DockingManager.SetHeader(petsWindow, $"{PetsListTitle} ({VerifiedPetsView.Count})");

            var existing = PetPlayersView.FirstOrDefault(item => item.Pet.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
              PetPlayersView.Remove(existing);
              DockingManager.SetHeader(petMappingWindow, $"{PetOwnersTitle} ({PetPlayersView.Count})");
            }

            main.CheckComputeStats();
          }
        }, DispatcherPriority.DataBind);
      };
    }

    // keep this on UI thread
    internal static void LoadVerified(ContentControl playersWindow, ContentControl petsWindow, List<string> players, List<string> pets)
    {
      UpdateWindow(VerifiedPlayersView, playersWindow, players, PlayerListTitle, true);
      UpdateWindow(VerifiedPetsView, petsWindow, pets, PetsListTitle, false);

      static void UpdateWindow(ObservableCollection<dynamic> view, ContentControl window, List<string> names, string title, bool isPlayer)
      {
        foreach (var name in names)
        {
          UiUtil.InsertNameIntoSortedList(name, view, isPlayer);
        }

        DockingManager.SetHeader(window, $"{title} ({view.Count})");
      }
    }

    // keep this on UI thread
    internal static void LoadPetOwners(ContentControl petMappingWindow, List<PetMapping> mappings)
    {
      foreach (var mapping in mappings)
      {
        InsertPetMapping(mapping);
      }

      DockingManager.SetHeader(petMappingWindow, $"{PetOwnersTitle} ({PetPlayersView.Count})");
    }

    internal static void FireDamageSummaryOptionsChanged(string option) => EventsDamageSummaryOptionsChanged?.Invoke(option);

    internal static void FireHealingSummaryOptionsChanged(string option) => EventsHealingSummaryOptionsChanged?.Invoke(option);

    internal static void UpdateDeleteChatMenu(MenuItem deleteChat)
    {
      deleteChat.Items.Clear();
      ChatDB.GetArchivedPlayers().ForEach(player =>
      {
        var item = new MenuItem { IsEnabled = true, Header = player };
        deleteChat.Items.Add(item);

        item.Click += (_, _) =>
        {
          var msgDialog = new MessageWindow($"Clear Chat Archive for {player}?", Resource.CLEAR_CHAT,
            MessageWindow.IconType.Warn, "Yes");
          msgDialog.ShowDialog();

          if (msgDialog.IsYes1Clicked)
          {
            if (!ChatDB.Instance.DeleteArchivedPlayer(player))
            {
              deleteChat.Items.Remove(item);
              deleteChat.IsEnabled = deleteChat.Items.Count > 0;
            }
          }
        };
      });

      deleteChat.IsEnabled = deleteChat.Items.Count > 0;
    }

    internal static async Task CreateBackupAsync()
    {
      var saveFileDialog = new SaveFileDialog();

      // get file name
      var filename = FileUtil.BuildBackupFilename();
      saveFileDialog.Filter = "EQLogParser Backup Files (*.zip)|*.zip";
      saveFileDialog.FileName = string.Join("", filename.Split(Path.GetInvalidFileNameChars()));

      if (saveFileDialog.ShowDialog() == true)
      {
        var dialog = new MessageWindow($"Creating EQLogParser Backup", Resource.CREATE_BACKUP, MessageWindow.IconType.Save, null, null, false, true);
        dialog.Show();

        ChatDB.Instance.Stop();
        await Task.Delay(250);

        var accessError = false;
        var source = Environment.ExpandEnvironmentVariables(ConfigUtil.AppData);
        var backupFile = saveFileDialog.FileName;

        try
        {
          if (File.Exists(backupFile))
          {
            File.Delete(backupFile);
          }

          // create checkpoint before backup
          await TriggerStateDB.Instance.CreateCheckpoint();
          ZipFile.CreateFromDirectory(source, backupFile, CompressionLevel.Optimal, false);
        }
        catch (Exception ex)
        {
          accessError = true;
          Log.Error("Problem creating backup file.", ex);
        }
        finally
        {
          // init if enabled
          ChatDB.Instance.Init();
          await UiUtil.InvokeAsync(() =>
          {
            dialog.Close();

            if (accessError)
            {
              new MessageWindow("Error Creating Backup. See log file for details.", Resource.CREATE_BACKUP,
                MessageWindow.IconType.Save).ShowDialog();
            }
          });
        }
      }
    }

    internal static void Restore()
    {
      var dialog = new CommonOpenFileDialog
      {
        // Set to false because we're opening a file, not selecting a folder
        IsFolderPicker = false,
      };

      // Show dialog and read result
      dialog.Filters.Add(new CommonFileDialogFilter("EQLogParser_backup.zip", "*.zip"));
      if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
      {
        Task.Delay(150).ContinueWith(async _ =>
        {
          string temp = null;
          var worked = false;
          var source = Environment.ExpandEnvironmentVariables(ConfigUtil.AppData);

          try
          {
            using var archive = ZipFile.OpenRead(dialog.FileName);
            // Check for the presence of the folder at the top level
            var containsFolder = archive.Entries
              .Any(entry => entry.FullName.StartsWith("config/triggers.db", StringComparison.OrdinalIgnoreCase));

            if (containsFolder)
            {
              UiUtil.InvokeNow(() =>
              {
                new MessageWindow("Click OK to Restore and Restart.", Resource.RESTORE_FROM_BACKUP, MessageWindow.IconType.Info).ShowDialog();
              });

              if (Directory.Exists(source))
              {
                temp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
                ZipFile.CreateFromDirectory(source, temp, CompressionLevel.Optimal, false);
                Directory.Delete(source, true);
              }

              Directory.CreateDirectory(source);
              ZipFile.ExtractToDirectory(dialog.FileName, source);
              worked = true;

              await Task.Delay(500);
              UiUtil.InvokeNow(() =>
              {
                Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _mainWindow.Close();
              });

              // restart
              var processModule = Process.GetCurrentProcess().MainModule;
              if (processModule != null)
              {
                var exePath = processModule.FileName;
                Process.Start(exePath);
              }

              if (!string.IsNullOrEmpty(temp) && File.Exists(temp))
              {
                File.Delete(temp);
              }

              UiUtil.InvokeNow(() =>
              {
                Application.Current.Shutdown();
              });
            }
            else
            {
              await UiUtil.InvokeAsync(() =>
              {
                new MessageWindow("Invalid backup file. Cannot restore.", Resource.RESTORE_FROM_BACKUP).ShowDialog();
              });
            }
          }
          catch (Exception ex)
          {
            Log.Error("Problem restoring backup file.", ex);

            // restore
            if (!worked && !string.IsNullOrEmpty(temp))
            {
              try
              {
                await UiUtil.InvokeAsync(() =>
                {
                  new MessageWindow("Problem restoring backup. See Log for details.", Resource.RESTORE_FROM_BACKUP).ShowDialog();
                  Application.Current.ShutdownMode = ShutdownMode.OnLastWindowClose;
                });
                Directory.Delete(source, true);
                ZipFile.ExtractToDirectory(temp, source);
              }
              catch (Exception e)
              {
                Log.Error("Problem rolling back restore.", e);
              }
            }
          }
        });
      }
    }

    internal static void ExportFights(string currentFile, List<Fight> fights)
    {
      var saveFileDialog = new SaveFileDialog();
      var fileName = $"eqlog_{ConfigUtil.PlayerName}_{ConfigUtil.ServerName}-selected.txt";
      saveFileDialog.Filter = "Text Files (*.txt)|*.txt";
      saveFileDialog.FileName = string.Join("", fileName.Split(Path.GetInvalidFileNameChars()));

      if (saveFileDialog.ShowDialog() == true)
      {
        var dialog = new MessageWindow($"Saving {fights.Count} Selected Fights.", Resource.FILEMENU_SAVE_FIGHTS,
          MessageWindow.IconType.Save);

        Task.Delay(150).ContinueWith(async _ =>
        {
          var accessError = false;

          try
          {
            using (var os = File.Open(saveFileDialog.FileName, FileMode.Create))
            {
              var range = new TimeRange();
              fights.ForEach(fight =>
              {
                range.Add(new TimeSegment(fight.BeginTime - 15, fight.LastTime));
              });

              if (range.TimeSegments.Count > 0)
              {
                using var f = File.OpenRead(currentFile);
                var s = FileUtil.GetStreamReader(f, range.TimeSegments[0].BeginTime);
                while (!s.EndOfStream)
                {
                  var line = s.ReadLine();
                  if (string.IsNullOrEmpty(line) || line.Length <= AppSettings.ActionIndex)
                  {
                    continue;
                  }

                  var action = line[AppSettings.ActionIndex..];
                  if (ChatLineParser.ParseChatType(action) != null)
                  {
                    continue;
                  }

                  if (TimeRange.TimeCheck(line, range.TimeSegments[0].BeginTime, range, out var exceeds))
                  {
                    os.Write(Encoding.UTF8.GetBytes(line));
                    os.Write(Encoding.UTF8.GetBytes(Environment.NewLine));
                  }

                  if (exceeds)
                  {
                    break;
                  }
                }
              }
            }

            UiUtil.InvokeNow(() => dialog.Close());
          }
          catch (IOException ex)
          {
            Log.Error(ex);
            accessError = true;
          }
          catch (UnauthorizedAccessException uax)
          {
            Log.Error(uax);
          }
          catch (SecurityException se)
          {
            Log.Error(se);
          }
          catch (ArgumentNullException ane)
          {
            Log.Error(ane);
          }
          finally
          {
            await UiUtil.InvokeAsync(() =>
            {
              dialog.Close();

              if (accessError)
              {
                new MessageWindow("Error Saving. Can not access save file.", Resource.FILEMENU_SAVE_FIGHTS, MessageWindow.IconType.Save).ShowDialog();
              }
            });
          }
        });

        dialog.ShowDialog();
      }
    }

    internal static void ExportAsHtml(Dictionary<string, SummaryTable> tables)
    {
      try
      {
        var saveFileDialog = new SaveFileDialog
        {
          Filter = "HTML Files (*.html)|*.html"
        };

        var fileName = DateUtil.GetCurrentDate("MM-dd-yy") + " ";
        if (tables.Values.FirstOrDefault() is { } summary)
        {
          fileName += summary.GetTargetTitle();
        }
        else
        {
          fileName += "No Summaries Exported";
        }

        saveFileDialog.FileName = string.Join("", fileName.Split(Path.GetInvalidFileNameChars()));

        if (saveFileDialog.ShowDialog() == true)
        {
          TextUtils.SaveHtml(saveFileDialog.FileName, tables);
        }
      }
      catch (IOException ex)
      {
        Log.Error(ex);
      }
      catch (UnauthorizedAccessException uax)
      {
        Log.Error(uax);
      }
      catch (SecurityException se)
      {
        Log.Error(se);
      }
      catch (ArgumentNullException ane)
      {
        Log.Error(ane);
      }
    }

    internal static void OpenFileWithDefault(string fileName)
    {
      try
      {
        Process.Start(new ProcessStartInfo { FileName = fileName, UseShellExecute = true });
      }
      catch (Exception ex)
      {
        Log.Error(ex);
      }
    }

    private static void InsertPetMapping(PetMapping mapping)
    {
      var existing = PetPlayersView.FirstOrDefault(item => item.Pet.Equals(mapping.Pet, StringComparison.OrdinalIgnoreCase));
      if (existing != null)
      {
        if (existing.Owner != mapping.Owner)
        {
          PetPlayersView.Remove(existing);
          UiUtil.InsertPetMappingIntoSortedList(mapping, PetPlayersView);
        }
      }
      else
      {
        UiUtil.InsertPetMappingIntoSortedList(mapping, PetPlayersView);
      }
    }
  }
}
