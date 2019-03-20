using ActiproSoftware.Windows.Controls.Docking;
using LiveCharts.Wpf;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  public class ZeroConverter : IValueConverter
  {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
      if (value.GetType() == typeof(double))
      {
        return System.Convert.ToDouble(value) > 0 ? value.ToString() : "-";
      }
      return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
      if (value is string)
      {
        double decValue;
        if (!double.TryParse((string) value, out decValue))
        {
          decValue = 0;
        }
        return decValue;
      }
      return 0;
    }
  }

  class Helpers
  {
    private static SortableNameComparer TheSortableNameComparer = new SortableNameComparer();
    internal static ConcurrentDictionary<string, string> SpellAbbrvCache = new ConcurrentDictionary<string, string>();

    internal static string AbbreviateSpellName(string spell)
    {
      string result;
      if (!SpellAbbrvCache.TryGetValue(spell, out result))
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

    internal static DocumentWindow OpenChart(DockSite dockSite, DocumentWindow chartWindow, DockHost host, List<string> choices, string title)
    {
      DocumentWindow newChartWindow = chartWindow;

      if (chartWindow != null && chartWindow.IsOpen)
      {
        // just focus
        OpenWindow(chartWindow);
      }
      else
      {
        var lineChart = new LineChart(choices);
        newChartWindow = new DocumentWindow(dockSite, title, title, null, lineChart);

        OpenWindow(newChartWindow);
        newChartWindow.CanFloat = true;
        newChartWindow.CanClose = true;

        if (host != null)
        {
          newChartWindow.MoveToMdi(host);
          if (newChartWindow.CanMoveToNextContainer)
          {
            newChartWindow.MoveToNextContainer();
          }
        }
        else
        {
          newChartWindow.MoveToNewHorizontalContainer();
        }

        //newChartWindow.ContainerDockedSize = new Size(800, 50);
      }

      return newChartWindow;
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

    internal static uint ParseUInt(string str)
    {
      uint y = 0;
      for (int i = 0; i < str.Length; i++)
      {
        if (!char.IsDigit(str[i]))
        {
          return uint.MaxValue;
        }

        y = y * 10 + (Convert.ToUInt32(str[i]) - '0');
      }
      return y;
    }

    internal static void OpenWindow(DockingWindow window)
    {
      if (!window.IsOpen)
      {
        window.IsOpen = true;
      }
      else
      {
        window.Focus();
      }

      if (!window.IsActive)
      {
        window.Activate();
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
        if (!Char.IsLetter(part, i))
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
        return x.Name.CompareTo(y.Name);
      }
    }
  }

  internal class DateUtil
  {
    // counting this thing is really slow
    private string LastDateTimeString = "";
    private double LastDateTime;

    internal bool HasTimeInRange(double now, string line, int lastMins)
    {
      bool found = false;
      if (line.Length > 24)
      {
        double dateTime = ParseDate(line.Substring(1, 24));
        TimeSpan diff = TimeSpan.FromSeconds(now - dateTime);
        found = (diff.TotalMinutes < lastMins);
      }
      return found;
    }

    internal double ParseDate(string timeString)
    {
      double result = double.NaN;

      if (LastDateTimeString == timeString)
      {
        return LastDateTime;
      }

      DateTime dateTime;
      DateTime.TryParseExact(timeString, "ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dateTime);
      if (dateTime == DateTime.MinValue)
      {
        DateTime.TryParseExact(timeString, "ddd MMM  d HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dateTime);
      }

      if (dateTime == DateTime.MinValue)
      {
        LastDateTime = double.NaN;
      }
      else
      {
        result = LastDateTime = dateTime.Ticks / TimeSpan.FromSeconds(1).Ticks;
      }

      LastDateTimeString = timeString;
      return result;
    }
  }

  internal class DictionaryListHelper<T1, T2>
  {
    internal void AddToList(Dictionary<T1, List<T2>> dict, T1 key, T2 value)
    {
      lock(dict)
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
      lock(dict)
      {
        if (!dict.ContainsKey(key))
        {
          dict[key] = default(T2);
        }
      }

      lock(key)
      {
        dynamic temp = dict[key];
        temp += value;
        dict[key] = temp;
      }
    }
  }

  internal static class TextBoxBehavior
  {
    public static readonly DependencyProperty TripleClickSelectAllProperty = DependencyProperty.RegisterAttached("TripleClickSelectAll", typeof(bool), typeof(TextBoxBehavior), new PropertyMetadata(false, OnPropertyChanged));

    private static void OnPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      var tb = d as TextBox;
      if (tb != null)
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

    internal static void SetTripleClickSelectAll(DependencyObject element, bool value)
    {
      element.SetValue(TripleClickSelectAllProperty, value);
    }

    internal static bool GetTripleClickSelectAll(DependencyObject element)
    {
      return (bool) element.GetValue(TripleClickSelectAllProperty);
    }
  }
}
