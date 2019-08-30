using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Data;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DamageLog.xaml
  /// </summary>
  public partial class DamageLog : UserControl
  {
    private readonly object CollectionLock = new object();
    private ObservableCollection<DamageRecord> Records = new ObservableCollection<DamageRecord>();
    private ObservableCollection<string> Actions = new ObservableCollection<string>();
    private ObservableCollection<string> Defenders = new ObservableCollection<string>();
    private ObservableCollection<string> Types = new ObservableCollection<string>();

    private string CurrentDefenderFilter = null;
    private string CurrentActionFilter = null;
    private string CurrentTypeFilter = null;
    private bool CurrentShowPetsFilter = true;

    public DamageLog(CombinedStats currentStats, PlayerStats playerStats, List<List<ActionBlock>> groups)
    {
      InitializeComponent();

      titleLabel.Content = currentStats?.ShortTitle;
      var view = CollectionViewSource.GetDefaultView(Records);

      view.Filter = new Predicate<object>(item =>
      {
        var record = (RowWrapper)item;
        return (string.IsNullOrEmpty(CurrentTypeFilter) || CurrentTypeFilter == record.Type) &&
        (string.IsNullOrEmpty(CurrentActionFilter) || CurrentActionFilter == record.SubType) && 
        (string.IsNullOrEmpty(CurrentDefenderFilter) || CurrentDefenderFilter == record.Defender) && 
        (CurrentShowPetsFilter || !record.IsPet);
      });

      BindingOperations.EnableCollectionSynchronization(Records, CollectionLock);
      BindingOperations.EnableCollectionSynchronization(Actions, CollectionLock);
      BindingOperations.EnableCollectionSynchronization(Defenders, CollectionLock);
      BindingOperations.EnableCollectionSynchronization(Types, CollectionLock);

      Actions.Add("All Actions");
      Types.Add("All Types");
      Defenders.Add("All Defenders");

      actionList.ItemsSource = Actions;
      actionList.SelectedIndex = 0;
      defenderList.ItemsSource = Defenders;
      defenderList.SelectedIndex = 0;
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

              if (action is DamageRecord record && !string.IsNullOrEmpty(record.Attacker) && !string.IsNullOrEmpty(playerStats.OrigName) && record.Type != Labels.MISS)
              {
                bool isPet = false;
                if (record.Attacker.Equals(playerStats.OrigName, StringComparison.OrdinalIgnoreCase) ||
                (isPet = playerStats.OrigName.Equals(PlayerManager.Instance.GetPlayerFromPet(record.Attacker), StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(record.AttackerOwner) && record.AttackerOwner.Equals(playerStats.OrigName, StringComparison.OrdinalIgnoreCase))))
                {
                  var wrapper = new RowWrapper()
                  {
                    Attacker = record.Attacker,
                    Defender = record.Defender,
                    Type = record.Type,
                    SubType = record.SubType,
                    Total = record.Total,
                    CritColor = LineModifiersParser.IsCrit(record.ModifiersMask) ? TableColors.ACTIVEICON : TableColors.EMPTYICON,
                    LuckyColor = LineModifiersParser.IsLucky(record.ModifiersMask) ? TableColors.ACTIVEICON : TableColors.EMPTYICON,
                    TwincastColor = LineModifiersParser.IsTwincast(record.ModifiersMask) ? TableColors.ACTIVEICON : TableColors.EMPTYICON,
                    Time = currentTime,
                    IsPet = isPet
                  };

                  lock(CollectionLock)
                  {
                    Records.Add(wrapper);
                  }

                  PopulateOption(uniqueActions, record.SubType, Actions);
                  PopulateOption(uniqueDefenders, record.Defender, Defenders);
                  PopulateOption(uniqueTypes, record.Type, Types);
                }
              }
            });
          });
        });

        Dispatcher.InvokeAsync(() =>
        {
          actionList.IsEnabled = true;
          typeList.IsEnabled = true;
          defenderList.IsEnabled = true;
          showPets.IsEnabled = true;
        });

        Helpers.SetBusy(false);
      }, TaskScheduler.Default);
    }

    private void PopulateOption(Dictionary<string, byte> cache, string value, ObservableCollection<string> list)
    {
      if (!cache.ContainsKey(value))
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
        CurrentDefenderFilter = defenderList.SelectedIndex == 0 ? null : defenderList.SelectedItem as string;
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

    internal class RowWrapper : DamageRecord
    {
      public string CritColor { get; set; }
      public string LuckyColor { get; set; }
      public string TwincastColor { get; set; }
      public double Time { get; set; }
      public bool IsPet { get; set; }
    }
  }
}
