using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for SpellCountGrid.xaml
  /// </summary>
  public partial class SpellCountTable : UserControl
  {
    private List<string> PlayerList;
    private SpellCounts TheSpellCounts;
    private ObservableCollection<SpellCountRow> SpellRowsView = new ObservableCollection<SpellCountRow>();
    private List<string> CountTypes = new List<string>() { "Spells By Count", "Spells By Percent" };
    private int CurrentCountType = 0;

    public SpellCountTable()
    {
      InitializeComponent();

      spellCountDataGrid.Sorting += (s, e2) =>
      {
        if (e2.Column.Header != null && (e2.Column.Header.ToString() != ""))
        {
          e2.Column.SortDirection = e2.Column.SortDirection ?? ListSortDirection.Ascending;
        }
      };

      spellCountDataGrid.ItemsSource = SpellRowsView;
      countType.ItemsSource = CountTypes;
      countType.SelectedIndex = 0;
    }

    public void ShowSpells(List<PlayerStats> selectedStats, CombinedStats currentStats)
    {
      PlayerList = new List<string>();
      foreach (var stats in selectedStats)
      {
        string name = stats.Name;
        if (currentStats.Children.ContainsKey(stats.Name) && currentStats.Children[stats.Name].Count > 1)
        {
          name = currentStats.Children[stats.Name].First().Name;
        }

        PlayerList.Add(name);
      }

      var raidStats = currentStats.RaidStats;
      DateTime start = raidStats.BeginTimes.First();
      DateTime end = raidStats.LastTimes.Last();
      TheSpellCounts = SpellCountBuilder.GetSpellCounts(PlayerList, start.AddSeconds(-10), end);
      Display();
    }

    private void Display()
    {
      if (TheSpellCounts != null)
      {
        Dispatcher.InvokeAsync(() =>
        {
          spellCountDataGrid.Columns.Add(new DataGridTextColumn()
          {
            Header = "",
            Binding = new Binding("Spell"),
            CellStyle = Application.Current.Resources["SpellGridCellStyle"] as Style
          });
        });

        int totalCasts = TheSpellCounts.UniqueSpellCounts.Values.Sum();
        int colCount = 0;
        foreach (string name in TheSpellCounts.SortedPlayers)
        {
          string colBinding = "Values[" + colCount + "]"; // dont use colCount directory since it will change during Dispatch
          double total = TheSpellCounts.TotalCountMap.ContainsKey(name) ? TheSpellCounts.TotalCountMap[name] : 0;

          if (CurrentCountType == 1)
          {
            total = Math.Round(total / totalCasts * 100, 2);
          }

          Dispatcher.InvokeAsync(() =>
          {
            DataGridTextColumn col = new DataGridTextColumn() { Header = name + " = " + total, Binding = new Binding(colBinding) };
            col.CellStyle = Application.Current.Resources["RightAlignGridCellStyle"] as Style;
            col.HeaderStyle = Application.Current.Resources["BrightCenterGridHeaderStyle"] as Style;
            spellCountDataGrid.Columns.Add(col);
          });

          Thread.Sleep(5);
          colCount++;
        }

        string totalHeader = CurrentCountType == 0 ? "Total Count = " + totalCasts : "Percent of Casts";
        Dispatcher.InvokeAsync(() =>
        {
          DataGridTextColumn col = new DataGridTextColumn() { Header = totalHeader, Binding = new Binding("Values[" + colCount + "]") };
          col.CellStyle = Application.Current.Resources["RightAlignGridCellStyle"] as Style;
          col.HeaderStyle = Application.Current.Resources["BrightCenterGridHeaderStyle"] as Style;
          spellCountDataGrid.Columns.Add(col);
        });

        foreach (var spell in TheSpellCounts.SpellList)
        {
          SpellCountRow row = new SpellCountRow() { Spell = spell, Values = new double[TheSpellCounts.SortedPlayers.Count + 1] };
          row.IsReceived = spell.StartsWith("Received");

          int i;
          for (i = 0; i < TheSpellCounts.SortedPlayers.Count; i++)
          {
            if (TheSpellCounts.PlayerCountMap.ContainsKey(TheSpellCounts.SortedPlayers[i]))
            {
              if (TheSpellCounts.PlayerCountMap[TheSpellCounts.SortedPlayers[i]].ContainsKey(spell))
              {
                if (CurrentCountType == 0)
                {
                  row.Values[i] = TheSpellCounts.PlayerCountMap[TheSpellCounts.SortedPlayers[i]][spell];
                }
                else
                {
                  row.Values[i] = Math.Round((double)TheSpellCounts.PlayerCountMap[TheSpellCounts.SortedPlayers[i]][spell] /
                    TheSpellCounts.TotalCountMap[TheSpellCounts.SortedPlayers[i]] * 100, 2);
                }
              }
              else
              {
                row.Values[i] = CurrentCountType == 0 ? 0 : 0.0;
              }
            }
          }

          row.Values[i] = CurrentCountType == 0 ? TheSpellCounts.UniqueSpellCounts[spell] : Math.Round((double) TheSpellCounts.UniqueSpellCounts[spell] / totalCasts * 100, 2);
          Dispatcher.InvokeAsync(() => SpellRowsView.Add(row));
          Thread.Sleep(5);
        }
      }
    }

    private void CountType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (SpellRowsView.Count > 0)
      {
        SpellRowsView.Clear();
      }

      if (spellCountDataGrid.Columns.Count > 0)
      {
        spellCountDataGrid.Columns.Clear();
      }

      CurrentCountType = (sender as ComboBox).SelectedIndex;
      Display();
    }
  }
}
