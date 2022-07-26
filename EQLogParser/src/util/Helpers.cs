using Syncfusion.Windows.Tools.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EQLogParser
{
  class Helpers
  {
    internal static DictionaryAddHelper<long, int> LongIntAddHelper = new DictionaryAddHelper<long, int>();

    public static void AddAction(List<ActionBlock> blockList, IAction action, double beginTime)
    {
      if (blockList.LastOrDefault() is ActionBlock last && last.BeginTime == beginTime)
      {
        last.Actions.Add(action);
      }
      else
      {
        var newSegment = new ActionBlock() { BeginTime = beginTime };
        newSegment.Actions.Add(action);
        blockList.Add(newSegment);
      }
    }

    internal static void CreateImage(Dispatcher dispatcher, FrameworkElement content, Label titleLabel = null)
    {
      Task.Delay(100).ContinueWith((task) => dispatcher.InvokeAsync(() =>
      {
        var wasHidden = content.Visibility != Visibility.Visible;
        content.Visibility = Visibility.Visible;

        int titlePadding = 0;
        int titleHeight = 0;
        int titleWidth = 0;
        if (titleLabel != null)
        {
          titlePadding = (int)titleLabel.Padding.Top + (int)titleLabel.Padding.Bottom;
          titleHeight = (int)titleLabel.ActualHeight - titlePadding - 4;
          titleWidth = (int)titleLabel.DesiredSize.Width;
        }

        var height = (int)content.ActualHeight + (int)titleHeight + (int)titlePadding;
        var width = (int)content.ActualWidth;

        var dpiScale = VisualTreeHelper.GetDpi(content);
        RenderTargetBitmap rtb = new RenderTargetBitmap(width, height + 20, dpiScale.PixelsPerInchX, dpiScale.PixelsPerInchY, PixelFormats.Pbgra32);

        DrawingVisual dv = new DrawingVisual();
        using (DrawingContext ctx = dv.RenderOpen())
        {
          var brush = Application.Current.Resources["ContentBackground"] as SolidColorBrush;
          ctx.DrawRectangle(brush, null, new Rect(new Point(0, 0), new Size(width, height + 20)));

          if (titleLabel != null)
          {
            var titleBrush = new VisualBrush(titleLabel);
            ctx.DrawRectangle(titleBrush, null, new Rect(new Point(4, titlePadding / 2), new Size(titleWidth, titleHeight)));
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
      }), TaskScheduler.Default);
    }

    internal static void LoadDictionary(string path)
    {
      var dict = new ResourceDictionary
      {
        Source = new Uri(path, UriKind.RelativeOrAbsolute)
      };

      foreach (var key in dict.Keys)
      {
        Application.Current.Resources[key] = dict[key];
      }
    }

    internal static void CloseWindow(DockingManager dockSite, ContentControl window)
    {
      if (window != null)
      {
        var state = (DockingManager.GetState(window) == DockState.Hidden) ? DockState.Dock : DockState.Hidden;
        if (state == DockState.Hidden && window?.Tag as string != "Hide")
        {
          if (dockSite.Children.Contains(window))
          {
            dockSite.Children.Remove(window);
          }
          else if (dockSite.DocContainer != null && dockSite.DocContainer.Items.Contains(window))
          {
            dockSite.DocContainer.Items.Remove(window);
          }

          (window.Content as IDisposable)?.Dispose();
        }
        else
        {
          DockingManager.SetState(window, state);
        }
      }
    }

    internal static bool OpenWindow(DockingManager dockSite, Dictionary<string, ContentControl> opened, out ContentControl window,
      Type type = null, string key = "", string title = "")
    {
      bool nowOpen = false;
      window = null;

      if (opened != null && opened.TryGetValue(key, out ContentControl control))
      {
        CloseWindow(dockSite, control);
      }
      else if (type != null)
      {
        var instance = Activator.CreateInstance(type);
        window = new ContentControl { Name = key };
        DockingManager.SetHeader(window, title);
        DockingManager.SetState(window, DockState.Document);
        window.Content = instance;
        dockSite.Children.Add(window);
        nowOpen = true;
      }

      return nowOpen;
    }

    internal static bool OpenChart(Dictionary<string, ContentControl> opened, DockingManager dockSite, string key, List<string> choices,
      string title, DocumentTabControl tabControl, bool includePets, out ContentControl window)
    {
      bool nowOpen = false;
      window = null;

      if (opened != null && opened.TryGetValue(key, out ContentControl control))
      {
        CloseWindow(dockSite, control);
      }
      else
      {
        var chart = new LineChart(choices, includePets);
        window = new ContentControl { Name = key };
        DockingManager.SetHeader(window, title);
        DockingManager.SetState(window, DockState.Document);
        window.Content = chart;

        if (dockSite.DocContainer.Items.Count == 0)
        {
          dockSite.Children.Add(window);
        }
        else if (tabControl == null || tabControl.Items.Count == 0)
        {
          dockSite.CreateHorizontalTabGroup(window);
        }
        else
        {
          tabControl.Container.AddElementToTabGroup(tabControl, window);
        }

        nowOpen = true;
      }

      return nowOpen;
    }

    internal static string CreateRecordKey(string type, string subType)
    {
      string key = subType;

      if (type == Labels.DD || type == Labels.DOT)
      {
        key = type + "=" + key;
      }

      return key;
    }
  }

  internal class DictionaryUniqueListHelper<T1, T2>
  {
    internal int AddToList(Dictionary<T1, List<T2>> dict, T1 key, T2 value)
    {
      int size = 0;
      lock (dict)
      {
        if (!dict.ContainsKey(key))
        {
          dict[key] = new List<T2>();
        }

        if (!dict[key].Contains(value))
        {
          dict[key].Add(value);
          size = dict[key].Count;
        }
      }
      return size;
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
          dict[key] = default;
        }

        dynamic temp = dict[key];
        temp += value;
        dict[key] = temp;
      }
    }
  }
}
