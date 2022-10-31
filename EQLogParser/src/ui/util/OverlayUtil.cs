using FontAwesome5;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace EQLogParser
{
  class OverlayUtil
  {
    internal const double OPACITY = 0.55;
    internal const double DATA_OPACITY = 0.8;
    internal static readonly SolidColorBrush TEXTBRUSH = new SolidColorBrush(Colors.White);
    internal static readonly SolidColorBrush UPBRUSH = new SolidColorBrush(Colors.White);
    internal static readonly SolidColorBrush DOWNBRUSH = new SolidColorBrush(Colors.Red);
    internal static readonly SolidColorBrush TITLEBRUSH = new SolidColorBrush(Color.FromRgb(254, 156, 30));
    private static bool IsDamageOverlayEnabled = false;
    private static OverlayWindow Overlay = null;

    private OverlayUtil()
    {

    }

    internal static bool LoadSettings() => IsDamageOverlayEnabled = ConfigUtil.IfSet("IsDamageOverlayEnabled");

    internal static void CloseOverlay()
    {
      if (Overlay != null)
      {
        Overlay.Visibility = Visibility.Hidden;
      }
    }

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
        if (Overlay == null)
        {
          Overlay = new OverlayWindow(configure);
          Overlay.Show();
        }
        else
        {
          if (configure)
          {
            Overlay.SetConfigure();
          }
          else
          {
            Overlay.SetActive();
          }

          Overlay.Visibility = Visibility.Visible;
        }
      }, System.Windows.Threading.DispatcherPriority.Send);

      if (saveFirst)
      {
        ConfigUtil.Save();
      }
    }

    internal static void ResetOverlay()
    {
      CloseOverlay();
      DataManager.Instance.ResetOverlayFights();
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

    internal static Button CreateButton(string tooltip, string content, double size)
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
      button.FontSize = size;
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
  }
}
