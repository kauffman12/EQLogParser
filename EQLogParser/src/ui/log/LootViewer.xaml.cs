using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace EQLogParser
{
  public partial class LootViewer : IDocumentContent
  {
    private const string AllNpcs = "All NPCs";
    private const string AllPlayers = "All Players";
    private const string AllItems = "All Loot";
    private const string OnlyAssigned = "Only Assigned";
    private const string OnlyCurrency = "Only Currency";
    private const string OnlyItems = "Only Items";

    private readonly DispatcherTimer _reloadTimer;
    private readonly List<string> _options = ["Individual View", "Summary View"];
    private readonly ObservableCollection<LootRow> _individualRecords = [];
    private readonly ObservableCollection<LootRow> _totalRecords = [];
    private bool _showSummaryView;
    private string _currentSelectedItem = AllItems;
    private string _currentSelectedPlayer = AllPlayers;
    private string _currentSelectedNpc = AllNpcs;
    private bool _ready;

    public LootViewer()
    {
      InitializeComponent();
      optionsList.ItemsSource = _options;
      optionsList.SelectedIndex = 0;

      // default these columns to descending
      var desc = new[] { "Quantity", "Time" };
      dataGrid.SortColumnsChanging += (s, e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      dataGrid.SortColumnsChanged += (s, e) => DataGridUtil.SortColumnsChanged(s, e, desc);
      RecordManager.Instance.RecordsUpdatedEvent += RecordsUpdatedEvent;
      MainActions.EventsThemeChanged += EventsThemeChanged;
      dataGrid.ItemsSource = _individualRecords;
      _reloadTimer = UiUtil.CreateTimer(ReloadTimerTick, 1500, false);
    }

    private void ReloadTimerTick(object sender, EventArgs e) => Load();

    private void RecordsUpdatedEvent(string type)
    {
      if (type == RecordManager.LootRecords && !_reloadTimer.IsEnabled)
      {
        _reloadTimer.Start();
      }
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(dataGrid, titleLabel.Content.ToString());
    private async void CreateImageClick(object sender, RoutedEventArgs e) => await DataGridUtil.CreateImageAsync(dataGrid, titleLabel);
    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);
    private void LogLoadingComplete(string _) => Load();

    private void Load()
    {
      _individualRecords.Clear();
      _totalRecords.Clear();

      var totalRecords = new List<LootRow>();
      var uniquePlayers = new Dictionary<string, byte>();
      var uniqueItems = new Dictionary<string, byte>();
      var uniqueNpcs = new Dictionary<string, byte>();

      var players = new List<string>
      {
        AllPlayers
      };

      var itemNames = new List<string>
      {
        AllItems,
        OnlyAssigned,
        OnlyCurrency,
        OnlyItems
      };

      var npcs = new List<string>
      {
       AllNpcs
      };

      var i = 0;
      foreach (var (beginTime, looted) in RecordManager.Instance.GetAllLoot().Reverse())
      {
        var row = new LootRow { BeginTime = beginTime, Record = looted };
        if (_individualRecords.Count > i)
        {
          if (!_individualRecords[i].BeginTime.Equals(row.BeginTime) || !_individualRecords[i].Record.Equals(row.Record))
          {
            _individualRecords[i] = row;
          }
        }
        else
        {
          _individualRecords.Add(row);
        }

        UpdateTotals(totalRecords, looted);
        uniquePlayers[looted.Player] = 1;

        // currency loots/splits
        if (!string.IsNullOrEmpty(looted.Npc))
        {
          uniqueNpcs[looted.Npc] = 1;
        }

        if (!looted.IsCurrency)
        {
          uniqueItems[looted.Item] = 1;
        }

        i++;
      }

      for (var j = _individualRecords.Count - 1; j >= i; j--)
      {
        _individualRecords.RemoveAt(j);
      }

      foreach (var player in uniquePlayers.Keys.OrderBy(player => player))
      {
        players.Add(player);
      }

      foreach (var item in uniqueItems.Keys.OrderBy(item => item))
      {
        itemNames.Add(item);
      }

      foreach (var npc in uniqueNpcs.Keys.OrderBy(npc => npc))
      {
        npcs.Add(npc);
      }

      i = 0;
      foreach (var row in totalRecords.OrderByDescending(row => row.Record.Quantity))
      {
        if (_totalRecords.Count > i)
        {
          if (!_totalRecords[i].BeginTime.Equals(row.BeginTime) || !_totalRecords[i].Record.Equals(row.Record))
          {
            _totalRecords[i] = row;
          }
        }
        else
        {
          _totalRecords.Add(row);
        }

        i++;
      }

      for (var j = _totalRecords.Count - 1; j >= i; j--)
      {
        _totalRecords.RemoveAt(j);
      }

      UpdateItems(itemsList, itemNames, _currentSelectedItem, AllItems);
      UpdateItems(playersList, players, _currentSelectedPlayer, AllPlayers);
      UpdateItems(npcsList, npcs, _currentSelectedNpc, AllNpcs);
      dataGrid?.View?.Refresh();
      UpdateTitle();
      _reloadTimer.Stop();
    }

    private static void UpdateItems(ComboBox combo, List<string> items, string selected, string original)
    {
      if (combo.ItemsSource is not List<string> current || !current.SequenceEqual(items))
      {
        combo.ItemsSource = items;
        combo.SelectedItem = items.Contains(selected) ? selected : original;
      }
    }

    private void UpdateTitle()
    {
      var count = dataGrid?.View != null ? dataGrid.View.Records.Count : 0;
      titleLabel.Content = count == 0 ? "No Loot Found" : count + " Loot Entries Found";
    }

    private void OptionsChanged(object sender, SelectionChangedEventArgs e)
    {
      if (dataGrid is { View: not null })
      {
        _showSummaryView = optionsList.SelectedIndex != 0;
        _currentSelectedPlayer = playersList.SelectedItem as string ?? _currentSelectedPlayer;
        _currentSelectedItem = itemsList.SelectedItem as string ?? _currentSelectedItem;
        _currentSelectedNpc = npcsList.SelectedItem as string ?? _currentSelectedNpc;

        if (_showSummaryView && dataGrid.ItemsSource != _totalRecords)
        {
          dataGrid.ItemsSource = _totalRecords;
          dataGrid.Columns[0].IsHidden = dataGrid.Columns[4].IsHidden = true;
          dataGrid.SortColumnDescriptions.Clear();
        }
        else if (!_showSummaryView && dataGrid.ItemsSource != _individualRecords)
        {
          dataGrid.ItemsSource = _individualRecords;
          dataGrid.Columns[0].IsHidden = dataGrid.Columns[4].IsHidden = false;
          dataGrid.SortColumnDescriptions.Add(new SortColumnDescription
          {
            ColumnName = "BeginTime",
            SortDirection = ListSortDirection.Descending
          });
        }
        else
        {
          dataGrid.View.Refresh();
        }

        UpdateTitle();
      }
    }

    private void ItemsSourceChanged(object sender, GridItemsSourceChangedEventArgs e)
    {
      if (dataGrid.ItemsSource != null)
      {
        dataGrid.View.Filter = item =>
        {
          if (item is not LootRow row)
          {
            return false;
          }

          var found = (_currentSelectedItem == AllItems || (row.Record.IsCurrency && _currentSelectedItem == OnlyCurrency) ||
            (!row.Record.IsCurrency && _currentSelectedItem == OnlyItems && row.Record.Quantity != 0) ||
            (_currentSelectedItem == OnlyAssigned && !row.Record.IsCurrency && row.Record.Quantity == 0) || _currentSelectedItem == row.Record.Item) &&
            (_currentSelectedPlayer == AllPlayers || row.Record.Player == _currentSelectedPlayer);
          return found && (_currentSelectedNpc == AllNpcs || row.Record.Npc == _currentSelectedNpc);
        };

        dataGrid.View.RefreshFilter();
      }

      UpdateTitle();
    }

    private static void UpdateTotals(List<LootRow> totalRecords, LootRecord looted)
    {
      if (App.AutoMap.Map(looted, new LootRecord()) is { } copied)
      {
        var row = new LootRow { BeginTime = 0, Record = copied };
        if (totalRecords.FirstOrDefault(item =>
          !looted.IsCurrency && !item.Record.IsCurrency && looted.Player == item.Record.Player && looted.Item == item.Record.Item) is { } existingItem)
        {
          existingItem.Record.Quantity += looted.Quantity;
        }
        else if (totalRecords.FirstOrDefault(item => looted.IsCurrency && item.Record.IsCurrency && looted.Player == item.Record.Player) is { } existingMoney)
        {
          existingMoney.Record.Quantity += looted.Quantity;
          existingMoney.Record.Item = GetMoneyDescription(existingMoney.Record.Quantity);
        }
        else
        {
          totalRecords.Add(row);
        }
      }
    }

    private static string GetMoneyDescription(uint amount)
    {
      var values = new List<string>();

      if (amount / 1000 is var plat and > 0)
      {
        values.Add(plat + " Platinum");
      }

      var rem = amount % 1000;
      if (rem / 100 is var gold and > 0)
      {
        values.Add(gold + " Gold");
      }

      rem = amount % 100;
      if (rem / 10 is var silver and > 0)
      {
        values.Add(silver.ToString(CultureInfo.CurrentCulture) + " Silver");
      }

      if (rem % 10 is var copper and > 0)
      {
        values.Add(copper.ToString(CultureInfo.CurrentCulture) + " Copper");
      }

      return string.Join(", ", values);
    }

    private void ContentLoaded(object sender, RoutedEventArgs e)
    {
      if (VisualParent != null && !_ready)
      {
        RecordManager.Instance.RecordsUpdatedEvent += RecordsUpdatedEvent;
        MainActions.EventsLogLoadingComplete += LogLoadingComplete;
        Load();
        _ready = true;
      }
    }

    public void HideContent()
    {
      RecordManager.Instance.RecordsUpdatedEvent -= RecordsUpdatedEvent;
      MainActions.EventsLogLoadingComplete -= LogLoadingComplete;
      _ready = false;
    }

    private void AutoGeneratingColumn(object sender, AutoGeneratingColumnArgs e)
    {
      var mapping = e.Column.MappingName;
      if (mapping is "BeginTime")
      {
        e.Column.DisplayBinding = new Binding
        {
          Path = new PropertyPath(mapping),
          Converter = new DateTimeConverter()
        };
        e.Column.TextAlignment = TextAlignment.Center;
        e.Column.Width = MainActions.CurrentDateTimeWidth;
        e.Column.HeaderText = "Time";
      }
      else if (mapping == "Record.Player")
      {
        e.Column.HeaderText = "Player";
        e.Column.Width = DataGridUtil.CalculateMinGridHeaderWidth(e.Column.HeaderText);
      }
      else if (mapping == "Record.Item")
      {
        e.Column.HeaderText = "Item";
        e.Column.Width = MainActions.CurrentItemWidth;
      }
      else if (mapping == "Record.Quantity")
      {
        e.Column.HeaderText = "Quantity";
        e.Column.CellTemplateSelector = new LootQuantityTemplateSelector();
        e.Column.Width = DataGridUtil.CalculateMinGridHeaderWidth(e.Column.HeaderText);
      }
      else if (mapping == "Record.Npc")
      {
        e.Column.HeaderText = "Npc";
        e.Column.Width = MainActions.CurrentItemWidth;
      }
      else if (mapping == "Record.IsCurrency")
      {
        e.Cancel = true;
      }
    }
  }

  internal class LootRow
  {
    public double BeginTime { get; set; }
    public LootRecord Record { get; set; }
  }
}
