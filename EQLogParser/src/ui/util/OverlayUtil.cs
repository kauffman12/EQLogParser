using FontAwesome5;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace EQLogParser
{
  class OverlayUtil
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal const double OPACITY = 0.55;
    internal const double DATA_OPACITY = 0.8;
    internal static readonly SolidColorBrush TEXTBRUSH = new SolidColorBrush(Colors.White);
    internal static readonly SolidColorBrush UPBRUSH = new SolidColorBrush(Colors.White);
    internal static readonly SolidColorBrush DOWNBRUSH = new SolidColorBrush(Colors.Red);
    internal static readonly SolidColorBrush TITLEBRUSH = new SolidColorBrush(Color.FromRgb(254, 156, 30));
    private static bool IsDamageOverlayEnabled = false;
    private static OverlayWindow Overlay = null;
    private static OverlayConfigWindow OverlayConfig = null;

    private OverlayUtil()
    {

    }

    internal static void CloseOverlay()
    {
     if (Overlay != null)
      {
        Overlay.Pause();
      }
    }

    internal static bool LoadSettings() => IsDamageOverlayEnabled = ConfigUtil.IfSet("IsDamageOverlayEnabled");

    internal static void OpenIfEnabled()
    {
      if (IsDamageOverlayEnabled)
      {
        OpenOverlay();
      }
    }

    internal static void OpenOverlay(bool configure = false, bool saveFirst = false)
    {
      Application.Current.Dispatcher.Invoke(() =>
      {
        if (configure)
        {
          CloseOverlay();

          if (OverlayConfig != null)
          {
            OverlayConfig.Visibility = Visibility.Visible;
          }
          else
          {
            OverlayConfig = new OverlayConfigWindow();
            OverlayConfig.Show();
          }
        }
        else
        {
          if (OverlayConfig != null)
          {
            OverlayConfig.Visibility = Visibility.Hidden;
          }

          if (Overlay != null)
          {
            Overlay.Visibility = Visibility.Visible;
            Overlay.Resume();
          }
          else
          {
            Overlay = new OverlayWindow();
            Overlay.Show();
          }
        }
      }, System.Windows.Threading.DispatcherPriority.Send);

      if (saveFirst)
      {
        ConfigUtil.Save();
      }
    }

    internal static bool ToggleOverlay()
    {
      IsDamageOverlayEnabled = !IsDamageOverlayEnabled;
      ConfigUtil.SetSetting("IsDamageOverlayEnabled", IsDamageOverlayEnabled.ToString(CultureInfo.CurrentCulture));

      if (IsDamageOverlayEnabled)
      {
        OpenOverlay(true, false);
      }
      else
      {
        CloseOverlay();
      }

      return IsDamageOverlayEnabled;
    }

    // From: https://gist.github.com/zihotki/09fc41d52981fb6f93a81ebf20b35cd5
    internal static Color ChangeColorBrightness(Color color, float correctionFactor)
    {
      float red = color.R;
      float green = color.G;
      float blue = color.B;

      if (correctionFactor < 0)
      {
        correctionFactor = 1 + correctionFactor;
        red *= correctionFactor;
        green *= correctionFactor;
        blue *= correctionFactor;
      }
      else
      {
        red = (255 - red) * correctionFactor + red;
        green = (255 - green) * correctionFactor + green;
        blue = (255 - blue) * correctionFactor + blue;
      }

      return Color.FromArgb(color.A, (byte)red, (byte)green, (byte)blue);
    }

    internal static Brush CreateBrush(Color color)
    {
      var brush = new LinearGradientBrush
      {
        StartPoint = new Point(0.5, 0),
        EndPoint = new Point(0.5, 1)
      };

      brush.GradientStops.Add(new GradientStop(color, 0.5));
      brush.GradientStops.Add(new GradientStop(ChangeColorBrightness(color, -0.5f), 1.0));
      return brush;
    }

    internal static Button CreateButton(string tooltip, string content)
    {
      var button = new Button { Background = null, BorderBrush = null, IsEnabled = true };
      button.SetValue(Panel.ZIndexProperty, 3);
      button.Foreground = TEXTBRUSH;
      button.VerticalAlignment = VerticalAlignment.Top;
      button.Padding = new Thickness(0, 0, 0, 0);
      button.FontFamily = new FontFamily("Segoe MDL2 Assets");
      button.VerticalAlignment = VerticalAlignment.Center;
      button.Margin = new Thickness(5, 3, 0, 0);
      button.ToolTip = new ToolTip { Content = tooltip };
      button.Content = content;
      button.Focusable = false;
      return button;
    }

    internal static TextBlock CreateTextBlock()
    {
      var textBlock = new TextBlock { Foreground = TEXTBRUSH };
      textBlock.SetValue(Panel.ZIndexProperty, 3);
      textBlock.FontFamily = new FontFamily("Lucidia Console");
      textBlock.VerticalAlignment = VerticalAlignment.Center;
      return textBlock;
    }

    internal static StackPanel CreateDamageStackPanel()
    {
      var stack = new StackPanel { Orientation = Orientation.Horizontal };
      stack.SetValue(Panel.ZIndexProperty, 3);
      stack.SetValue(Canvas.RightProperty, 4.0);
      return stack;
    }

    internal static ImageAwesome CreateImageAwesome()
    {
      var image = new ImageAwesome { Margin = new Thickness { Bottom = 1, Right = 2 }, Opacity = 0.0, Foreground = UPBRUSH, Icon = EFontAwesomeIcon.None };
      image.SetValue(Panel.ZIndexProperty, 3);
      image.VerticalAlignment = VerticalAlignment.Center;
      return image;
    }

    internal static Image CreateImage()
    {
      var image = new Image { Margin = new Thickness(0, 0, 4, 1) };
      image.SetValue(Panel.ZIndexProperty, 3);
      image.VerticalAlignment = VerticalAlignment.Center;
      return image;
    }
    internal static StackPanel CreateNameStackPanel()
    {
      var stack = new StackPanel { Orientation = Orientation.Horizontal };
      stack.SetValue(Panel.ZIndexProperty, 3);
      stack.SetValue(Canvas.LeftProperty, 4.0);
      return stack;
    }

    internal static Rectangle CreateRectangle(string colorResource, double opacity)
    {
      var rectangle = new Rectangle();
      rectangle.SetValue(Panel.ZIndexProperty, 1);
      rectangle.Opacity = opacity;
      rectangle.SetResourceReference(Rectangle.FillProperty, colorResource);
      return rectangle;
    }

    internal static Rectangle CreateRectangle(Color color, double opacity)
    {
      var rectangle = new Rectangle();
      rectangle.SetValue(Panel.ZIndexProperty, 1);
      rectangle.Opacity = opacity;
      rectangle.Fill = CreateBrush(color);
      return rectangle;
    }

    internal static void CreateTitleRow(Brush brush, Canvas canvas, out Rectangle rect, out StackPanel panel, out TextBlock block,
      out StackPanel damagePanel, out TextBlock damageBlock)
    {
      rect = CreateRectangle("OverlayCurrentBrush", DATA_OPACITY);
      canvas.Children.Add(rect);

      panel = CreateNameStackPanel();
      block = CreateTextBlock();
      block.Foreground = brush;
      panel.Children.Add(block);
      canvas.Children.Add(panel);

      damagePanel = CreateDamageStackPanel();
      damageBlock = CreateTextBlock();
      damagePanel.Children.Add(damageBlock);
      canvas.Children.Add(damagePanel);
    }

    internal static void LoadSettings(List<Color> colors, out bool hideOthers, out bool showCritRate, out string selectedClass, out int damageMode,
      out int fontSize, out int maxRows, out string width, out string height, out string top, out string left)
    {
      // dimensions
      width = ConfigUtil.GetSetting("OverlayWidth");
      height = ConfigUtil.GetSetting("OverlayHeight");
      top = ConfigUtil.GetSetting("OverlayTop");
      left = ConfigUtil.GetSetting("OverlayLeft");

      // Hide other player names on overlay
      hideOthers = ConfigUtil.IfSet("HideOverlayOtherPlayers");

      // Hide/Show crit rate
      showCritRate = ConfigUtil.IfSet("ShowOverlayCritRate");

      // selected class
      selectedClass = EQLogParser.Resource.ANY_CLASS;
      var savedClass = ConfigUtil.GetSetting("SelectedOverlayClass");
      if (!string.IsNullOrEmpty(savedClass) && PlayerManager.Instance.GetClassList().Contains(savedClass))
      {
        selectedClass = savedClass;
      }

      // fonts
      var fontSizeString = ConfigUtil.GetSetting("OverlayFontSize");
      fontSize = 13;
      if (fontSizeString != null && int.TryParse(fontSizeString, out fontSize) && fontSize < 6 && fontSize > 25)
      {
        fontSize = 13;
      }

      // Max Rows
      var maxRowsString = ConfigUtil.GetSetting("MaxOverlayRows");
      maxRows = 5;
      if (!string.IsNullOrEmpty(maxRowsString) && int.TryParse(maxRowsString, out int max) && max >= 5 && max <= 10)
      {
        maxRows = max;
      }

      // damage mode
      damageMode = ConfigUtil.GetSettingAsInteger("OverlayDamageMode");

      // load defaults
      colors.Add((Color)ColorConverter.ConvertFromString("#2e7d32"));
      colors.Add((Color)ColorConverter.ConvertFromString("#01579b"));
      colors.Add((Color)ColorConverter.ConvertFromString("#006064"));
      colors.Add((Color)ColorConverter.ConvertFromString("#673ab7"));
      colors.Add((Color)ColorConverter.ConvertFromString("#37474f"));
      colors.Add((Color)ColorConverter.ConvertFromString("#37474f"));
      colors.Add((Color)ColorConverter.ConvertFromString("#37474f"));
      colors.Add((Color)ColorConverter.ConvertFromString("#37474f"));
      colors.Add((Color)ColorConverter.ConvertFromString("#37474f"));
      colors.Add((Color)ColorConverter.ConvertFromString("#37474f"));

      for (int i = 0; i < colors.Count; i++)
      {
        try
        {
          string name = ConfigUtil.GetSetting(string.Format("OverlayRankColor{0}", i + 1));
          if (!string.IsNullOrEmpty(name) && ColorConverter.ConvertFromString(name) is Color color)
          {
            colors[i] = color; // override
          }
        }
        catch (FormatException ex)
        {
          LOG.Error("Invalid Overlay Color", ex);
        }
      }
    }

    internal static void SetVisible(Canvas overlayCanvas, bool visible)
    {
      overlayCanvas.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

      foreach (var child in overlayCanvas.Children)
      {
        var element = child as FrameworkElement;
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
      }
    }
  }
}
