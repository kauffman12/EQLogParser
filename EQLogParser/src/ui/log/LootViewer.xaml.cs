using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for LootViewer.xaml
  /// </summary>
  public partial class LootViewer : UserControl, IDisposable
  {
    private const string ALLNPCS = "All NPCs";
    private const string ALLPLAYERS = "All Players";
    private const string ALLITEMS = "All Loot";
    private const string ONLYASS = "Only Assigned";
    private const string ONLYCURR = "Only Currency";
    private const string ONLYITEMS = "Only Items";

    private readonly List<string> Options = new() { "Individual View", "Summary View" };
    private readonly ObservableCollection<LootRow> IndividualRecords = new();
    private readonly ObservableCollection<LootRow> TotalRecords = new();
    private bool ShowSummaryView;
    private string CurrentSelectedItem = ALLITEMS;
    private string CurrentSelectedPlayer = ALLPLAYERS;
    private string CurrentSelectedNpc = ALLNPCS;

    public LootViewer()
    {
      InitializeComponent();

      optionsList.ItemsSource = Options;
      optionsList.SelectedIndex = 0;

      // default these columns to descending
      var desc = new[] { "Quantity", "Time" };
      dataGrid.SortColumnsChanging += (s, e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      dataGrid.SortColumnsChanged += (s, e) => DataGridUtil.SortColumnsChanged(s, e, desc);
      MainActions.EventsLogLoadingComplete += EventsLogLoadingComplete;
      DataGridUtil.UpdateTableMargin(dataGrid);
      MainActions.EventsThemeChanged += EventsThemeChanged;

      dataGrid.ItemsSource = IndividualRecords;
      Load();
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(dataGrid, titleLabel.Content.ToString());
    private void CreateImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(dataGrid, titleLabel);
    private void RefreshClick(object sender, RoutedEventArgs e) => Load();
    private void EventsLogLoadingComplete(string _) => Load();
    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);

    private void Load()
    {
      TotalRecords.Clear();
      IndividualRecords.Clear();

      var totalRecords = new List<LootRow>();
      var uniquePlayers = new Dictionary<string, byte>();
      var uniqueItems = new Dictionary<string, byte>();
      var uniqueNpcs = new Dictionary<string, byte>();

      var players = new List<string>
      {
        ALLPLAYERS
      };

      var itemNames = new List<string>
      {
        ALLITEMS,
        ONLYASS,
        ONLYCURR,
        ONLYITEMS
      };

      var npcs = new List<string>
      {
       ALLNPCS
      };

      DataManager.Instance.GetAllLoot().ForEach(block =>
      {
        // lock since actions can be removed by the parsing thread
        lock (block.Actions)
        {
          block.Actions.ForEach(record =>
          {
            if (record is LootRecord looted)
            {
              IndividualRecords.Add(CreateRow(looted, block.BeginTime));
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
            }
          });
        }
      });

      foreach (ref var player in uniquePlayers.Keys.OrderBy(player => player).ToArray().AsSpan())
      {
        players.Add(player);
      }

      foreach (ref var item in uniqueItems.Keys.OrderBy(item => item).ToArray().AsSpan())
      {
        itemNames.Add(item);
      }

      foreach (ref var npc in uniqueNpcs.Keys.OrderBy(npc => npc).ToArray().AsSpan())
      {
        npcs.Add(npc);
      }

      foreach (ref var row in totalRecords.OrderByDescending(row => row.Quantity).ToArray().AsSpan())
      {
        TotalRecords.Add(row);
      }

      itemsList.ItemsSource = itemNames;
      playersList.ItemsSource = players;
      npcsList.ItemsSource = npcs;
      itemsList.SelectedItem = CurrentSelectedItem;
      playersList.SelectedItem = CurrentSelectedPlayer;
      npcsList.SelectedItem = CurrentSelectedNpc;
      dataGrid?.View?.Refresh();
      dataGrid?.GridColumnSizer.ResetAutoCalculationforAllColumns();
      UpdateTitle();
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
        dataGrid.View.Filter = obj =>
        {
          var found = false;

          if (obj is LootRow row)
          {
            found = (CurrentSelectedItem == ALLITEMS || (row.IsCurrency && CurrentSelectedItem == ONLYCURR) ||
                     (!row.IsCurrency && CurrentSelectedItem == ONLYITEMS) ||
                     (CurrentSelectedItem == ONLYASS && !row.IsCurrency && row.Quantity == 0) ||
                     CurrentSelectedItem == row.Item) &&
                    (CurrentSelectedPlayer == ALLPLAYERS || row.Player == CurrentSelectedPlayer);

            found = found && (CurrentSelectedNpc == ALLNPCS || row.Npc == CurrentSelectedNpc);
          }

          return found;
        };

        dataGrid.View.RefreshFilter();
      }

      UpdateTitle();
    }

    private static LootRow CreateRow(LootRecord looted, double time = 0)
    {
      return new LootRow
      {
        Time = time,
        Item = looted.Item,
        Quantity = looted.Quantity,
        Player = looted.Player,
        IsCurrency = looted.IsCurrency,
        Npc = string.IsNullOrEmpty(looted.Npc) ? "-" : looted.Npc
      };
    }

    private static void UpdateTotals(List<LootRow> totalRecords, LootRecord looted)
    {
      var row = CreateRow(looted);
      if (totalRecords.AsParallel().FirstOrDefault(item => !looted.IsCurrency && !item.IsCurrency && looted.Player == item.Player && looted.Item == item.Item) is { } existingItem)
      {
        existingItem.Quantity += looted.Quantity;
      }
      else if (totalRecords.AsParallel().FirstOrDefault(item => looted.IsCurrency && item.IsCurrency && looted.Player == item.Player) is { } existingMoney)
      {
        existingMoney.Quantity += looted.Quantity;
        existingMoney.Item = GetMoneyDescription(existingMoney.Quantity);
      }
      else
      {
        totalRecords.Add(row);
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
    private bool disposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        MainActions.EventsThemeChanged -= EventsThemeChanged;
        MainActions.EventsLogLoadingComplete -= EventsLogLoadingComplete;
        dataGrid.Dispose();
        disposedValue = true;
      }
    }

    // This code added to correctly implement the disposable pattern.
    public void Dispose()
    {
      // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
      Dispose(true);
      // TODO: uncomment the following line if the finalizer is overridden above.
      GC.SuppressFinalize(this);
    }
    #endregion
  }
}
