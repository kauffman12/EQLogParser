using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for LootViewer.xaml
  /// </summary>
  public partial class LootViewer : UserControl, IDisposable
  {
    private const string ALLPLAYERS = "All Players";
    private const string ALLITEMS = "All Loot";
    private const string ONLYASS = "Only Assigned";
    private const string ONLYCURR = "Only Currency";
    private const string ONLYITEMS = "Only Items";

    private readonly List<string> Options = new List<string>() { "Individual View", "Summary View" };
    private readonly ObservableCollection<LootRow> IndividualRecords = new ObservableCollection<LootRow>();
    private readonly ObservableCollection<LootRow> TotalRecords = new ObservableCollection<LootRow>();
    private bool ShowSummaryView = false;
    private string CurrentSelectedItem = ALLITEMS;
    private string CurrentSelectedPlayer = ALLPLAYERS;

    public LootViewer()
    {
      InitializeComponent();

      optionsList.ItemsSource = Options;
      optionsList.SelectedIndex = 0;
      dataGrid.ItemsSource = IndividualRecords;

      // default these columns to descending
      string[] desc = new string[] { "Quantity" };
      dataGrid.SortColumnsChanging += (object s, GridSortColumnsChangingEventArgs e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      dataGrid.SortColumnsChanged += (object s, GridSortColumnsChangedEventArgs e) => DataGridUtil.SortColumnsChanged(s, e, desc);
      (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete += EventsLogLoadingComplete;
      Load();
    }

    private void Load()
    {
      TotalRecords.Clear();
      IndividualRecords.Clear();

      List<LootRow> totalRecords = new List<LootRow>();
      Dictionary<string, byte> uniquePlayers = new Dictionary<string, byte>();
      Dictionary<string, byte> uniqueItems = new Dictionary<string, byte>();

      List<string> players = new List<string>
      {
        ALLPLAYERS
      };

      List<string> itemNames = new List<string>
      {
        ALLITEMS,
        ONLYASS,
        ONLYCURR,
        ONLYITEMS
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

      foreach (ref var row in totalRecords.OrderByDescending(row => row.Quantity).ToArray().AsSpan())
      {
        TotalRecords.Add(row);
      }

      itemsList.ItemsSource = itemNames;
      playersList.ItemsSource = players;
      itemsList.SelectedItem = CurrentSelectedItem;
      playersList.SelectedItem = CurrentSelectedPlayer;

      // delay before view is available
      Dispatcher.InvokeAsync(() => UpdateUI());
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(dataGrid, titleLabel.Content.ToString());
    private void CreateImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(dataGrid, titleLabel);
    private void RefreshClick(object sender, RoutedEventArgs e) => Load();
    private void EventsLogLoadingComplete(object sender, bool e) => Load();

    private void UpdateUI()
    {
      dataGrid.View.RefreshFilter();
      titleLabel.Content = dataGrid.View.Records.Count == 0 ? "No Loot Found" : dataGrid.View.Records.Count + " Loot Entries Found";
    }

    private void OptionsChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
      if (dataGrid != null && dataGrid.View != null)
      {
        ShowSummaryView = optionsList.SelectedIndex != 0;
        CurrentSelectedPlayer = playersList.SelectedItem as string;
        CurrentSelectedItem = itemsList.SelectedItem as string;

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

        if (dataGrid.View.Filter == null)
        {
          dataGrid.View.Filter = new Predicate<object>(obj =>
          {
            bool found = false;

            if (obj is LootRow row)
            {
              found = (CurrentSelectedItem == ALLITEMS || row.IsCurrency && CurrentSelectedItem == ONLYCURR ||
              !row.IsCurrency && CurrentSelectedItem == ONLYITEMS || CurrentSelectedItem == ONLYASS && !row.IsCurrency && row.Quantity == 0 || CurrentSelectedItem == row.Item) &&
              (CurrentSelectedPlayer == ALLPLAYERS || row.Player == CurrentSelectedPlayer);
            }

            return found;
          });
        }

        UpdateUI();
      }
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

      if (totalRecords.AsParallel().FirstOrDefault(item => !looted.IsCurrency && !item.IsCurrency && looted.Player == item.Player && looted.Item == item.Item) is LootRow existingItem)
      {
        existingItem.Quantity += looted.Quantity;
      }
      else if (totalRecords.AsParallel().FirstOrDefault(item => looted.IsCurrency && item.IsCurrency && looted.Player == item.Player) is LootRow existingMoney)
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
      List<string> values = new List<string>();

      if (amount / 1000 is uint plat && plat > 0)
      {
        values.Add(plat + " Platinum");
      }

      var rem = amount % 1000;
      if (rem / 100 is uint gold && gold > 0)
      {
        values.Add(gold + " Gold");
      }

      rem = amount % 100;
      if (rem / 10 is uint silver && silver > 0)
      {
        values.Add(silver.ToString(CultureInfo.CurrentCulture) + " Silver");
      }

      if (rem % 10 is uint copper && copper > 0)
      {
        values.Add(copper.ToString(CultureInfo.CurrentCulture) + " Copper");
      }

      return string.Join(", ", values);
    }

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete -= EventsLogLoadingComplete;
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
