using FontAwesome5;
using Microsoft.Win32;
using Newtonsoft.Json;
using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for SpellCountTable.xaml
  /// </summary>
  public partial class SpellCountTable : CastTable
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private List<string> PlayerList;
    private SpellCountData TheSpellCounts;
    private double Time;
    private readonly DictionaryAddHelper<string, uint> AddHelper = new DictionaryAddHelper<string, uint>();
    private readonly Dictionary<string, byte> HiddenSpells = new Dictionary<string, byte>();
    private readonly List<string> CountTypes = new List<string>() { "Counts", "Percentages", "Counts/Minute" };
    private readonly List<string> MinFreqs = new List<string>() { "Any Frequency", "Frequency > 1", "Frequency > 2", "Frequency > 3", "Frequency > 4", "Frequency > 5" };
    private readonly HashSet<string> SortDescs = new HashSet<string>() { "totalColumn" };
    private int CurrentCountType = 0;
    private int CurrentMinFreqCount = 0;
    private string Title;

    public SpellCountTable()
    {
      InitializeComponent();
      dataGrid.IsEnabled = false;
      UIElementUtil.SetEnabled(controlPanel.Children, false);
      countTypes.ItemsSource = CountTypes;
      countTypes.SelectedIndex = 0;
      minFreqList.ItemsSource = MinFreqs;
      minFreqList.SelectedIndex = 0;

      InitCastTable(dataGrid, titleLabel, selectedCastTypes, selectedSpellRestrictions);
      // default these columns to descending
      dataGrid.SortColumnsChanging += (object s, GridSortColumnsChangingEventArgs e) => DataGridUtil.SortColumnsChanging(s, e, SortDescs);
      dataGrid.SortColumnsChanged += (object s, GridSortColumnsChangedEventArgs e) => DataGridUtil.SortColumnsChanged(s, e, SortDescs);
    }

    internal void Init(List<PlayerStats> selectedStats, CombinedStats currentStats)
    {
      Title = currentStats?.ShortTitle ?? "";
      Time = currentStats.RaidStats.TotalSeconds;

      var raidStats = currentStats?.RaidStats;

      if (selectedStats != null && raidStats != null)
      {
        PlayerList = selectedStats.Select(stats => stats.OrigName).Distinct().ToList();
        TheSpellCounts = SpellCountBuilder.GetSpellCounts(PlayerList, raidStats);
        Display();
      }
    }

    private void Display()
    {
      dataGrid.Columns.Clear();
      var headerCol = new GridTextColumn
      {
        HeaderText = "",
        MappingName = "Spell",
        CellStyle = DataGridUtil.CreateHighlightForegroundStyle("Spell", new ReceivedSpellColorConverter())
      };

      dataGrid.Columns.Add(headerCol);

      Task.Delay(100).ContinueWith(task =>
      {
        var filteredPlayerMap = new Dictionary<string, Dictionary<string, uint>>();
        var totalCountMap = new Dictionary<string, uint>();
        var uniqueSpellsMap = new Dictionary<string, uint>();

        uint totalCasts = 0;
        PlayerList.ForEach(player =>
        {
          filteredPlayerMap[player] = new Dictionary<string, uint>();

          if (TheSpellCounts.PlayerCastCounts.ContainsKey(player))
          {
            foreach (ref var id in TheSpellCounts.PlayerCastCounts[player].Keys.ToArray().AsSpan())
            {
              if (PassFilters(TheSpellCounts.UniqueSpells[id], false))
              {
                totalCasts = UpdateMaps(id, player, TheSpellCounts.PlayerCastCounts[player][id], TheSpellCounts.MaxCastCounts, totalCountMap, uniqueSpellsMap, filteredPlayerMap, false, totalCasts);
              }
            }
          }

          if (TheSpellCounts.PlayerReceivedCounts.ContainsKey(player))
          {
            foreach (ref var id in TheSpellCounts.PlayerReceivedCounts[player].Keys.ToArray().AsSpan())
            {
              if (PassFilters(TheSpellCounts.UniqueSpells[id], true))
              {
                totalCasts = UpdateMaps(id, player, TheSpellCounts.PlayerReceivedCounts[player][id], TheSpellCounts.MaxReceivedCounts, totalCountMap, uniqueSpellsMap, filteredPlayerMap, true, totalCasts);
              }
            }
          }
        });

        var sortedPlayers = totalCountMap.Keys.OrderByDescending(key => totalCountMap[key]).ToList();
        var sortedSpellList = uniqueSpellsMap.Keys.OrderByDescending(key => uniqueSpellsMap[key]).ToList();

        var colCount = 0;
        foreach (string name in sortedPlayers)
        {
          double total = totalCountMap.ContainsKey(name) ? totalCountMap[name] : 0;

          double amount = 0.0;
          switch (CurrentCountType)
          {
            case 0:
              amount = total;
              break;
            case 1:
              amount = totalCasts > 0 ? Math.Round(total / totalCasts * 100, 2) : 0;
              break;
            case 2:
              amount = Time > 0 ? Math.Round(total / Time * 60, 2) : 0;
              break;
          }

          var header = string.Format("{0} = {1}", name, amount.ToString());

          Dispatcher.InvokeAsync(() =>
          {
            var playerCol = new GridTextColumn
            {
              HeaderText = header,
              MappingName = name,
              SortMode = Syncfusion.Data.DataReflectionMode.Value,
              DisplayBinding = new Binding(name + "Text"),
              TextAlignment = TextAlignment.Right,
              ShowHeaderToolTip = true,
              HeaderToolTipTemplate = Application.Current.Resources["HeaderSpellCountsTemplateToolTip"] as DataTemplate
            };

            dataGrid.Columns.Add(playerCol);
          });

          colCount++;
          SortDescs.Add(name);
        }

        string headerAmount = "";
        switch (CurrentCountType)
        {
          case 0:
            headerAmount = totalCasts.ToString(CultureInfo.CurrentCulture);
            break;
          case 1:
            headerAmount = "100";
            break;
          case 2:
            headerAmount = Time > 0 ? Math.Round(totalCasts / Time * 60, 2).ToString(CultureInfo.CurrentCulture) : "0";
            break;
        }

        string totalHeader = string.Format("Total = {0}", headerAmount);

        Dispatcher.InvokeAsync(() =>
        {
          var totalCol = new GridTextColumn
          {
            HeaderText = totalHeader,
            MappingName = "totalColumn",
            SortMode = Syncfusion.Data.DataReflectionMode.Value,
            DisplayBinding = new Binding("totalColumnText"),
            TextAlignment = TextAlignment.Right
          };

          dataGrid.Columns.Add(totalCol);
        });

        int existingIndex = 0;
        var list = new List<IDictionary<string, object>>();
        foreach (var spell in sortedSpellList)
        {
          var row = (list.Count > existingIndex) ? list[existingIndex] : new ExpandoObject();
          row["Spell"] = spell;

          for (int i = 0; i < sortedPlayers.Count; i++)
          {
            if (filteredPlayerMap.ContainsKey(sortedPlayers[i]))
            {
              if (filteredPlayerMap[sortedPlayers[i]].ContainsKey(spell))
              {
                switch (CurrentCountType)
                {
                  case 0:
                    AddPlayerRow(sortedPlayers[i], spell, filteredPlayerMap[sortedPlayers[i]][spell], row);
                    break;
                  case 1:
                    var percent = totalCountMap[sortedPlayers[i]] > 0 ? Math.Round((double)filteredPlayerMap[sortedPlayers[i]][spell] / totalCountMap[sortedPlayers[i]] * 100, 2) : 0.0;
                    AddPlayerRow(sortedPlayers[i], spell, percent, row);
                    break;
                  case 2:
                    var rate = Time > 0 ? Math.Round(filteredPlayerMap[sortedPlayers[i]][spell] / Time * 60, 2) : 0.0;
                    AddPlayerRow(sortedPlayers[i], spell, rate, row);
                    break;
                }
              }
              else
              {
                row[sortedPlayers[i] + "Text"] = CurrentCountType == 0 ? "0" : "0.0";
                row[sortedPlayers[i]] = 0.0;
              }
            }
          }

          switch (CurrentCountType)
          {
            case 0:
              row["totalColumn"] = uniqueSpellsMap[spell];
              break;
            case 1:
              row["totalColumn"] = Math.Round((double)uniqueSpellsMap[spell] / totalCasts * 100, 2);
              break;
            case 2:
              row["totalColumn"] = Time > 0 ? Math.Round(uniqueSpellsMap[spell] / Time * 60, 2) : 0.0;
              break;
          }

          row["totalColumnText"] = row["totalColumn"].ToString();

          if (list.Count <= existingIndex)
          {
            list.Add(row);
          }

          existingIndex++;
        }

        Dispatcher.InvokeAsync(() =>
        {
          titleLabel.Content = Title;
          dataGrid.ItemsSource = list;
          dataGrid.IsEnabled = true;
          UIElementUtil.SetEnabled(controlPanel.Children, true);
        });
      });
    }

    private void GridSizeChanged(object sender, SizeChangedEventArgs e) => UIElementUtil.CheckHideTitlePanel(titlePanel, controlPanel);
    private void OptionsChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateOptions(true);

    private void AddPlayerRow(string player, string spell, double value, IDictionary<string, object> row)
    {
      double count = value;
      string countText = value.ToString();
      if (TheSpellCounts.PlayerInterruptedCounts.ContainsKey(player) &&
        TheSpellCounts.PlayerInterruptedCounts[player].TryGetValue(spell, out uint interrupts) && interrupts > 0)
      {
        countText = countText + " (" + TheSpellCounts.PlayerInterruptedCounts[player][spell] + ")";
        count += interrupts;
      }

      row[player + "Text"] = countText;
      row[player] = count;
    }

    private uint UpdateMaps(string id, string player, uint playerCount, Dictionary<string, uint> maxCounts, Dictionary<string, uint> totalCountMap,
      Dictionary<string, uint> uniqueSpellsMap, Dictionary<string, Dictionary<string, uint>> filteredPlayerMap, bool received, uint totalCasts)
    {
      string name = TheSpellCounts.UniqueSpells[id].NameAbbrv;

      if (received)
      {
        name = "Received " + name;
      }

      if (!HiddenSpells.ContainsKey(name) && maxCounts[id] > CurrentMinFreqCount)
      {
        AddHelper.Add(totalCountMap, player, playerCount);
        AddHelper.Add(uniqueSpellsMap, name, playerCount);
        AddHelper.Add(filteredPlayerMap[player], name, playerCount);
        totalCasts += playerCount;
      }

      return totalCasts;
    }

    private void UpdateOptions(bool force = false)
    {
      if (dataGrid?.View != null && (force || CurrentCountType != countTypes.SelectedIndex || CurrentMinFreqCount != minFreqList.SelectedIndex))
      {
        CurrentCountType = countTypes.SelectedIndex;
        CurrentMinFreqCount = minFreqList.SelectedIndex;
        titleLabel.Content = "Loading...";
        dataGrid.ItemsSource = null;
        dataGrid.IsEnabled = false;
        UIElementUtil.SetEnabled(controlPanel.Children, false);
        Display();
      }
    }

    private void CastTypesChanged(object sender, EventArgs e)
    {
      if (dataGrid?.View != null && selectedCastTypes?.Items != null)
      {
        if (UpdateSelectedCastTypes(selectedCastTypes) || UpdateSelectedRestrictions(selectedSpellRestrictions))
        {
          titleLabel.Content = "Loading...";
          dataGrid.IsEnabled = false;
          dataGrid.ItemsSource = null;
          UIElementUtil.SetEnabled(controlPanel.Children, false);
          Display();
        }
      }
    }

    private void RefreshClick(object sender, RoutedEventArgs e)
    {
      HiddenSpells.Clear();
      UpdateOptions(true);
    }

    private void ImportClick(object sender, RoutedEventArgs e)
    {
      try
      {
        // WPF doesn't have its own file chooser so use Win32 Version
        OpenFileDialog dialog = new OpenFileDialog
        {
          // filter to txt files
          DefaultExt = ".scf.gz",
          Filter = "Spell Count File (*.scf.gz) | *.scf.gz"
        };

        // show dialog and read result
        if (dialog.ShowDialog().Value)
        {
          FileInfo gzipFileName = new FileInfo(dialog.FileName);

          GZipStream decompressionStream = new GZipStream(gzipFileName.OpenRead(), CompressionMode.Decompress);
          var reader = new StreamReader(decompressionStream);
          string json = reader?.ReadToEnd();
          reader?.Close();

          var data = JsonConvert.DeserializeObject<SpellCountsSerialized>(json);

          // copy data
          PlayerList = PlayerList.Union(data.PlayerNames).ToList();

          foreach (var player in data.TheSpellData.PlayerCastCounts.Keys)
          {
            TheSpellCounts.PlayerCastCounts[player] = data.TheSpellData.PlayerCastCounts[player];
          }

          foreach (var player in data.TheSpellData.PlayerInterruptedCounts.Keys)
          {
            TheSpellCounts.PlayerInterruptedCounts[player] = data.TheSpellData.PlayerInterruptedCounts[player];
          }

          foreach (var player in data.TheSpellData.PlayerReceivedCounts.Keys)
          {
            TheSpellCounts.PlayerReceivedCounts[player] = data.TheSpellData.PlayerReceivedCounts[player];
          }

          foreach (var spellId in data.TheSpellData.MaxCastCounts.Keys)
          {
            if (!TheSpellCounts.MaxCastCounts.ContainsKey(spellId) || TheSpellCounts.MaxCastCounts[spellId] < data.TheSpellData.MaxCastCounts[spellId])
            {
              TheSpellCounts.MaxCastCounts[spellId] = data.TheSpellData.MaxCastCounts[spellId];
            }
          }

          foreach (var spellId in data.TheSpellData.MaxReceivedCounts.Keys)
          {
            if (!TheSpellCounts.MaxReceivedCounts.ContainsKey(spellId) || TheSpellCounts.MaxReceivedCounts[spellId] < data.TheSpellData.MaxReceivedCounts[spellId])
            {
              TheSpellCounts.MaxReceivedCounts[spellId] = data.TheSpellData.MaxReceivedCounts[spellId];
            }
          }

          foreach (var spellData in data.TheSpellData.UniqueSpells.Keys)
          {
            if (!TheSpellCounts.UniqueSpells.ContainsKey(spellData))
            {
              TheSpellCounts.UniqueSpells[spellData] = data.TheSpellData.UniqueSpells[spellData];
            }
          }

          UpdateOptions(true);
        }
      }
      catch (IOException ex)
      {
        LOG.Error(ex);
      }
      catch (UnauthorizedAccessException uax)
      {
        LOG.Error(uax);
      }
      catch (SecurityException se)
      {
        LOG.Error(se);
      }
    }

    private void ExportClick(object sender, RoutedEventArgs e)
    {
      try
      {
        var data = new SpellCountsSerialized { TheSpellData = TheSpellCounts };
        data.PlayerNames.AddRange(PlayerList);

        var result = JsonConvert.SerializeObject(data);
        SaveFileDialog saveFileDialog = new SaveFileDialog();
        string filter = "Spell Count File (*.scf.gz)|*.scf.gz";
        saveFileDialog.Filter = filter;
        if (saveFileDialog.ShowDialog().Value)
        {
          FileInfo gzipFileName = new FileInfo(saveFileDialog.FileName);
          FileStream gzipTargetAsStream = gzipFileName.Create();
          GZipStream gzipStream = new GZipStream(gzipTargetAsStream, CompressionMode.Compress);
          var writer = new StreamWriter(gzipStream);
          writer?.Write(result);
          writer?.Close();
        }
      }
      catch (IOException ex)
      {
        LOG.Error(ex);
      }
      catch (UnauthorizedAccessException uax)
      {
        LOG.Error(uax);
      }
      catch (SecurityException se)
      {
        LOG.Error(se);
      }
    }

    private void CopyBBCodeClick(object sender, RoutedEventArgs e)
    {
      try
      {
        var export = DataGridUtil.BuildExportData(dataGrid);
        string result = TextFormatUtils.BuildBBCodeTable(export.Item1, export.Item2, titleLabel.Content as string);
        Clipboard.SetDataObject(result);
      }
      catch (ArgumentNullException ane)
      {
        Clipboard.SetDataObject("EQLogParser Error: Failed to create BBCode\r\n");
        LOG.Error(ane);
      }
      catch (ExternalException ex)
      {
        LOG.Error(ex);
      }
    }

    private void CopyGamparseClick(object sender, RoutedEventArgs e)
    {
      try
      {
        var export = DataGridUtil.BuildExportData(dataGrid);
        string result = TextFormatUtils.BuildGamparseList(export.Item1, export.Item2, titleLabel.Content as string);
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

    private void RemoveSelectedRowsClick(object sender, RoutedEventArgs e)
    {
      Dispatcher.InvokeAsync(() =>
      {
        foreach (var selected in dataGrid.SelectedItems)
        {
          if (selected is IDictionary<string, object> spr)
          {
            HiddenSpells[spr["Spell"] as string] = 1;
            dataGrid.View.Remove(spr);
          }
        }
      }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void RemoveSpellMouseDown(object sender, MouseButtonEventArgs e)
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (sender is ImageAwesome image && image.DataContext is IDictionary<string, object> spr)
        {
          HiddenSpells[spr["Spell"] as string] = 1;
          dataGrid.View.Remove(spr);
        }
      });
    }
  }

  internal class SpellCountsSerialized
  {
    public List<string> PlayerNames { get; } = new List<string>();
    public SpellCountData TheSpellData { get; set; }
  }

  public class SpellCountComparer : IComparer
  {
    private readonly bool Ascending;
    private readonly string Column;

    public SpellCountComparer(string column, bool ascending)
    {
      Ascending = ascending;
      Column = column;
    }

    public int Compare(object x, object y)
    {
      int result = 0;

      if (x is IDictionary<string, object> d1 && y is IDictionary<string, object> d2)
      {
        if (double.TryParse(d1[Column] as string, out double v1) && double.TryParse(d2[Column] as string, out double v2))
        {
          result = v1.CompareTo(v2);
        }
      }

      if (Ascending)
      {
        result *= -1;
      }

      return result;
    }
  }
}
