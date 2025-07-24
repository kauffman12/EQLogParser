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
  public partial class HitLogViewer : UserControl, IDisposable
  {
    private readonly Columns _checkBoxColumns = [];
    private readonly Columns _textColumns = [];
    private readonly List<string> _columnIds = ["Hits", "Critical", "Lucky", "Twincast", "Rampage", "Riposte", "Strikethrough"];

    private string _actedOption = Labels.Unk;
    private List<List<ActionGroup>> _currentGroups;
    private bool _defending;
    private PlayerStats _playerStats;
    private string _currentActedFilter;
    private string _currentActionFilter;
    private string _currentTypeFilter;
    private bool _currentShowPetsFilter = true;
    private bool _currentGroupActionsFilter = true;
    private string _title;

    public HitLogViewer()
    {
      InitializeComponent();
      dataGrid.IsEnabled = false;
      UiElementUtil.SetEnabled(controlPanel.Children, false);

      // Time
      AddColumn(new GridTextColumn
      {
        MappingName = "BeginTime",
        DisplayBinding = new Binding
        {
          Path = new PropertyPath("BeginTime"),
          Converter = new DateTimeConverter()
        },
        TextAlignment = TextAlignment.Center,
        Width = MainActions.CurrentDateTimeWidth,
        HeaderText = "Time"
      });

      AddColumn(new GridTextColumn
      {
        MappingName = "TimeSince",
        TextAlignment = TextAlignment.Center,
        HeaderText = "Since",
        Width = MainActions.CurrentShortWidth
      });

      AddColumn(new GridTextColumn { MappingName = "Type", HeaderText = "Type" });
      AddColumn(new GridTextColumn
      {
        MappingName = "SubType",
        HeaderText = "Action",
        Width = MainActions.CurrentSpellWidth
      });

      AddColumn(new GridNumericColumn
      {
        MappingName = "Total",
        TextAlignment = TextAlignment.Right,
        NumberDecimalDigits = 0,
        NumberGroupSizes = [3],
        HeaderText = ""
      });

      AddColumn(new GridNumericColumn
      {
        MappingName = "OverTotal",
        TextAlignment = TextAlignment.Right,
        NumberDecimalDigits = 0,
        NumberGroupSizes = [3],
        HeaderText = "Over Healed"
      });

      _columnIds.ForEach(name =>
      {
        var column = new GridCheckBoxColumn
        {
          MappingName = name,
          SortMode = DataReflectionMode.Value,
          HeaderText = name
        };

        column.Width = name != "Strikethrough" ? MainActions.CurrentShortWidth : DataGridUtil.CalculateMinGridHeaderWidth(name);
        _checkBoxColumns.Add(column);
      });

      _columnIds.ForEach(name =>
      {
        var column = new GridTextColumn
        {
          MappingName = name,
          SortMode = DataReflectionMode.Value,
          HeaderText = name,
          TextAlignment = TextAlignment.Right,
          Width = DataGridUtil.CalculateMinGridHeaderWidth(name)
        };

        column.Width = name != "Strikethrough" ? MainActions.CurrentShortWidth : DataGridUtil.CalculateMinGridHeaderWidth(name);
        _textColumns.Add(column);
      });

      AddColumn(new GridTextColumn
      {
        MappingName = "Actor",
        HeaderText = "",
        Width = MainActions.CurrentNpcWidth
      });

      AddColumn(new GridTextColumn { MappingName = "ActorClass", HeaderText = "" });

      AddColumn(new GridTextColumn
      {
        MappingName = "Acted",
        HeaderText = "",
        Width = MainActions.CurrentNpcWidth
      });

      dataGrid.Columns = _textColumns;

      // default these columns to descending
      var desc = _columnIds.ToList();
      desc.Add("Total");
      desc.Add("OverTotal");

      dataGrid.SortColumnsChanging += (s, e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      dataGrid.SortColumnsChanged += (s, e) => DataGridUtil.SortColumnsChanged(s, e, desc);
      MainActions.EventsThemeChanged += EventsThemeChanged;
    }

    private void AddColumn(GridColumn column)
    {
      if (double.IsNaN(column.Width))
      {
        column.Width = DataGridUtil.CalculateMinGridHeaderWidth(column.HeaderText);
      }

      _checkBoxColumns.Add(column);
      _textColumns.Add(column);
    }

    internal async Task InitAsync(CombinedStats currentStats, PlayerStats playerStats, List<List<ActionGroup>> groups, bool defending = false)
    {
      _currentGroups = groups;
      _defending = defending;
      _playerStats = playerStats;
      _title = currentStats?.ShortTitle;

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
        _actedOption = "All Defenders";
        _textColumns[4].HeaderText = "Damage";
        _textColumns[5].IsHidden = true;
        _textColumns[10].IsHidden = _checkBoxColumns[10].IsHidden = true;
        _textColumns[11].IsHidden = _checkBoxColumns[11].IsHidden = true;
        _textColumns[12].IsHidden = _checkBoxColumns[12].IsHidden = true;
        _textColumns[13].HeaderText = "Attacker";
        _textColumns[14].HeaderText = "Attacker Class";
        _textColumns[15].HeaderText = "Defender";
        showPets.Visibility = Visibility.Visible;
      }
      else if (firstAction is DamageRecord)
      {
        _actedOption = "All Attackers";
        _textColumns[4].HeaderText = "Damage";
        _textColumns[5].IsHidden = true;
        _textColumns[7].IsHidden = _checkBoxColumns[7].IsHidden = true;
        _textColumns[8].IsHidden = _checkBoxColumns[8].IsHidden = true;
        _textColumns[9].IsHidden = _checkBoxColumns[9].IsHidden = true;
        _textColumns[13].HeaderText = "Defender";
        _textColumns[14].HeaderText = "Defender Class";
        _textColumns[15].HeaderText = "Attacker";
        showPets.Visibility = Visibility.Collapsed;
      }
      else if (firstAction is HealRecord)
      {
        _actedOption = "All Healed Players";
        _textColumns[4].HeaderText = "Heal";
        _textColumns[10].IsHidden = _checkBoxColumns[10].IsHidden = true;
        _textColumns[11].IsHidden = _checkBoxColumns[11].IsHidden = true;
        _textColumns[12].IsHidden = _checkBoxColumns[12].IsHidden = true;
        _textColumns[13].HeaderText = "Healer";
        _textColumns[14].HeaderText = "Healer Class";
        _textColumns[15].HeaderText = "Healed";
        showPets.Visibility = Visibility.Collapsed;
      }

      await Load();
    }

    private async Task Load()
    {
      _textColumns[6].IsHidden = _checkBoxColumns[6].IsHidden = !_currentGroupActionsFilter;

      await Task.Delay(100);

      var uniqueDefenders = new ConcurrentDictionary<string, bool>();
      var uniqueActions = new ConcurrentDictionary<string, bool>();
      var uniqueTypes = new ConcurrentDictionary<string, bool>();
      var list = new List<HitLogRow>();

      if (_currentGroups != null)
      {
        foreach (var group in _currentGroups)
        {
          foreach (var block in group)
          {
            var precise = 0.0;
            var rowCache = new Dictionary<string, HitLogRow>();
            foreach (var action in block.Actions.ToArray())
            {
              precise += 0.000001;
              if (CreateRow(rowCache, _playerStats, action, block.BeginTime + precise, _defending) is { } row && !_currentGroupActionsFilter)
              {
                lock (list)
                {
                  list.Add(row);
                }

                PopulateRow(row, uniqueActions, uniqueDefenders, uniqueTypes);
              }
            }

            if (_currentGroupActionsFilter)
            {
              foreach (var row in rowCache.Values)
              {
                lock (list)
                {
                  list.Add(row);
                }

                PopulateRow(row, uniqueActions, uniqueDefenders, uniqueTypes);
              }
            }
          }
        }
      }

      var lastSeen = new Dictionary<string, double>();
      foreach (var row in list.OrderBy(row => row.BeginTime))
      {
        if (lastSeen.TryGetValue(row.SubType, out var lastTime)) // 1 day
        {
          var diff = Math.Floor(row.BeginTime) - lastTime;
          if (diff is > 0 and < 3600)
          {
            var t = TimeSpan.FromSeconds(diff);
            row.TimeSince = string.Format(CultureInfo.CurrentCulture, "{0:D2}:{1:D2}", t.Minutes, t.Seconds);
          }
        }

        lastSeen[row.SubType] = Math.Floor(row.BeginTime);
      }

      var actions = new List<string> { "All Actions" };
      var acted = new List<string> { _actedOption };
      var types = new List<string> { "All Types" };
      actions.AddRange(uniqueActions.Keys.OrderBy(x => x));
      acted.AddRange(uniqueDefenders.Keys.OrderBy(x => x));
      types.AddRange(uniqueTypes.Keys.OrderBy(x => x));

      await Dispatcher.InvokeAsync(() =>
      {
        actedList.ItemsSource = acted;

        if (_currentActedFilter == null)
        {
          actedList.SelectedIndex = 0;
        }
        else if (acted.IndexOf(_currentActedFilter) is var actedIndex and > -1)
        {
          actedList.SelectedIndex = actedIndex;
        }
        else
        {
          _currentActedFilter = null;
          actedList.SelectedIndex = 0;
        }

        dataGrid.SortColumnDescriptions.Clear();
        if (_currentGroupActionsFilter)
        {
          dataGrid.SortColumnDescriptions.Add(new SortColumnDescription { ColumnName = "BeginTime", SortDirection = ListSortDirection.Ascending });
          dataGrid.SortColumnDescriptions.Add(new SortColumnDescription { ColumnName = "Total", SortDirection = ListSortDirection.Descending });
        }
        else
        {
          dataGrid.SortColumnDescriptions.Add(new SortColumnDescription { ColumnName = "BeginTime", SortDirection = ListSortDirection.Ascending });
        }

        actionList.ItemsSource = actions;
        typeList.ItemsSource = types;
        actionList.SelectedIndex = 0;
        typeList.SelectedIndex = 0;

        dataGrid.ItemsSource = CollectionViewSource.GetDefaultView(list);
        dataGrid.IsEnabled = true;
        titleLabel.Content = _title;
        UiElementUtil.SetEnabled(controlPanel.Children, true);
      });
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(dataGrid, titleLabel.Content.ToString());
    private async void CreateImageClick(object sender, RoutedEventArgs e) => await DataGridUtil.CreateImageAsync(dataGrid, titleLabel);
    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);

    private void ItemsSourceChanged(object sender, GridItemsSourceChangedEventArgs e)
    {
      if (dataGrid.View != null)
      {
        dataGrid.View.Filter = item =>
        {
          var record = (HitLogRow)item;
          return (string.IsNullOrEmpty(_currentTypeFilter) || _currentTypeFilter == record.Type) &&
                 (string.IsNullOrEmpty(_currentActionFilter) || _currentActionFilter == record.SubType) &&
                 (string.IsNullOrEmpty(_currentActedFilter) || _currentActedFilter == record.Acted) &&
                 (_currentShowPetsFilter || !record.IsPet);
        };

        dataGrid.SelectedItems.Clear();
        dataGrid.View.RefreshFilter();
      }
    }

    private HitLogRow CreateRow(IDictionary<string, HitLogRow> rowCache, PlayerStats playerStats, IAction action,
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
        row.BeginTime = currentTime;

        if (_currentGroupActionsFilter)
        {
          var rowKey = GetRowKey(row, _currentActedFilter != null);
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

        row.IsGroupingEnabled = _currentGroupActionsFilter;
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
      return string.Format(CultureInfo.CurrentCulture, "{0}-{1}-{2}-{3}", row.Actor, useActedKey ? row.Acted : "", row.SubType, Math.Floor(row.BeginTime));
    }

    private void OptionsChanged(object sender, EventArgs e)
    {
      if (dataGrid is { View: not null })
      {
        _currentActedFilter = actedList.SelectedIndex == 0 ? null : actedList.SelectedItem as string;
        _currentActionFilter = actionList.SelectedIndex == 0 ? null : actionList.SelectedItem as string;
        _currentTypeFilter = typeList.SelectedIndex == 0 ? null : typeList.SelectedItem as string;
        _currentShowPetsFilter = showPets.IsChecked == true;

        var refresh = _currentGroupActionsFilter == groupHits.IsChecked == true;
        _currentGroupActionsFilter = groupHits.IsChecked == true;

        if (refresh)
        {
          dataGrid.SelectedItems.Clear();
          dataGrid.View.RefreshFilter();
        }
        else
        {
          Dispatcher.InvokeAsync(async () =>
          {
            titleLabel.Content = "Loading...";
            dataGrid.ItemsSource = null;
            dataGrid.IsEnabled = false;
            UiElementUtil.SetEnabled(controlPanel.Children, false);
            if (_currentGroupActionsFilter && dataGrid.Columns != _textColumns)
            {
              dataGrid.Columns = _textColumns;
            }
            else if (!_currentGroupActionsFilter && dataGrid.Columns != _checkBoxColumns)
            {
              dataGrid.Columns = _checkBoxColumns;
            }

            await Load();
          }, DispatcherPriority.Background);
        }
      }
    }

    private static void PopulateRow(HitLogRow row, ConcurrentDictionary<string, bool> uniqueActions, ConcurrentDictionary<string, bool> uniqueDefenders,
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

    #region IDisposable Support
    private bool _disposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!_disposedValue)
      {
        MainActions.EventsThemeChanged -= EventsThemeChanged;
        dataGrid?.Dispose();
        _disposedValue = true;
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

  public class HitLogRow : HitRecord
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
    public double BeginTime { get; set; }
    public bool IsPet { get; set; }
    public bool IsGroupingEnabled { get; set; }
    public string TimeSince { get; set; }
  }
}
