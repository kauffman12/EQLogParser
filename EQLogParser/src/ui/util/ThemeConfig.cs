using FontAwesome5;
using Syncfusion.SfSkinManager;
using Syncfusion.Themes.MaterialDarkCustom.WPF;
using Syncfusion.Themes.MaterialLight.WPF;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using FontFamily = System.Windows.Media.FontFamily;

namespace EQLogParser
{
  /// <summary>
  /// Centralizes all theme, font, and resource logic.
  /// Maintains the current theme state and fires EventsThemeChanged when it changes.
  /// </summary>
  internal static class ThemeConfig
  {
    private const string DefaultTheme = "MaterialDark";

    internal static string CurrentTheme;
    internal static string CurrentFontFamily;
    internal static double CurrentFontSize;
    internal static double CurrentDateTimeWidth;
    internal static double CurrentItemWidth;
    internal static double CurrentNpcWidth;
    internal static double CurrentNameWidth;
    internal static double CurrentSpellWidth;
    internal static double CurrentShortWidth;
    internal static double CurrentShortestWidth;
    internal static double CurrentMediumWidth;
    internal static DropShadowEffect OverlayTextEffect;

    internal static event Action<string> EventsThemeChanged;

    private static MainWindow _mainWindow;

    internal static void Init(MainWindow main)
    {
      _mainWindow = main;

      // load theme and fonts
      CurrentFontFamily = ConfigUtil.GetSetting("ApplicationFontFamily", "Segoe UI");
      CurrentFontSize = ConfigUtil.GetSettingAsDouble("ApplicationFontSize", 12);
      CurrentTheme = ConfigUtil.GetSetting("CurrentTheme", DefaultTheme);
      OverlayTextEffect = new DropShadowEffect { ShadowDepth = 2, Direction = 330, Color = Colors.Black, Opacity = 0.75, BlurRadius = 2 };

      if (UiElementUtil.GetSystemFontFamilies().FirstOrDefault(font => font.Source == CurrentFontFamily) == null)
      {
        CurrentFontFamily = "Segoe UI";
      }

      Application.Current.Resources["EQChatFontSize"] = 16.0; // changed when chat archive loads
      Application.Current.Resources["EQChatFontFamily"] = new FontFamily("Segoe UI");
      Application.Current.Resources["EQLogFontSize"] = 16.0; // changed when chat archive loads
      Application.Current.Resources["EQLogFontFamily"] = new FontFamily("Segoe UI");
    }

    /// <summary>
    /// Applies the current theme to a dependency object (used by popup windows).
    /// </summary>
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

    /// <summary>
    /// Initializes application-wide theme resources (brushes, font sizes, settings).
    /// </summary>
    internal static void InitThemes()
    {
      // constant for all themes
      Application.Current.Resources["PreviewBackgroundBrush"] = UiUtil.GetBrush("#BB000000");
      Application.Current.Resources["DamageOverlayBackgroundBrush"] = UiUtil.GetBrush("#99000000");
      Application.Current.Resources["DamageOverlayDamageBrush"] = UiUtil.GetBrush("#FFF");
      Application.Current.Resources["DamageOverlayProgressBrush"] = UiUtil.GetBrush("#FF1D397E");

      UiUtil.InvokeNow(() =>
      {
        SetThemeFontSizes();
        RegisterThemeSettings();
      }, DispatcherPriority.Send);
    }

    /// <summary>
    /// Sets the application theme. Pass null to apply the current saved theme.
    /// </summary>
    internal static void SetTheme(string theme = null)
    {
      CurrentTheme = theme ?? DefaultTheme;
      _ = UiUtil.InvokeAsync(() =>
      {
        if (theme != null)
        {
          RegisterThemeSettings();
        }

        ChangeTheme(_mainWindow);
        // set after change succeeds
        ConfigUtil.SetSetting("CurrentTheme", CurrentTheme);
      }, DispatcherPriority.Send);

      _ = UiUtil.InvokeAsync(() =>
      {
        EventsThemeChanged?.Invoke(CurrentTheme);
      }, DispatcherPriority.DataBind);
    }

    /// <summary>
    /// Changes the application font family and refreshes all themed elements.
    /// </summary>
    internal static void ChangeThemeFontFamily(string family)
    {
      if (CurrentFontFamily != family)
      {
        CurrentFontFamily = family;
        _ = UiUtil.InvokeAsync(() =>
        {
          RegisterThemeSettings();
          ChangeTheme(_mainWindow);
          // set after change succeeds
          ConfigUtil.SetSetting("ApplicationFontFamily", CurrentFontFamily);
          EventsThemeChanged?.Invoke(CurrentTheme);
        }, DispatcherPriority.Send);
      }
    }

