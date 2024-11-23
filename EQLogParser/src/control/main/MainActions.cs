using FontAwesome5;
using log4net;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;
using Syncfusion.SfSkinManager;
using Syncfusion.Themes.MaterialDarkCustom.WPF;
using Syncfusion.Themes.MaterialLight.WPF;
using Syncfusion.UI.Xaml.Grid;
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
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;

namespace EQLogParser
{
  internal static partial class MainActions
  {
    internal static event Action<string> EventsLogLoadingComplete;
    internal static event Action<string> EventsThemeChanged;
    internal static event Action<List<Fight>> EventsFightSelectionChanged;
    internal static event Action<string> EventsChartOpened;
    internal static event Action<PlayerStatsSelectionChangedEventArgs> EventsDamageSelectionChanged;
    internal static event Action<PlayerStatsSelectionChangedEventArgs> EventsHealingSelectionChanged;
    internal static event Action<PlayerStatsSelectionChangedEventArgs> EventsTankingSelectionChanged;
    internal static readonly HttpClient TheHttpClient = new();
    internal static string CurrentTheme;
    internal static string CurrentFontFamily;
    internal static double CurrentFontSize;
    internal static double CurrentDateTimeWidth;
    internal static double CurrentItemWidth;
    internal static double CurrentNpcWidth;
    internal static double CurrentNameWidth;
    internal static double CurrentSpellWidth;
    internal static double CurrentShortWidth;
    internal static double CurrentMediumWidth;

    private const string PetsListTitle = "Verified Pets ({0})";
    private const string PlayerListTitle = "Verified Players ({0})";
    private const string PetOwnersTitle = "Pet Owners ({0})";
    private static readonly ObservableCollection<dynamic> VerifiedPlayersView = [];
    private static readonly ObservableCollection<dynamic> VerifiedPetsView = [];
    private static readonly ObservableCollection<PetMapping> PetPlayersView = [];
    private static readonly SortablePetMappingComparer TheSortablePetMappingComparer = new();
    private static readonly SortableNameComparer TheSortableNameComparer = new();
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static MainWindow _mainWindow;

    internal static void FireChartOpened(string name) => EventsChartOpened?.Invoke(name);
    internal static void FireDamageSelectionChanged(PlayerStatsSelectionChangedEventArgs args) => EventsDamageSelectionChanged?.Invoke(args);
    internal static void FireTankingSelectionChanged(PlayerStatsSelectionChangedEventArgs args) => EventsTankingSelectionChanged?.Invoke(args);
    internal static void FireHealingSelectionChanged(PlayerStatsSelectionChangedEventArgs args) => EventsHealingSelectionChanged?.Invoke(args);
    internal static void FireLoadingEvent(string log) => EventsLogLoadingComplete?.Invoke(log);
    internal static void FireFightSelectionChanged(List<Fight> fights) => EventsFightSelectionChanged?.Invoke(fights);
    internal static DockingManager GetDockSite() => _mainWindow.dockSite;
    internal static Window GetOwner() => _mainWindow;

    internal static void SetMainWindow(MainWindow main)
    {
      _mainWindow = main;

      // load theme and fonts
      CurrentFontFamily = ConfigUtil.GetSetting("ApplicationFontFamily", "Segoe UI");
      CurrentFontSize = ConfigUtil.GetSettingAsDouble("ApplicationFontSize", 12);
      CurrentTheme = ConfigUtil.GetSetting("CurrentTheme", "MaterialDark");

      if (UiElementUtil.GetSystemFontFamilies().FirstOrDefault(font => font.Source == CurrentFontFamily) == null)
      {
        Log.Info(CurrentFontFamily + " Not Found, Trying Default");
        CurrentFontFamily = "Segoe UI";
      }

      Application.Current.Resources["EQChatFontSize"] = 16.0; // changed when chat archive loads
      Application.Current.Resources["EQChatFontFamily"] = new FontFamily("Segoe UI");
      Application.Current.Resources["EQLogFontSize"] = 16.0; // changed when chat archive loads
      Application.Current.Resources["EQLogFontFamily"] = new FontFamily("Segoe UI");
    }

