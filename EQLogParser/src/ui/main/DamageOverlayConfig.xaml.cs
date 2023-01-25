using FontAwesome5;
using Syncfusion.Windows.Shared;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DamageOverlayConfig.xaml
  /// </summary>
  public partial class DamageOverlayConfig : ChromelessWindow
  {
    private readonly List<ColorPicker> ColorPickerList = new List<ColorPicker>();
    private readonly List<StackPanel> NamePanels = new List<StackPanel>();
    private readonly List<Image> NameIconList = new List<Image>();
    private readonly List<TextBlock> NameBlockList = new List<TextBlock>();
    private readonly List<StackPanel> DamagePanels = new List<StackPanel>();
    private readonly List<TextBlock> DamageBlockList = new List<TextBlock>();
    private readonly List<ImageAwesome> DamageRateList = new List<ImageAwesome>();
    private readonly List<Rectangle> EmptyList = new List<Rectangle>();
    private readonly List<Rectangle> RectangleList = new List<Rectangle>();
    private readonly List<Color> ColorList = new List<Color>();
    private TextBlock TitleBlock;
    private StackPanel TitlePanel;
    private TextBlock TitleDamageBlock;
    private StackPanel TitleDamagePanel;
    private Rectangle TitleRectangle;
    private int CurrentMaxRows;
    private MainWindow Main = null;

    public DamageOverlayConfig()
    {
      InitializeComponent();

      MainActions.SetTheme(this, MainWindow.CurrentTheme);
      Application.Current.Resources["OverlayCurrentBrush"] = Application.Current.Resources["ContentBackgroundAlt2"];

      Main = Application.Current.MainWindow as MainWindow;
      Main.EventsThemeChanged += EventsThemeChanged;

      configPanel.SetValue(Panel.ZIndexProperty, 3);
      configPanel.SetValue(Canvas.RightProperty, 4.0);
      savePanel.SetValue(Panel.ZIndexProperty, 3);
      savePanel.SetValue(Canvas.BottomProperty, 1.0);
      overlayCanvas.SetResourceReference(Canvas.BackgroundProperty, "ContentBackground");

      OverlayUtil.LoadSettings(ColorList, out bool hidePlayers, out bool showCritRate, out string selectedClass,
        out int damageMode, out int fontSize, out CurrentMaxRows, out string width, out string height, out string top, out string left);

      showNameSelection.SelectedIndex = hidePlayers ? 1 : 0;
      showCritRateSelection.SelectedIndex = showCritRate ? 1 : 0;

      var list = PlayerManager.Instance.GetClassList();
      list.Insert(0, EQLogParser.Resource.ANY_CLASS);
      classesList.ItemsSource = list;
      classesList.SelectedItem = selectedClass;

      foreach (var item in damageModeSelection.Items.Cast<ComboBoxItem>())
      {
        if ((string)item.Tag == damageMode.ToString())
        {
          damageModeSelection.SelectedItem = item;
        }
      }

      maxRowsSelection.SelectedItem = maxRowsSelection.Items[CurrentMaxRows - 5];

      CreateRows();
      OverlayUtil.SetVisible(overlayCanvas, true);
      LoadTestData();

      var margin = SystemParameters.WindowNonClientFrameThickness;
      if (width != null && double.TryParse(width, out double dvalue) && !double.IsNaN(dvalue))
      {
        Width = dvalue + margin.Left + margin.Right;
      }

      if (height != null && double.TryParse(height, out dvalue) && !double.IsNaN(dvalue))
      {
        Height = dvalue + margin.Top + margin.Bottom;
      }

      if (top != null && double.TryParse(top, out dvalue) && !double.IsNaN(dvalue))
      {
        var test = dvalue - margin.Top;
        if (test >= SystemParameters.VirtualScreenTop && test < SystemParameters.VirtualScreenHeight)
        {
          Top = test;
        }
      }

      if (left != null && double.TryParse(left, out dvalue) && !double.IsNaN(dvalue))
      {
        var test = dvalue - margin.Left;
        if (test >= SystemParameters.VirtualScreenLeft && test < SystemParameters.VirtualScreenWidth)
        {
          Left = test;
        }
      }

      bool fontHasBeenSet = false;
      foreach (var item in fontSizeSelection.Items)
      {
        if ((item as ComboBoxItem).Tag as string == fontSize.ToString())
        {
          fontSizeSelection.SelectedItem = item;
          SetFont(fontSize);
          fontHasBeenSet = true;
        }
      }

      if (!fontHasBeenSet)
      {
        SetFont(fontSize);
      }
    }

    private void EventsThemeChanged(object sender, string e)
    {
      MainActions.SetTheme(this, MainWindow.CurrentTheme);
      Application.Current.Resources["OverlayCurrentBrush"] = Application.Current.Resources["ContentBackgroundAlt2"];
    }

    private void LoadTestData()
    {
      for (int i = 0; i < CurrentMaxRows - 1; i++)
      {
        NameBlockList[i].Text = i + 1 + ". Example Player Name";
        NameBlockList[i].FontStyle = FontStyles.Italic;
        NameBlockList[i].FontWeight = FontWeights.Light;

        switch (i)
        {
          case 0:
          case 1:
            NameIconList[i].Source = PlayerManager.NEC_ICON;
            break;
          case 2:
          case 3:
            NameIconList[i].Source = PlayerManager.MAG_ICON;
            break;
          case 4:
          case 5:
            NameIconList[i].Source = PlayerManager.BER_ICON;
            break;
          case 6:
          case 7:
            NameIconList[i].Source = PlayerManager.SHD_ICON;
            break;
          default:
            NameIconList[i].Source = PlayerManager.DRU_ICON;
            break;
        }
      }

      NameBlockList[CurrentMaxRows - 1].Text = CurrentMaxRows + ". ...";
      NameBlockList[CurrentMaxRows - 1].FontStyle = FontStyles.Italic;
      NameBlockList[CurrentMaxRows - 1].FontWeight = FontWeights.Light;
      NameIconList[CurrentMaxRows - 1].Source = PlayerManager.WIZ_ICON;
    }

    private void Resize(double height, double width)
    {
      double rowHeight = height / (CurrentMaxRows + 1);

      UIElementUtil.SetSize(TitlePanel, rowHeight, double.NaN);
      UIElementUtil.SetSize(TitleDamagePanel, rowHeight, double.NaN);
      UIElementUtil.SetSize(configPanel, rowHeight, double.NaN);
      UIElementUtil.SetSize(savePanel, rowHeight, width);

      UIElementUtil.SetSize(TitleRectangle, rowHeight, width);
      for (int i = 0; i < CurrentMaxRows; i++)
      {
        // should only effect test data
        var percent = Convert.ToDouble(70 - 10 * i) / 100;
        percent = percent > 0.0 ? percent : 0.0;
        UIElementUtil.SetSize(RectangleList[i], rowHeight, width * percent);
        UIElementUtil.SetSize(EmptyList[i], rowHeight, width - (width * percent));
        EmptyList[i].SetValue(Canvas.LeftProperty, width * percent);
        UIElementUtil.SetSize(NamePanels[i], rowHeight, double.NaN);
        UIElementUtil.SetSize(DamagePanels[i], rowHeight, double.NaN);

        double pos = rowHeight * (i + 1);
        RectangleList[i].SetValue(Canvas.TopProperty, pos);
        EmptyList[i].SetValue(Canvas.TopProperty, pos);
        NamePanels[i].SetValue(Canvas.TopProperty, pos);
        DamagePanels[i].SetValue(Canvas.TopProperty, pos);
      }
    }

    private void CreateRows()
    {
      OverlayUtil.CreateTitleRow(OverlayUtil.TEXTBRUSH, overlayCanvas, out TitleRectangle, out TitlePanel, out TitleBlock,
        out TitleDamagePanel, out TitleDamageBlock);

      TitlePanel.SizeChanged += TitleResizing;
      TitleDamagePanel.SizeChanged += TitleResizing;

      for (int i = 0; i < CurrentMaxRows; i++)
      {
        var rectangle = OverlayUtil.CreateRectangle(ColorList[i], OverlayUtil.DATA_OPACITY);
        RectangleList.Add(rectangle);
        overlayCanvas.Children.Add(rectangle);

        var empty = OverlayUtil.CreateRectangle("OverlayCurrentBrush", OverlayUtil.OPACITY);
        EmptyList.Add(empty);
        overlayCanvas.Children.Add(empty);

        var nameStack = OverlayUtil.CreateNameStackPanel();
        NamePanels.Add(nameStack);

        var classImage = OverlayUtil.CreateImage();
        NameIconList.Add(classImage);
        nameStack.Children.Add(classImage);

        var nameBlock = OverlayUtil.CreateTextBlock();
        NameBlockList.Add(nameBlock);
        nameStack.Children.Add(nameBlock);
        overlayCanvas.Children.Add(nameStack);

        var damageStack = OverlayUtil.CreateDamageStackPanel();
        DamagePanels.Add(damageStack);

        var colorPicker = new ColorPicker
        {
          Width = 35,
          Height = 20,
          Color = ColorList[i],
          IsGradientPropertyEnabled = false,
          EnableSolidToGradientSwitch = false,
          Tag = string.Format(CultureInfo.CurrentCulture, "OverlayRankColor{0}", i + 1),
          BorderThickness = new Thickness(0),
          Background = new SolidColorBrush(Colors.Transparent)
        };

        colorPicker.HeaderTemplate = Application.Current.Resources["ColorPickerMinHeaderTemplate"] as DataTemplate;
        colorPicker.ColorChanged += (DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        {
          rectangle.Fill = OverlayUtil.CreateBrush(colorPicker.Color);
        };

        ColorPickerList.Add(colorPicker);
        damageStack.Children.Add(colorPicker);

        overlayCanvas.Children.Add(damageStack);
      }
    }

    private void TitleResizing(object sender, SizeChangedEventArgs e) => TitlePanel.MaxWidth = ActualWidth - TitleDamagePanel.ActualWidth - 24;

    private void ChromeMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => e.Handled = true;

    private void SetFont(int size)
    {
      TitleBlock.FontSize = size;
      TitleDamageBlock.FontSize = size;

      for (int i = 0; i < CurrentMaxRows; i++)
      {
        NameBlockList[i].FontSize = size;
        NameIconList[i].Height = size + 4;
        NameIconList[i].MaxHeight = size + 4;

        // empty during configure
        if (DamageRateList.Count > 0)
        {
          DamageRateList[i].Height = size;
          DamageRateList[i].Width = size;
          DamageRateList[i].MaxHeight = size;
          DamageRateList[i].MaxWidth = size;
          DamageBlockList[i].FontSize = size;
        }
      }
    }

    private void PanelSizeChanged(object sender, SizeChangedEventArgs e) => Resize(e.NewSize.Height, e.NewSize.Width);

    private void FontSizeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (TitleBlock != null && int.TryParse((fontSizeSelection.SelectedValue as ComboBoxItem).Tag as string, out int fontSize))
      {
        SetFont(fontSize);
      }
    }

    private void MaxRowsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (TitleBlock != null && int.TryParse((maxRowsSelection.SelectedValue as ComboBoxItem).Tag as string, out int maxRows))
      {
        // clear out old rows
        overlayCanvas.Children.Remove(TitleRectangle);
        overlayCanvas.Children.Remove(TitlePanel);
        overlayCanvas.Children.Remove(TitleDamagePanel);
        TitlePanel.SizeChanged -= TitleResizing;
        TitleDamagePanel.SizeChanged -= TitleResizing;
        RectangleList.ForEach(rectangle => overlayCanvas.Children.Remove(rectangle));
        EmptyList.ForEach(empty => overlayCanvas.Children.Remove(empty));
        NamePanels.ForEach(nameStack => overlayCanvas.Children.Remove(nameStack));
        DamagePanels.ForEach(damageStack => overlayCanvas.Children.Remove(damageStack));
        RectangleList.Clear();
        EmptyList.Clear();
        NamePanels.Clear();
        DamagePanels.Clear();
        NameBlockList.Clear();
        NameIconList.Clear();

        CurrentMaxRows = maxRows;
        ConfigUtil.SetSetting("MaxOverlayRows", CurrentMaxRows.ToString());
        CreateRows();
        OverlayUtil.SetVisible(overlayCanvas, true);

        int.TryParse((fontSizeSelection.SelectedValue as ComboBoxItem).Tag as string, out int fontSize);
        SetFont(fontSize);

        LoadTestData();
        Resize(overlayCanvas.ActualHeight, overlayCanvas.ActualWidth);
      }
    }

    private void CancelClick(object sender, RoutedEventArgs e) => OverlayUtil.OpenDamageOverlay(false, true);

    private void SaveClick(object sender, RoutedEventArgs e)
    {
      if (!double.IsNaN(overlayCanvas.ActualHeight) && overlayCanvas.ActualHeight > 0)
      {
        ConfigUtil.SetSetting("OverlayHeight", overlayCanvas.ActualHeight.ToString());
      }

      if (!double.IsNaN(overlayCanvas.ActualWidth) && overlayCanvas.ActualWidth > 0)
      {
        ConfigUtil.SetSetting("OverlayWidth", overlayCanvas.ActualWidth.ToString());
      }

      var margin = SystemParameters.WindowNonClientFrameThickness;
      if (Top + margin.Top >= SystemParameters.VirtualScreenTop && (Left + margin.Left) >= SystemParameters.VirtualScreenLeft)
      {
        ConfigUtil.SetSetting("OverlayTop", (Top + margin.Top).ToString());
        ConfigUtil.SetSetting("OverlayLeft", (Left + margin.Left).ToString());
      }

      if (TitleBlock != null)
      {
        if (int.TryParse((fontSizeSelection.SelectedValue as ComboBoxItem).Tag as string, out int size))
        {
          ConfigUtil.SetSetting("OverlayFontSize", size.ToString());
        }

        ConfigUtil.SetSetting("OverlayDamageMode", (string)(damageModeSelection.SelectedItem as ComboBoxItem).Tag);
        ConfigUtil.SetSetting("HideOverlayOtherPlayers", (showNameSelection.SelectedIndex == 1).ToString());
        ConfigUtil.SetSetting("ShowOverlayCritRate", (showCritRateSelection.SelectedIndex == 1).ToString());
        ConfigUtil.SetSetting("SelectedOverlayClass", classesList.SelectedItem.ToString());

        ColorPickerList.ForEach(colorPicker =>
        {
          ConfigUtil.SetSetting(colorPicker.Tag as string, colorPicker.Color.ToString());
        });
      }

      OverlayUtil.OpenDamageOverlay(false, true);
    }

    private void WindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
      if (Main != null)
      {
        Main.EventsThemeChanged -= EventsThemeChanged;
        Main = null;
      }

      TitlePanel.SizeChanged -= TitleResizing;
      TitleDamagePanel.SizeChanged -= TitleResizing;
    }
  }
}
