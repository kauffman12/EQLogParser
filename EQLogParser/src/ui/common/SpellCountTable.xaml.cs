using log4net;
using Microsoft.Win32;
using Syncfusion.Data;
using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for SpellCountTable.xaml
  /// </summary>
  public partial class SpellCountTable : CastTable
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private List<string> PlayerList;
    private SpellCountData TheSpellCounts;
    private double Time;
    private readonly Dictionary<string, byte> HiddenSpells = new();
    private readonly List<string> CountTypes = new() { "Counts", "Percentages", "Counts/Minute" };
    private readonly List<string> MinFreqs = new() { "Any Frequency", "Frequency > 1", "Frequency > 2", "Frequency > 3", "Frequency > 4", "Frequency > 5" };
    private readonly HashSet<string> SortDescs = new() { "totalColumn" };
    private readonly TotalColumnComparer TotalColumnComparer = new();
    private int CurrentCountType;
    private int CurrentMinFreqCount;
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
      dataGrid.SortColumnsChanging += (s, e) => DataGridUtil.SortColumnsChanging(s, e, SortDescs);
      dataGrid.SortColumnsChanged += (s, e) => DataGridUtil.SortColumnsChanged(s, e, SortDescs);
    }

    internal void Init(List<PlayerStats> selectedStats, CombinedStats currentStats)
    {
      Title = currentStats?.ShortTitle ?? "";
      if (currentStats != null)
      {
        Time = currentStats.RaidStats.TotalSeconds;
        var raidStats = currentStats.RaidStats;
        if (selectedStats != null && raidStats != null)
        {
          PlayerList = selectedStats.Select(stats => stats.OrigName).Distinct().ToList();
          TheSpellCounts = SpellCountBuilder.GetSpellCounts(PlayerList, raidStats);
          Display();
        }
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

      Task.Delay(100).ContinueWith(_ =>
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
                totalCasts = UpdateMaps(id, player, TheSpellCounts.PlayerCastCounts[player][id], TheSpellCounts.MaxCastCounts,
                  totalCountMap, uniqueSpellsMap, filteredPlayerMap, false, totalCasts);
              }
            }
          }

          if (TheSpellCounts.PlayerReceivedCounts.ContainsKey(player))
          {
            foreach (ref var id in TheSpellCounts.PlayerReceivedCounts[player].Keys.ToArray().AsSpan())
            {
              if (PassFilters(TheSpellCounts.UniqueSpells[id], true))
              {
                totalCasts = UpdateMaps(id, player, TheSpellCounts.PlayerReceivedCounts[player][id], TheSpellCounts.MaxReceivedCounts,
                  totalCountMap, uniqueSpellsMap, filteredPlayerMap, true, totalCasts);
              }
            }
          }
        });

        Dispatcher.InvokeAsync(() =>
        {
          var playerColumns = new List<GridColumn>();
          foreach (var name in totalCountMap.Keys)
          {
            double playerTotal = totalCountMap.TryGetValue(name, out var value) ? value : 0;
            var header = GetHeaderValue(name, playerTotal, totalCasts);
            var playerCol = new GridTextColumn
            {
              HeaderText = header,
              MappingName = name,
              SortMode = DataReflectionMode.Value,
              DisplayBinding = new Binding(name + "Text"),
              TextAlignment = TextAlignment.Right,
              ShowHeaderToolTip = true,
              HeaderToolTipTemplate = Application.Current.Resources["HeaderSpellCountsTemplateToolTip"] as DataTemplate
            };

            playerColumns.Add(playerCol);
            SortDescs.Add(name);
          }

          playerColumns.OrderBy(col => col.HeaderText, TotalColumnComparer).ToList().ForEach(col => dataGrid.Columns.Add(col));

          var totalCol = new GridTextColumn
          {
            HeaderText = GetHeaderValue("Total", totalCasts, totalCasts),
            MappingName = "totalColumn",
            SortMode = DataReflectionMode.Value,
            DisplayBinding = new Binding("totalColumnText"),
            TextAlignment = TextAlignment.Right
          };

          dataGrid.Columns.Add(totalCol);
        });

        var existingIndex = 0;
        var playerNames = totalCountMap.Keys.ToList();
        var list = new List<IDictionary<string, object>>();
        foreach (var spell in uniqueSpellsMap.Keys.OrderByDescending(key => uniqueSpellsMap[key]))
        {
          var row = (list.Count > existingIndex) ? list[existingIndex] : new ExpandoObject();
          row["Spell"] = spell;

          foreach (var name in playerNames)
          {
            if (filteredPlayerMap.ContainsKey(name))
            {
              if (filteredPlayerMap[name].ContainsKey(spell))
              {
                AddPlayerRow(name, spell, filteredPlayerMap[name][spell], totalCountMap[name], row);
              }
              else
              {
                row[name + "Text"] = CurrentCountType == 0 ? "0" : "0.0";
                row[name] = 0d;
              }
            }
          }

          row["totalColumn"] = uniqueSpellsMap[spell];
          row["totalColumnText"] = GetFormattedValue(uniqueSpellsMap[spell], totalCasts);

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

    private void CreateLargeImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(dataGrid, titleLabel, true);
    private void GridSizeChanged(object sender, SizeChangedEventArgs e) => UIElementUtil.CheckHideTitlePanel(titlePanel, controlPanel);
    private void OptionsChanged(object sender, SelectionChangedEventArgs e) => UpdateOptions(true);

    private void AddPlayerRow(string player, string spell, double value, double playerTotal, IDictionary<string, object> row)
    {
      var countText = GetFormattedValue(value, playerTotal);
      if (TheSpellCounts.PlayerInterruptedCounts.ContainsKey(player) &&
        TheSpellCounts.PlayerInterruptedCounts[player].TryGetValue(spell, out var interrupts) && interrupts > 0)
      {
        countText = countText + " (" + TheSpellCounts.PlayerInterruptedCounts[player][spell] + ")";
      }

      row[player + "Text"] = countText;
      row[player] = value;
    }

    private uint UpdateMaps(string id, string player, uint playerCount, Dictionary<string, uint> maxCounts, Dictionary<string, uint> totalCountMap,
      Dictionary<string, uint> uniqueSpellsMap, Dictionary<string, Dictionary<string, uint>> filteredPlayerMap, bool received, uint totalCasts)
    {
      var name = TheSpellCounts.UniqueSpells[id].NameAbbrv;

      if (received)
      {
        name = "Received " + name;
      }

      if (!HiddenSpells.ContainsKey(name) && maxCounts[id] > CurrentMinFreqCount)
      {
        AddValue(totalCountMap, player, playerCount);
        AddValue(uniqueSpellsMap, name, playerCount);
        AddValue(filteredPlayerMap[player], name, playerCount);
        totalCasts += playerCount;
      }

      return totalCasts;

      void AddValue(Dictionary<string, uint> dict, string name, uint amount)
      {
        if (!dict.TryAdd(name, amount))
        {
          dict[name] += amount;
        }
      }
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
        var dialog = new OpenFileDialog
        {
          // filter to txt files
          DefaultExt = ".scf.gz",
          Filter = "Spell Count File (*.scf.gz) | *.scf.gz"
        };

        // show dialog and read result
        if (dialog.ShowDialog().Value)
        {
          var gzipFileName = new FileInfo(dialog.FileName);

          var decompressionStream = new GZipStream(gzipFileName.OpenRead(), CompressionMode.Decompress);
          var reader = new StreamReader(decompressionStream);
          var json = reader?.ReadToEnd();
          reader?.Close();

          var data = JsonSerializer.Deserialize<SpellCountsSerialized>(json);

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
            TheSpellCounts.UniqueSpells.TryAdd(spellData, data.TheSpellData.UniqueSpells[spellData]);
          }

          UpdateOptions(true);
        }
      }
      catch (Exception ex)
      {
        new MessageWindow("Problem Importing Spell Counts Data. Check Error Log for details.", Resource.EXPORT_ERROR).ShowDialog();
        Log.Error(ex);
      }
    }

    private void ExportClick(object sender, RoutedEventArgs e)
    {
      try
      {
        var data = new SpellCountsSerialized { TheSpellData = TheSpellCounts };
        data.PlayerNames.AddRange(PlayerList);

        var result = JsonSerializer.Serialize(data);
        var saveFileDialog = new SaveFileDialog();
        var filter = "Spell Count File (*.scf.gz)|*.scf.gz";
        saveFileDialog.Filter = filter;
        if (saveFileDialog.ShowDialog().Value)
        {
          var gzipFileName = new FileInfo(saveFileDialog.FileName);
          var gzipTargetAsStream = gzipFileName.Create();
          var gzipStream = new GZipStream(gzipTargetAsStream, CompressionMode.Compress);
          var writer = new StreamWriter(gzipStream);
          writer?.Write(result);
          writer?.Close();
        }
      }
      catch (Exception ex)
      {
        new MessageWindow("Problem Exporting Spell Counts Data. Check Error Log for details.", Resource.EXPORT_ERROR).ShowDialog();
        Log.Error(ex);
      }
    }

    private void CopyBBCodeClick(object sender, RoutedEventArgs e)
    {
      try
      {
        var export = DataGridUtil.BuildExportData(dataGrid);
        var result = TextUtils.BuildBBCodeTable(export.Item1, export.Item2, titleLabel.Content as string);
        Clipboard.SetDataObject(result);
      }
      catch (ArgumentNullException ane)
      {
        Clipboard.SetDataObject("EQLogParser Error: Failed to create BBCode\r\n");
        Log.Error(ane);
      }
      catch (ExternalException ex)
      {
        Log.Error(ex);
      }
    }

    private void CopyGamparseClick(object sender, RoutedEventArgs e)
    {
      try
      {
        var export = DataGridUtil.BuildExportData(dataGrid);
        var result = TextUtils.BuildGamparseList(export.Item1, export.Item2, titleLabel.Content as string);
        Clipboard.SetDataObject(result);
      }
      catch (ArgumentNullException ane)
      {
        Clipboard.SetDataObject("EQ Log Parser Error: Failed to create BBCode\r\n");
        Log.Error(ane);
      }
      catch (ExternalException ex)
      {
        Log.Error(ex);
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
            HiddenSpells[spr["Spell"] as string ?? string.Empty] = 1;
            dataGrid.View.Remove(spr);
            UpdateCounts();
          }
        }
      }, DispatcherPriority.Background);
    }

    private void RemoveSpellMouseDown(object sender, MouseButtonEventArgs e)
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (sender is Border { DataContext: IDictionary<string, object> spr })
        {
          HiddenSpells[spr["Spell"] as string] = 1;
          dataGrid.View.Remove(spr);
          UpdateCounts();
        }
      });
    }

    private void UpdateCounts()
    {
      var counts = PlayerList.ToDictionary(key => key, _ => 0.0);
      foreach (var record in dataGrid.View.Records)
      {
        var data = record.Data as dynamic;
        foreach (var value in data)
        {
          if (PlayerList.Contains(value.Key))
          {
            counts.TryGetValue(value.Key, out double count);
            counts[value.Key] = count + value.Value;
          }
        }
      }

      var total = counts.Values.Sum();
      var playerColumns = new List<GridColumn>();
      foreach (var col in dataGrid.Columns)
      {
        if (col.MappingName == "totalColumn")
        {
          col.HeaderText = GetHeaderValue("Total", total, total);
        }
        else if (counts.TryGetValue(col.MappingName, out var count))
        {
          col.HeaderText = GetHeaderValue(col.MappingName, count, total);
          playerColumns.Add(col);
        }
      }

      var colIndex = 1;
      playerColumns.OrderBy(key => key.HeaderText, TotalColumnComparer).ToList().ForEach(col => dataGrid.Columns[colIndex++] = col);
    }

    private string GetHeaderValue(string name, double amount, double total)
    {
      var result = 0.0;
      switch (CurrentCountType)
      {
        case 0:
          result = amount;
          break;
        case 1:
          result = total > 0 ? Math.Round(amount / total * 100, 2) : 0;
          break;
        case 2:
          result = Time > 0 ? Math.Round(amount / Time * 60, 2) : 0;
          break;
      }

      return $"{name} = {result}";
    }

    private string GetFormattedValue(double value, double playerTotal)
    {
      if (CurrentCountType == 1)
      {
        return (playerTotal > 0 ? Math.Round(value / playerTotal * 100, 2) : 0.0).ToString(CultureInfo.InvariantCulture);
      }

      if (CurrentCountType == 2)
      {
        return (Time > 0 ? Math.Round(value / Time * 60, 2) : 0.0).ToString(CultureInfo.InvariantCulture);
      }

      return value.ToString(CultureInfo.InvariantCulture);
    }
  }

  internal class SpellCountsSerialized
  {
    public List<string> PlayerNames { get; set; } = new();
    public SpellCountData TheSpellData { get; set; }
  }

  public class TotalColumnComparer : IComparer<string>
  {
    public int Compare(string x, string y)
    {
      var xValues = x.Split(" = ");
      var yValues = y.Split(" = ");
      var xDouble = double.Parse(xValues[1]);
      var yDouble = double.Parse(yValues[1]);

      if (yDouble > xDouble)
      {
        return 1;
      }

      if (yDouble < xDouble)
      {
        return -1;
      }

      return String.Compare(x, y, StringComparison.Ordinal);
    }
  }
}
