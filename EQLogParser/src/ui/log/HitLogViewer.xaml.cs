using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
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
    private readonly ObservableCollection<HitLogRow> Records = new ObservableCollection<HitLogRow>();
    private readonly ObservableCollection<string> Actions = new ObservableCollection<string>();
    private readonly ObservableCollection<string> Types = new ObservableCollection<string>();
    private readonly Dictionary<string, double> LastSeenCache = new Dictionary<string, double>();
    private readonly Columns CheckBoxColumns = new Columns();
    private readonly Columns TextColumns = new Columns();
    private readonly string[] ColumnIds = new string[] { "Hits", "Critical", "Lucky", "Twincast", "Rampage", "Riposte", "Strikethrough" };

    private string ActedOption = Labels.UNK;
    private List<List<ActionBlock>> CurrentGroups;
    private bool Defending;
    private PlayerStats PlayerStats;
    private string CurrentActedFilter = null;
    private string CurrentActionFilter = null;
    private string CurrentTypeFilter = null;
    private bool CurrentShowPetsFilter = true;
    private bool CurrentGroupActionsFilter = true;

    public HitLogViewer()
    {
      InitializeComponent();

      for (int i = 0; i <= 5; i++)
      {
        CheckBoxColumns.Add(dataGrid.Columns[i]);
        TextColumns.Add(dataGrid.Columns[i]);
      }

      ColumnIds.ToList().ForEach(name =>
      {
        CheckBoxColumns.Add(new GridCheckBoxColumn
        {
          MappingName = name,
          SortMode = Syncfusion.Data.DataReflectionMode.Value,
          HeaderText = name
        });
      });

      ColumnIds.ToList().ForEach(name =>
      {
        TextColumns.Add(new GridTextColumn
        {
          MappingName = name,
          SortMode = Syncfusion.Data.DataReflectionMode.Value,
          HeaderText = name,
          TextAlignment = TextAlignment.Right
        });
      });

      for (int i = 6; i < dataGrid.Columns.Count; i++)
      {
        CheckBoxColumns.Add(dataGrid.Columns[i]);
        TextColumns.Add(dataGrid.Columns[i]);
      }

      dataGrid.Columns = TextColumns;
    }

    internal void Init(CombinedStats currentStats, PlayerStats playerStats, List<List<ActionBlock>> groups, bool defending = false)
    {
      CurrentGroups = groups;
      Defending = defending;
      PlayerStats = playerStats;
      titleLabel.Content = currentStats?.ShortTitle;

      // hide columns dependnig on type of data
      // TextColumns contains the same instance of columns 0 to 5 and 13 to end
      var firstAction = groups?.First()?.First()?.Actions?.First();
      if (firstAction is DamageRecord && !defending)
      {
        ActedOption = "All Defenders";
        TextColumns[4].HeaderText = "Damage";
        TextColumns[5].IsHidden = true;
        TextColumns[10].IsHidden = CheckBoxColumns[10].IsHidden = true;
        TextColumns[11].IsHidden = CheckBoxColumns[11].IsHidden = true;
        TextColumns[12].IsHidden = CheckBoxColumns[12].IsHidden = true;
        TextColumns[13].HeaderText = "Attacker";
        TextColumns[14].HeaderText = "Attacker Class";
        TextColumns[15].HeaderText = "Defender";
        showPets.Visibility = Visibility.Visible;
      }
      else if (firstAction is DamageRecord && defending)
      {
        ActedOption = "All Attackers";
        TextColumns[4].HeaderText = "Damage";
        TextColumns[5].IsHidden = true;
        TextColumns[7].IsHidden = CheckBoxColumns[7].IsHidden = true;
        TextColumns[8].IsHidden = CheckBoxColumns[8].IsHidden = true;
        TextColumns[9].IsHidden = CheckBoxColumns[9].IsHidden = true;
        TextColumns[13].HeaderText = "Defender";
        TextColumns[14].HeaderText = "Defender Class";
        TextColumns[15].HeaderText = "Attacker";
        showPets.Visibility = Visibility.Collapsed;
      }
      else if (firstAction is HealRecord)
      {
        ActedOption = "All Healed Players";
        TextColumns[4].HeaderText = "Heal";
        TextColumns[10].IsHidden = CheckBoxColumns[10].IsHidden = true;
        TextColumns[11].IsHidden = CheckBoxColumns[11].IsHidden = true;
        TextColumns[12].IsHidden = CheckBoxColumns[12].IsHidden = true;
        TextColumns[13].HeaderText = "Healer";
        TextColumns[14].HeaderText = "Healer Class";
        TextColumns[15].HeaderText = "Healed";
        showPets.Visibility = Visibility.Collapsed;
      }

      actionList.ItemsSource = Actions;
      typeList.ItemsSource = Types;
      dataGrid.ItemsSource = CollectionViewSource.GetDefaultView(Records);
      Actions.Add("All Actions");
      Types.Add("All Types");
      actionList.SelectedIndex = 0;
      typeList.SelectedIndex = 0;
      Display(true);
    }

    private void Display(bool init = false)
    {
      if (init || actionList.IsEnabled)
      {
        TextColumns[6].IsHidden = CheckBoxColumns[6].IsHidden = !CurrentGroupActionsFilter;

        var rowCache = new Dictionary<string, HitLogRow>();
        Dictionary<string, byte> uniqueDefenders = new Dictionary<string, byte>();
        Dictionary<string, byte> uniqueActions = new Dictionary<string, byte>();
        Dictionary<string, byte> uniqueTypes = new Dictionary<string, byte>();
        ObservableCollection<string> acted = new ObservableCollection<string> { ActedOption };

        Records.Clear();
        LastSeenCache.Clear();
        CurrentGroups?.ForEach(group =>
        {
          group.ForEach(block =>
          {
            rowCache.Clear();
            block.Actions.ForEach(action =>
            {
              if (CreateRow(rowCache, PlayerStats, action, block.BeginTime, Defending) is HitLogRow row && !CurrentGroupActionsFilter)
              {
                AddRow(row, uniqueActions, uniqueDefenders, uniqueTypes, acted);
              }
            });

            if (CurrentGroupActionsFilter)
            {
              foreach (var row in rowCache.Values.OrderByDescending(row => row.Total))
              {
                AddRow(row, uniqueActions, uniqueDefenders, uniqueTypes, acted);
              }
            }
          });
        });

        actedList.ItemsSource = acted;

        if (CurrentActedFilter == null)
        {
          actedList.SelectedIndex = 0;
        }
        else if (acted.IndexOf(CurrentActedFilter) is int actedIndex && actedIndex > -1)
        {
          actedList.SelectedIndex = actedIndex;
        }
        else
        {
          CurrentActedFilter = null;
          actedList.SelectedIndex = 0;
        }
      }
    }

    private void AddRow(HitLogRow row, Dictionary<string, byte> uniqueActions, Dictionary<string, byte> uniqueDefenders, Dictionary<string, byte> uniqueTypes, ObservableCollection<string> acted)
    {
      Records.Add(row);
      PopulateOption(uniqueActions, row.SubType, Actions);
      PopulateOption(uniqueDefenders, row.Acted, acted);
      PopulateOption(uniqueTypes, row.Type, Types);
    }

    private void PopulateOption(Dictionary<string, byte> cache, string value, ObservableCollection<string> list)
    {
      if (!string.IsNullOrEmpty(value) && !cache.ContainsKey(value))
      {
        cache[value] = 1;

        if (list.Count == 1)
        {
          list.Insert(1, value);
        }
        else
        {
          int i = 1;
          int found = -1;
          foreach (var item in list.Skip(1))
          {
            if (string.Compare(item, value, StringComparison.OrdinalIgnoreCase) is int index && index >= 0)
            {
              found = index == 0 ? -2 : i;
              break;
            }

            i++;
          }

          if (found == -1)
          {
            list.Add(value);
          }
          else if (found > 0)
          {
            list.Insert(found, value);
          }
        }
      }
    }

    private HitLogRow CreateRow(Dictionary<string, HitLogRow> rowCache, PlayerStats playerStats, IAction action, double currentTime, bool defending = false)
    {
      HitLogRow row = null;

      if (action is DamageRecord damage)
      {
        if (!defending && !string.IsNullOrEmpty(damage.Attacker) && !string.IsNullOrEmpty(playerStats.OrigName) && StatsUtil.IsHitType(damage.Type))
        {
          bool isPet = false;
          if (damage.Attacker.Equals(playerStats.OrigName, StringComparison.OrdinalIgnoreCase) ||
          (isPet = playerStats.OrigName.Equals(PlayerManager.Instance.GetPlayerFromPet(damage.Attacker), StringComparison.OrdinalIgnoreCase) ||
          (!string.IsNullOrEmpty(damage.AttackerOwner) && damage.AttackerOwner.Equals(playerStats.OrigName, StringComparison.OrdinalIgnoreCase))))
          {
            row = new HitLogRow
            {
              Actor = damage.Attacker,
              ActorClass = PlayerManager.Instance.GetPlayerClass(damage.Attacker),
              Acted = damage.Defender,
              IsPet = isPet,
              TimeSince = "-"
            };
          }
        }
        else if (defending && !string.IsNullOrEmpty(damage.Defender) && !string.IsNullOrEmpty(playerStats.OrigName) && StatsUtil.IsHitType(damage.Type))
        {
          if (damage.Defender.Equals(playerStats.OrigName, StringComparison.OrdinalIgnoreCase))
          {
            row = new HitLogRow
            {
              Actor = damage.Defender,
              ActorClass = PlayerManager.Instance.GetPlayerClass(damage.Defender),
              Acted = damage.Attacker,
              IsPet = false,
              TimeSince = "-"
            };
          }
        }
      }
      else if (action is HealRecord heal && !string.IsNullOrEmpty(heal.Healer) && !string.IsNullOrEmpty(playerStats.OrigName))
      {
        if (heal.Healer.Equals(playerStats.OrigName, StringComparison.OrdinalIgnoreCase))
        {
          row = new HitLogRow
          {
            Actor = heal.Healer,
            ActorClass = PlayerManager.Instance.GetPlayerClass(heal.Healer),
            Acted = heal.Healed,
            IsPet = false,
            TimeSince = "-"
          };
        }
      }

      if (row != null && action is HitRecord hit)
      {
        row.Type = hit.Type;
        row.SubType = hit.SubType;
        row.Time = currentTime;

        if (CurrentGroupActionsFilter)
        {
          var rowKey = GetRowKey(row, CurrentActedFilter != null);
          if (rowCache.TryGetValue(rowKey, out HitLogRow previous))
          {
            if (row.Acted != previous.Acted && previous.Acted != "Multiple")
            {
              previous.Acted = "Multiple";
            }

            row = previous;
          }
          else
          {
            rowCache[rowKey] = row;
          }
        }

        row.IsGroupingEnabled = CurrentGroupActionsFilter;
        row.Total += hit.Total;
        row.OverTotal += hit.OverTotal;
        row.Critical += (uint)(LineModifiersParser.IsCrit(hit.ModifiersMask) ? 1 : 0);
        row.Lucky += (uint)(LineModifiersParser.IsLucky(hit.ModifiersMask) ? 1 : 0);
        row.Twincast += (uint)(LineModifiersParser.IsTwincast(hit.ModifiersMask) ? 1 : 0);
        row.Rampage += (uint)(LineModifiersParser.IsRampage(hit.ModifiersMask) ? 1 : 0);
        row.Riposte += (uint)(LineModifiersParser.IsRiposte(hit.ModifiersMask) ? 1 : 0);
        row.Strikethrough += (uint)(LineModifiersParser.IsStrikethrough(hit.ModifiersMask) ? 1 : 0);
        row.Hits++;

        if (LastSeenCache.TryGetValue(row.SubType, out double lastTime)) // 1 day
        {
          var diff = row.Time - lastTime;
          if (diff > 0 && diff < 3600)
          {
            TimeSpan t = TimeSpan.FromSeconds(diff);
            row.TimeSince = string.Format(CultureInfo.CurrentCulture, "{0:D2}:{1:D2}", t.Minutes, t.Seconds);
          }
        }

        LastSeenCache[row.SubType] = row.Time;
      }

      return row;
    }

    private static string GetRowKey(HitLogRow row, bool useActedKey = false)
    {
      return string.Format(CultureInfo.CurrentCulture, "{0}-{1}-{2}-{3}", row.Actor, useActedKey ? row.Acted : "", row.SubType, row.Time);
    }

    private void OptionsChanged(object sender, EventArgs e)
    {
      if (dataGrid != null && dataGrid.View != null)
      {
        CurrentActedFilter = actedList.SelectedIndex == 0 ? null : actedList.SelectedItem as string;
        CurrentActionFilter = actionList.SelectedIndex == 0 ? null : actionList.SelectedItem as string;
        CurrentTypeFilter = typeList.SelectedIndex == 0 ? null : typeList.SelectedItem as string;
        CurrentShowPetsFilter = showPets.IsChecked.Value;

        var refresh = CurrentGroupActionsFilter == groupHits.IsChecked.Value;
        CurrentGroupActionsFilter = groupHits.IsChecked.Value;

        if (CurrentGroupActionsFilter && dataGrid.Columns != TextColumns)
        {
          dataGrid.Columns = TextColumns;
        }
        else if (!CurrentGroupActionsFilter && dataGrid.Columns != CheckBoxColumns)
        {
          dataGrid.Columns = CheckBoxColumns;
        }

        if (dataGrid.View.Filter == null)
        {
          dataGrid.View.Filter = new Predicate<object>(item =>
          {
            var record = (HitLogRow)item;
            return (string.IsNullOrEmpty(CurrentTypeFilter) || CurrentTypeFilter == record.Type) &&
            (string.IsNullOrEmpty(CurrentActionFilter) || CurrentActionFilter == record.SubType) &&
            (string.IsNullOrEmpty(CurrentActedFilter) || CurrentActedFilter == record.Acted) &&
            (CurrentShowPetsFilter || !record.IsPet);
          });
        }

        if (refresh)
        {
          dataGrid.View.RefreshFilter();
        }
        else
        {
          Display();
        }
      }
    }
  }
}
