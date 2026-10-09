using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Dynamic;
using System.Linq;
using System.Reflection;
using System.Threading;
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
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
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
      else
      {
        Log.Warn("Attempted to set Clipboard Text to null");
      }
    }

    internal static DispatcherTimer CreateTimer(EventHandler tickHandler, int interval, bool start, DispatcherPriority priority = DispatcherPriority.Normal)
    {
      var timer = new DispatcherTimer(priority) { Interval = TimeSpan.FromMilliseconds(interval) };
      timer.Tick += tickHandler;
      return timer;
    }

    /*
     * InsertNameIntoSortedList and InsertPetMappingIntoSortedList are deleted (2026-10-09) with the three windows that used them:
     * sorted ObservableCollection inserts of dynamic rows, one per registry sighting, to keep the retired Verified Players / Verified
     * Pets / Pet Owners grids in order. `ExpandoObject as dynamic` was also why those panes could not be typed or tested. The identity
     * pane's census replaces all three lists and reads typed rows straight out of the derive.
     */

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
        dest.RemoveAt(i);
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

    // Action (no result)
    internal static Task InvokeAsync(Action action, DispatcherPriority priority = DispatcherPriority.Normal, CancellationToken ct = default)
    {
      var dispatcher = Application.Current?.Dispatcher;
      if (dispatcher == null || dispatcher.CheckAccess())
      {
        action();
        return Task.CompletedTask;
      }

      return dispatcher.InvokeAsync(action, priority, ct).Task;
    }

    // Func<T> (sync result)
    internal static Task<T> InvokeAsync<T>(Func<T> func, DispatcherPriority priority = DispatcherPriority.Normal, CancellationToken ct = default)
    {
      var dispatcher = Application.Current?.Dispatcher;
      if (dispatcher == null || dispatcher.CheckAccess())
      {
        return Task.FromResult(func());
      }

      return dispatcher.InvokeAsync(func, priority, ct).Task;
    }

    // Func<Task> (async work)
    internal static Task InvokeAsync(Func<Task> asyncAction, DispatcherPriority priority = DispatcherPriority.Normal, CancellationToken ct = default)
    {
      var dispatcher = Application.Current?.Dispatcher;
      if (dispatcher == null || dispatcher.CheckAccess())
      {
        // Already on UI (or no dispatcher) — just run it here
        return asyncAction();
      }

      // Hop to UI to *start* the async action, then unwrap/await it
      return dispatcher.InvokeAsync(async () =>
      {
        await asyncAction().ConfigureAwait(false);
      }, priority, ct).Task;
    }

    /* Fire-and-forget post that cannot lose a failure: the callback's exception, plus a fault or a
     * cancellation of the dispatcher post itself, all reach the log. Use this instead of
     * `_ = InvokeAsync(...)`, which leaves them as unobserved task exceptions. */
    internal static void InvokeAsyncLogged(Action action, string context, DispatcherPriority priority = DispatcherPriority.Normal)
    {
      try
      {
        // NotOnRanToCompletion (not OnlyOnFaulted): a post cancelled by dispatcher shutdown never
        // runs and never faults, and a silently dropped UI callback is exactly the kind of thing a
        // bug report cannot explain. Runs for already-completed tasks too, so an inline failure is
        // observed as well. Cancellation only happens at teardown, hence Debug.
        _ = InvokeAsync(action, priority).ContinueWith(
          t =>
          {
            if (t.Exception is { } ex) Log.Error($"{context} (dispatcher callback)", ex.GetBaseException());
            else if (t.IsCanceled) Log.Debug($"{context}: dispatcher post cancelled (shutting down?)");
          },
          CancellationToken.None,
          TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
          TaskScheduler.Default);
      }
      catch (Exception ex)
      {
        // no dispatcher / ran inline and threw before a task existed
        Log.Error($"{context} (inline)", ex);
      }
    }


  }
}
