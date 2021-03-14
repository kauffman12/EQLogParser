using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for LootViewer.xaml
  /// </summary>
  public partial class LootViewer : UserControl, IDisposable
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private const string ALLPLAYERS = "All Players";
    private const string ALLITEMS = "All Loot";
    private const string ONLYCURR = "Only Currency";
    private const string ONLYITEMS = "Only Items";

    private static object CollectionLock = new object();
    private List<string> Options = new List<string>() { "Individual View", "Summary View" };
    private ObservableCollection<LootRow> IndividualRecords = new ObservableCollection<LootRow>();
    private ObservableCollection<LootRow> TotalRecords = new ObservableCollection<LootRow>();
    private ICollectionView IndividualView = null;
    private ICollectionView SummaryView = null;
    private bool ShowSummaryView = false;
    private string CurrentSelectedItem = ALLITEMS;
    private string CurrentSelectedPlayer = ALLPLAYERS;
    private static bool Running = false;

    public LootViewer()
    {
      InitializeComponent();

      optionsList.ItemsSource = Options;
      optionsList.SelectedIndex = 0;

      IndividualView = CollectionViewSource.GetDefaultView(IndividualRecords);
      SummaryView = CollectionViewSource.GetDefaultView(TotalRecords);

      IndividualView.Filter = SummaryView.Filter = new Predicate<object>(obj =>
      {
        bool found = false;

        if (obj is LootRow row)
        {
          found = (CurrentSelectedItem == ALLITEMS || row.IsCurrency && CurrentSelectedItem == ONLYCURR ||
          !row.IsCurrency && CurrentSelectedItem == ONLYITEMS || CurrentSelectedItem == row.Item) && 
          (CurrentSelectedPlayer == ALLPLAYERS || row.Player == CurrentSelectedPlayer);
        }

        return found;
      });

      dataGrid.ItemsSource = IndividualView;

      BindingOperations.EnableCollectionSynchronization(IndividualRecords, CollectionLock);
      BindingOperations.EnableCollectionSynchronization(TotalRecords, CollectionLock);

      dataGrid.Sorting += (s, e2) =>
      {
        if (!string.IsNullOrEmpty(e2.Column.Header as string) && e2.Column.Header as string == "Quantity")
        {
          e2.Column.SortDirection = e2.Column.SortDirection ?? ListSortDirection.Ascending;
        }
        else
        {
          e2.Column.SortDirection = e2.Column.SortDirection ?? ListSortDirection.Descending;
        }
      };

      (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete += LootViewer_EventsLogLoadingComplete;
      Load();
    }

    private void Load()
    {
      if (!Running)
      {
        Running = true;
        itemsList.IsEnabled = playersList.IsEnabled = optionsList.IsEnabled = false;

        Task.Delay(75).ContinueWith(task =>
        {
          lock (CollectionLock)
          {
            TotalRecords.Clear();
            IndividualRecords.Clear();
          }

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
            ONLYCURR,
            ONLYITEMS
          };

          DataManager.Instance.GetAllLoot().ForEach(blocks =>
          {
            blocks.Actions.ForEach(record =>
            {
              if (record is LootRecord looted)
              {
                lock(CollectionLock)
                {
                  IndividualRecords.Add(CreateRow(looted, blocks.BeginTime));
                }

                UpdateTotals(totalRecords, looted);

                uniquePlayers[looted.Player] = 1;
                if (!looted.IsCurrency)
                {
                  uniqueItems[looted.Item] = 1;
                }
              }
            });
          });

          uniquePlayers.Keys.OrderBy(player => player).ToList().ForEach(newPlayer => players.Add(newPlayer));
          uniqueItems.Keys.OrderBy(item => item).ToList().ForEach(newItem => itemNames.Add(newItem));

          lock (CollectionLock)
          {
            totalRecords.OrderByDescending(row => row.Quantity).ToList().ForEach(s => TotalRecords.Add(s));
          }

          Dispatcher.InvokeAsync(() =>
          {
            itemsList.ItemsSource = itemNames;
            playersList.ItemsSource = players;

            UpdateUI();

            itemsList.SelectedItem = CurrentSelectedItem;
            playersList.SelectedItem = CurrentSelectedPlayer;

            Running = false;
          });

        }, TaskScheduler.Default);
      }
    }

    private void UpdateUI()
    {
      (dataGrid.ItemsSource as ICollectionView)?.Refresh();
      titleLabel.Content = dataGrid.Items.Count == 0 ? "No Loot Found" : dataGrid.Items.Count + " Loot Entries Found";
      itemsList.IsEnabled = itemsList.Items.Count > 3;
      playersList.IsEnabled = playersList.Items.Count > 1;
      optionsList.IsEnabled = true;
    }

    private void LoadingRow(object sender, DataGridRowEventArgs e)
    {
      // set header count
      if (e.Row != null)
      {
        e.Row.Header = (e.Row.GetIndex() + 1).ToString(CultureInfo.CurrentCulture);
      }
    }

    private void OptionsChanged(object sender, SelectionChangedEventArgs e)
    {
      if (!Running && dataGrid != null && IndividualRecords?.Count > 0)
      {
        ShowSummaryView = optionsList.SelectedIndex != 0;
        CurrentSelectedPlayer = playersList.SelectedItem as string;
        CurrentSelectedItem = itemsList.SelectedItem as string;

        if (ShowSummaryView && dataGrid.ItemsSource != SummaryView)
        {
          dataGrid.ItemsSource = SummaryView;
          dataGrid.Columns[0].Visibility = dataGrid.Columns[4].Visibility = Visibility.Collapsed;
        }
        else if (!ShowSummaryView && dataGrid.ItemsSource != IndividualView)
        {
          dataGrid.ItemsSource = IndividualView;
          dataGrid.Columns[0].Visibility = dataGrid.Columns[4].Visibility = Visibility.Visible;
        }

        UpdateUI();
      }
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e)
    {
      try
      {
        var export = BuildExportData();
        string result = TextFormatUtils.BuildCsv(export.Item1, export.Item2);
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

    private Tuple<List<string>, List<List<object>>> BuildExportData()
    {
      List<string> header = new List<string>();
      List<List<object>> data = new List<List<object>>();

      for (int col = 0; col < dataGrid.Columns.Count; col++)
      {
        if (dataGrid.Columns[col].Visibility == Visibility.Visible)
        {
          header.Add(dataGrid.Columns[col].Header as string);
        }
      }

      foreach (var item in dataGrid.Items)
      {
        if (item is LootRow looted)
        {
          var row = new List<object>();
          for (int col = 0; col < dataGrid.Columns.Count; col++)
          {
            if (dataGrid.Columns[col].Visibility == Visibility.Visible)
            {
              switch(dataGrid.Columns[col].Header.ToString())
              {
                case "Time":
                  row.Add(DateUtil.FormatSimpleDate(looted.Time));
                  break;
                case "Player":
                  row.Add(looted.Player);
                  break;
                case "Item":
                  row.Add(looted.Item);
                  break;
                case "Quantity":
                  if (looted.IsCurrency)
                  {
                    row.Add("");
                  }
                  else
                  {
                    row.Add(looted.Quantity);
                  }
                  break;
                case "NPC":
                  row.Add(looted.Npc);
                  break;
              }
            }
          }

          data.Add(row);
        }
      }

      return new Tuple<List<string>, List<List<object>>>(header, data);
    }

    private static LootRow CreateRow(LootRecord looted, double time = 0)
    {
      return new LootRow()
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
        values.Add(silver + " Silver");
      }

      if (rem % 10 is uint copper && copper > 0)
      {
        values.Add(copper + " Copper");
      }

      return string.Join(", ", values);
    }

    private void LootViewer_EventsLogLoadingComplete(object sender, bool e) => Load();
    private void RefreshMouseClick(object sender, MouseButtonEventArgs e) => Load();

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      DamageStatsManager.Instance.FireChartEvent(new GenerateStatsOptions() { RequestChartData = true }, "UPDATE");

      if (!disposedValue)
      {
        if (disposing)
        {
          // TODO: dispose managed state (managed objects).
        }

        (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete -= LootViewer_EventsLogLoadingComplete;
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
