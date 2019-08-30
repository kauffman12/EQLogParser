using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Data;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DamageLog.xaml
  /// </summary>
  public partial class DamageLog : UserControl
  {
    private ObservableCollection<DamageRecord> Records = new ObservableCollection<DamageRecord>();
    private string CurrentDefenderFilter = null;
    private string CurrentActionFilter = null;
    private string CurrentTypeFilter = null;
    private bool CurrentShowPetsFilter = true;

    public DamageLog(CombinedStats currentStats, PlayerStats playerStats, List<List<ActionBlock>> groups)
    {
      InitializeComponent();

      titleLabel.Content = currentStats?.ShortTitle;
      Dictionary<string, byte> uniqueDefenders = new Dictionary<string, byte>();
      Dictionary<string, byte> uniqueActions = new Dictionary<string, byte>();
      Dictionary<string, byte> uniqueTypes = new Dictionary<string, byte>();

      var view = CollectionViewSource.GetDefaultView(Records);
      view.Filter = new Predicate<object>(item =>
      {
        var record = (RowWrapper)item;
        return (string.IsNullOrEmpty(CurrentTypeFilter) || CurrentTypeFilter == record.Type) &&
        (string.IsNullOrEmpty(CurrentActionFilter) || CurrentActionFilter == record.SubType) && 
        (string.IsNullOrEmpty(CurrentDefenderFilter) || CurrentDefenderFilter == record.Defender) && 
        (CurrentShowPetsFilter || !record.IsPet);
      });

      groups?.ForEach(group =>
      {
        group.ForEach(block =>
        {
          block.Actions.ForEach(action =>
          {
            var theTime = DateUtil.FormatDate(block.BeginTime);

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
                  Time = theTime,
                  IsPet = isPet
                };

                Records.Add(wrapper);
                uniqueDefenders[record.Defender] = 1;
                uniqueActions[record.SubType] = 1;
                uniqueTypes[record.Type] = 1;
              }
            }  
          });
        });
      });

      var list = uniqueActions.Keys.OrderBy(item => item).ToList();
      list.Insert(0, "All Actions");
      actionList.ItemsSource = list;
      actionList.SelectedIndex = 0;

      list = uniqueTypes.Keys.OrderBy(item => item).ToList();
      list.Insert(0, "All Types");
      typeList.ItemsSource = list;
      typeList.SelectedIndex = 0;

      list = uniqueDefenders.Keys.OrderBy(item => item).ToList();
      list.Insert(0, "All Defenders");
      defenderList.ItemsSource = list;
      defenderList.SelectedIndex = 0;

      dataGrid.ItemsSource = view;
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

    internal class RowWrapper : DamageRecord
    {
      public string CritColor { get; set; }
      public string LuckyColor { get; set; }
      public string TwincastColor { get; set; }
      public string Time { get; set; }
      public bool IsPet { get; set; }
    }
  }
}
