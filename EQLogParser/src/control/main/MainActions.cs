using FontAwesome5;
using log4net;
using Microsoft.Win32;
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

namespace EQLogParser
{
  static class MainActions
  {
    internal static event Action<string> EventsLogLoadingComplete;
    internal static event Action<string> EventsThemeChanged;
    internal static readonly HttpClient THE_HTTP_CLIENT = new();
    private const string PetsListTitle = "Verified Pets ({0})";
    private const string PlayerListTitle = "Verified Players ({0})";
    private static readonly ObservableCollection<dynamic> VerifiedPlayersView = new();
    private static readonly ObservableCollection<dynamic> VerifiedPetsView = new();
    private static readonly ObservableCollection<PetMapping> PetPlayersView = new();
    private static readonly SortablePetMappingComparer TheSortablePetMappingComparer = new();
    private static readonly SortableNameComparer TheSortableNameComparer = new();
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    internal static void FireLoadingEvent(string log) => EventsLogLoadingComplete?.Invoke(log);
    internal static void FireThemeChanged(string theme) => EventsThemeChanged?.Invoke(theme);

    internal static void CheckVersion(TextBlock errorText)
    {
      var version = Application.ResourceAssembly.GetName().Version;
      Task.Delay(2000).ContinueWith(_ =>
      {
        try
        {
          var request = THE_HTTP_CLIENT.GetStringAsync("https://github.com/kauffman12/EQLogParser/blob/master/README.md");
          request.Wait();

          var matches = new Regex(@"EQLogParser-((\d)\.(\d)\.(\d?\d?\d))\.(msi|exe)").Match(request.Result);
          if (version != null && matches.Success && matches.Groups.Count == 6 && int.TryParse(matches.Groups[2].Value, out var v1) &&
              int.TryParse(matches.Groups[3].Value, out var v2) && int.TryParse(matches.Groups[4].Value, out var v3)
              && (v1 > version.Major || (v1 == version.Major && v2 > version.Minor) ||
                  (v1 == version.Major && v2 == version.Minor && v3 > version.Build)))
          {
            UIUtil.InvokeAsync(async () =>
            {
              var msg = new MessageWindow($"Version {matches.Groups[1].Value} is Available. Download and Install?",
                Resource.CHECK_VERSION, MessageWindow.IconType.Question, "Yes");
              msg.ShowDialog();

              if (msg.IsYes1Clicked)
              {
                var url = "https://github.com/kauffman12/EQLogParser/raw/master/Release/EQLogParser-" + matches.Groups[1].Value + "." + matches.Groups[5].Value;

                try
                {
                  await using var download = await THE_HTTP_CLIENT.GetStreamAsync(url);

                  var path = Environment.ExpandEnvironmentVariables("%userprofile%\\Downloads");
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

                  var fullPath = $"{path}\\EQLogParser-{matches.Groups[1].Value}.msi";
                  await using (var fs = new FileStream(fullPath, FileMode.Create))
                  {
                    await download.CopyToAsync(fs);
                  }

                  if (File.Exists(fullPath))
                  {
                    var process = Process.Start("msiexec", "/i \"" + fullPath + "\"");
                    if (process is { HasExited: false })
                    {
                      await Task.Delay(1000).ContinueWith(_ => { UIUtil.InvokeAsync(() => Application.Current.MainWindow?.Close()); });
                    }
                  }
                }
                catch (Exception ex2)
                {
                  new MessageWindow("Problem Install Updates. Check Error Log for Details.", Resource.CHECK_VERSION).ShowDialog();
                  Log.Error("Error Installing Updates", ex2);
                }
              }
            });
          }
        }
        catch (Exception ex)
        {
          Log.Error("Error Checking for Updates", ex);
          UIUtil.InvokeAsync(() => errorText.Text = "Update Check Failed. Firewall?");
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

    internal static void CreateFontFamiliesMenuItems(MenuItem parent, RoutedEventHandler callback, string currentFamily)
    {
      foreach (var family in UIElementUtil.GetCommonFontFamilyNames())
      {
        parent.Items.Add(CreateMenuItem(family, callback, EFontAwesomeIcon.Solid_Check));
      }

      return;

      MenuItem CreateMenuItem(string name, RoutedEventHandler handler, EFontAwesomeIcon awesome)
      {
        var imageAwesome = new ImageAwesome
        {
          Icon = awesome,
          Style = (Style)Application.Current.Resources["EQIconStyle"],
          Visibility = (name == currentFamily) ? Visibility.Visible : Visibility.Hidden
        };

        var menuItem = new MenuItem { Header = name };
        menuItem.Click += handler;
        menuItem.Icon = imageAwesome;
        return menuItem;
      }
    }

    internal static void CreateFontSizesMenuItems(MenuItem parent, RoutedEventHandler callback, double currentSize)
    {
      parent.Items.Add(CreateMenuItem(10, callback, EFontAwesomeIcon.Solid_Check));
      parent.Items.Add(CreateMenuItem(11, callback, EFontAwesomeIcon.Solid_Check));
      parent.Items.Add(CreateMenuItem(12, callback, EFontAwesomeIcon.Solid_Check));
      parent.Items.Add(CreateMenuItem(13, callback, EFontAwesomeIcon.Solid_Check));
      parent.Items.Add(CreateMenuItem(14, callback, EFontAwesomeIcon.Solid_Check));
      return;

      MenuItem CreateMenuItem(double size, RoutedEventHandler handler, EFontAwesomeIcon awesome)
      {
        var imageAwesome = new ImageAwesome
        {
          Icon = awesome,
          Style = (Style)Application.Current.Resources["EQIconStyle"],
          Visibility = UIUtil.DoubleEquals(size, currentSize) ? Visibility.Visible : Visibility.Hidden
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

    internal static void SetTheme(Window window, string theme)
    {
      if (window != null)
      {
        switch (theme)
        {
          case "MaterialLight":
            SfSkinManager.SetTheme(window, new Theme("MaterialLight"));
            break;
          default:
            SfSkinManager.SetTheme(window, new Theme("MaterialDarkCustom;MaterialDark"));
            break;
        }
      }
    }

    internal static void LoadTheme(MainWindow main, string theme)
    {
      Application.Current.Resources["EQTitleSize"] = MainWindow.CurrentFontSize + 2;
      Application.Current.Resources["EQContentSize"] = MainWindow.CurrentFontSize;
      Application.Current.Resources["EQButtonHeight"] = MainWindow.CurrentFontSize + 12 + (MainWindow.CurrentFontSize % 2 == 0 ? 1 : 0);
      Application.Current.Resources["EQTableHeaderRowHeight"] = MainWindow.CurrentFontSize + 14;
      Application.Current.Resources["EQTableRowHeight"] = MainWindow.CurrentFontSize + 12;

      if (theme == "MaterialLight")
      {
        var themeSettings = new MaterialLightThemeSettings
        {
          PrimaryBackground = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FF343434")! },
          FontFamily = new FontFamily(MainWindow.CurrentFontFamily),
          BodyAltFontSize = MainWindow.CurrentFontSize - 2,
          BodyFontSize = MainWindow.CurrentFontSize,
          HeaderFontSize = MainWindow.CurrentFontSize + 4,
          SubHeaderFontSize = MainWindow.CurrentFontSize + 2,
          SubTitleFontSize = MainWindow.CurrentFontSize,
          TitleFontSize = MainWindow.CurrentFontSize + 2
        };
        SfSkinManager.RegisterThemeSettings("MaterialLight", themeSettings);
        Application.Current.Resources["EQGoodForegroundBrush"] = new SolidColorBrush { Color = Colors.DarkGreen };
        Application.Current.Resources["EQMenuIconBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FF3d7baf")! };
        Application.Current.Resources["EQSearchBackgroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FFa7baab")! };
        Application.Current.Resources["EQWarnBackgroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FFeaa6ac")! };
        Application.Current.Resources["EQWarnForegroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FFb02021")! };
        Application.Current.Resources["EQStopForegroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FFcc434d")! };
        Application.Current.Resources["EQDisabledBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#88000000")! };
        SfSkinManager.SetTheme(main, new Theme("MaterialLight"));
        LoadDictionary("/Syncfusion.Themes.MaterialLight.WPF;component/MSControl/CheckBox.xaml");
        LoadDictionary("/Syncfusion.Themes.MaterialLight.WPF;component/SfDataGrid/SfDataGrid.xaml");
        LoadDictionary("/Syncfusion.Themes.MaterialLight.WPF;component/Common/Brushes.xaml");
        main.BorderBrush = Application.Current.Resources["ContentBackgroundAlt2"] as SolidColorBrush;

        if (!string.IsNullOrEmpty(main.statusText?.Text))
        {
          main.statusText.Foreground = Application.Current.Resources["EQGoodForegroundBrush"] as SolidColorBrush;
        }
      }
      else
      {
        var themeSettings = new MaterialDarkCustomThemeSettings
        {
          PrimaryBackground = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FFE1E1E1")! },
          FontFamily = new FontFamily(MainWindow.CurrentFontFamily),
          BodyAltFontSize = MainWindow.CurrentFontSize - 2,
          BodyFontSize = MainWindow.CurrentFontSize,
          HeaderFontSize = MainWindow.CurrentFontSize + 4,
          SubHeaderFontSize = MainWindow.CurrentFontSize + 2,
          SubTitleFontSize = MainWindow.CurrentFontSize,
          TitleFontSize = MainWindow.CurrentFontSize + 2
        };
        SfSkinManager.RegisterThemeSettings("MaterialDarkCustom", themeSettings);

        Application.Current.Resources["EQGoodForegroundBrush"] = new SolidColorBrush { Color = Colors.LightGreen };
        Application.Current.Resources["EQMenuIconBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FF4F9FE2")! };
        Application.Current.Resources["EQSearchBackgroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FF314435")! };
        Application.Current.Resources["EQWarnBackgroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FF96410d")! };
        Application.Current.Resources["EQWarnForegroundBrush"] = new SolidColorBrush { Color = Colors.Orange };
        Application.Current.Resources["EQStopForegroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FFcc434d")! };
        Application.Current.Resources["EQDisabledBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#88FFFFFF")! };
        SfSkinManager.SetTheme(main, new Theme("MaterialDarkCustom"));
        LoadDictionary("/Syncfusion.Themes.MaterialDarkCustom.WPF;component/MSControl/CheckBox.xaml");
        LoadDictionary("/Syncfusion.Themes.MaterialDarkCustom.WPF;component/SfDataGrid/SfDataGrid.xaml");
        LoadDictionary("/Syncfusion.Themes.MaterialDarkCustom.WPF;component/Common/Brushes.xaml");
        main.BorderBrush = Application.Current.Resources["ContentBackgroundAlt2"] as SolidColorBrush;

        if (!string.IsNullOrEmpty(main.statusText?.Text))
        {
          main.statusText.Foreground = Application.Current.Resources["EQGoodForegroundBrush"] as SolidColorBrush;
        }
      }

      Application.Current.Resources["PreviewBackgroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#BB000000")! };
      Application.Current.Resources["DamageOverlayBackgroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#99000000")! };
      Application.Current.Resources["DamageOverlayDamageBrush"] = new SolidColorBrush { Color = Colors.White };
      Application.Current.Resources["DamageOverlayProgressBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FF1D397E")! };

      MainActions.FireThemeChanged(theme);
    }

    // should already run on the UI thread
    internal static void Clear(ContentControl petsWindow, ContentControl playersWindow)
    {
      PetPlayersView.Clear();
      VerifiedPetsView.Clear();
      VerifiedPlayersView.Clear();

      var entry = new ExpandoObject() as dynamic;
      entry.Name = Labels.UNASSIGNED;
      VerifiedPlayersView.Add(entry);
      DockingManager.SetHeader(petsWindow, string.Format(PetsListTitle, VerifiedPetsView.Count));
      DockingManager.SetHeader(playersWindow, string.Format(PlayerListTitle, VerifiedPlayersView.Count));
    }

    internal static Dictionary<string, ContentControl> GetOpenWindows(DockingManager dockSite, DocumentTabControl chartTab)
    {
      var opened = new Dictionary<string, ContentControl>();
      foreach (var child in dockSite.Children)
      {
        if (child is ContentControl control)
        {
          opened[control.Name] = control;
        }
      }

      if (chartTab is { Container: not null })
      {
        foreach (var child in chartTab.Container.Items)
        {
          if (child is ContentControl control)
          {
            opened[control.Name] = control;
          }
        }
      }

      return opened;
    }

    internal static dynamic InsertNameIntoSortedList(string name, ObservableCollection<object> collection)
    {
      var entry = new ExpandoObject() as dynamic;
      entry.Name = name;

      int index = collection.ToList().BinarySearch(entry, TheSortableNameComparer);
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
        UIUtil.InvokeNow(() =>
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

          DockingManager.SetHeader(petMappingWindow, "Pet Owners (" + PetPlayersView.Count + ")");
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
        UIUtil.InvokeNow(() =>
        {
          var entry = InsertNameIntoSortedList(name, VerifiedPlayersView);
          entry.PlayerClass = PlayerManager.Instance.GetPlayerClass(name);
          DockingManager.SetHeader(playersWindow, string.Format(PlayerListTitle, VerifiedPlayersView.Count));
        });
      };

      PlayerManager.Instance.EventsUpdatePlayerClass += (name, playerClass) =>
      {
        UIUtil.InvokeNow(() =>
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
        UIUtil.InvokeNow(() =>
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
              DockingManager.SetHeader(petMappingWindow, "Pet Owners (" + PetPlayersView.Count + ")");
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
        InsertNameIntoSortedList(name, VerifiedPetsView);
        DockingManager.SetHeader(petsWindow, string.Format(PetsListTitle, VerifiedPetsView.Count));
      });

      PlayerManager.Instance.EventsRemoveVerifiedPet += (_, name) =>
      {
        UIUtil.InvokeNow(() =>
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
              DockingManager.SetHeader(petMappingWindow, "Pet Owners (" + PetPlayersView.Count + ")");
            }

            main.CheckComputeStats();
          }
        });
      };
    }

    internal static void ExportFights(List<Fight> fights)
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
                using var f = File.OpenRead(MainWindow.CurrentLogFile);
                var s = FileUtil.GetStreamReader(f, range.TimeSegments[0].BeginTime);
                while (!s.EndOfStream)
                {
                  var line = s.ReadLine();
                  if (string.IsNullOrEmpty(line) || line.Length <= MainWindow.ACTION_INDEX)
                  {
                    continue;
                  }

                  var action = line[MainWindow.ACTION_INDEX..];
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

            UIUtil.InvokeNow(() => dialog.Close());
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
            UIUtil.InvokeAsync(() =>
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
          TextUtils.SaveHTML(saveFileDialog.FileName, tables);
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
  }
}
