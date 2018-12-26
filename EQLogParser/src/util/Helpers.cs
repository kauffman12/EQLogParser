using LiveCharts;
using LiveCharts.Wpf;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EQLogParser
{
  class Helpers
  {
    internal static string AbbreviateSpellName(string spell)
    {
      string result = spell;

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

      return result;
    }

    internal static void ChartResetView(CartesianChart theChart)
    {
      theChart.AxisY[0].MaxValue = double.NaN;
      theChart.AxisY[0].MinValue = 0;
      theChart.AxisX[0].MinValue = double.NaN;
      theChart.AxisX[0].MaxValue = double.NaN;
    }

    internal static SeriesCollection CreateLineChartSeries(Dictionary<string, List<long>> playerValues = null)
    {
      var seriesCollection = new SeriesCollection();
      Dictionary<string, LineSeries> seriesPerPlayer = new Dictionary<string, LineSeries>();
      foreach (string player in playerValues.Keys)
      {
        if (player != null && !seriesPerPlayer.ContainsKey(player))
        {
          seriesPerPlayer[player] = new LineSeries();
          seriesPerPlayer[player].Title = player;
          seriesPerPlayer[player].Values = new ChartValues<long>();
          seriesCollection.Add(seriesPerPlayer[player]);
        }

        seriesPerPlayer[player].Values.AddRange(playerValues[player].Cast<object>());
      }

      return seriesCollection;
    }

    internal static long ParseLong(string str)
    {
      long y = 0;
      for (int i = 0; i < str.Length; i++)
      {
        if (!Char.IsDigit(str[i]))
        {
          return long.MaxValue;
        }

        y = y * 10 + (str[i] - '0');
      }
      return y;
    }

    internal static void OpenWindow(ActiproSoftware.Windows.Controls.Docking.DockingWindow window)
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

    internal static string FormatDateTime(DateTime dateTime)
    {
      return dateTime.ToString("MMM dd HH:mm:ss");
    }

    internal static string FormatTimeSpan(TimeSpan diff)
    {
      string result = "Inactivity > ";
      if (diff.Days >= 1)
      {
        result += diff.Days + " days";
      }
      else if (diff.Hours >= 1)
      {
        result += diff.Hours + " hours";
      }
      else
      {
        result += diff.Minutes + " minutes";
      }

      return result;
    }

    internal static string FormatDamage(long total)
    {
      string result;

      if (total < 1000)
      {
        result = total.ToString();
      }
      else if (total < 1000000)
      {
        result = Math.Round((decimal)total / 1000, 1) + "K";
      }
      else
      {
        result = Math.Round((decimal)total / 1000 / 1000, 1) + "M";
      }

      return result;
    }

    internal static bool IsPetOrMount(string part, int start, out int len)
    {
      bool found = false;
      len = -1;

      int end = 2;
      if (part.Length >= (start + ++end) && part.Substring(start, 3) == "pet" ||
        part.Length >= (start + ++end) && part.Substring(start, 4) == "ward" && !(part.Length > (start + 5) && part[start + 5] != 'e') ||
        part.Length >= (start + ++end) && part.Substring(start, 5) == "Mount" ||
        part.Length >= (start + ++end) && part.Substring(start, 6) == "warder")
      {
        found = true;
        len = end;
      }
      return found;
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
  }

  internal class DateUtil
  {
    // counting this thing is really slow
    private String LastDateTimeString = "";
    private DateTime LastDateTime;

    internal bool HasTimeInRange(DateTime now, string line, int lastMins)
    {
      bool found = false;
      if (line.Length > 24)
      {
        DateTime dateTime = ParseDate(line.Substring(1, 24));
        TimeSpan diff = now.Subtract(dateTime);
        found = (diff.TotalMinutes < lastMins);
      }
      return found;
    }

    internal DateTime ParseDate(string timeString)
    {
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

      LastDateTime = dateTime;
      LastDateTimeString = timeString;
      return dateTime;
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
      if (!dict.ContainsKey(key))
      {
        dict[key] = default(T2);
      }

      dynamic temp = dict[key];
      temp += value;
      dict[key] = temp;
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
      return (bool)element.GetValue(TripleClickSelectAllProperty);
    }
  }
}