    /// <summary>
    /// Changes the application font size and refreshes all themed elements.
    /// </summary>
    internal static void ChangeThemeFontSizes(double size)
    {
      if (!CurrentFontSize.Equals(size))
      {
        CurrentFontSize = size;
        _ = UiUtil.InvokeAsync(() =>
        {
          SetThemeFontSizes();
          RegisterThemeSettings();
          ChangeTheme(_mainWindow);
          // set after change succeeds
          ConfigUtil.SetSetting("ApplicationFontSize", CurrentFontSize);
          EventsThemeChanged?.Invoke(CurrentTheme);
        }, DispatcherPriority.Send);
      }
    }

    /// <summary>
    /// Populates a menu with font family options, marking the current one as checked.
    /// </summary>
    internal static void CreateFontFamiliesMenuItems(MenuItem parent, RoutedEventHandler callback)
    {
      foreach (var family in UiElementUtil.GetCommonFontFamilyNames())
      {
        parent.Items.Add(CreateMenuItem(family, callback, EFontAwesomeIcon.Solid_Check));
      }

      static MenuItem CreateMenuItem(string name, RoutedEventHandler handler, EFontAwesomeIcon awesome)
      {
        var imageAwesome = new ImageAwesome
        {
          Icon = awesome,
          Style = (Style)Application.Current.Resources["EQIconStyle"],
          Visibility = name == CurrentFontFamily ? Visibility.Visible : Visibility.Hidden
        };

        var menuItem = new MenuItem { Header = name };
        menuItem.Click += handler;
        menuItem.Icon = imageAwesome;
        return menuItem;
      }
    }

    /// <summary>
    /// Populates a menu with font size options (10-18pt), marking the current one as checked.
    /// </summary>
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

