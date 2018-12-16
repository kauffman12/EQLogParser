using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EQLogParser
{
  class Utils
  {
    // counting this thing is really slow
    private static int DateCount = 0;
    private static ConcurrentDictionary<string, DateTime> DateTimeCache = new ConcurrentDictionary<string, DateTime>();

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

    internal static DateTime ParseDate(string timeString)
    {
      DateTime dateTime;

      if (!DateTimeCache.ContainsKey(timeString))
      {
        DateTime.TryParseExact(timeString, "ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dateTime);
        if (dateTime == DateTime.MinValue)
        {
          DateTime.TryParseExact(timeString, "ddd MMM  d HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dateTime);
        }

        DateTimeCache.TryAdd(timeString, dateTime);
      }
      else
      {
        dateTime = DateTimeCache[timeString];
      }

      // dont let it get too big but it coudl be re-used between different log files
      if (DateTimeCache.TryAdd(timeString, dateTime))
      {
        DateCount++;
      }

      if (DateCount > 50000)
      {
        DateTimeCache.Clear();
        DateCount = 0;
      }

      return dateTime;
    }

    internal static bool HasTimeInRange(DateTime now, string line, int lastMins)
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
  }

  internal class DictionaryListHelper<T1, T2>
  {
    internal void AddToList(Dictionary<T1, List<T2>> dict, T1 key, T2 value)
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

    internal void AddToList(ConcurrentDictionary<T1, List<T2>> dict, T1 key, T2 value)
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
