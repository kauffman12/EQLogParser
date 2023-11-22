using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for LootViewer.xaml
  /// </summary>
  public partial class LootViewer : IDisposable
  {
    private const string ALL_NPCS = "All NPCs";
    private const string ALL_PLAYERS = "All Players";
    private const string ALL_ITEMS = "All Loot";
    private const string ONLY_ASSIGNED = "Only Assigned";
    private const string ONLY_CURRENCY = "Only Currency";
    private const string ONLY_ITEMS = "Only Items";

    private readonly DispatcherTimer ReloadTimer;
    private readonly List<string> Options = new() { "Individual View", "Summary View" };
    private readonly ObservableCollection<LootRow> IndividualRecords = new();
    private readonly ObservableCollection<LootRow> TotalRecords = new();
    private bool ShowSummaryView;
    private string CurrentSelectedItem = ALL_ITEMS;
    private string CurrentSelectedPlayer = ALL_PLAYERS;
    private string CurrentSelectedNpc = ALL_NPCS;

    public LootViewer()
    {
      InitializeComponent();
      optionsList.ItemsSource = Options;
      optionsList.SelectedIndex = 0;

      // default these columns to descending
      var desc = new[] { "Quantity", "Time" };
      dataGrid.SortColumnsChanging += (s, e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      dataGrid.SortColumnsChanged += (s, e) => DataGridUtil.SortColumnsChanged(s, e, desc);
      RecordManager.Instance.RecordsUpdatedEvent += RecordsUpdatedEvent;
      DataGridUtil.UpdateTableMargin(dataGrid);
      MainActions.EventsThemeChanged += EventsThemeChanged;
      dataGrid.ItemsSource = IndividualRecords;

      ReloadTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1500) };
      ReloadTimer.Tick += ReloadTimerTick;
      Load();
    }

    private void ReloadTimerTick(object sender, EventArgs e) => Load();

    private void RecordsUpdatedEvent(string type)
    {
      if (type == RecordManager.LOOT_RECORDS && !ReloadTimer.IsEnabled)
      {
        ReloadTimer.Start();
      }
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(dataGrid, titleLabel.Content.ToString());
    private void CreateImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(dataGrid, titleLabel);
    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);

    private void Load()
    {
      var totalRecords = new List<LootRow>();
      var uniquePlayers = new Dictionary<string, byte>();
      var uniqueItems = new Dictionary<string, byte>();
      var uniqueNpcs = new Dictionary<string, byte>();

      var players = new List<string>
      {
        ALL_PLAYERS
      };

      var itemNames = new List<string>
      {
        ALL_ITEMS,
        ONLY_ASSIGNED,
        ONLY_CURRENCY,
        ONLY_ITEMS
      };

      var npcs = new List<string>
      {
       ALL_NPCS
      };

      var i = 0;
      foreach (var (beginTime, looted) in RecordManager.Instance.GetAllLoot().Reverse())
      {
        var row = new LootRow { Time = beginTime, Record = looted };
        if (IndividualRecords.Count > i)
        {
          if (!IndividualRecords[i].Time.Equals(row.Time) || !IndividualRecords[i].Record.Equals(row.Record))
          {
            IndividualRecords[i] = row;
          }
        }
        else
        {
          IndividualRecords.Add(row);
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

      for (var j = IndividualRecords.Count - 1; j >= i; j--)
      {
        IndividualRecords.RemoveAt(j);
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
        if (TotalRecords.Count > i)
        {
          if (!TotalRecords[i].Time.Equals(row.Time) || !TotalRecords[i].Record.Equals(row.Record))
          {
            TotalRecords[i] = row;
          }
        }
        else
        {
          TotalRecords.Add(row);
        }

        i++;
      }

      for (var j = TotalRecords.Count - 1; j >= i; j--)
      {
        TotalRecords.RemoveAt(j);
      }

      UpdateItems(itemsList, itemNames, CurrentSelectedItem);
      UpdateItems(playersList, players, CurrentSelectedPlayer);
      UpdateItems(npcsList, npcs, CurrentSelectedNpc);
      dataGrid?.View?.Refresh();
      dataGrid?.GridColumnSizer.ResetAutoCalculationforAllColumns();
      UpdateTitle();
      ReloadTimer.Stop();
    }

    private void UpdateItems(ComboBox combo, List<string> items, string selected)
    {
      if (combo.ItemsSource is not List<string> current || !current.SequenceEqual(items))
      {
        combo.ItemsSource = items;
        combo.SelectedItem = selected;
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
        ShowSummaryView = optionsList.SelectedIndex != 0;
        CurrentSelectedPlayer = playersList.SelectedItem as string;
        CurrentSelectedItem = itemsList.SelectedItem as string;
        CurrentSelectedNpc = npcsList.SelectedItem as string;

        if (ShowSummaryView && dataGrid.ItemsSource != TotalRecords)
        {
          dataGrid.ItemsSource = TotalRecords;
          dataGrid.Columns[0].IsHidden = dataGrid.Columns[4].IsHidden = true;
        }
        else if (!ShowSummaryView && dataGrid.ItemsSource != IndividualRecords)
        {
          dataGrid.ItemsSource = IndividualRecords;
          dataGrid.Columns[0].IsHidden = dataGrid.Columns[4].IsHidden = false;
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
      if (dataGrid.View != null)
      {
        dataGrid.View.Filter = item =>
        {
          if (item is not LootRow row)
          {
            return false;
          }

          var found = (CurrentSelectedItem == ALL_ITEMS || (row.Record.IsCurrency && CurrentSelectedItem == ONLY_CURRENCY) ||
            (!row.Record.IsCurrency && CurrentSelectedItem == ONLY_ITEMS) ||
            (CurrentSelectedItem == ONLY_ASSIGNED && !row.Record.IsCurrency && row.Record.Quantity == 0) || CurrentSelectedItem == row.Record.Item) &&
            (CurrentSelectedPlayer == ALL_PLAYERS || row.Record.Player == CurrentSelectedPlayer);
          return found && (CurrentSelectedNpc == ALL_NPCS || row.Record.Npc == CurrentSelectedNpc);
        };

        dataGrid.View.RefreshFilter();
      }

      UpdateTitle();
    }

    private static void UpdateTotals(ICollection<LootRow> totalRecords, LootRecord looted)
    {
      if (App.AutoMap.Map(looted, new LootRecord()) is { } copied)
      {
        var row = new LootRow { Time = 0, Record = copied };
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

    #region IDisposable Support
    private bool DisposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!DisposedValue)
      {
        ReloadTimer.Tick -= ReloadTimerTick;
        MainActions.EventsThemeChanged -= EventsThemeChanged;
        RecordManager.Instance.RecordsUpdatedEvent -= RecordsUpdatedEvent;
        dataGrid.Dispose();
        DisposedValue = true;
      }
    }

    // This code added to correctly implement the disposable pattern.
    public void Dispose()
    {
      // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
      Dispose(true);
      GC.SuppressFinalize(this);
    }
    #endregion
  }
}
