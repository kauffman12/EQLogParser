using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EQLogParser
{
  static class UIElementUtil
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly string[] CommonFontFamilies =
    {
      "Arial", "Calibri", "Cambria", "Cascadia Code", "Century Gothic", "Lucida Sans",
      "Open Sans", "Segoe UI", "Tahoma", "Roboto", "Helvetica"
    };

    internal static void CreateImage(Dispatcher dispatcher, FrameworkElement content, Label titleLabel = null)
    {
      Task.Delay(100).ContinueWith((task) => dispatcher.InvokeAsync(() =>
      {
        var wasHidden = content.Visibility != Visibility.Visible;
        content.Visibility = Visibility.Visible;

        var titlePadding = 0;
        var titleHeight = 0;
        var titleWidth = 0;
        if (titleLabel != null)
        {
          titlePadding = (int)titleLabel.Padding.Top + (int)titleLabel.Padding.Bottom;
          titleHeight = (int)titleLabel.ActualHeight - titlePadding - 4;
          titleWidth = (int)titleLabel.DesiredSize.Width;
        }

        var height = (int)content.ActualHeight + titleHeight + titlePadding;
        var width = (int)content.ActualWidth;

        var dpiScale = GetDpi();
        var rtb = new RenderTargetBitmap(width, height + 20, dpiScale, dpiScale, PixelFormats.Pbgra32);

        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
          var brush = Application.Current.Resources["ContentBackground"] as SolidColorBrush;
          ctx.DrawRectangle(brush, null, new Rect(new Point(0, 0), new Size(width, height + 20)));

          if (titleLabel != null)
          {
            var titleBrush = new VisualBrush(titleLabel);
            ctx.DrawRectangle(titleBrush, null, new Rect(new Point(4, titlePadding / 2), new Size(titleWidth, titleHeight)));
          }

          var chartBrush = new VisualBrush(content);
          ctx.DrawRectangle(chartBrush, null, new Rect(new Point(0, titleHeight + titlePadding), new Size(width, height - titleHeight)));
        }

        rtb.Render(dv);
        Clipboard.SetImage(rtb);

        if (wasHidden)
        {
          content.Visibility = Visibility.Hidden;
        }
      }), TaskScheduler.Default);
    }

    internal static ReadOnlyCollection<FontFamily> GetSystemFontFamilies()
    {
      var systemFontFamilies = new List<FontFamily>();
      foreach (var fontFamily in Fonts.SystemFontFamilies)
      {
        try
        {
          // trigger the exception
          var unused = fontFamily.FamilyNames;

          // add the font if it didn't throw
          systemFontFamilies.Add(fontFamily);
        }
        catch (ArgumentException e)
        {
          // certain fonts cause WPF 4 to throw an exception when the FamilyNames property is accessed; ignore them
          LOG.Debug(e);
        }
      }

      return systemFontFamilies.OrderBy(f => f.Source).ToList().AsReadOnly();
    }

    internal static ReadOnlyCollection<string> GetCommonFontFamilyNames()
    {
      var common = new List<string>();
      foreach (var fontFamily in GetSystemFontFamilies())
      {
        if (CommonFontFamilies.Contains(fontFamily.Source))
        {
          common.Add(fontFamily.Source);
        }
      }
      return common.OrderBy(name => name).ToList().AsReadOnly();
    }

    internal static double GetDpi()
    {
      var dpi = 96.0;
      var source = PresentationSource.FromVisual(Application.Current.MainWindow);
      if (source != null)
      {
        var matrix = source.CompositionTarget.TransformToDevice;
        dpi = 96.0 * matrix.M11; // DPI X value
      }
      else
      {
        var dpiTransform = VisualTreeHelper.GetDpi(Application.Current.MainWindow);
        dpi = dpiTransform.PixelsPerInchX; // DPI X value
      }

      return 96.0; // workaround since I think the framework is scaling for us. This was breaking with 4K displays (120 DPI)
    }

    internal static void CheckHideTitlePanel(Panel titlePanel, Panel optionsPanel)
    {
      var settingsLoc = optionsPanel.PointToScreen(new Point(0, 0));
      var titleLoc = titlePanel.PointToScreen(new Point(0, 0));

      if ((titleLoc.X + titlePanel.ActualWidth) > (settingsLoc.X + 10))
      {
        titlePanel.Visibility = Visibility.Hidden;
      }
      else
      {
        titlePanel.Visibility = Visibility.Visible;
      }
    }

    internal static void ClearMenuEvents(ItemCollection collection, RoutedEventHandler func)
    {
      foreach (var item in collection)
      {
        if (item is MenuItem m)
        {
          m.Click -= func;
        }
      }
    }

    internal static void SetComboBoxTitle(ComboBox columns, int count, string value, bool hasSelectAll = false)
    {
      if (columns.Items.Count == 0)
      {
        columns.SelectedIndex = -1;
      }
      else
      {
        if (!(columns.SelectedItem is ComboBoxItemDetails selected))
        {
          selected = hasSelectAll ? columns.Items[2] as ComboBoxItemDetails : columns.Items[0] as ComboBoxItemDetails;
        }

        var total = hasSelectAll ? columns.Items.Count - 2 : columns.Items.Count;
        var countString = total == count ? "All" : count.ToString();
        var text = countString + " " + value + ((total == count) ? "" : " Selected");
        if (text[0] == '0')
        {
          text = "No" + text.Substring(1);
        }
        selected.SelectedText = text;
        columns.SelectedIndex = -1;
        columns.SelectedItem = selected;
      }
    }

    internal static void SetEnabled(UIElementCollection collection, bool isEnabled)
    {
      foreach (var child in collection)
      {
        if (child is UIElement elem)
        {
          elem.IsEnabled = isEnabled;
        }
      }
    }

    internal static void SetSize(FrameworkElement element, double height, double width)
    {
      if (!double.IsNaN(height) && element.Height != height)
      {
        element.Height = height;
      }

      if (!double.IsNaN(width) && element.Width != width)
      {
        element.Width = width;
      }
    }
  }
}
