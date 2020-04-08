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
using System.Windows.Threading;

namespace EQLogParser
{
  class Helpers
  {
    internal static ConcurrentDictionary<string, string> SpellAbbrvCache = new ConcurrentDictionary<string, string>();
    internal static DictionaryAddHelper<long, int> LongIntAddHelper = new DictionaryAddHelper<long, int>();
    private static readonly SortableNameComparer TheSortableNameComparer = new SortableNameComparer();
    private static Dispatcher MainDispatcher;

    internal static void SetDispatcher(Dispatcher mainDispatcher)
    {
      MainDispatcher = mainDispatcher;
    }

    internal static string AbbreviateSpellName(string spell)
    {
      if (!SpellAbbrvCache.TryGetValue(spell, out string result))
      {
        result = spell;
        int index;
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

    internal static void SetBusy(bool state)
    {
      MainDispatcher.InvokeAsync(() => (Application.Current.MainWindow as MainWindow)?.Busy(state));
    }

    internal static string ToLower(string name)
    {
      return char.ToLower(name[0], CultureInfo.CurrentCulture) + name.Substring(1);
    }

    internal static string ToUpper(string name)
    {
      return char.ToUpper(name[0], CultureInfo.CurrentCulture) + name.Substring(1);
    }

    internal static void ChartResetView(CartesianChart theChart)
    {
      theChart.AxisY[0].MaxValue = double.NaN;
      theChart.AxisY[0].MinValue = 0;
      theChart.AxisX[0].MinValue = double.NaN;
      theChart.AxisX[0].MaxValue = double.NaN;
    }

    internal static void DataGridSelectAll(FrameworkElement sender)
    {
      if (sender?.Parent is ContextMenu menu)
      {
        (menu.PlacementTarget as DataGrid)?.SelectAll();
      }
    }

    internal static void DataGridUnselectAll(FrameworkElement sender)
    {
      if (sender?.Parent is ContextMenu menu)
      {
        (menu.PlacementTarget as DataGrid)?.UnselectAll();
      }
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
      public int Compare(SortableName x, SortableName y)
      {
        return string.CompareOrdinal(x.Name, y.Name);
      }
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
