using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private List<string> PlayerList;
    private SpellCountData TheSpellCounts;
    private ObservableCollection<SpellCountRow> SpellRowsView = new ObservableCollection<SpellCountRow>();
    private DictionaryAddHelper<string, int> AddHelper = new DictionaryAddHelper<string, int>();
    private List<string> CastTypes = new List<string>() { "Cast And Received", "Cast Spells", "Received Spells" };
    private List<string> CountTypes = new List<string>() { "Spells By Count", "Spells By Percent" };
    private List<string> MinFreqs = new List<string>() { "Any Freq", "Freq > 1", "Freq > 2", "Freq > 3", "Freq > 4" };
    private List<string> SpellTypes = new List<string>() { "Any Type", "Beneficial", "Detrimental" };
    private int CurrentCastType = 0;
    private int CurrentCountType = 0;
    private int CurrentMinFreqCount = 0;
    private int CurrentSpellType = 0;
    private MainWindow TheMainWindow;
    private static bool running = false;

    public SpellCountTable(MainWindow mainWindow, string title)
    {
      InitializeComponent();
      TheMainWindow = mainWindow;
      titleLabel.Content = title;

      spellCountDataGrid.Sorting += (s, e2) =>
      {
        if (e2.Column.Header != null && (e2.Column.Header.ToString() != ""))
        {
          e2.Column.SortDirection = e2.Column.SortDirection ?? ListSortDirection.Ascending;
        }
      };

      spellCountDataGrid.ItemsSource = SpellRowsView;
      castTypes.ItemsSource = CastTypes;
      castTypes.SelectedIndex = 0;
      countTypes.ItemsSource = CountTypes;
      countTypes.SelectedIndex = 0;
      minFreqList.ItemsSource = MinFreqs;
      minFreqList.SelectedIndex = 0;
      spellTypes.ItemsSource = SpellTypes;
      spellTypes.SelectedIndex = 0;
    }

    public void ShowSpells(List<PlayerStats> selectedStats, CombinedDamageStats currentStats)
    {
      if (selectedStats != null && currentStats != null)
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

        TheSpellCounts = SpellCountBuilder.GetSpellCounts(PlayerList, currentStats.RaidStats);
        Display();
      }
    }

    public void ShowSpells(List<PlayerStats> selectedStats, CombinedHealStats currentStats)
    {
      if (selectedStats != null && currentStats != null)
      {
        PlayerList = new List<string>();
        foreach (var stats in selectedStats)
        {
          PlayerList.Add(stats.Name);
        }

        TheSpellCounts = SpellCountBuilder.GetSpellCounts(PlayerList, currentStats.RaidStats);
        Display();
      }
    }

    private void Display()
    {
      if (running == false)
      {
        running = true;
        Dispatcher.InvokeAsync(() =>
        {
          castTypes.IsEnabled = countTypes.IsEnabled = minFreqList.IsEnabled = false;
          TheMainWindow.Busy(true);
        });

        Task.Delay(20).ContinueWith(task =>
        {
          try
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

              Dictionary<string, Dictionary<string, int>> filteredPlayerMap = new Dictionary<string, Dictionary<string, int>>();
              Dictionary<string, int> totalCountMap = new Dictionary<string, int>();
              Dictionary<string, int> uniqueSpellsMap = new Dictionary<string, int>();

              int totalCasts = 0;
              PlayerList.ForEach(player =>
              {
                filteredPlayerMap[player] = new Dictionary<string, int>();

                if ((CurrentCastType == 0 || CurrentCastType == 1) && TheSpellCounts.PlayerCastCounts.ContainsKey(player))
                {
                  foreach (string id in TheSpellCounts.PlayerCastCounts[player].Keys)
                  {
                    totalCasts = UpdateMaps(id, player, TheSpellCounts.PlayerCastCounts[player][id], TheSpellCounts.MaxCastCounts,
                      totalCountMap, uniqueSpellsMap, filteredPlayerMap, false, totalCasts);
                  }
                }

                if ((CurrentCastType == 0 || CurrentCastType == 2) && TheSpellCounts.PlayerReceivedCounts.ContainsKey(player))
                {
                  foreach (string id in TheSpellCounts.PlayerReceivedCounts[player].Keys)
                  {
                    totalCasts = UpdateMaps(id, player, TheSpellCounts.PlayerReceivedCounts[player][id], TheSpellCounts.MaxReceivedCounts,
                      totalCountMap, uniqueSpellsMap, filteredPlayerMap, true, totalCasts);
                  }
                }
              });

              List<string> sortedPlayers = totalCountMap.Keys.OrderByDescending(key => totalCountMap[key]).ToList();
              List<string> sortedSpellList = uniqueSpellsMap.Keys.OrderByDescending(key => uniqueSpellsMap[key]).ToList();

              int colCount = 0;
              foreach (string name in sortedPlayers)
              {
                string colBinding = "Values[" + colCount + "]"; // dont use colCount directory since it will change during Dispatch
                double total = totalCountMap.ContainsKey(name) ? totalCountMap[name] : 0;
                string header = name + " = " + ((CurrentCountType == 0) ? total.ToString() : Math.Round(total / totalCasts * 100, 2).ToString());

                Dispatcher.InvokeAsync(() =>
                {
                  DataGridTextColumn col = new DataGridTextColumn() { Header = header, Binding = new Binding(colBinding) };
                  col.CellStyle = Application.Current.Resources["RightAlignGridCellStyle"] as Style;
                  col.HeaderStyle = Application.Current.Resources["BrightCenterGridHeaderStyle"] as Style;
                  spellCountDataGrid.Columns.Add(col);
                });

                Thread.Sleep(5);
                colCount++;
              }

              string totalHeader = CurrentCountType == 0 ? "Total Count = " + totalCasts : "Percent of Total (" + totalCasts + ")";
              Dispatcher.InvokeAsync(() =>
              {
                DataGridTextColumn col = new DataGridTextColumn() { Header = totalHeader, Binding = new Binding("Values[" + colCount + "]") };
                col.CellStyle = Application.Current.Resources["RightAlignGridCellStyle"] as Style;
                col.HeaderStyle = Application.Current.Resources["BrightCenterGridHeaderStyle"] as Style;
                spellCountDataGrid.Columns.Add(col);
              });

              foreach (var spell in sortedSpellList)
              {
                SpellCountRow row = new SpellCountRow() { Spell = spell, Values = new double[sortedPlayers.Count + 1] };
                row.IsReceived = spell.StartsWith("Received");

                int i;
                for (i = 0; i < sortedPlayers.Count; i++)
                {
                  if (filteredPlayerMap.ContainsKey(sortedPlayers[i]))
                  {
                    if (filteredPlayerMap[sortedPlayers[i]].ContainsKey(spell))
                    {
                      if (CurrentCountType == 0)
                      {
                        row.Values[i] = filteredPlayerMap[sortedPlayers[i]][spell];
                      }
                      else
                      {
                        row.Values[i] = Math.Round((double)filteredPlayerMap[sortedPlayers[i]][spell] /
                          totalCountMap[sortedPlayers[i]] * 100, 2);
                      }
                    }
                    else
                    {
                      row.Values[i] = CurrentCountType == 0 ? 0 : 0.0;
                    }
                  }
                }

                row.Values[i] = CurrentCountType == 0 ? uniqueSpellsMap[spell] : Math.Round((double)uniqueSpellsMap[spell] / totalCasts * 100, 2);
                Dispatcher.InvokeAsync(() => SpellRowsView.Add(row));
                Thread.Sleep(5);
              }
            }
          }
          catch (Exception ex)
          {
            LOG.Error(ex);
          }
          finally
          {
            Dispatcher.InvokeAsync(() =>
            {
              castTypes.IsEnabled = countTypes.IsEnabled = minFreqList.IsEnabled = true;
              TheMainWindow.Busy(false);
            });
            running = false;
          }
        });
      }
    }

    private int UpdateMaps(string id, string player, int playerCount, Dictionary<string, int> maxCounts, Dictionary<string, int> totalCountMap,
      Dictionary<string, int> uniqueSpellsMap, Dictionary<string, Dictionary<string, int>> filteredPlayerMap, bool received, int totalCasts)
    {
      int updatedCount = totalCasts;
      var spellData = TheSpellCounts.UniqueSpells[id];

      if (CurrentSpellType == 0 || (CurrentSpellType == 1 && spellData.Beneficial) || (CurrentSpellType == 2 && !spellData.Beneficial))
      {
        string name = spellData.SpellAbbrv;

        if (received)
        {
          name = "Received " + name;
        }

        if (maxCounts[id] > CurrentMinFreqCount)
        {
          AddHelper.Add(totalCountMap, player, playerCount);
          AddHelper.Add(uniqueSpellsMap, name, playerCount);
          AddHelper.Add(filteredPlayerMap[player], name, playerCount);
          totalCasts += playerCount;
        }
      }

      return totalCasts;
    }

    private void Options_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (SpellRowsView.Count > 0)
      {
        SpellRowsView.Clear();
      }

      if (spellCountDataGrid.Columns.Count > 0)
      {
        spellCountDataGrid.Columns.Clear();
      }

      CurrentCastType = castTypes.SelectedIndex;
      CurrentCountType = countTypes.SelectedIndex;
      CurrentMinFreqCount = minFreqList.SelectedIndex;
      CurrentSpellType = spellTypes.SelectedIndex;
      Display();
    }
  }
}
