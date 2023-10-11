using Syncfusion.Data;
using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DamageLog.xaml
  /// </summary>
  public partial class HitLogViewer : UserControl, IDisposable
  {
    private readonly Columns CheckBoxColumns = new();
    private readonly Columns TextColumns = new();
    private readonly List<string> ColumnIds = new() { "Hits", "Critical", "Lucky", "Twincast", "Rampage", "Riposte", "Strikethrough" };

    private string ActedOption = Labels.UNK;
    private List<List<ActionGroup>> CurrentGroups;
    private bool Defending;
    private PlayerStats PlayerStats;
    private string CurrentActedFilter;
    private string CurrentActionFilter;
    private string CurrentTypeFilter;
    private bool CurrentShowPetsFilter = true;
    private bool CurrentGroupActionsFilter = true;
    private string Title;

    public HitLogViewer()
    {
      InitializeComponent();
      dataGrid.IsEnabled = false;
      UIElementUtil.SetEnabled(controlPanel.Children, false);

      for (var i = 0; i <= 5; i++)
      {
        CheckBoxColumns.Add(dataGrid.Columns[i]);
        TextColumns.Add(dataGrid.Columns[i]);
      }

      ColumnIds.ForEach(name =>
      {
        CheckBoxColumns.Add(new GridCheckBoxColumn
        {
          MappingName = name,
          SortMode = DataReflectionMode.Value,
          HeaderText = name
        });
      });

      ColumnIds.ForEach(name =>
      {
        TextColumns.Add(new GridTextColumn
        {
          MappingName = name,
          SortMode = DataReflectionMode.Value,
          HeaderText = name,
          TextAlignment = TextAlignment.Right
        });
      });

      for (var i = 6; i < dataGrid.Columns.Count; i++)
      {
        CheckBoxColumns.Add(dataGrid.Columns[i]);
        TextColumns.Add(dataGrid.Columns[i]);
      }

      dataGrid.Columns = TextColumns;

      // default these columns to descending
      var desc = ColumnIds.ToList();
      desc.Add("Total");
      desc.Add("OverTotal");

      dataGrid.SortColumnsChanging += (s, e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      dataGrid.SortColumnsChanged += (s, e) => DataGridUtil.SortColumnsChanged(s, e, desc);
      (Application.Current.MainWindow as MainWindow).EventsThemeChanged += EventsThemeChanged;
    }

    internal void Init(CombinedStats currentStats, PlayerStats playerStats, List<List<ActionGroup>> groups, bool defending = false)
    {
      CurrentGroups = groups;
      Defending = defending;
      PlayerStats = playerStats;
      Title = currentStats?.ShortTitle;

      IAction firstAction = null;
      foreach (var group in groups)
      {
        foreach (var list in group)
        {
          foreach (var action in list.Actions)
          {
            firstAction = action;
            break;
          }
        }
      }

      if (firstAction is DamageRecord && !defending)
      {
        // hide columns dependnig on type of data
        // TextColumns contains the same instance of columns 0 to 5 and 13 to end
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

      DataGridUtil.UpdateTableMargin(dataGrid);
      Display();
    }

    private void Display()
    {
      TextColumns[6].IsHidden = CheckBoxColumns[6].IsHidden = !CurrentGroupActionsFilter;

      Task.Delay(100).ContinueWith(task =>
      {
        var uniqueDefenders = new ConcurrentDictionary<string, bool>();
        var uniqueActions = new ConcurrentDictionary<string, bool>();
        var uniqueTypes = new ConcurrentDictionary<string, bool>();
        var list = new List<HitLogRow>();

        if (CurrentGroups != null)
        {
          foreach (ref var group in CurrentGroups.ToArray().AsSpan())
          {
            Parallel.ForEach(group, block =>
            {
              var precise = 0.0;
              var rowCache = new Dictionary<string, HitLogRow>();
              foreach (ref var action in block.Actions.ToArray().AsSpan())
              {
                precise += 0.000001;
                if (CreateRow(rowCache, PlayerStats, action, block.BeginTime + precise, Defending) is { } row && !CurrentGroupActionsFilter)
                {
                  lock (list)
                  {
                    list.Add(row);
                  }

                  PopulateRow(row, uniqueActions, uniqueDefenders, uniqueTypes);
                }
              }

              if (CurrentGroupActionsFilter)
              {
                foreach (ref var row in rowCache.Values.ToArray().AsSpan())
                {
                  lock (list)
                  {
                    list.Add(row);
                  }

                  PopulateRow(row, uniqueActions, uniqueDefenders, uniqueTypes);
                }
              }
            });
          }
        }

        var lastSeen = new Dictionary<string, double>();
        foreach (var row in list.OrderBy(row => row.Time))
        {
          if (lastSeen.TryGetValue(row.SubType, out var lastTime)) // 1 day
          {
            var diff = Math.Floor(row.Time) - lastTime;
            if (diff is > 0 and < 3600)
            {
              var t = TimeSpan.FromSeconds(diff);
              row.TimeSince = string.Format(CultureInfo.CurrentCulture, "{0:D2}:{1:D2}", t.Minutes, t.Seconds);
            }
          }

          lastSeen[row.SubType] = Math.Floor(row.Time);
        }

        var actions = new List<string> { "All Actions" };
        var acted = new List<string> { ActedOption };
        var types = new List<string> { "All Types" };
        actions.AddRange(uniqueActions.Keys.OrderBy(x => x));
        acted.AddRange(uniqueDefenders.Keys.OrderBy(x => x));
        types.AddRange(uniqueTypes.Keys.OrderBy(x => x));

        Dispatcher.InvokeAsync(() =>
        {
          actedList.ItemsSource = acted;

          if (CurrentActedFilter == null)
          {
            actedList.SelectedIndex = 0;
          }
          else if (acted.IndexOf(CurrentActedFilter) is int actedIndex and > -1)
          {
            actedList.SelectedIndex = actedIndex;
          }
          else
          {
            CurrentActedFilter = null;
            actedList.SelectedIndex = 0;
          }

          dataGrid.SortColumnDescriptions.Clear();
          if (CurrentGroupActionsFilter)
          {
            dataGrid.SortColumnDescriptions.Add(new SortColumnDescription { ColumnName = "Time", SortDirection = ListSortDirection.Ascending });
            dataGrid.SortColumnDescriptions.Add(new SortColumnDescription { ColumnName = "Total", SortDirection = ListSortDirection.Descending });
          }
          else
          {
            dataGrid.SortColumnDescriptions.Add(new SortColumnDescription { ColumnName = "Time", SortDirection = ListSortDirection.Ascending });
          }

          actionList.ItemsSource = actions;
          typeList.ItemsSource = types;
          actionList.SelectedIndex = 0;
          typeList.SelectedIndex = 0;
          dataGrid.ItemsSource = CollectionViewSource.GetDefaultView(list);
          dataGrid.IsEnabled = true;
          titleLabel.Content = Title;
          UIElementUtil.SetEnabled(controlPanel.Children, true);
        });
      });
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(dataGrid, titleLabel.Content.ToString());
    private void CreateImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(dataGrid, titleLabel);
    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);

    private void PopulateRow(HitLogRow row, ConcurrentDictionary<string, bool> uniqueActions, ConcurrentDictionary<string, bool> uniqueDefenders,
      ConcurrentDictionary<string, bool> uniqueTypes)
    {
      if (row.SubType != null)
      {
        uniqueActions[row.SubType] = true;
      }

      if (row.Acted != null)
      {
        uniqueDefenders[row.Acted] = true;
      }

      if (row.Type != null)
      {
        uniqueTypes[row.Type] = true;
      }
    }

    private void ItemsSourceChanged(object sender, GridItemsSourceChangedEventArgs e)
    {
      if (dataGrid.View != null)
      {
        dataGrid.View.Filter = item =>
        {
          var record = (HitLogRow)item;
          return (string.IsNullOrEmpty(CurrentTypeFilter) || CurrentTypeFilter == record.Type) &&
                 (string.IsNullOrEmpty(CurrentActionFilter) || CurrentActionFilter == record.SubType) &&
                 (string.IsNullOrEmpty(CurrentActedFilter) || CurrentActedFilter == record.Acted) &&
                 (CurrentShowPetsFilter || !record.IsPet);
        };

        dataGrid.SelectedItems.Clear();
        dataGrid.View.RefreshFilter();
      }
    }

    private HitLogRow CreateRow(Dictionary<string, HitLogRow> rowCache, PlayerStats playerStats, IAction action,
      double currentTime, bool defending = false)
    {
      HitLogRow row = null;

      if (action is DamageRecord damage)
      {
        if (!defending && !string.IsNullOrEmpty(damage.Attacker) && !string.IsNullOrEmpty(playerStats.OrigName) && StatsUtil.IsHitType(damage.Type))
        {
          var isPet = false;
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
          if (rowCache.TryGetValue(rowKey, out var previous))
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
      }

      return row;
    }

    private static string GetRowKey(HitLogRow row, bool useActedKey = false)
    {
      return string.Format(CultureInfo.CurrentCulture, "{0}-{1}-{2}-{3}", row.Actor, useActedKey ? row.Acted : "", row.SubType, Math.Floor(row.Time));
    }

    private void OptionsChanged(object sender, EventArgs e)
    {
      if (dataGrid is { View: not null })
      {
        CurrentActedFilter = actedList.SelectedIndex == 0 ? null : actedList.SelectedItem as string;
        CurrentActionFilter = actionList.SelectedIndex == 0 ? null : actionList.SelectedItem as string;
        CurrentTypeFilter = typeList.SelectedIndex == 0 ? null : typeList.SelectedItem as string;
        CurrentShowPetsFilter = showPets.IsChecked.Value;

        var refresh = CurrentGroupActionsFilter == groupHits.IsChecked.Value;
        CurrentGroupActionsFilter = groupHits.IsChecked.Value;

        if (refresh)
        {
          dataGrid.SelectedItems.Clear();
          dataGrid.View.RefreshFilter();
        }
        else
        {
          Dispatcher.InvokeAsync(() =>
          {
            titleLabel.Content = "Loading...";
            dataGrid.ItemsSource = null;
            dataGrid.IsEnabled = false;
            UIElementUtil.SetEnabled(controlPanel.Children, false); if (CurrentGroupActionsFilter && dataGrid.Columns != TextColumns)
            {
              dataGrid.Columns = TextColumns;
            }
            else if (!CurrentGroupActionsFilter && dataGrid.Columns != CheckBoxColumns)
            {
              dataGrid.Columns = CheckBoxColumns;
            }

            Display();
          }, DispatcherPriority.Background);
        }
      }
    }

    #region IDisposable Support
    private bool disposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        (Application.Current.MainWindow as MainWindow).EventsThemeChanged -= EventsThemeChanged;
        dataGrid?.Dispose();
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

  internal class HitLogRow : HitRecord
  {
    public string Actor { get; set; }
    public string ActorClass { get; set; }
    public string Acted { get; set; }
    public uint Hits { get; set; }
    public uint Critical { get; set; }
    public uint Lucky { get; set; }
    public uint Twincast { get; set; }
    public uint Rampage { get; set; }
    public uint Riposte { get; set; }
    public uint Strikethrough { get; set; }
    public double Time { get; set; }
    public bool IsPet { get; set; }
    public bool IsGroupingEnabled { get; set; }
    public string TimeSince { get; set; }
  }
}
