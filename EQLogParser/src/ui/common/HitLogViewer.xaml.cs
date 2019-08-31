using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DamageLog.xaml
  /// </summary>
  public partial class HitLogViewer : UserControl
  {
    private readonly object CollectionLock = new object();
    private ObservableCollection<LogRow> Records = new ObservableCollection<LogRow>();
    private ObservableCollection<string> Actions = new ObservableCollection<string>();
    private ObservableCollection<string> Acted = new ObservableCollection<string>();
    private ObservableCollection<string> Types = new ObservableCollection<string>();

    private string CurrentDefenderFilter = null;
    private string CurrentActionFilter = null;
    private string CurrentTypeFilter = null;
    private bool CurrentShowPetsFilter = true;

    public HitLogViewer(CombinedStats currentStats, PlayerStats playerStats, List<List<ActionBlock>> groups, bool defending = false)
    {
      InitializeComponent();

      titleLabel.Content = currentStats?.ShortTitle;
      var view = CollectionViewSource.GetDefaultView(Records);

      view.Filter = new Predicate<object>(item =>
      {
        var record = (LogRow)item;
        return (string.IsNullOrEmpty(CurrentTypeFilter) || CurrentTypeFilter == record.Type) &&
        (string.IsNullOrEmpty(CurrentActionFilter) || CurrentActionFilter == record.SubType) && 
        (string.IsNullOrEmpty(CurrentDefenderFilter) || CurrentDefenderFilter == record.Acted) && 
        (CurrentShowPetsFilter || !record.IsPet);
      });

      BindingOperations.EnableCollectionSynchronization(Records, CollectionLock);
      BindingOperations.EnableCollectionSynchronization(Actions, CollectionLock);
      BindingOperations.EnableCollectionSynchronization(Acted, CollectionLock);
      BindingOperations.EnableCollectionSynchronization(Types, CollectionLock);

      Actions.Add("All Actions");
      Types.Add("All Types");

      var firstAction = groups?.First()?.First()?.Actions?.First();
      if (firstAction is DamageRecord && !defending)
      {
        Acted.Add("All Defenders");
        dataGrid.Columns[3].Header = "Damage";
        dataGrid.Columns[8].Header = "Attacker";
        dataGrid.Columns[9].Header = "Defender";
        showPets.Visibility = Visibility.Visible;
      }
      else if (firstAction is DamageRecord && defending)
      {
        Acted.Add("All Attackers");
        dataGrid.Columns[3].Header = "Damage";
        dataGrid.Columns[5].Visibility = Visibility.Collapsed;
        dataGrid.Columns[6].Visibility = Visibility.Collapsed;
        dataGrid.Columns[7].Visibility = Visibility.Collapsed;
        dataGrid.Columns[8].Header = "Defender";
        dataGrid.Columns[9].Header = "Attacker";
        showPets.Visibility = Visibility.Collapsed;
        divider.Visibility = Visibility.Collapsed;
      }
      else if (firstAction is HealRecord)
      {
        Acted.Add("All Healed Players");
        dataGrid.Columns[3].Header = "Heal";
        dataGrid.Columns[4].Visibility = Visibility.Visible;
        dataGrid.Columns[8].Header = "Healer";
        dataGrid.Columns[9].Header = "Healed";
        showPets.Visibility = Visibility.Collapsed;
        divider.Visibility = Visibility.Collapsed;
      }

      actionList.ItemsSource = Actions;
      actionList.SelectedIndex = 0;
      actedList.ItemsSource = Acted;
      actedList.SelectedIndex = 0;
      typeList.ItemsSource = Types;
      typeList.SelectedIndex = 0;
      dataGrid.ItemsSource = view;

      Task.Delay(125).ContinueWith(task =>
      {
        Helpers.SetBusy(true);

        Dictionary<string, byte> uniqueDefenders = new Dictionary<string, byte>();
        Dictionary<string, byte> uniqueActions = new Dictionary<string, byte>();
        Dictionary<string, byte> uniqueTypes = new Dictionary<string, byte>();

        groups?.ForEach(group =>
        {
          group.ForEach(block =>
          {
            block.Actions.ForEach(action =>
            {
              var currentTime = block.BeginTime;
              if (CreateRow(playerStats, action, block.BeginTime, defending) is LogRow row)
              {
                lock (CollectionLock)
                {
                  Records.Add(row);
                }

                PopulateOption(uniqueActions, row.SubType, Actions);
                PopulateOption(uniqueDefenders, row.Acted, Acted);
                PopulateOption(uniqueTypes, row.Type, Types);
              }
            });
          });
        });

        Dispatcher.InvokeAsync(() =>
        {
          actionList.IsEnabled = true;
          typeList.IsEnabled = true;
          actedList.IsEnabled = true;
          showPets.IsEnabled = true;
        });

        Helpers.SetBusy(false);
      }, TaskScheduler.Default);
    }

    private void PopulateOption(Dictionary<string, byte> cache, string value, ObservableCollection<string> list)
    {
      if (!string.IsNullOrEmpty(value) && !cache.ContainsKey(value))
      {
        cache[value] = 1;

        lock (CollectionLock)
        {
          if (list.Count == 1)
          {
            list.Insert(1, value);
          }
          else
          {
            var index = list.Skip(1).ToList().FindIndex(item => string.Compare(item, value, StringComparison.OrdinalIgnoreCase) >= 0);
            if (index == -1)
            {
              list.Add(value);
            }
            else
            {
              list.Insert(index + 1, value);
            }
          }
        }
      }
    }

    private void OptionsChanged(object sender, EventArgs e)
    {
      if (dataGrid != null && Records.Count > 0)
      {
        CurrentDefenderFilter = actedList.SelectedIndex == 0 ? null : actedList.SelectedItem as string;
        CurrentActionFilter = actionList.SelectedIndex == 0 ? null : actionList.SelectedItem as string;
        CurrentTypeFilter = typeList.SelectedIndex == 0 ? null : typeList.SelectedItem as string;
        CurrentShowPetsFilter = showPets.IsChecked.Value;
        (dataGrid.ItemsSource as ICollectionView)?.Refresh();
      }
    }

    private void LoadingRow(object sender, DataGridRowEventArgs e)
    {
      // set header count
      e.Row.Header = (e.Row.GetIndex() + 1).ToString(CultureInfo.CurrentCulture);
    }

    private LogRow CreateRow(PlayerStats playerStats, IAction action, double currentTime, bool defending = false)
    {
      LogRow row = null;
      if (action is DamageRecord damage && !defending && !string.IsNullOrEmpty(damage.Attacker) && !string.IsNullOrEmpty(playerStats.OrigName) && damage.Type != Labels.MISS)
      {
        bool isPet = false;
        if (damage.Attacker.Equals(playerStats.OrigName, StringComparison.OrdinalIgnoreCase) ||
        (isPet = playerStats.OrigName.Equals(PlayerManager.Instance.GetPlayerFromPet(damage.Attacker), StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrEmpty(damage.AttackerOwner) && damage.AttackerOwner.Equals(playerStats.OrigName, StringComparison.OrdinalIgnoreCase))))
        {
          row = new LogRow() { Actor = damage.Attacker, Acted = damage.Defender, IsPet = isPet };
        }
      }
      else if (action is DamageRecord tanking && defending && !string.IsNullOrEmpty(tanking.Defender) && !string.IsNullOrEmpty(playerStats.OrigName) && tanking.Type != Labels.MISS)
      {
        if (tanking.Defender.Equals(playerStats.OrigName, StringComparison.OrdinalIgnoreCase))
        {
          row = new LogRow() { Actor = tanking.Defender, Acted = tanking.Attacker, IsPet = false };
        }
      }
      else if (action is HealRecord heal && !string.IsNullOrEmpty(heal.Healer) && !string.IsNullOrEmpty(playerStats.OrigName))
      {
        if (heal.Healer.Equals(playerStats.OrigName, StringComparison.OrdinalIgnoreCase))
        {
          row = new LogRow() { Actor = heal.Healer, Acted = heal.Healed, IsPet = false };
        }
      }

      if (row != null && action is HitRecord hit)
      {
        row.Type = hit.Type;
        row.SubType = hit.SubType;
        row.Total = hit.Total;
        row.OverTotal = hit.OverTotal;
        row.CritColor = LineModifiersParser.IsCrit(hit.ModifiersMask) ? TableColors.ACTIVEICON : TableColors.EMPTYICON;
        row.LuckyColor = LineModifiersParser.IsLucky(hit.ModifiersMask) ? TableColors.ACTIVEICON : TableColors.EMPTYICON;
        row.TwincastColor = LineModifiersParser.IsTwincast(hit.ModifiersMask) ? TableColors.ACTIVEICON : TableColors.EMPTYICON;
        row.Time = currentTime;
      }

      return row;
    }

    internal class LogRow
    {
      public string Actor { get; set; }
      public string Acted { get; set; }
      public string CritColor { get; set; }
      public string LuckyColor { get; set; }
      public string SubType { get; set; }
      public string Type { get; set; }
      public string TwincastColor { get; set; }
      public double Time { get; set; }
      public uint Total { get; set; }
      public uint OverTotal { get; set; }
      public bool IsPet { get; set; }
    }
  }
}
