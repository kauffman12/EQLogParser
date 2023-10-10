using FontAwesome5;
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
    private const string PETS_LIST_TITLE = "Verified Pets ({0})";
    private const string PLAYER_LIST_TITLE = "Verified Players ({0})";
    private static readonly ObservableCollection<dynamic> VerifiedPlayersView = new();
    private static readonly ObservableCollection<dynamic> VerifiedPetsView = new();
    private static readonly ObservableCollection<PetMapping> PetPlayersView = new();
    private static readonly SortablePetMappingComparer TheSortablePetMappingComparer = new();
    private static readonly SortableNameComparer TheSortableNameComparer = new();
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal static void CheckVersion(TextBlock errorText)
    {
      var version = Application.ResourceAssembly.GetName().Version;
      var dispatcher = Application.Current.Dispatcher;
      Task.Delay(2000).ContinueWith(task =>
      {
        HttpClient client = null;
        try
        {
          client = new HttpClient();
          var request = client.GetStringAsync(@"https://github.com/kauffman12/EQLogParser/blob/master/README.md");
          request.Wait();

          var matches = new Regex(@"EQLogParser-((\d)\.(\d)\.(\d?\d?\d))\.(msi|exe)").Match(request.Result);
          if (matches.Success && matches.Groups.Count == 6 && int.TryParse(matches.Groups[2].Value, out var v1) &&
            int.TryParse(matches.Groups[3].Value, out var v2) && int.TryParse(matches.Groups[4].Value, out var v3)
            && (v1 > version.Major || (v1 == version.Major && v2 > version.Minor) ||
            (v1 == version.Major && v2 == version.Minor && v3 > version.Build)))
          {
            dispatcher.InvokeAsync((Action)(async () =>
            {
              var msg = new MessageWindow("Version " + matches.Groups[1].Value + " is Available. Download and Install?",
                Resource.CHECK_VERSION, MessageWindow.IconType.Question, "Yes");
              msg.ShowDialog();

              if (msg.IsYes1Clicked)
              {
                HttpClient downloadClient = null;
                var url = "https://github.com/kauffman12/EQLogParser/raw/master/Release/EQLogParser-" +
                  matches.Groups[1].Value + "." + matches.Groups[5].Value;

                try
                {
                  downloadClient = new HttpClient();
                  await using var download = await downloadClient.GetStreamAsync(url);

                  var path = Environment.ExpandEnvironmentVariables("%userprofile%\\Downloads");
                  if (!Directory.Exists(path))
                  {
                    new MessageWindow("Unable to Access Downloads Folder. Can Not Download Update.",
                      Resource.CHECK_VERSION).ShowDialog();
                    return;
                  }

                  path += "\\AutoUpdateEQLogParser";
                  if (!Directory.Exists(path))
                  {
                    Directory.CreateDirectory(path);
                  }

                  var fullPath = path + "\\EQLogParser-" + matches.Groups[1].Value + ".msi";
                  await using (var fs = new FileStream(fullPath, FileMode.Create))
                  {
                    await download.CopyToAsync(fs);
                  }

                  if (File.Exists(fullPath))
                  {
                    var process = Process.Start("msiexec", "/i \"" + fullPath + "\"");
                    if (!process.HasExited)
                    {
                      Task.Delay(1000).ContinueWith(task =>
                      {
                        dispatcher.InvokeAsync(() => Application.Current.MainWindow.Close());
                      });
                    }
                  }
                }
                catch (Exception ex2)
                {
                  new MessageWindow("Problem Install Updates. Check Error Log for Details.", Resource.CHECK_VERSION).ShowDialog();
                  LOG.Error("Error Installing Updates", ex2);
                }
                finally
                {
                  downloadClient?.Dispose();
                }
              }
            }));
          }
        }
        catch (Exception ex)
        {
          LOG.Error("Error Checking for Updates", ex);
          Application.Current.Dispatcher.InvokeAsync(() =>
          {
            errorText.Text = "Update Check Failed. Firewall?";
          });
        }
        finally
        {
          client?.Dispose();
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
        LOG.Error(e);
      }
    }

    internal static void CreateFontFamiliesMenuItems(MenuItem parent, RoutedEventHandler callback, string currentFamily)
    {
      foreach (var family in UIElementUtil.GetCommonFontFamilyNames())
      {
        parent.Items.Add(createMenuItem(family, callback, EFontAwesomeIcon.Solid_Check));
      }

      MenuItem createMenuItem(string name, RoutedEventHandler handler, EFontAwesomeIcon awesome)
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
      parent.Items.Add(createMenuItem(10, callback, EFontAwesomeIcon.Solid_Check));
      parent.Items.Add(createMenuItem(11, callback, EFontAwesomeIcon.Solid_Check));
      parent.Items.Add(createMenuItem(12, callback, EFontAwesomeIcon.Solid_Check));
      parent.Items.Add(createMenuItem(13, callback, EFontAwesomeIcon.Solid_Check));
      parent.Items.Add(createMenuItem(14, callback, EFontAwesomeIcon.Solid_Check));
      MenuItem createMenuItem(double size, RoutedEventHandler handler, EFontAwesomeIcon awesome)
      {
        var imageAwesome = new ImageAwesome
        {
          Icon = awesome,
          Style = (Style)Application.Current.Resources["EQIconStyle"],
          Visibility = (size == currentSize) ? Visibility.Visible : Visibility.Hidden
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
      MenuItem CreateMenuItem(string name, string value, RoutedEventHandler handler, EFontAwesomeIcon awesome)
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
        if (item is MenuItem menuItem && menuItem.Icon is ImageAwesome image)
        {
          image.Visibility = (menuItem == selectedItem) ? Visibility.Visible : Visibility.Hidden;
        }
      }
    }

    internal static void SetTheme(Window window, string theme)
    {
      if (window != null)
      {
        if (theme == "MaterialLight")
        {
          SfSkinManager.SetTheme(window, new Theme("MaterialLight"));
        }
        else
        {
          SfSkinManager.SetTheme(window, new Theme("MaterialDarkCustom;MaterialDark"));
        }
      }
    }

    internal static void LoadTheme(MainWindow main, string theme)
    {
      Application.Current.Resources["EQTitleSize"] = MainWindow.CurrentFontSize + 2;
      Application.Current.Resources["EQContentSize"] = MainWindow.CurrentFontSize;
      Application.Current.Resources["EQButtonHeight"] = MainWindow.CurrentFontSize + 14;
      Application.Current.Resources["EQTableHeaderRowHeight"] = MainWindow.CurrentFontSize + 14;
      Application.Current.Resources["EQTableRowHeight"] = MainWindow.CurrentFontSize + 12;

      if (theme == "MaterialLight")
      {
        var themeSettings = new MaterialLightThemeSettings
        {
          PrimaryBackground = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FF343434") },
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
        Application.Current.Resources["EQMenuIconBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FF3d7baf") };
        Application.Current.Resources["EQSearchBackgroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FFa7baab") };
        Application.Current.Resources["EQWarnBackgroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FFeaa6ac") };
        Application.Current.Resources["EQWarnForegroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FFb02021") };
        Application.Current.Resources["EQStopForegroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FFcc434d") };
        Application.Current.Resources["EQDisabledBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#88000000") };
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
          PrimaryBackground = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FFE1E1E1") },
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
        Application.Current.Resources["EQMenuIconBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FF4F9FE2") };
        Application.Current.Resources["EQSearchBackgroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FF314435") };
        Application.Current.Resources["EQWarnBackgroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FF96410d") };
        Application.Current.Resources["EQWarnForegroundBrush"] = new SolidColorBrush { Color = Colors.Orange };
        Application.Current.Resources["EQStopForegroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FFcc434d") };
        Application.Current.Resources["EQDisabledBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#88FFFFFF") };
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

      Application.Current.Resources["PreviewBackgroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#BB000000") };
      Application.Current.Resources["DamageOverlayBackgroundBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#99000000") };
      Application.Current.Resources["DamageOverlayDamageBrush"] = new SolidColorBrush { Color = Colors.White };
      Application.Current.Resources["DamageOverlayProgressBrush"] = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString("#FF1D397E") };
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
      DockingManager.SetHeader(petsWindow, string.Format(PETS_LIST_TITLE, VerifiedPetsView.Count));
      DockingManager.SetHeader(playersWindow, string.Format(PLAYER_LIST_TITLE, VerifiedPlayersView.Count));
    }

    internal static Dictionary<string, ContentControl> GetOpenWindows(DockingManager dockSite, DocumentTabControl ChartTab)
    {
      var opened = new Dictionary<string, ContentControl>();
      foreach (var child in dockSite.Children)
      {
        if (child is ContentControl control)
        {
          opened[control.Name] = control;
        }
      }

      if (ChartTab != null && ChartTab.Container != null)
      {
        foreach (var child in ChartTab.Container.Items)
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
      PlayerManager.Instance.EventsNewPetMapping += (sender, mapping) =>
      {
        Application.Current.Dispatcher.InvokeAsync(() =>
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
      PlayerManager.Instance.EventsNewVerifiedPlayer += (sender, name) =>
      {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
          var entry = InsertNameIntoSortedList(name, VerifiedPlayersView);
          entry.PlayerClass = PlayerManager.Instance.GetPlayerClass(name);
          DockingManager.SetHeader(playersWindow, string.Format(PLAYER_LIST_TITLE, VerifiedPlayersView.Count));
        });
      };

      PlayerManager.Instance.EventsUpdatePlayerClass += (name, playerClass) =>
      {
        Application.Current.Dispatcher.InvokeAsync(() =>
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

      PlayerManager.Instance.EventsRemoveVerifiedPlayer += (sender, name) =>
      {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
          var found = VerifiedPlayersView.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
          if (found != null)
          {
            VerifiedPlayersView.Remove(found);
            DockingManager.SetHeader(playersWindow, string.Format(PLAYER_LIST_TITLE, VerifiedPlayersView.Count));

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
      PlayerManager.Instance.EventsNewVerifiedPet += (sender, name) => main.Dispatcher.InvokeAsync(() =>
      {
        InsertNameIntoSortedList(name, VerifiedPetsView);
        DockingManager.SetHeader(petsWindow, string.Format(PETS_LIST_TITLE, VerifiedPetsView.Count));
      });

      PlayerManager.Instance.EventsRemoveVerifiedPet += (sender, name) =>
      {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
          var found = VerifiedPetsView.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
          if (found != null)
          {
            VerifiedPetsView.Remove(found);
            DockingManager.SetHeader(petsWindow, string.Format(PETS_LIST_TITLE, VerifiedPetsView.Count));

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
      var fileName = "eqlog_" + ConfigUtil.PlayerName + "_" + ConfigUtil.ServerName + "-selected.txt";
      saveFileDialog.Filter = "Text Files (*.txt)|*.txt";
      saveFileDialog.FileName = string.Join("", fileName.Split(Path.GetInvalidFileNameChars()));

      if (saveFileDialog.ShowDialog().Value)
      {
        var dialog = new MessageWindow("Saving " + fights.Count + " Selected Fights.", Resource.FILEMENU_SAVE_FIGHTS,
          MessageWindow.IconType.Save);

        Task.Delay(150).ContinueWith(task =>
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
                var s = Helpers.GetStreamReader(f, range.TimeSegments[0].BeginTime);
                while (!s.EndOfStream)
                {
                  var line = s.ReadLine();
                  if (!string.IsNullOrEmpty(line) && line.Length > MainWindow.ACTION_INDEX)
                  {
                    var action = line[MainWindow.ACTION_INDEX..];
                    if (ChatLineParser.ParseChatType(action) == null)
                    {
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
              }
            }

            Application.Current.Dispatcher.InvokeAsync(() => dialog?.Close());
          }
          catch (IOException ex)
          {
            LOG.Error(ex);
            accessError = true;
          }
          catch (UnauthorizedAccessException uax)
          {
            LOG.Error(uax);
          }
          catch (SecurityException se)
          {
            LOG.Error(se);
          }
          catch (ArgumentNullException ane)
          {
            LOG.Error(ane);
          }
          finally
          {
            Application.Current.Dispatcher.InvokeAsync(() => dialog?.Close());

            if (accessError)
            {
              Application.Current.Dispatcher.InvokeAsync(() =>
              {
                new MessageWindow("Error Saving. Can not access save file.", Resource.FILEMENU_SAVE_FIGHTS, MessageWindow.IconType.Save).Show();
              });
            }
          }
        });

        dialog.ShowDialog();
      }
    }

    internal static void ExportAsHTML(Dictionary<string, SummaryTable> tables)
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

        if (saveFileDialog.ShowDialog().Value)
        {
          TextUtils.SaveHTML(saveFileDialog.FileName, tables);
        }
      }
      catch (IOException ex)
      {
        LOG.Error(ex);
      }
      catch (UnauthorizedAccessException uax)
      {
        LOG.Error(uax);
      }
      catch (SecurityException se)
      {
        LOG.Error(se);
      }
      catch (ArgumentNullException ane)
      {
        LOG.Error(ane);
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
        LOG.Error(ex);
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
