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
  public partial class SpellCountTable
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    private List<string> _playerList;
    private SpellCountData _theSpellCounts;
    private double _time;
    private readonly Dictionary<string, byte> _hiddenSpells = [];
    private readonly List<string> _countTypes = ["Counts", "Percentages", "Counts/Minute"];
    private readonly List<string> _minFreqs = ["Any Frequency", "Frequency > 1", "Frequency > 2", "Frequency > 3", "Frequency > 4", "Frequency > 5"];
    private readonly HashSet<string> _sortDescs = ["totalColumn"];
    private readonly TotalColumnComparer _totalColumnComparer = new();
    private int _currentCountType;
    private int _currentMinFreqCount;
    private string _title;

    public SpellCountTable()
    {
      InitializeComponent();
      dataGrid.IsEnabled = false;
      UiElementUtil.SetEnabled(controlPanel.Children, false);
      countTypes.ItemsSource = _countTypes;
      countTypes.SelectedIndex = 0;
      minFreqList.ItemsSource = _minFreqs;
      minFreqList.SelectedIndex = 0;

      InitCastTable(dataGrid, titleLabel, selectedCastTypes, selectedSpellRestrictions);
      // default these columns to descending
      dataGrid.SortColumnsChanging += (s, e) => DataGridUtil.SortColumnsChanging(s, e, _sortDescs);
      dataGrid.SortColumnsChanged += (s, e) => DataGridUtil.SortColumnsChanged(s, e, _sortDescs);
    }

    internal void Init(List<PlayerStats> selectedStats, CombinedStats currentStats)
    {
      _title = currentStats?.ShortTitle ?? "";
      if (currentStats != null)
      {
        _time = currentStats.RaidStats.TotalSeconds;
        var raidStats = currentStats.RaidStats;
        if (raidStats != null)
        {
          var selected = selectedStats?.Select(stats => stats.OrigName).Distinct().ToList();
          _theSpellCounts = SpellCountBuilder.GetSpellCounts(selected, raidStats);
          _playerList = [.. _theSpellCounts.UniquePlayers.Keys];
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
        CellStyle = DataGridUtil.CreateHighlightForegroundStyle("Spell", new ReceivedSpellColorConverter()),
        Width = MainActions.CurrentSpellWidth
      };

      dataGrid.Columns.Add(headerCol);

      Task.Delay(100).ContinueWith(_ =>
      {
        var filteredPlayerMap = new Dictionary<string, Dictionary<string, uint>>();
        var totalCountMap = new Dictionary<string, uint>();
        var uniqueSpellsMap = new Dictionary<string, uint>();

        uint totalCasts = 0;
        _playerList.ForEach(player =>
        {
          filteredPlayerMap[player] = [];
          if (_theSpellCounts.PlayerCastCounts.ContainsKey(player))
          {
            foreach (var id in _theSpellCounts.PlayerCastCounts[player].Keys)
            {
              if (PassFilters(_theSpellCounts.UniqueSpells[id], false))
              {
                totalCasts = UpdateMaps(id, player, _theSpellCounts.PlayerCastCounts[player][id], _theSpellCounts.MaxCastCounts,
                  totalCountMap, uniqueSpellsMap, filteredPlayerMap, false, totalCasts);
              }
            }
          }

          if (_theSpellCounts.PlayerReceivedCounts.TryGetValue(player, out var value))
          {
            foreach (var id in value.Keys)
            {
              if (PassFilters(_theSpellCounts.UniqueSpells[id], true))
              {
                totalCasts = UpdateMaps(id, player, value[id], _theSpellCounts.MaxReceivedCounts,
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
              HeaderToolTipTemplate = Application.Current.Resources["HeaderSpellCountsTemplateToolTip"] as DataTemplate,
              Width = DataGridUtil.CalculateMinGridHeaderWidth(header)
            };

            playerColumns.Add(playerCol);
            _sortDescs.Add(name);
          }

          playerColumns.OrderBy(col => col.HeaderText, _totalColumnComparer).ToList().ForEach(col => dataGrid.Columns.Add(col));

          var totalText = GetHeaderValue("Total", totalCasts, totalCasts);
          var totalCol = new GridTextColumn
          {
            HeaderText = totalText,
            MappingName = "totalColumn",
            SortMode = DataReflectionMode.Value,
            DisplayBinding = new Binding("totalColumnText"),
            TextAlignment = TextAlignment.Right,
            Width = DataGridUtil.CalculateMinGridHeaderWidth(totalText)
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

          foreach (var name in CollectionsMarshal.AsSpan(playerNames))
          {
            if (filteredPlayerMap.TryGetValue(name, out var mapValue))
            {
              if (mapValue.TryGetValue(spell, out var spellValue))
              {
                AddPlayerRow(name, spell, spellValue, totalCountMap[name], row);
              }
              else
              {
                row[name + "Text"] = _currentCountType == 0 ? "0" : "0.0";
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
          titleLabel.Content = _title;
          dataGrid.ItemsSource = list;
          dataGrid.IsEnabled = true;
          UiElementUtil.SetEnabled(controlPanel.Children, true);
        });
      });
    }

    private void GridSizeChanged(object sender, SizeChangedEventArgs e) => UiElementUtil.CheckHideTitlePanel(titlePanel, controlPanel);
    private void OptionsChanged(object sender, SelectionChangedEventArgs e) => UpdateOptions(true);

    private void AddPlayerRow(string player, string spell, double theValue, double playerTotal, IDictionary<string, object> row)
    {
      var countText = GetFormattedValue(theValue, playerTotal);
      if (_theSpellCounts.PlayerInterruptedCounts.TryGetValue(player, out var value) && value.TryGetValue(spell, out var interrupts) && interrupts > 0)
      {
        countText = countText + " (" + value[spell] + ")";
      }

      row[player + "Text"] = countText;
      row[player] = theValue;
    }

    private uint UpdateMaps(string id, string player, uint playerCount, Dictionary<string, uint> maxCounts, Dictionary<string, uint> totalCountMap,
      Dictionary<string, uint> uniqueSpellsMap, Dictionary<string, Dictionary<string, uint>> filteredPlayerMap, bool received, uint totalCasts)
    {
      var name = _theSpellCounts.UniqueSpells[id].NameAbbrv;

      if (received)
      {
        name = "Received " + name;
      }

      if (!_hiddenSpells.ContainsKey(name) && maxCounts[id] > _currentMinFreqCount)
      {
        AddValue(totalCountMap, player, playerCount);
        AddValue(uniqueSpellsMap, name, playerCount);
        AddValue(filteredPlayerMap[player], name, playerCount);
        totalCasts += playerCount;
      }

      return totalCasts;

      static void AddValue(Dictionary<string, uint> dict, string theName, uint amount)
      {
        if (!dict.TryAdd(theName, amount))
        {
          dict[theName] += amount;
        }
      }
    }

    private void UpdateOptions(bool force = false)
    {
      if (dataGrid?.View != null && (force || _currentCountType != countTypes.SelectedIndex || _currentMinFreqCount != minFreqList.SelectedIndex))
      {
        _currentCountType = countTypes.SelectedIndex;
        _currentMinFreqCount = minFreqList.SelectedIndex;
        titleLabel.Content = "Loading...";
        dataGrid.ItemsSource = null;
        dataGrid.IsEnabled = false;
        UiElementUtil.SetEnabled(controlPanel.Children, false);
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
          UiElementUtil.SetEnabled(controlPanel.Children, false);
          Display();
        }
      }
    }

    private void RefreshClick(object sender, RoutedEventArgs e)
    {
      _hiddenSpells.Clear();
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
        if (dialog.ShowDialog() == true)
        {
          var gzipFileName = new FileInfo(dialog.FileName);

          var decompressionStream = new GZipStream(gzipFileName.OpenRead(), CompressionMode.Decompress);
          var reader = new StreamReader(decompressionStream);
          var json = reader.ReadToEnd();
          reader.Close();

          var data = JsonSerializer.Deserialize<SpellCountsSerialized>(json);

          // copy data
          _playerList = _playerList.Union(data.PlayerNames).ToList();

          foreach (var player in data.TheSpellData.PlayerCastCounts.Keys)
          {
            _theSpellCounts.PlayerCastCounts[player] = data.TheSpellData.PlayerCastCounts[player];
          }

          foreach (var player in data.TheSpellData.PlayerInterruptedCounts.Keys)
          {
            _theSpellCounts.PlayerInterruptedCounts[player] = data.TheSpellData.PlayerInterruptedCounts[player];
          }

          foreach (var player in data.TheSpellData.PlayerReceivedCounts.Keys)
          {
            _theSpellCounts.PlayerReceivedCounts[player] = data.TheSpellData.PlayerReceivedCounts[player];
          }

          foreach (var spellId in data.TheSpellData.MaxCastCounts.Keys)
          {
            if (!_theSpellCounts.MaxCastCounts.ContainsKey(spellId) || _theSpellCounts.MaxCastCounts[spellId] < data.TheSpellData.MaxCastCounts[spellId])
            {
              _theSpellCounts.MaxCastCounts[spellId] = data.TheSpellData.MaxCastCounts[spellId];
            }
          }

          foreach (var spellId in data.TheSpellData.MaxReceivedCounts.Keys)
          {
            if (!_theSpellCounts.MaxReceivedCounts.ContainsKey(spellId) || _theSpellCounts.MaxReceivedCounts[spellId] < data.TheSpellData.MaxReceivedCounts[spellId])
            {
              _theSpellCounts.MaxReceivedCounts[spellId] = data.TheSpellData.MaxReceivedCounts[spellId];
            }
          }

          foreach (var spellData in data.TheSpellData.UniqueSpells.Keys)
          {
            _theSpellCounts.UniqueSpells.TryAdd(spellData, data.TheSpellData.UniqueSpells[spellData]);
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
        var data = new SpellCountsSerialized { TheSpellData = _theSpellCounts };
        data.PlayerNames.AddRange(_playerList);

        var result = JsonSerializer.Serialize(data);
        var saveFileDialog = new SaveFileDialog();
        const string filter = "Spell Count File (*.scf.gz)|*.scf.gz";
        saveFileDialog.Filter = filter;
        if (saveFileDialog.ShowDialog() == true)
        {
          var gzipFileName = new FileInfo(saveFileDialog.FileName);
          var gzipTargetAsStream = gzipFileName.Create();
          var gzipStream = new GZipStream(gzipTargetAsStream, CompressionMode.Compress);
          var writer = new StreamWriter(gzipStream);
          writer.Write(result);
          writer.Close();
        }
      }
      catch (Exception ex)
      {
        new MessageWindow("Problem Exporting Spell Counts Data. Check Error Log for details.", Resource.EXPORT_ERROR).ShowDialog();
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
            _hiddenSpells[spr["Spell"] as string ?? string.Empty] = 1;
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
          _hiddenSpells[spr["Spell"] as string ?? string.Empty] = 1;
          dataGrid.View.Remove(spr);
          UpdateCounts();
        }
      });
    }

    private void UpdateCounts()
    {
      var counts = _playerList.ToDictionary(key => key, _ => 0.0);
      foreach (var record in dataGrid.View.Records)
      {
        var data = record.Data as dynamic;
        foreach (var value in data)
        {
          if (_playerList.Contains(value.Key))
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
      playerColumns.OrderBy(key => key.HeaderText, _totalColumnComparer).ToList().ForEach(col => dataGrid.Columns[colIndex++] = col);
    }

    private string GetHeaderValue(string name, double amount, double total)
    {
      var result = _currentCountType switch
      {
        0 => amount,
        1 => total > 0 ? Math.Round(amount / total * 100, 2) : 0,
        2 => _time > 0 ? Math.Round(amount / _time * 60, 2) : 0,
        _ => 0.0
      };

      return $"{name} = {result}";
    }

    private string GetFormattedValue(double value, double playerTotal)
    {
      if (_currentCountType == 1)
      {
        return (playerTotal > 0 ? Math.Round(value / playerTotal * 100, 2) : 0.0).ToString(CultureInfo.InvariantCulture);
      }

      if (_currentCountType == 2)
      {
        return (_time > 0 ? Math.Round(value / _time * 60, 2) : 0.0).ToString(CultureInfo.InvariantCulture);
      }

      return value.ToString(CultureInfo.InvariantCulture);
    }
  }

  internal class SpellCountsSerialized
  {
    public List<string> PlayerNames { get; set; } = [];
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

      return string.Compare(x, y, StringComparison.Ordinal);
    }
  }
}