    internal static void AddDocumentWindows(DockingManager dockSite)
    {
      SyncFusionUtil.AddDocument(dockSite, typeof(TriggersTester), "triggerTestWindow", "Trigger Tester");
      SyncFusionUtil.AddDocument(dockSite, typeof(TriggersLogView), "triggerLogWindow", "Trigger Log");
      SyncFusionUtil.AddDocument(dockSite, typeof(QuickShareLogView), "quickShareLogWindow", "Quick Share Log");
      SyncFusionUtil.AddDocument(dockSite, typeof(HealingSummary), "healingSummaryWindow", "Healing Summary");
      SyncFusionUtil.AddDocument(dockSite, typeof(TankingSummary), "tankingSummaryWindow", "Tanking Summary");
      SyncFusionUtil.AddDocument(dockSite, typeof(DamageChart), "damageChartWindow", "DPS Chart");
      SyncFusionUtil.AddDocument(dockSite, typeof(HealingChart), "healingChartWindow", "Healing Chart");
      SyncFusionUtil.AddDocument(dockSite, typeof(TankingChart), "tankingChartWindow", "Tanking Chart");
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

    internal static void AddAndCopyDamageParse(CombinedStats combined, List<PlayerStats> selected)
    {
      UiUtil.InvokeNow(() => _mainWindow?.AddAndCopyDamageParse(combined, selected));
    }

    internal static void AddAndCopyTankParse(CombinedStats combined, List<PlayerStats> selected)
    {
      UiUtil.InvokeNow(() => _mainWindow?.AddAndCopyTankParse(combined, selected));
    }

    internal static void CopyToEqClick(string label)
    {
      UiUtil.InvokeNow(() => _mainWindow?.CopyToEqClick(label));
    }

    internal static void ShowTriggersEnabled(bool show)
    {
      UiUtil.InvokeNow(() => _mainWindow?.ShowTriggersEnabled(show));
    }

    internal static void CloseDamageOverlay(bool reopen)
    {
      UiUtil.InvokeNow(() =>
      {
        _mainWindow?.CloseDamageOverlay();
        if (reopen)
        {
          _mainWindow?.OpenDamageOverlayIfEnabled(false, true);
        }
      });
    }

    internal static TimeRange GetAllRanges()
    {
      TimeRange result = null;
      UiUtil.InvokeNow(() =>
      {
        result = _mainWindow?.GetFightTable()?.GetAllRanges();
      });

      return result ?? new TimeRange();
    }

    internal static List<Fight> GetFights()
    {
      List<Fight> result = null;
      UiUtil.InvokeNow(() =>
      {
        result = _mainWindow?.GetFightTable()?.GetFights();
      });

      return result;
    }

    internal static List<Fight> GetSelectedFights()
    {
      List<Fight> result = null;
      UiUtil.InvokeNow(() =>
      {
        result = _mainWindow?.GetFightTable()?.GetSelectedFights();
      });

      return result;
    }

    internal static void CheckVersion(TextBlock errorText)
    {
      var version = Application.ResourceAssembly.GetName().Version;
      Task.Delay(3000).ContinueWith(_ =>
      {
        try
        {
          var request = TheHttpClient.GetStringAsync("https://github.com/kauffman12/EQLogParser/blob/master/README.md");
          request.Wait();

          var matches = InstallerName().Match(request.Result);
          if (version != null && matches.Success && matches.Groups.Count == 5 && int.TryParse(matches.Groups[2].Value, out var v1) &&
              int.TryParse(matches.Groups[3].Value, out var v2) && int.TryParse(matches.Groups[4].Value, out var v3)
              && (v1 > version.Major || (v1 == version.Major && v2 > version.Minor) ||
                  (v1 == version.Major && v2 == version.Minor && v3 > version.Build)))
          {
            async void Action()
            {
              var msg = new MessageWindow($"Version {matches.Groups[1].Value} is Available. Download and Install?", Resource.CHECK_VERSION,
                MessageWindow.IconType.Question, "Yes");
              msg.ShowDialog();

              if (msg.IsYes1Clicked)
              {
                var url = "https://github.com/kauffman12/EQLogParser/raw/master/Release/EQLogParser-install-" + matches.Groups[1].Value + ".exe";

                try
                {
                  await using var download = await TheHttpClient.GetStreamAsync(url);
                  var path = NativeMethods.GetDownloadsFolderPath();
                  if (!Directory.Exists(path))
                  {
                    new MessageWindow("Unable to Access Downloads Folder. Can Not Download Update.", Resource.CHECK_VERSION).ShowDialog();
                    return;
                  }

                  path += "\\AutoUpdateEQLogParser";
                  if (!Directory.Exists(path))
                  {
                    Directory.CreateDirectory(path);
                  }

                  var fullPath = $"{path}\\EQLogParser-install-{matches.Groups[1].Value}.exe";
                  await using (var fs = new FileStream(fullPath, FileMode.Create))
                  {
                    await download.CopyToAsync(fs);
                  }

                  if (File.Exists(fullPath))
                  {
                    var process = Process.Start(fullPath);
                    if (process is { HasExited: false })
                    {
                      await Task.Delay(1000).ContinueWith(_ => { UiUtil.InvokeAsync(() => _mainWindow?.Close()); });
                    }
                  }
                }
                catch (Exception ex2)
                {
                  new MessageWindow("Problem Installing Updates. Check Error Log for Details.", Resource.CHECK_VERSION).ShowDialog();
                  Log.Error("Error Installing Updates", ex2);
                }
              }
            }

            UiUtil.InvokeAsync(Action);
          }
        }
        catch (Exception ex)
        {
          Log.Error($"Error Checking for Updates: {ex.Message}");
          UiUtil.InvokeAsync(() => errorText.Text = "Update Check Failed. Firewall?");
        }
      });
    }

    internal static void Cleanup()
    {
      try
      {
        var path = Environment.ExpandEnvironmentVariables("%userprofile%\\Downloads");
        if (!Directory.Exists(path))
        {
          return;
        }

        path += "\\AutoUpdateEQLogParser";
        if (Directory.Exists(path))
        {
          foreach (var file in Directory.GetFiles(path))
          {
            var test = Path.GetFileName(file).Trim();
            if (test.StartsWith("EQLogParser") && test.EndsWith(".msi"))
            {
              File.Delete(file);
            }
          }
        }
      }
      catch (Exception e)
      {
        Log.Error(e);
      }
    }

    internal static void CreateFontFamiliesMenuItems(MenuItem parent, RoutedEventHandler callback)
    {
      foreach (var family in UiElementUtil.GetCommonFontFamilyNames())
      {
        parent.Items.Add(CreateMenuItem(family, callback, EFontAwesomeIcon.Solid_Check));
      }

      return;

      static MenuItem CreateMenuItem(string name, RoutedEventHandler handler, EFontAwesomeIcon awesome)
      {
        var imageAwesome = new ImageAwesome
        {
          Icon = awesome,
          Style = (Style)Application.Current.Resources["EQIconStyle"],
          Visibility = (name == CurrentFontFamily) ? Visibility.Visible : Visibility.Hidden
        };

        var menuItem = new MenuItem { Header = name };
        menuItem.Click += handler;
        menuItem.Icon = imageAwesome;
        return menuItem;
      }
    }

    internal static void CreateFontSizesMenuItems(MenuItem parent, RoutedEventHandler callback)
    {
      parent.Items.Add(CreateMenuItem(10, callback, EFontAwesomeIcon.Solid_Check));
      parent.Items.Add(CreateMenuItem(11, callback, EFontAwesomeIcon.Solid_Check));
      parent.Items.Add(CreateMenuItem(12, callback, EFontAwesomeIcon.Solid_Check));
      parent.Items.Add(CreateMenuItem(13, callback, EFontAwesomeIcon.Solid_Check));
      parent.Items.Add(CreateMenuItem(14, callback, EFontAwesomeIcon.Solid_Check));
      parent.Items.Add(CreateMenuItem(15, callback, EFontAwesomeIcon.Solid_Check));
      parent.Items.Add(CreateMenuItem(16, callback, EFontAwesomeIcon.Solid_Check));
      parent.Items.Add(CreateMenuItem(17, callback, EFontAwesomeIcon.Solid_Check));
      parent.Items.Add(CreateMenuItem(18, callback, EFontAwesomeIcon.Solid_Check));
      return;

      static MenuItem CreateMenuItem(double size, RoutedEventHandler handler, EFontAwesomeIcon awesome)
      {
        var imageAwesome = new ImageAwesome
        {
          Icon = awesome,
          Style = (Style)Application.Current.Resources["EQIconStyle"],
          Visibility = size.Equals(CurrentFontSize) ? Visibility.Visible : Visibility.Hidden
        };

        var menuItem = new MenuItem { Header = size + "pt", Tag = size };
        menuItem.Click += handler;
        menuItem.Icon = imageAwesome;
        return menuItem;
      }
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
      return;

      static MenuItem CreateMenuItem(string name, string value, RoutedEventHandler handler, EFontAwesomeIcon awesome)
      {
        var imageAwesome = new ImageAwesome { Icon = awesome, Style = (Style)Application.Current.Resources["EQIconStyle"] };
        var menuItem = new MenuItem { Header = name, Tag = value };
        menuItem.Click += handler;
        menuItem.Icon = imageAwesome;
        return menuItem;
      }
    }

    internal static void UpdateCheckedMenuItem(MenuItem selectedItem, ItemCollection items)
    {
      foreach (var item in items)
      {
        if (item is MenuItem { Icon: ImageAwesome image } menuItem)
        {
          image.Visibility = (menuItem == selectedItem) ? Visibility.Visible : Visibility.Hidden;
        }
      }
    }

    internal static void SetCurrentTheme(DependencyObject obj)
    {
      if (obj != null)
      {
        switch (CurrentTheme)
        {
          case "MaterialLight":
            SfSkinManager.SetTheme(obj, new Theme("MaterialLight"));
            break;
          default:
            SfSkinManager.SetTheme(obj, new Theme("MaterialDarkCustom;MaterialDark"));
            break;
        }
      }
    }

    internal static void InitThemes(MainWindow main)
    {
      // constant for all themes
      Application.Current.Resources["PreviewBackgroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#BB000000")! };
      Application.Current.Resources["DamageOverlayBackgroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#99000000")! };
      Application.Current.Resources["DamageOverlayDamageBrush"] = new SolidColorBrush { Color = Colors.White };
      Application.Current.Resources["DamageOverlayProgressBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FF1D397E")! };

