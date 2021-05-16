using ActiproSoftware.Windows.Controls.Docking;
using LiveCharts.Wpf;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EQLogParser
{
  class Helpers
  {
    internal static DictionaryAddHelper<long, int> LongIntAddHelper = new DictionaryAddHelper<long, int>();
    private static readonly SortableNameComparer TheSortableNameComparer = new SortableNameComparer();
    private static readonly SortablePetMappingComparer TheSortablePetMappingComparer = new SortablePetMappingComparer();

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

    internal static void CopyImage(Dispatcher dispatcher, FrameworkElement content, FrameworkElement title = null)
    {
      Task.Delay(100).ContinueWith((task) => dispatcher.InvokeAsync(() =>
      {
        var wasHidden = content.Visibility != Visibility.Visible;
        content.Visibility = Visibility.Visible;

        var titleHeight = title?.ActualHeight ?? 0;
        var titleWidth = title?.ActualWidth ?? 0;
        var height = (int)content.ActualHeight + (int)titleHeight;
        var width = (int)content.ActualWidth;

        var dpiScale = VisualTreeHelper.GetDpi(content);
        RenderTargetBitmap rtb = new RenderTargetBitmap(width, height, dpiScale.PixelsPerInchX, dpiScale.PixelsPerInchY, PixelFormats.Pbgra32);

        DrawingVisual dv = new DrawingVisual();
        using (DrawingContext ctx = dv.RenderOpen())
        {
          var grayBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2d2d30"));
          ctx.DrawRectangle(grayBrush, null, new Rect(new Point(0, 0), new Size(width, height)));

          if (title != null)
          {
            var titleBrush = new VisualBrush(title);
            ctx.DrawRectangle(titleBrush, null, new Rect(new Point(0, 0), new Size(titleWidth, titleHeight)));
          }

          var chartBrush = new VisualBrush(content);
          ctx.DrawRectangle(chartBrush, null, new Rect(new Point(0, titleHeight), new Size(width, height - titleHeight)));
        }

        rtb.Render(dv);
        Clipboard.SetImage(rtb);

        if (wasHidden)
        {
          content.Visibility = Visibility.Hidden;
        }
      }), TaskScheduler.Default);
    }

    internal static void ChartResetView(CartesianChart theChart)
    {
      theChart.AxisY[0].MaxValue = double.NaN;
      theChart.AxisY[0].MinValue = 0;
      theChart.AxisX[0].MinValue = double.NaN;
      theChart.AxisX[0].MaxValue = double.NaN;
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

    internal static void InsertPetMappingIntoSortedList(PetMapping mapping, ObservableCollection<PetMapping> collection)
    {
      int index = collection.ToList().BinarySearch(mapping, TheSortablePetMappingComparer);
      if (index < 0)
      {
        collection.Insert(~index, mapping);
      }
      else
      {
        collection.Insert(index, mapping);
      }
    }

    internal static DocumentWindow OpenWindow(DockSite dockSite, DocumentWindow window, Type type, string key, string title)
    {
      if (window?.IsOpen == true)
      {
        window.Close();
        window = null;
      }
      else
      {
        var instance = Activator.CreateInstance(type);
        window = new DocumentWindow(dockSite, key, title, null, instance);
        OpenWindow(window);
      }

      return window;
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

    internal static void HandleChartUpdate(Dispatcher dispatcher, DocumentWindow window, DataPointEvent e)
    {
      dispatcher.InvokeAsync(() =>
      {
        if (window?.IsOpen == true)
        {
          (window.Content as LineChart)?.HandleUpdateEvent(e);
        }
      });
    }

    internal static void RepositionCharts(DocumentWindow window, params DocumentWindow[] charts)
    {
      if (window.ParentContainer is TabbedMdiContainer tabControl)
      {
        bool moved = false;
        foreach (var child in tabControl.Windows.Reverse().ToList())
        {
          if (charts.Contains(child))
          {
            if (child.IsOpen && !moved)
            {
              moved = true;
              child.MoveToNewHorizontalContainer();
            }
            else if (child.IsOpen && moved)
            {
              child.MoveToNextContainer();
            }

            (child.Content as LineChart)?.FixSize();
          }
        }
      }
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

    private class SortableNameComparer : IComparer<SortableName>
    {
      public int Compare(SortableName x, SortableName y) => string.CompareOrdinal(x?.Name, y?.Name);
    }

    private class SortablePetMappingComparer : IComparer<PetMapping>
    {
      public int Compare(PetMapping x, PetMapping y) => string.CompareOrdinal(x?.Owner, y?.Owner);
    }
  }

  internal class DictionaryListHelper<T1, T2>
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
