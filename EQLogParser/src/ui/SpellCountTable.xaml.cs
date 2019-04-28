using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
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

    private static bool Running = false;

    private List<string> PlayerList;
    private SpellCountData TheSpellCounts;
    private ObservableCollection<SpellCountRow> SpellRowsView = new ObservableCollection<SpellCountRow>();
    private DictionaryAddHelper<string, uint> AddHelper = new DictionaryAddHelper<string, uint>();
    private List<string> CastTypes = new List<string>() { "Cast And Received", "Cast Spells", "Received Spells" };
    private List<string> CountTypes = new List<string>() { "Spells By Count", "Spells By Percent" };
    private List<string> MinFreqs = new List<string>() { "Any Freq", "Freq > 1", "Freq > 2", "Freq > 3", "Freq > 4" };
    private List<string> SpellTypes = new List<string>() { "Any Type", "Beneficial", "Detrimental" };
    private int CurrentCastType = 0;
    private int CurrentCountType = 0;
    private int CurrentMinFreqCount = 0;
    private int CurrentSpellType = 0;
    private bool CurrentShowSelfOnly = false;
    private bool Ready = false;

    public SpellCountTable(string title)
    {
      InitializeComponent();
      titleLabel.Content = title;

      dataGrid.Sorting += (s, e2) =>
      {
        if (!string.IsNullOrEmpty(e2.Column.Header as string))
        {
          e2.Column.SortDirection = e2.Column.SortDirection ?? ListSortDirection.Ascending;
        }
      };

      dataGrid.ItemsSource = SpellRowsView;
      castTypes.ItemsSource = CastTypes;
      castTypes.SelectedIndex = 0;
      countTypes.ItemsSource = CountTypes;
      countTypes.SelectedIndex = 0;
      minFreqList.ItemsSource = MinFreqs;
      minFreqList.SelectedIndex = 0;
      spellTypes.ItemsSource = SpellTypes;
      spellTypes.SelectedIndex = 0;
      Ready = true;
    }

    public void ShowSpells(List<PlayerStats> selectedStats, CombinedStats currentStats)
    {
      var childStats = currentStats?.Children;
      var raidStats = currentStats?.RaidStats;

      if (selectedStats != null && raidStats != null)
      {
        PlayerList = new List<string>();
        foreach (var stats in selectedStats)
        {
          string name = stats.Name;
          if (childStats != null && childStats.ContainsKey(stats.Name) && childStats[stats.Name].Count > 1)
          {
            name = childStats[stats.Name].First().Name;
          }

          PlayerList.Add(name);
        }

        TheSpellCounts = SpellCountBuilder.GetSpellCounts(PlayerList, raidStats);
        Display();
      }
    }

    private void Display()
    {
      if (Running == false)
      {
        Running = true;
        Dispatcher.InvokeAsync(() =>
        {
          castTypes.IsEnabled = countTypes.IsEnabled = minFreqList.IsEnabled = false;
          (Application.Current.MainWindow as MainWindow).Busy(true);
        });

        Task.Delay(20).ContinueWith(task =>
        {
          try
          {
            if (TheSpellCounts != null)
            {
              Dispatcher.InvokeAsync(() =>
              {
                dataGrid.Columns.Add(new DataGridTextColumn()
                {
                  Header = "",
                  Binding = new Binding("Spell"),
                  CellStyle = Application.Current.Resources["SpellGridCellStyle"] as Style
                });
              });

              Dictionary<string, Dictionary<string, uint>> filteredPlayerMap = new Dictionary<string, Dictionary<string, uint>>();
              Dictionary<string, uint> totalCountMap = new Dictionary<string, uint>();
              Dictionary<string, uint> uniqueSpellsMap = new Dictionary<string, uint>();

              uint totalCasts = 0;
              PlayerList.ForEach(player =>
              {
                filteredPlayerMap[player] = new Dictionary<string, uint>();

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
                string header = name + " = " + ((CurrentCountType == 0) ? total.ToString(CultureInfo.CurrentCulture) : Math.Round(total / totalCasts * 100, 2).ToString(CultureInfo.CurrentCulture));

                Dispatcher.InvokeAsync(() =>
                {
                  DataGridTextColumn col = new DataGridTextColumn() { Header = header, Binding = new Binding(colBinding) };
                  col.CellStyle = Application.Current.Resources["RightAlignGridCellStyle"] as Style;
                  col.HeaderStyle = Application.Current.Resources["BrightCenterGridHeaderStyle"] as Style;
                  dataGrid.Columns.Add(col);
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
                dataGrid.Columns.Add(col);
              });

              foreach (var spell in sortedSpellList)
              {
                SpellCountRow row = new SpellCountRow() { Spell = spell, Values = new double[sortedPlayers.Count + 1] };
                row.IsReceived = spell.StartsWith("Received", StringComparison.Ordinal);

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
            throw;
          }
          finally
          {
            Dispatcher.InvokeAsync(() =>
            {
              castTypes.IsEnabled = countTypes.IsEnabled = minFreqList.IsEnabled = true;
              (Application.Current.MainWindow as MainWindow).Busy(false);
            });

            Running = false;
          }
        }, TaskScheduler.Default);
      }
    }

    private uint UpdateMaps(string id, string player, uint playerCount, Dictionary<string, uint> maxCounts, Dictionary<string, uint> totalCountMap,
      Dictionary<string, uint> uniqueSpellsMap, Dictionary<string, Dictionary<string, uint>> filteredPlayerMap, bool received, uint totalCasts)
    {
      var spellData = TheSpellCounts.UniqueSpells[id];
      if ((CurrentSpellType == 0 || (CurrentSpellType == 1 && spellData.Beneficial) || (CurrentSpellType == 2 && !spellData.Beneficial)) 
        && (!received || CurrentShowSelfOnly == true || spellData.LandsOnOther.Length > 0))
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

    private void OptionsChanged()
    {
      if (Ready)
      {
        if (SpellRowsView.Count > 0)
        {
          SpellRowsView.Clear();
        }

        if (dataGrid.Columns.Count > 0)
        {
          dataGrid.Columns.Clear();
        }

        CurrentCastType = castTypes.SelectedIndex;
        CurrentCountType = countTypes.SelectedIndex;
        CurrentMinFreqCount = minFreqList.SelectedIndex;
        CurrentSpellType = spellTypes.SelectedIndex;
        CurrentShowSelfOnly = showSelfOnly.IsChecked.Value;
        Display();
      }
    }

    private void Options_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      OptionsChanged();
    }

    private void SelfOnlyChange(object sender, RoutedEventArgs e)
    {
      OptionsChanged();
    }

    private void CopyBBCode_Click(object sender, RoutedEventArgs e)
    {
      try
      {
        List<string> header = new List<string>();
        List<List<string>> data = new List<List<string>>();

        header.Add("");
        for (int i = 1; i < dataGrid.Columns.Count; i++)
        {
          header.Add(dataGrid.Columns[i].Header as string);
        }

        foreach (var item in dataGrid.Items)
        {
          var counts = item as SpellCountRow;
          List<string> row = new List<string> { counts.Spell };
          foreach (var value in counts.Values)
          {
            row.Add(value.ToString(CultureInfo.CurrentCulture));
          }

          data.Add(row);
        }

        string result = TextFormatUtils.BuildBBCodeTable(header, data, titleLabel.Content as string);
        Clipboard.SetDataObject(result);
      }
      catch (ArgumentNullException ane)
      {
        Clipboard.SetDataObject("EQ Log Parser Error: Failed to create BBCode\r\n");
        LOG.Error(ane);
      }
      catch (ExternalException ex)
      {
        LOG.Error(ex);
      }
    }

  }
}
