using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EQLogParser
{
  class UIElementUtil
  {
    internal static double GetDpi()
    {
      var dpi = 96.0;
      PresentationSource source = PresentationSource.FromVisual(Application.Current.MainWindow);
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
        string countString = total == count ? "All" : count.ToString();
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