    /// <summary>
    /// Updates the checkmark on a group of menu items to reflect the selected one.
    /// </summary>
    internal static void UpdateCheckedMenuItem(MenuItem selectedItem, ItemCollection items)
    {
      foreach (var item in items)
      {
        if (item is MenuItem { Icon: ImageAwesome image } menuItem)
        {
          image.Visibility = menuItem == selectedItem ? Visibility.Visible : Visibility.Hidden;
        }
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
      CurrentShortestWidth = 4.0 * CurrentFontSize;
      CurrentMediumWidth = 6.5 * CurrentFontSize;
      Application.Current.Resources["EQGridTitleHeight"] = new GridLength(18 + CurrentFontSize);
      Application.Current.Resources["EQGridFooterHeight"] = new GridLength(10 + CurrentFontSize);
      Application.Current.Resources["EQFightGridTitleHeight"] = new GridLength(21 + CurrentFontSize);
      Application.Current.Resources["EQTriggerCharacterList"] = new GridLength(180 + (CurrentFontSize * 4));
      Application.Current.Resources["EQGridWindowTitleHeight"] = new GridLength(14 + CurrentFontSize);
      Application.Current.Resources["EQWindowTitleHeight"] = 14 + CurrentFontSize;
      Application.Current.Resources["EQAlertIconSize"] = CurrentFontSize + 18;
      Application.Current.Resources["EQTitleSize"] = CurrentFontSize + 2;
      Application.Current.Resources["EQWindowButtonWidth"] = 20 + CurrentFontSize;
      Application.Current.Resources["EQWindowButtonTextSize"] = CurrentFontSize + 2;
      Application.Current.Resources["EQWindowButtonTextSize1"] = CurrentFontSize + 3;
      Application.Current.Resources["EQContentSizePlus"] = CurrentFontSize + 1;
      Application.Current.Resources["EQContentSize"] = CurrentFontSize;
      Application.Current.Resources["EQDescriptionSize"] = CurrentFontSize - 1;
      Application.Current.Resources["EQSubDescriptionSize"] = CurrentFontSize - 2;
      Application.Current.Resources["EQButtonHeight"] = CurrentFontSize + 12 + (CurrentFontSize % 2 == 0 ? 1 : 0);
      Application.Current.Resources["EQTabHeaderHeight"] = CurrentFontSize + 12;
      Application.Current.Resources["EQTableHeaderRowHeight"] = CurrentFontSize + 14;
      Application.Current.Resources["EQTableRowHeight"] = CurrentFontSize + 12;
      Application.Current.Resources["EQTableSixRowHeight"] = ((CurrentFontSize + 12) * 6) + (CurrentFontSize + 14);
      Application.Current.Resources["EQTableTenRowHeight"] = ((CurrentFontSize + 12) * 10) + (CurrentFontSize + 14);
      Application.Current.Resources["EQTableFifteenRowHeight"] = ((CurrentFontSize + 12) * 15) + (CurrentFontSize + 14);
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
          PrimaryBackground = UiUtil.GetBrush("#FF343434"),
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
          PrimaryBackground = UiUtil.GetBrush("#FFE1E1E1"),
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
        Application.Current.Resources["EQGoodForegroundBrush"] = UiUtil.GetBrush(Colors.DarkGreen);
        Application.Current.Resources["EQMenuIconBrush"] = UiUtil.GetBrush("#FF3d7baf");
        Application.Current.Resources["EQSearchBackgroundBrush"] = UiUtil.GetBrush("#FFa7baab");
        Application.Current.Resources["EQWarnBackgroundBrush"] = UiUtil.GetBrush("#FFeaa6ac");
        Application.Current.Resources["EQWarnForegroundBrush"] = UiUtil.GetBrush("#FF946e00");
        Application.Current.Resources["EQStopForegroundBrush"] = UiUtil.GetBrush("#FFcc434d");
        Application.Current.Resources["EQDisabledBrush"] = UiUtil.GetBrush("#88000000");
        LoadDictionary("/Syncfusion.Themes.MaterialLight.WPF;component/MSControl/CheckBox.xaml");
        LoadDictionary("/Syncfusion.Themes.MaterialLight.WPF;component/SfDataGrid/SfDataGrid.xaml");
        LoadDictionary("/Syncfusion.Themes.MaterialLight.WPF;component/Common/Brushes.xaml");
        LoadDictionary("/Syncfusion.Themes.MaterialLight.WPF;component/DockingManager/DockingManager.xaml");

        if (!string.IsNullOrEmpty(main.statusText?.Text))
        {
          main.statusText.Foreground = Application.Current.Resources["EQGoodForegroundBrush"] as SolidColorBrush;
        }
      }
      else
      {
        Application.Current.Resources["EQGoodForegroundBrush"] = UiUtil.GetBrush(Colors.LightGreen);
        Application.Current.Resources["EQMenuIconBrush"] = UiUtil.GetBrush("#FF4F9FE2");
        Application.Current.Resources["EQSearchBackgroundBrush"] = UiUtil.GetBrush("#FF314435");
        Application.Current.Resources["EQWarnBackgroundBrush"] = UiUtil.GetBrush("#FF96410d");
        Application.Current.Resources["EQWarnForegroundBrush"] = UiUtil.GetBrush("Orange");
        Application.Current.Resources["EQStopForegroundBrush"] = UiUtil.GetBrush("#FFcc434d");
        Application.Current.Resources["EQDisabledBrush"] = UiUtil.GetBrush("#88FFFFFF");
        LoadDictionary("/Syncfusion.Themes.MaterialDarkCustom.WPF;component/MSControl/CheckBox.xaml");
        LoadDictionary("/Syncfusion.Themes.MaterialDarkCustom.WPF;component/SfDataGrid/SfDataGrid.xaml");
        LoadDictionary("/Syncfusion.Themes.MaterialDarkCustom.WPF;component/Common/Brushes.xaml");
        LoadDictionary("/Syncfusion.Themes.MaterialDarkCustom.WPF;component/DockingManager/DockingManager.xaml");

        if (!string.IsNullOrEmpty(main.statusText?.Text))
        {
          main.statusText.Foreground = Application.Current.Resources["EQGoodForegroundBrush"] as SolidColorBrush;
        }
      }

      // after everything loads fix the tab height
      if (Application.Current.Resources["EQTabHeaderHeight"] is double height && MainActions.GetDockSite() is var dockSite)
      {
        var tabStyle = new Style
        {
          TargetType = typeof(Syncfusion.Windows.Tools.Controls.TabItemExt),
          BasedOn = (Style)Application.Current.FindResource(typeof(Syncfusion.Windows.Tools.Controls.TabItemExt))
        };
        tabStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, height));
        dockSite.SetValue(Syncfusion.Windows.Tools.Controls.DockingManager.DocumentTabItemStyleProperty, tabStyle);

        var docStyle = new Style
        {
          TargetType = typeof(Syncfusion.Windows.Tools.Controls.DockHeaderPresenter),
          BasedOn = (Style)Application.Current.FindResource(typeof(Syncfusion.Windows.Tools.Controls.DockHeaderPresenter))
        };
        docStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, height));
        dockSite.SetValue(Syncfusion.Windows.Tools.Controls.DockingManager.DockHeaderStyleProperty, docStyle);

        dockSite.SidePanelSize = height;
      }
    }

    private static void ChangeTheme(MainWindow main)
    {
      var theme = CurrentTheme == "MaterialLight" ? new Theme("MaterialLight") : new Theme("MaterialDarkCustom;MaterialDark");
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

      Application.Current.Resources.MergedDictionaries.Add(dict);
    }
  }
}
