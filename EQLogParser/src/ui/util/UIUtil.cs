using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Dynamic;
using System.Linq;
using System.Reflection;
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
    internal static readonly SolidColorBrush DefaultBrush = new(Colors.Gray);
    internal static readonly SortableNameComparer TheSortableNameComparer = new();
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly SortablePetMappingComparer TheSortablePetMappingComparer = new();
    private static readonly ConcurrentDictionary<string, SolidColorBrush> BrushCache = new();

    static UiUtil()
    {
      DefaultBrush.Freeze();
    }

    internal static void SetClipboardText(string text)
    {
      if (text != null)
      {
        _ = InvokeAsync(() =>
        {
          try
          {
            Clipboard.SetText(text);
          }
          catch (Exception ex)
          {
            Log.Error($"Failed to set Clipboard Text: {ex.Message}");
          }
        }, DispatcherPriority.DataBind);
      }
    }

    internal static DispatcherTimer CreateTimer(EventHandler tickHandler, int interval, bool start, DispatcherPriority priority = DispatcherPriority.Normal)
    {
      var timer = new DispatcherTimer(priority) { Interval = TimeSpan.FromMilliseconds(interval) };
      timer.Tick += tickHandler;
      return timer;
    }

    internal static dynamic InsertNameIntoSortedList(string name, ObservableCollection<object> collection, bool isPlayer = false)
    {
      var entry = new ExpandoObject() as dynamic;
      entry.Name = name;

      var index = collection.ToList().BinarySearch(entry, TheSortableNameComparer);
      if (index < 0)
      {
        collection.Insert(~index, entry);
      }
      else
      {
        entry = collection[index];
      }

      if (isPlayer)
      {
        entry.PlayerClass = PlayerManager.Instance.GetPlayerClass(name);
      }

      return entry;
    }

    internal static void InsertPetMappingIntoSortedList(PetMapping mapping, ObservableCollection<PetMapping> collection)
    {
      var index = collection.ToList().BinarySearch(mapping, TheSortablePetMappingComparer);
      if (index < 0)
      {
        collection.Insert(~index, mapping);
      }
      else
      {
        collection.Insert(index, mapping);
      }
    }

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

    internal static SolidColorBrush GetBrush(Color color)
    {
      var hex = color.ToHexString();
      return GetBrush(hex);
    }

    // return a static brush for the given color
    internal static SolidColorBrush GetBrush(string color, bool useDefault = true)
    {
      SolidColorBrush brush = null;

      try
      {
        if (!string.IsNullOrEmpty(color) && !BrushCache.TryGetValue(color, out brush))
        {

          brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
          BrushCache[color] = brush;
          brush.Freeze();
        }
      }
      catch (Exception)
      {
        // ignore errors in brush conversion
      }

      if (brush == null && useDefault)
      {
        brush = DefaultBrush;
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

    internal static async Task InvokeAsync(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
    {
      if (Application.Current?.Dispatcher is { } dispatcher)
      {
        await dispatcher.InvokeAsync(action, priority);
      }
    }

    internal static async Task InvokeAsync(Func<Task> asyncAction, DispatcherPriority priority = DispatcherPriority.Normal)
    {
      if (Application.Current?.Dispatcher is { } dispatcher)
      {
        await dispatcher.InvokeAsync(async () =>
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

    private class SortablePetMappingComparer : IComparer<PetMapping>
    {
      public int Compare(PetMapping x, PetMapping y)
      {
        return string.CompareOrdinal(x?.Owner, y?.Owner);
      }
    }

    internal class SortableNameComparer : IComparer<object>
    {
      public int Compare(object x, object y)
      {
        return string.CompareOrdinal(((dynamic)x)?.Name, ((dynamic)y)?.Name);
      }
    }
  }
}
