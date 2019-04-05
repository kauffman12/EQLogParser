using ActiproSoftware.Windows.Controls.Docking;
using LiveCharts.Wpf;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace EQLogParser
{
  internal class TimedActionComparer : IComparer<TimedAction>
  {
    public int Compare(TimedAction x, TimedAction y)
    {
      return x.BeginTime.CompareTo(y.BeginTime);
    }
  }

  internal class ReverseTimedActionComparer : IComparer<TimedAction>
  {
    public int Compare(TimedAction x, TimedAction y)
    {
      return y.BeginTime.CompareTo(x.BeginTime);
    }
  }

  internal class ZeroConverter : IValueConverter
  {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
      if (value.GetType() == typeof(double))
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

  internal class ComboBoxItemTemplateSelector : DataTemplateSelector
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

  class Helpers
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal static TimedActionComparer TimedActionComparer = new TimedActionComparer();
    internal static ReverseTimedActionComparer ReverseTimedActionComparer = new ReverseTimedActionComparer();
    internal static ConcurrentDictionary<string, string> SpellAbbrvCache = new ConcurrentDictionary<string, string>();
    internal static DictionaryAddHelper<long, int> LongIntAddHelper = new DictionaryAddHelper<long, int>();
    internal static DictionaryAddHelper<string, uint> StringUIntAddHelper = new DictionaryAddHelper<string, uint>();
    private static readonly SortableNameComparer TheSortableNameComparer = new SortableNameComparer();

    internal static string AbbreviateSpellName(string spell)
    {
      if (!SpellAbbrvCache.TryGetValue(spell, out string result))
      {
        result = spell;

        int index = -1;
        if ((index = spell.IndexOf(" Rk. ", StringComparison.Ordinal)) > -1)
        {
          result = spell.Substring(0, index);
        }
        else if ((index = spell.LastIndexOf(" ", StringComparison.Ordinal)) > -1)
        {
          bool isARank = true;
          for (int i = index + 1; i < spell.Length && isARank; i++)
          {
            switch (spell[i])
            {
              case 'I':
              case 'V':
              case 'X':
              case 'L':
              case 'C':
              case '0':
              case '1':
              case '2':
              case '3':
              case '4':
              case '5':
              case '6':
              case '7':
              case '8':
              case '9':
                break;
              default:
                isARank = false;
                break;
            }
          }

          if (isARank)
          {
            result = spell.Substring(0, index);
          }
        }

        SpellAbbrvCache[spell] = result;
      }

      return string.Intern(result);
    }

    internal static void ChartResetView(CartesianChart theChart)
    {
      theChart.AxisY[0].MaxValue = double.NaN;
      theChart.AxisY[0].MinValue = 0;
      theChart.AxisX[0].MinValue = double.NaN;
      theChart.AxisX[0].MaxValue = double.NaN;
    }

    internal static void DataGridSelectAll(object sender)
    {
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      DataGrid callingDataGrid = menu.PlacementTarget as DataGrid;
      callingDataGrid.SelectAll();
    }

    internal static void DataGridUnselectAll(object sender)
    {
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      DataGrid callingDataGrid = menu.PlacementTarget as DataGrid;
      callingDataGrid.UnselectAll();
    }

    internal static void InsertNameIntoSortedList(string name, ObservableCollection<SortableName> collection)
    {
      var entry = new SortableName() { Name = string.Intern(name) };
      int index = collection.ToList().BinarySearch(entry, TheSortableNameComparer);
      if (index < 0)
      {
        collection.Insert(~index, entry);
      }
      else
      {
        collection.Insert(index, entry);
      }
    }

    internal static void OpenWindow(DockingWindow window)
    {
      if (!window.IsOpen)
      {
        window.IsOpen = true;
        if (!window.IsActive)
        {
          window.Activate();
        }
      }
      else
      {
        window.Close();
      }
    }

    internal static List<string> ReadList(string fileName)
    {
      List<string> result = new List<string>();

      try
      {
        if (File.Exists(fileName))
        {
          result.AddRange(File.ReadAllLines(fileName));
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

      return result;
    }

    internal static void SaveList(string fileName, List<string> list)
    {
      try
      {
        File.WriteAllLines(fileName, list);
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
    }

    internal static DocumentWindow OpenNewTab(DockSite dockSite, string id, string title, object content, double width = 0, double height = 0)
    {
      var window = new DocumentWindow(dockSite, id, title, null, content);

      if (width != 0 && height != 0)
      {
        window.ContainerDockedSize = new Size(width, height);
      }

      OpenWindow(window);
      window.MoveToLast();
      return window;
    }

    internal static bool IsPossiblePlayerName(string part, int stop = -1)
    {
      if (stop == -1)
      {
        stop = part.Length;
      }

      bool found = stop < 3 ? false : true;
      for (int i = 0; found != false && i < stop; i++)
      {
        if (!char.IsLetter(part, i))
        {
          found = false;
          break;
        }
      }

      return found;
    }

    private class SortableNameComparer : IComparer<SortableName>
    {
      public int Compare(SortableName x, SortableName y)
      {
        return string.CompareOrdinal(x.Name, y.Name);
      }
    }
  }

  internal class DictionaryListHelper<T1, T2>
  {
    internal void AddToList(Dictionary<T1, List<T2>> dict, T1 key, T2 value)
    {
      lock (dict)
      {
        if (!dict.ContainsKey(key))
        {
          dict[key] = new List<T2>();
        }

        if (!dict[key].Contains(value))
        {
          dict[key].Add(value);
        }
      }
    }
  }

  internal class DictionaryAddHelper<T1, T2>
  {
    internal void Add(Dictionary<T1, T2> dict, T1 key, T2 value)
    {
      lock (dict)
      {
        if (!dict.ContainsKey(key))
        {
          dict[key] = default(T2);
        }
      }

      lock (key)
      {
        dynamic temp = dict[key];
        temp += value;
        dict[key] = temp;
      }
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
      element.SetValue(TripleClickSelectAllProperty, value);
    }

    public static bool GetTripleClickSelectAll(DependencyObject element)
    {
      return (bool)element.GetValue(TripleClickSelectAllProperty);
    }
  }
}
