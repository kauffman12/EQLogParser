using log4net;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EQLogParser
{
  internal static class UiElementUtil
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly string[] CommonFontFamilies =
    [
      "Arial", "Calibri", "Cambria", "Century Gothic", "Georgia", "Helvetica", "Lucida Sans",
      "Open Sans", "Segoe UI", "Roboto", "Tahoma", "Times New Roman", "Trebuchet MS", "Verdana"
    ];

    internal static BitmapImage CreateBitmap(string path)
    {
      if (!string.IsNullOrEmpty(path))
      {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        return bitmap;
      }

      return null;
    }

    internal static void CreateImage(Dispatcher dispatcher, FrameworkElement content, Label titleLabel = null)
    {
      Task.Delay(100).ContinueWith(_ => dispatcher.InvokeAsync(() =>
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
            ctx.DrawRectangle(titleBrush, null, new Rect(new Point(4, titlePadding / 2.0), new Size(titleWidth, titleHeight)));
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

    internal static List<string> GetFontWeights()
    {
      return [.. typeof(FontWeights).GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(FontWeight)).Select(p => p.Name)];
    }

    internal static FontWeight GetFontWeightByName(string name)
    {
      var property = typeof(FontWeights).GetProperty(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);

      if (property != null && property.PropertyType == typeof(FontWeight))
      {
        return (FontWeight)property.GetValue(null);
      }

      return FontWeights.Normal;
    }

    internal static ReadOnlyCollection<FontFamily> GetSystemFontFamilies()
    {
      var systemFontFamilies = new List<FontFamily>();
      foreach (var fontFamily in Fonts.SystemFontFamilies)
      {
        try
        {
          // trigger the exception
          _ = fontFamily.FamilyNames;

          // add the font if it didn't throw
          systemFontFamilies.Add(fontFamily);
        }
        catch (ArgumentException e)
        {
          // certain fonts cause WPF 4 to throw an exception when the FamilyNames property is accessed; ignore them
          Log.Debug(e);
        }
      }

      return systemFontFamilies.OrderBy(f => f.Source).ToList().AsReadOnly();
    }

    internal static ReadOnlyCollection<string> GetCommonFontFamilyNames()
    {
      var common = (from fontFamily in GetSystemFontFamilies() where CommonFontFamilies.Contains(fontFamily.Source) select fontFamily.Source).ToList();
      return common.OrderBy(name => name).ToList().AsReadOnly();
    }

    internal static double ParseFontSize(string fontSize)
    {
      if (!string.IsNullOrEmpty(fontSize) && fontSize.Split("pt") is { Length: 2 } split && double.TryParse(split[0], NumberStyles.Any,
            CultureInfo.InvariantCulture, out var newFontSize))
      {
        return newFontSize;
      }

      // original default
      return 12;
    }

    internal static double GetDpi()
    {
      // var dpiTransform = VisualTreeHelper.GetDpi(Application.Current.MainWindow);
      //dpi = dpiTransform.PixelsPerInchX; // DPI X value
      return 96.0; // workaround since I think the framework is scaling for us. This was breaking with 4K displays (120 DPI)
    }

    internal static void CheckHideTitlePanel(Panel titlePanel, Panel optionsPanel)
    {
      var settingsLoc = optionsPanel.PointToScreen(new Point(0, 0));
      var titleLoc = titlePanel.PointToScreen(new Point(0, 0));
      titlePanel.Visibility = (titleLoc.X + titlePanel.ActualWidth) > (settingsLoc.X + 10) ? Visibility.Hidden : Visibility.Visible;
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
        if (columns.SelectedItem is not ComboBoxItemDetails selected)
        {
          selected = hasSelectAll ? columns.Items[2] as ComboBoxItemDetails : columns.Items[0] as ComboBoxItemDetails;
        }

        var total = hasSelectAll ? columns.Items.Count - 2 : columns.Items.Count;
        var countString = total == count ? "All" : count.ToString();
        var text = countString + " " + value + ((total == count) ? "" : " Selected");
        if (text[0] == '0')
        {
          text = "No" + text[1..];
        }

        if (selected != null)
        {
          selected.SelectedText = text;
          columns.SelectedIndex = -1;
          columns.SelectedItem = selected;
        }
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

    internal static double CalculateTextBoxHeight(FontFamily fontFamily, double fontSize, Thickness padding, Thickness borderThickness)
    {
      // Create the FormattedText object
      var formattedText = new FormattedText(
        "test",
        System.Globalization.CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        fontSize,
        Brushes.Black, // The brush doesn't affect size calculation
        VisualTreeHelper.GetDpi(new Window()).PixelsPerDip // This ensures the text size is scaled correctly for the display DPI
      );

      // Calculate the height required for the text
      var textHeight = formattedText.Height;

      // Add padding and border thickness to the height
      var totalHeight = textHeight + padding.Top + padding.Bottom + borderThickness.Top + borderThickness.Bottom;

      return Math.Round(totalHeight);
    }

    internal static Style CloneStyle(Style originalStyle)
    {
      if (originalStyle == null)
      {
        return null;
      }

      // Create a new style based on the original style
      var newStyle = new Style(originalStyle.TargetType, originalStyle.BasedOn);

      // Clone each setter
      foreach (var setterBase in originalStyle.Setters)
      {
        var originalSetter = (Setter)setterBase;
        // Check if the value of the setter is a Style
        if (originalSetter.Value is Style nestedStyle)
        {
          // Recursively clone the nested style
          var clonedNestedStyle = CloneStyle(nestedStyle);
          // Create a new setter with the cloned nested style
          var newSetter = new Setter(originalSetter.Property, clonedNestedStyle);
          newStyle.Setters.Add(newSetter);
        }
        else
        {
          // If the value is not a Style, just clone the setter as is
          var newSetter = new Setter(originalSetter.Property, originalSetter.Value);
          newStyle.Setters.Add(newSetter);
        }
      }

      // Clone triggers (if any)
      foreach (var trigger in originalStyle.Triggers)
      {
        newStyle.Triggers.Add(trigger);
      }

      // Clone resources (if any)
      foreach (DictionaryEntry resource in originalStyle.Resources)
      {
        newStyle.Resources.Add(resource.Key, resource.Value);
      }

      return newStyle;
    }
  }
}
