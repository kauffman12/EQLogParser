using System;
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

  internal static class UIUtil
  {
    internal static void InvokeAsync(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
    {
      if (Application.Current?.Dispatcher is Dispatcher dispatcher)
      {
        if (dispatcher.CheckAccess())
        {
          action();
        }
        else
        {
          dispatcher.InvokeAsync(action, priority);
        }
      }
    }
  }
}
