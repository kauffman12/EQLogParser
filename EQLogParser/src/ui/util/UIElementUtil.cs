using log4net;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
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

    internal static readonly BitmapImage BrokenIcon = new(new Uri(@"pack://application:,,,/icons/broken.png"));

    // Attached property to store the original icon source string (including eqsprite:// URIs)
    // This is needed because WPF can't load from custom URI schemes, so we store the original
    // URI string here and use it to recreate the bitmap when needed.
    public static readonly DependencyProperty OriginalIconSourceProperty =
      DependencyProperty.RegisterAttached("OriginalIconSource", typeof(string), typeof(UiElementUtil), new PropertyMetadata(null));

    public static string GetOriginalIconSource(DependencyObject obj) => (string)obj.GetValue(OriginalIconSourceProperty);
    public static void SetOriginalIconSource(DependencyObject obj, string value) => obj.SetValue(OriginalIconSourceProperty, value);

    internal static BitmapImage CreateBitmap(string path)
    {
      if (string.IsNullOrEmpty(path)) return null;

      // Support custom eqsprite URI format: eqsprite://path/to/sheet.tga/col/row
      // This uses standard URI format but we parse it manually since WPF doesn't support custom URI schemes.
      // The format allows us to store sprite references in BitmapImage.UriSource for persistence.
      if (path.StartsWith("eqsprite://", StringComparison.OrdinalIgnoreCase))
      {
        try
        {
          // Remove the scheme prefix and split on forward slashes
          var pathWithoutScheme = path.Substring("eqsprite://".Length);
          var parts = pathWithoutScheme.Split('/');
          
          // Format: eqsprite://C:/path/to/sheet.tga/col/row
          // We need at least 3 parts: [...path segments..., col, row]
          if (parts.Length >= 3)
          {
            // Last two parts are col and row
            var colStr = parts[^2];
            var rowStr = parts[^1];
            
            // Everything before the last two parts is the sheet path
            var sheet = string.Join("/", parts[0..^2]);
            
            if (int.TryParse(colStr, out var col) && int.TryParse(rowStr, out var row))
            {
              BitmapSource sheetBmp = null;
              if (sheet.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
              {
                sheetBmp = TgaLoader.Load(sheet);
              }
              else
              {
                sheetBmp = new BitmapImage(new Uri(sheet, UriKind.Absolute));
              }

              if (sheetBmp != null)
              {
                var rect = new Int32Rect(col * 40, row * 40, 40, 40);
                var cropped = new CroppedBitmap(sheetBmp, rect);
                // convert to BitmapImage via encoder
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(cropped));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                ms.Seek(0, SeekOrigin.Begin);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                // Store the eqsprite URI in attached property for persistence
                // We can't use UriSource because WPF will try to load from it and fail (custom scheme)
                SetOriginalIconSource(bmp, path);
                // Don't freeze - frozen bitmaps don't trigger PropertyChanged in bindings properly
                // bmp.Freeze();
                return bmp;
              }
            }
          }
        }
        catch (Exception ex)
        {
          Log.Error($"Error processing eqsprite URI: {path}", ex);
        }
        
        // if eqsprite URI format failed, return null
        return null;
      }

      // Regular file path - with broken icon fallback and attached property
      if (!File.Exists(path)) return BrokenIcon;

      try
      {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        // Also store the file path in attached property for consistency
        SetOriginalIconSource(bitmap, path);
        return bitmap;
      }
      catch (Exception ex)
      {
        Log.Error($"Error creating bitmap from path: {path}", ex);
        return BrokenIcon;
      }
    }

    internal static async Task CreateImage(Dispatcher dispatcher, FrameworkElement content, Label titleLabel = null)
    {
      await Task.Delay(150);

      await dispatcher.InvokeAsync(() =>
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
      });
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

    internal static void SetComboBoxTitle(ComboBox columns, string value, bool hasSelectAll = false)
    {
      if (columns.Items.Count == 0)
      {
        columns.SelectedIndex = -1;
      }
      else
      {
        if (columns.SelectedItem is not ComboBoxItemDetails selected)
        {
          selected = columns.Items[0] as ComboBoxItemDetails;
        }

        var start = hasSelectAll ? 2 : 0;
        var count = 0;
        for (var i = start; i < columns.Items.Count; i++)
        {
          if (columns.Items[i] is ComboBoxItemDetails details && details.IsChecked == true)
          {
            count++;
          }
        }

        var total = hasSelectAll ? columns.Items.Count - 2 : columns.Items.Count;
        var countString = total == count ? "All" : count.ToString(CultureInfo.InvariantCulture);
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

    internal static void PreviewSelectAllComboBox(ComboBox combo, ComboBoxItemDetails updated, int total)
    {
      if (updated.Text == "Unselect All")
      {
        if (updated.IsChecked == false) Toggle("Unselect All", false);
      }
      else if (updated.Text == "Select All")
      {
        if (updated.IsChecked == false) Toggle("Unselect All", true);
      }
      else
      {
        if (updated.IsChecked == true && combo.Items[0] is ComboBoxItemDetails { } selectAll && selectAll.IsChecked == true)
        {
          selectAll.IsChecked = false;
          updated.IsChecked = !updated.IsChecked;
          combo.Items.Refresh();
        }
        else if (updated.IsChecked == false && combo.Items[1] is ComboBoxItemDetails { } unselectAll && unselectAll.IsChecked == true)
        {
          unselectAll.IsChecked = false;
          updated.IsChecked = !updated.IsChecked;
          combo.Items.Refresh();
        }
        else
        {
          var count = 0;
          for (var i = 2; i < combo.Items.Count; i++)
          {
            if (combo.Items[i] is ComboBoxItemDetails { } classItem)
            {
              if (updated != classItem)
              {
                if (classItem.IsChecked == true)
                {
                  count++;
                }
              }
              else if (updated.IsChecked == false)
              {
                count++;
              }
            }
          }

          if (count == 0)
          {
            if (combo.Items[1] is ComboBoxItemDetails { } unselectAll2 && unselectAll2.IsChecked == false)
            {
              unselectAll2.IsChecked = true;
              updated.IsChecked = !updated.IsChecked;
              combo.Items.Refresh();
            }
          }
          else if (count == total)
          {
            if (combo.Items[0] is ComboBoxItemDetails { } selectAll2 && selectAll2.IsChecked == false)
            {
              selectAll2.IsChecked = true;
              updated.IsChecked = !updated.IsChecked;
              combo.Items.Refresh();
            }
          }
        }
      }

      void Toggle(string current, bool value)
      {
        foreach (var item in combo.Items)
        {
          if (item is ComboBoxItemDetails { } classItem)
          {
            if (!classItem.Text.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
              classItem.IsChecked = value;
            }
            else
            {
              classItem.IsChecked = !value;
            }
          }
        }

        combo.Items.Refresh();
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

    internal static double CalculateTextBoxHeight(TextBox textBox, Window context = null)
    {
      var dpi = VisualTreeHelper.GetDpi(context ?? MainActions.GetOwner()).PixelsPerDip;
      return CalculateTextHeight(dpi, textBox.FontFamily, textBox.FontSize, textBox.Padding, textBox.BorderThickness);
    }

    internal static double CalculateTextBlockHeight(TextBlock textBlock, Window context = null)
    {
      var dpi = VisualTreeHelper.GetDpi(context ?? MainActions.GetOwner()).PixelsPerDip;
      return CalculateTextHeight(dpi, textBlock.FontFamily, textBlock.FontSize, textBlock.Padding);
    }

    internal static double CalculateTextHeight(double dpi, FontFamily fontFamily, double fontSize, Thickness? padding = null, Thickness? borderThickness = null)
    {
      // Create the FormattedText object
      var formattedText = new FormattedText(
        "test",
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        fontSize,
        Brushes.Black, // The brush doesn't affect size calculation
        dpi
      );

      // Calculate the height required for the text
      var textHeight = formattedText.Height;

      // Add padding and border thickness to the height
      var totalHeight = textHeight + (padding?.Top ?? 0) + (padding?.Bottom ?? 0) + (borderThickness?.Top ?? 0) + (borderThickness?.Bottom ?? 0);

      return Math.Round(totalHeight);
    }

    internal static double CalculateTextBlockWidth(TextBlock textBlock, Window context = null)
    {
      var dpi = VisualTreeHelper.GetDpi(context ?? MainActions.GetOwner()).PixelsPerDip;
      return CalculateTextWidth(dpi, textBlock.FontFamily, textBlock.FontSize, textBlock.Text, textBlock.Padding);
    }

    internal static double CalculateTextWidth(double dpi, FontFamily fontFamily, double fontSize, string value, Thickness? padding = null, Thickness? borderThickness = null)
    {
      // Create the FormattedText object
      var formattedText = new FormattedText(
        value,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        fontSize,
        Brushes.Black, // The brush doesn't affect size calculation
        dpi
      );

      // Calculate the width required for the text
      var textWidth = formattedText.Width;

      // Add padding and border thickness to the width
      var totalWidth = textWidth + (padding?.Left ?? 0) + (padding?.Right ?? 0) + (borderThickness?.Left ?? 0) + (borderThickness?.Right ?? 0);

      return Math.Round(totalWidth);
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