      // init of settings needs to block
      UiUtil.InvokeNow(() =>
      {
        SetThemeFontSizes();
        RegisterThemeSettings();
        ChangeTheme(main);
      }, DispatcherPriority.Send);

      UiUtil.InvokeAsync(() =>
      {
        EventsThemeChanged?.Invoke(CurrentTheme);
      }, DispatcherPriority.DataBind);
    }

    internal static void ChangeTheme(string theme)
    {
      if (CurrentTheme != theme)
      {
        CurrentTheme = theme;
        UiUtil.InvokeAsync(() =>
        {
          RegisterThemeSettings();
          ChangeTheme(_mainWindow);
          // set after change succeeds
          ConfigUtil.SetSetting("CurrentTheme", CurrentTheme);
        }, DispatcherPriority.DataBind);

        UiUtil.InvokeAsync(() =>
        {
          EventsThemeChanged?.Invoke(CurrentTheme);
        }, DispatcherPriority.Background);
      }
    }

    internal static void ChangeThemeFontFamily(string family)
    {
      if (CurrentFontFamily != family)
      {
        CurrentFontFamily = family;
        UiUtil.InvokeAsync(() =>
        {
          RegisterThemeSettings();
          ChangeTheme(_mainWindow);
          // set after change succeeds
          ConfigUtil.SetSetting("ApplicationFontFamily", CurrentFontFamily);
          EventsThemeChanged?.Invoke(CurrentTheme);
        }, DispatcherPriority.DataBind);

        UiUtil.InvokeAsync(() =>
        {
          EventsThemeChanged?.Invoke(CurrentTheme);
        }, DispatcherPriority.Background);
      }
    }

    internal static void ChangeThemeFontSizes(double size)
    {
      if (!CurrentFontSize.Equals(size))
      {
        CurrentFontSize = size;
        UiUtil.InvokeAsync(() =>
        {
          SetThemeFontSizes();
          RegisterThemeSettings();
          ChangeTheme(_mainWindow);
          // set after change succeeds
          ConfigUtil.SetSetting("ApplicationFontSize", CurrentFontSize);
          EventsThemeChanged?.Invoke(CurrentTheme);
        }, DispatcherPriority.DataBind);

        UiUtil.InvokeAsync(() =>
        {
          EventsThemeChanged?.Invoke(CurrentTheme);
        }, DispatcherPriority.Background);
      }
    }

    // should already run on the UI thread
    internal static void Clear(ContentControl petsWindow, ContentControl playersWindow, ContentControl petMappingWindow)
    {
      PetPlayersView.Clear();
      VerifiedPetsView.Clear();
      VerifiedPlayersView.Clear();

      var entry = new ExpandoObject() as dynamic;
      entry.Name = Labels.Unassigned;
      VerifiedPlayersView.Add(entry);
      DockingManager.SetHeader(petsWindow, string.Format(PetsListTitle, VerifiedPetsView.Count));
      DockingManager.SetHeader(playersWindow, string.Format(PlayerListTitle, VerifiedPlayersView.Count));
      DockingManager.SetHeader(petMappingWindow, string.Format(PetOwnersTitle, PetPlayersView.Count));
    }

    internal static dynamic InsertNameIntoSortedList(string name, ObservableCollection<object> collection)
    {
      var entry = new ExpandoObject() as dynamic;
      entry.Name = name;

      var index = collection.ToList().BinarySearch(entry, TheSortableNameComparer);
      if (index < 0)
      {
        collection.Insert(~index, entry);
      }
      else
      {
        entry = collection[index];
      }

      return entry;
    }

    // should already run on the UI thread
    internal static void InitPetOwners(MainWindow main, SfDataGrid petMappingGrid, GridComboBoxColumn ownerList, ContentControl petMappingWindow)
    {
      // pet -> players
      petMappingGrid.ItemsSource = PetPlayersView;
      ownerList.ItemsSource = VerifiedPlayersView;
      PlayerManager.Instance.EventsNewPetMapping += (_, mapping) =>
      {
        // ignore swarm pets
        if (mapping.Pet?.EndsWith("`s pet") == true)
        {
          return;
        }

        UiUtil.InvokeAsync(() =>
        {
          var existing = PetPlayersView.FirstOrDefault(item => item.Pet.Equals(mapping.Pet, StringComparison.OrdinalIgnoreCase));
          if (existing != null)
          {
            if (existing.Owner != mapping.Owner)
            {
              PetPlayersView.Remove(existing);
              InsertPetMappingIntoSortedList(mapping, PetPlayersView);
            }
          }
          else
          {
            InsertPetMappingIntoSortedList(mapping, PetPlayersView);
          }

          DockingManager.SetHeader(petMappingWindow, string.Format(PetOwnersTitle, PetPlayersView.Count));
        });

        main.CheckComputeStats();
      };
    }

    // should already run on the UI thread
    internal static void InitVerifiedPlayers(MainWindow main, SfDataGrid playersGrid, GridComboBoxColumn classList,
      ContentControl playersWindow, ContentControl petMappingWindow)
    {
      // verified player table
      playersGrid.ItemsSource = VerifiedPlayersView;
      classList.ItemsSource = PlayerManager.Instance.GetClassList(true);
      PlayerManager.Instance.EventsNewVerifiedPlayer += (_, name) =>
      {
        UiUtil.InvokeAsync(() =>
        {
          var entry = InsertNameIntoSortedList(name, VerifiedPlayersView);
          entry.PlayerClass = PlayerManager.Instance.GetPlayerClass(name);
          DockingManager.SetHeader(playersWindow, string.Format(PlayerListTitle, VerifiedPlayersView.Count));
        });
      };

      PlayerManager.Instance.EventsUpdatePlayerClass += (name, playerClass) =>
      {
        UiUtil.InvokeAsync(() =>
        {
          var entry = new ExpandoObject() as dynamic;
          entry.Name = name;
          int index = VerifiedPlayersView.ToList().BinarySearch(entry, TheSortableNameComparer);
          if (index >= 0)
          {
            VerifiedPlayersView[index].PlayerClass = playerClass;
          }
        });
      };

      PlayerManager.Instance.EventsRemoveVerifiedPlayer += (_, name) =>
      {
        UiUtil.InvokeAsync(() =>
        {
          var found = VerifiedPlayersView.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
          if (found != null)
          {
            VerifiedPlayersView.Remove(found);
            DockingManager.SetHeader(playersWindow, string.Format(PlayerListTitle, VerifiedPlayersView.Count));

            var existing = PetPlayersView.FirstOrDefault(item => item.Owner.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
              PetPlayersView.Remove(existing);
              DockingManager.SetHeader(petMappingWindow, string.Format(PetOwnersTitle, PetPlayersView.Count));
            }

            main.CheckComputeStats();
          }
        });
      };
    }

    internal static void InitVerifiedPets(MainWindow main, SfDataGrid petsGrid, ContentControl petsWindow, ContentControl petMappingWindow)
    {
      // verified pets table
      petsGrid.ItemsSource = VerifiedPetsView;
      PlayerManager.Instance.EventsNewVerifiedPet += (_, name) => main.Dispatcher.InvokeAsync(() =>
      {
        UiUtil.InvokeAsync(() =>
        {
          InsertNameIntoSortedList(name, VerifiedPetsView);
          DockingManager.SetHeader(petsWindow, string.Format(PetsListTitle, VerifiedPetsView.Count));
        });
      });

      PlayerManager.Instance.EventsRemoveVerifiedPet += (_, name) =>
      {
        UiUtil.InvokeAsync(() =>
        {
          var found = VerifiedPetsView.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
          if (found != null)
          {
            VerifiedPetsView.Remove(found);
            DockingManager.SetHeader(petsWindow, string.Format(PetsListTitle, VerifiedPetsView.Count));

            var existing = PetPlayersView.FirstOrDefault(item => item.Pet.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
              PetPlayersView.Remove(existing);
              DockingManager.SetHeader(petMappingWindow, string.Format(PetOwnersTitle, PetPlayersView.Count));
            }

            main.CheckComputeStats();
          }
        });
      };
    }

    internal static void UpdateDamageOption(UIElement icon, bool enabled, string option)
    {
      ConfigUtil.SetSetting(option, enabled);
      icon.Visibility = enabled ? Visibility.Visible : Visibility.Hidden;
      var options = new GenerateStatsOptions();
      Task.Run(() => DamageStatsManager.Instance.RebuildTotalStats(options));
    }

    internal static void CreateBackup()
    {
      var saveFileDialog = new SaveFileDialog();
      var dateTime = DateTime.Now.ToString("yyyyMMdd-ssfff");
      var version = Application.ResourceAssembly.GetName().Version?.ToString();
      version = string.IsNullOrEmpty(version) ? "unknown" : version[..^2];
      var fileName = $"EQLogParser_backup_{version}_{dateTime}.zip";
      saveFileDialog.Filter = "EQLogParser Backup Files (*.zip)|*.zip";
      saveFileDialog.FileName = string.Join("", fileName.Split(Path.GetInvalidFileNameChars()));

      if (saveFileDialog.ShowDialog() == true)
      {
        var dialog = new MessageWindow($"Creating EQLogParser Backup", Resource.CREATE_BACKUP, MessageWindow.IconType.Save);
        ChatManager.Instance.Stop();

        Task.Delay(150).ContinueWith(async _ =>
        {
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
            await TriggerStateManager.Instance.CreateCheckpoint();
            ZipFile.CreateFromDirectory(source, backupFile, CompressionLevel.Optimal, false);
          }
          catch (Exception ex)
          {
            accessError = true;
            Log.Error("Problem creating backup file.", ex);
          }
          finally
          {
            ChatManager.Instance.Init();
            UiUtil.InvokeAsync(() =>
            {
              dialog.Close();

              if (accessError)
              {
                new MessageWindow("Error Creating Backup. See log file for details.", Resource.CREATE_BACKUP, MessageWindow.IconType.Save).ShowDialog();
              }
            });
          }
        });

        dialog.ShowDialog();
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
              _mainWindow.Dispatcher.Invoke(() =>
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
              _mainWindow.Dispatcher.Invoke(() =>
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

              _mainWindow.Dispatcher.Invoke(() =>
              {
                Application.Current.Shutdown();
              });
            }
            else
            {
              UiUtil.InvokeAsync(() =>
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
                UiUtil.InvokeAsync(() =>
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

        Task.Delay(150).ContinueWith(_ =>
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
                  if (string.IsNullOrEmpty(line) || line.Length <= MainWindow.ActionIndex)
                  {
                    continue;
                  }

                  var action = line[MainWindow.ActionIndex..];
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
            UiUtil.InvokeAsync(() =>
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

    private static void SetThemeFontSizes()
    {
      CurrentNameWidth = (10.0 * CurrentFontSize) + 25;
      CurrentNpcWidth = (10.0 * CurrentFontSize) + 50;
      CurrentDateTimeWidth = (10.0 * CurrentFontSize) - 10;
      CurrentSpellWidth = (10.0 * CurrentFontSize) + 90;
      CurrentItemWidth = (15.0 * CurrentFontSize) + 115;
      CurrentShortWidth = 5.0 * CurrentFontSize;
      CurrentMediumWidth = 6.5 * CurrentFontSize;
      Application.Current.Resources["EQGridTitleHeight"] = new GridLength(18 + CurrentFontSize);
      Application.Current.Resources["EQGridFooterHeight"] = new GridLength(10 + CurrentFontSize);
      Application.Current.Resources["EQFightGridTitleHeight"] = new GridLength(21 + CurrentFontSize);
      Application.Current.Resources["EQTriggerCharacterList"] = new GridLength(180 + (CurrentFontSize * 4));
      Application.Current.Resources["EQAlertIconSize"] = CurrentFontSize + 18;
      Application.Current.Resources["EQTitleSize"] = CurrentFontSize + 2;
      Application.Current.Resources["EQContentSize"] = CurrentFontSize;
      Application.Current.Resources["EQDescriptionSize"] = CurrentFontSize - 1;
      Application.Current.Resources["EQButtonHeight"] = CurrentFontSize + 12 + (CurrentFontSize % 2 == 0 ? 1 : 0);
      Application.Current.Resources["EQTabHeaderHeight"] = CurrentFontSize + 12;
      Application.Current.Resources["EQTableHeaderRowHeight"] = CurrentFontSize + 14;
      Application.Current.Resources["EQTableRowHeight"] = CurrentFontSize + 12;
      Application.Current.Resources["EQIconButtonHeight"] = CurrentFontSize + 6;
      Application.Current.Resources["EQTableRowHeaderWidth"] = 32 + ((CurrentFontSize - 10) * 2);
      Application.Current.Resources["EQTableShortRowHeaderWidth"] = 20 + ((CurrentFontSize - 10) * 2);
      Application.Current.Resources["EQTableExtendedRowHeaderWidth"] = 38 + ((CurrentFontSize - 10) * 2);
      Application.Current.Resources["EQCheckBoxScale"] = 0.9 + ((CurrentFontSize - 10) * 0.06);
      SyncFusionUtil.SetDesiredWidth("EQFightWindowWidth", 220 + (14.0 * CurrentFontSize), _mainWindow.npcWindow);
      SyncFusionUtil.SetDesiredWidth("EQPetMappingWindowWidth", 220 + (10.0 * CurrentFontSize), _mainWindow.petMappingWindow);
      SyncFusionUtil.SetDesiredWidth("EQPlayersWindowWidth", 180 + (10.0 * CurrentFontSize), _mainWindow.verifiedPlayersWindow);
      SyncFusionUtil.SetDesiredHeight("EQParseWindowHeight", 10.0 * (CurrentFontSize + 2), _mainWindow.playerParseTextWindow);
    }

    private static void RegisterThemeSettings()
    {
      if (CurrentTheme == "MaterialLight")
      {
        var themeSettings = new MaterialLightThemeSettings
        {
          PrimaryBackground = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FF343434")! },
          FontFamily = new FontFamily(CurrentFontFamily),
          BodyAltFontSize = CurrentFontSize - 2,
          BodyFontSize = CurrentFontSize,
          HeaderFontSize = CurrentFontSize + 4,
          SubHeaderFontSize = CurrentFontSize + 2,
          SubTitleFontSize = CurrentFontSize,
          TitleFontSize = CurrentFontSize + 2
        };

        SfSkinManager.RegisterThemeSettings("MaterialLight", themeSettings);
      }
      else
      {
        var themeSettings = new MaterialDarkCustomThemeSettings
        {
          PrimaryBackground = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FFE1E1E1")! },
          FontFamily = new FontFamily(CurrentFontFamily),
          BodyAltFontSize = CurrentFontSize - 2,
          BodyFontSize = CurrentFontSize,
          HeaderFontSize = CurrentFontSize + 4,
          SubHeaderFontSize = CurrentFontSize + 2,
          SubTitleFontSize = CurrentFontSize,
          TitleFontSize = CurrentFontSize + 2
        };

        SfSkinManager.RegisterThemeSettings("MaterialDarkCustom", themeSettings);
      }

      SetThemeResources(_mainWindow);
    }

    private static void SetThemeResources(MainWindow main)
    {
      if (CurrentTheme == "MaterialLight")
      {
        Application.Current.Resources["EQGoodForegroundBrush"] = new SolidColorBrush { Color = Colors.DarkGreen };
        Application.Current.Resources["EQMenuIconBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FF3d7baf")! };
        Application.Current.Resources["EQSearchBackgroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FFa7baab")! };
        Application.Current.Resources["EQWarnBackgroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FFeaa6ac")! };
        Application.Current.Resources["EQWarnForegroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FF946e00")! };
        Application.Current.Resources["EQStopForegroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FFcc434d")! };
        Application.Current.Resources["EQDisabledBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#88000000")! };
        LoadDictionary("/Syncfusion.Themes.MaterialLight.WPF;component/MSControl/CheckBox.xaml");
        LoadDictionary("/Syncfusion.Themes.MaterialLight.WPF;component/SfDataGrid/SfDataGrid.xaml");
        LoadDictionary("/Syncfusion.Themes.MaterialLight.WPF;component/Common/Brushes.xaml");
        LoadDictionary("/Syncfusion.Themes.MaterialLight.WPF;component/DockingManager/DockingManager.xaml");
        main.BorderBrush = Application.Current.Resources["ContentBackgroundAlt2"] as SolidColorBrush;

        if (!string.IsNullOrEmpty(main.statusText?.Text))
        {
          main.statusText.Foreground = Application.Current.Resources["EQGoodForegroundBrush"] as SolidColorBrush;
        }
      }
      else
      {
        Application.Current.Resources["EQGoodForegroundBrush"] = new SolidColorBrush { Color = Colors.LightGreen };
        Application.Current.Resources["EQMenuIconBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FF4F9FE2")! };
        Application.Current.Resources["EQSearchBackgroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FF314435")! };
        Application.Current.Resources["EQWarnBackgroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FF96410d")! };
        Application.Current.Resources["EQWarnForegroundBrush"] = new SolidColorBrush { Color = Colors.Orange };
        Application.Current.Resources["EQStopForegroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FFcc434d")! };
        Application.Current.Resources["EQDisabledBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#88FFFFFF")! };
        LoadDictionary("/Syncfusion.Themes.MaterialDarkCustom.WPF;component/MSControl/CheckBox.xaml");
        LoadDictionary("/Syncfusion.Themes.MaterialDarkCustom.WPF;component/SfDataGrid/SfDataGrid.xaml");
        LoadDictionary("/Syncfusion.Themes.MaterialDarkCustom.WPF;component/Common/Brushes.xaml");
        LoadDictionary("/Syncfusion.Themes.MaterialDarkCustom.WPF;component/DockingManager/DockingManager.xaml");
        main.BorderBrush = Application.Current.Resources["ContentBackgroundAlt2"] as SolidColorBrush;

        if (!string.IsNullOrEmpty(main.statusText?.Text))
        {
          main.statusText.Foreground = Application.Current.Resources["EQGoodForegroundBrush"] as SolidColorBrush;
        }
      }

      // after everything loads fix the tab height
      if (Application.Current.Resources["EQTabHeaderHeight"] is double height && GetDockSite() is var dockSite)
      {
        var tabStyle = new Style
        {
          TargetType = typeof(TabItemExt),
          BasedOn = (Style)Application.Current.FindResource(typeof(TabItemExt))
        };
        tabStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, height));
        dockSite.SetValue(DockingManager.DocumentTabItemStyleProperty, tabStyle);

        var docStyle = new Style
        {
          TargetType = typeof(DockHeaderPresenter),
          BasedOn = (Style)Application.Current.FindResource(typeof(DockHeaderPresenter))
        };
        docStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, height));
        dockSite.SetValue(DockingManager.DockHeaderStyleProperty, docStyle);

        dockSite.SidePanelSize = height;
      }
    }

    private static void ChangeTheme(MainWindow main)
    {
      var theme = CurrentTheme == "MaterialLight" ? new Theme("MaterialLight") : new Theme("MaterialDarkCustom");
      SfSkinManager.SetTheme(main, theme);

      // workaround for DM
      if (CurrentTheme == "MaterialLight")
      {
        Application.Current.Resources["EQDamageMeterCheckBoxForeground"] =
          Application.Current.Resources["ContentBackground"];
      }
      else
      {
        Application.Current.Resources["EQDamageMeterCheckBoxForeground"] =
          Application.Current.Resources["ContentForeground"];
      }
    }

    private static void LoadDictionary(string path)
    {
      var dict = new ResourceDictionary
      {
        Source = new Uri(path, UriKind.RelativeOrAbsolute)
      };

      foreach (var key in dict.Keys)
      {
        Application.Current.Resources[key] = dict[key];
      }
    }

    private static void InsertPetMappingIntoSortedList(PetMapping mapping, ObservableCollection<PetMapping> collection)
    {
      var index = collection.ToList().BinarySearch(mapping, TheSortablePetMappingComparer);
      if (index < 0)
      {
        collection.Insert(~index, mapping);
      }
      else
      {
        collection.Insert(index, mapping);
      }
    }

    private class SortablePetMappingComparer : IComparer<PetMapping>
    {
      public int Compare(PetMapping x, PetMapping y)
      {
        return string.CompareOrdinal(x?.Owner, y?.Owner);
      }
    }

    private class SortableNameComparer : IComparer<object>
    {
      public int Compare(object x, object y)
      {
        return string.CompareOrdinal(((dynamic)x)?.Name, ((dynamic)y)?.Name);
      }
    }

    [GeneratedRegex(@"EQLogParser-install-((\d)\.(\d)\.(\d?\d?\d))\.exe")]
    private static partial Regex InstallerName();
  }
}
