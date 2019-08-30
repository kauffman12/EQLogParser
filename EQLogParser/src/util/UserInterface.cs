using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace EQLogParser
{
  public class ComboBoxItemTemplateSelector : DataTemplateSelector
  {
    public List<DataTemplate> SelectedItemTemplates { get; } = new List<DataTemplate>();
    public List<DataTemplate> DropDownItemTemplates { get; } = new List<DataTemplate>();

    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
      return GetVisualParent<ComboBoxItem>(container) == null ? ChooseFrom(SelectedItemTemplates, item) : ChooseFrom(DropDownItemTemplates, item);
    }

    private static DataTemplate ChooseFrom(IEnumerable<DataTemplate> templates, object item)
    {
      DataTemplate result = null;

      if (item != null)
      {
        var targetType = item.GetType();
        result = templates.FirstOrDefault(t => (t.DataType as Type) == targetType);
      }

      return result;
    }

    private static T GetVisualParent<T>(DependencyObject child) where T : Visual
    {
      while (child != null && !(child is T))
      {
        child = VisualTreeHelper.GetParent(child);
      }

      return child as T;
    }
  }

  public static class TextBoxBehavior
  {
    public static readonly DependencyProperty TripleClickSelectAllProperty = DependencyProperty.RegisterAttached("TripleClickSelectAll", typeof(bool), typeof(TextBoxBehavior), new PropertyMetadata(false, OnPropertyChanged));

    private static void OnPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      if (d is TextBox tb)
      {
        var enable = (bool)e.NewValue;
        if (enable)
        {
          tb.PreviewMouseLeftButtonDown += OnTextBoxMouseDown;
        }
        else
        {
          tb.PreviewMouseLeftButtonDown -= OnTextBoxMouseDown;
        }
      }
    }

    private static void OnTextBoxMouseDown(object sender, MouseButtonEventArgs e)
    {
      if (e.ClickCount == 3)
      {
        ((TextBox)sender).SelectAll();
      }
    }

    public static void SetTripleClickSelectAll(DependencyObject element, bool value)
    {
      element?.SetValue(TripleClickSelectAllProperty, value);
    }

    public static bool GetTripleClickSelectAll(DependencyObject element)
    {
      return (bool)element?.GetValue(TripleClickSelectAllProperty);
    }
  }

  public class DateTimeConverter : IValueConverter
  {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
      if (value?.GetType() == typeof(double))
      {
        return DateUtil.FormatSimpleDate(System.Convert.ToDouble(value, CultureInfo.CurrentCulture));
      }
      return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
      double result = 0;
      if (value is string)
      {
        result = DateUtil.ParseSimpleDate((string)value);
      }
      return result;
    }
  }

  public class ZeroConverter : IValueConverter
  {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
      if (value?.GetType() == typeof(uint) || value?.GetType() == typeof(double))
      {
        return System.Convert.ToDouble(value, CultureInfo.CurrentCulture) > 0 ? value.ToString() : "-";
      }
      return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
      if (value is string)
      {
        if (!double.TryParse((string)value, out double decValue))
        {
          decValue = 0;
        }
        return decValue;
      }
      return 0;
    }
  }
}