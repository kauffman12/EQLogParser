using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace EQLogParser
{
  public static class ColorExtensions
  {
    public static string ToHexString(this Color color)
    {
      return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }
  }

  internal static class UiUtil
  {
    private static readonly ConcurrentDictionary<string, SolidColorBrush> BrushCache = new();

    internal static void UpdateObservable<T>(IEnumerable<T> source, ObservableCollection<T> dest)
    {
      var index = 0;
      foreach (var row in source)
      {
        if (dest.Count > index)
        {
          dest[index] = row;
        }
        else
        {
          dest.Add(row);
        }

        index++;
      }

      for (var i = dest.Count - 1; i >= index; i--)
      {
        dest.RemoveAt(index);
      }
    }

    internal static SolidColorBrush GetBrush(string color)
    {
      SolidColorBrush brush = null;
      if (!string.IsNullOrEmpty(color) && !BrushCache.TryGetValue(color, out brush))
      {
        brush = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString(color)! };
        BrushCache[color] = brush;
      }
      return brush;
    }

    internal static void InvokeNow(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
    {
      if (Application.Current?.Dispatcher is { } dispatcher)
      {
        if (dispatcher.CheckAccess())
        {
          action();
        }
        else
        {
          dispatcher.Invoke(action, priority);
        }
      }
    }

    internal static void InvokeAsync(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
    {
      if (Application.Current?.Dispatcher is { } dispatcher)
      {
        dispatcher.InvokeAsync(action, priority);
      }
    }

    internal static void InvokeAsync(Func<Task> asyncAction, DispatcherPriority priority = DispatcherPriority.Normal)
    {
      if (Application.Current?.Dispatcher is { } dispatcher)
      {
        dispatcher.InvokeAsync(async () =>
        {
          try
          {
            await asyncAction();
          }
          catch (Exception)
          {
            // ignore
          }
        }, priority);
      }
    }
  }
}
