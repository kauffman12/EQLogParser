using Syncfusion.Data;
using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.ScrollAxis;
using Syncfusion.Windows.Tools.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace EQLogParser
{
  public partial class FightTable
  {
    // time before creating new group
    public const int GroupTimeout = 120;

    // NPC Search
    private static int _currentFightSearchIndex;
    private static int _currentFightSearchDirection = 1;
    private static Fight _currentSearchEntry;
    private static bool _needSelectionChange;

    private readonly ObservableCollection<Fight> _fights = [];
    private readonly ObservableCollection<Fight> _nonTankingFights = [];
    private bool _currentShowBreaks;
    private bool _currentShowHp;
    private int _currentGroup = 1;
    private int _currentNonTankingGroup = 1;
    private uint _currentSortId = 1;
    private bool _needRefresh;
    private bool _isEveryOther;
    private TimeRange _allRanges;

    private readonly List<Fight> _fightsToProcess = [];
    private readonly List<Fight> _nonTankingFightsToProcess = [];
    private readonly DispatcherTimer _selectionTimer;
    private readonly DispatcherTimer _searchTextTimer;
    private readonly DispatcherTimer _updateTimer;

    public FightTable()
    {
      InitializeComponent();

      // fight search box
      fightSearchBox.FontStyle = FontStyles.Italic;
      fightSearchBox.Text = Resource.NPC_SEARCH_TEXT;

      menuItemClear.IsEnabled = menuItemSelectFight.IsEnabled = menuItemUnselectFight.IsEnabled =
        menuItemSetPet.IsEnabled = menuItemSetPlayer.IsEnabled = menuItemRefresh.IsEnabled = false;

      _selectionTimer = new DispatcherTimer(DispatcherPriority.Send) { Interval = new TimeSpan(0, 0, 0, 0, 750) };
      _selectionTimer.Tick += (_, _) =>
      {
        if (!rightClickMenu.IsOpen)
        {
          _allRanges = new TimeRange();
          var selected = dataGrid.SelectedItems?.Cast<Fight>().ToList();
          if (selected != null)
          {
            foreach (var fight in selected.OrderBy(sel => sel.Id))
            {
              if (!fight.IsInactivity)
              {
                _allRanges.Add(new TimeSegment(fight.BeginTime, fight.LastTime));
              }
            }
          }

          MainActions.FireFightSelectionChanged(selected);
        }
        else
        {
          _needSelectionChange = true;
        }

        _selectionTimer.Stop();
      };

      _searchTextTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 750) };
      _searchTextTimer.Tick += (_, _) =>
      {
        if (fightSearchBox.Text.Length > 0)
        {
          SearchForNpc();
        }

        _searchTextTimer.Stop();
      };

      _updateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1000) };
      _updateTimer.Tick += (_, _) => DoProcessFights();
      _updateTimer.Start();

      // read show hp setting
      _currentShowHp = ConfigUtil.IfSet("NpcShowHitPoints");
      fightShowHitPoints.IsChecked = _currentShowHp;

      // read show breaks and spells setting
      fightShowBreaks.IsChecked = _currentShowBreaks = ConfigUtil.IfSet("NpcShowInactivityBreaks", true);
      fightShowTanking.IsChecked = ConfigUtil.IfSet("NpcShowTanking", true);
      dataGrid.ItemsSource = fightShowTanking.IsChecked.Value ? _fights : _nonTankingFights;

      // default these columns to descending
      var desc = new[] { "SortId" };
      dataGrid.SortColumnsChanging += (s, e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      dataGrid.SortColumnsChanged += (s, e) => DataGridUtil.SortColumnsChanged(s, e, desc);

      DataManager.Instance.EventsClearedActiveData += EventsClearedActiveData;
      DataManager.Instance.EventsRemovedFight += EventsRemovedFight;
      DataManager.Instance.EventsNewFight += EventsNewFight;
      DataManager.Instance.EventsUpdateFight += EventsUpdateFight;
      DataManager.Instance.EventsNewNonTankingFight += EventsNewNonTankingFight;
      MainActions.EventsThemeChanged += EventsThemeChanged;
    }

    internal List<Fight> GetSelectedFights()
    {
      if (dataGrid?.SelectedItems is { } selected)
      {
        return selected.Cast<Fight>().Where(item => !item.IsInactivity).ToList();
      }

      return [];
    }

    internal List<Fight> GetFights()
    {
      if (dataGrid?.ItemsSource is ObservableCollection<Fight> fights)
      {
        return fights.Where(item => !item.IsInactivity).ToList();
      }

      return [];
    }

    internal TimeRange GetAllRanges()
    {
      return _allRanges;
    }

    private void EventsUpdateFight(object sender, Fight fight) => _needRefresh = true;
    private void EventsRemovedFight(object sender, string name) => RemoveFight(name);
    private void EventsNewFight(object sender, Fight fight) => ProcessFight(fight);
    private void EventsNewNonTankingFight(object sender, Fight fight) => ProcessNonTankingFight(fight);
    private void ClearClick(object sender, RoutedEventArgs e) => DataManager.Instance.Clear();
    private void SelectionChanged(object sender, GridSelectionChangedEventArgs e) => DataGridSelectionChanged();

    private void EventsThemeChanged(string _)
    {
      // just toggle row style to get it to refresh
      var style = dataGrid.RowStyle;
      dataGrid.RowStyle = null;
      dataGrid.RowStyle = style;
      DataGridUtil.RefreshTableColumns(dataGrid);
    }

    private void ItemsSourceChanged(object sender, GridItemsSourceChangedEventArgs e)
    {
      dataGrid.View.Filter = item => !(_currentShowBreaks == false && ((Fight)item).IsInactivity);
    }

    private static void RemoveFight(ObservableCollection<Fight> fights, string name)
    {
      for (var i = fights.Count - 1; i >= 0; i--)
      {
        if (!fights[i].IsInactivity && !string.IsNullOrEmpty(fights[i].Name) && fights[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
          fights.RemoveAt(i);
        }
      }
    }

    private void RightClickClosed(object sender, RoutedEventArgs e)
    {
      if (_needSelectionChange)
      {
        MainActions.FireFightSelectionChanged(dataGrid.SelectedItems?.Cast<Fight>().ToList());
        _needSelectionChange = false;
      }
    }

    private void RightClickOpening(object sender, ContextMenuEventArgs e)
    {
      var source = e.OriginalSource as dynamic;
      if (source.DataContext is Fight fight)
      {
        dataGrid.CurrentItem = fight;
      }
    }

    private void RemoveFight(string name)
    {
      Dispatcher.InvokeAsync(() =>
      {
        RemoveFight(_fights, name);
        RemoveFight(_nonTankingFights, name);
      }, DispatcherPriority.DataBind);
    }

    private async void SetPetClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItem is Fight { IsInactivity: false } npc)
      {
        var name = npc.Name;
        await Task.Delay(120);
        PlayerManager.Instance.AddVerifiedPet(name);
        PlayerManager.Instance.AddPetToPlayer(name, Labels.Unassigned);
        RemoveFight(name); // force in case already in the pet list for some reason
      }
    }

    private async void SetPlayerClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItem is Fight { IsInactivity: false } npc)
      {
        var name = npc.Name;
        var dateTime = DateUtil.ToDouble(DateTime.Now);
        await Task.Delay(120);
        PlayerManager.Instance.AddVerifiedPlayer(name, dateTime);
        RemoveFight(name); // force in case already in the player list for some reason
      }
    }

    private void ProcessFight(Fight fight)
    {
      lock (_fightsToProcess) _fightsToProcess.Add(fight);
    }

    private void ProcessNonTankingFight(Fight fight)
    {
      lock (_fightsToProcess) _nonTankingFightsToProcess.Add(fight);
    }

    private void DoProcessFights()
    {
      _isEveryOther = !_isEveryOther;

      List<Fight> processList = null;
      List<Fight> processNonTankingList = null;
      lock (_fightsToProcess)
      {
        if (_fightsToProcess.Count > 0)
        {
          processList = [];
          processList.AddRange(_fightsToProcess);
          _fightsToProcess.Clear();
        }

        if (_nonTankingFightsToProcess.Count > 0)
        {
          processNonTankingList = [];
          processNonTankingList.AddRange(_nonTankingFightsToProcess);
          _nonTankingFightsToProcess.Clear();
        }
      }

      if (processList != null)
      {
        var lastWithTankingTime = double.NaN;

        var searchAttempts = 0;
        foreach (var fight in _fights.Reverse())
        {
          if (searchAttempts++ == 30 || fight.IsInactivity)
          {
            break;
          }

          lastWithTankingTime = double.IsNaN(lastWithTankingTime) ? fight.LastTime : Math.Max(lastWithTankingTime, fight.LastTime);
        }

        processList.ForEach(fight =>
        {
          if (!double.IsNaN(lastWithTankingTime) && fight.BeginTime - lastWithTankingTime >= GroupTimeout)
          {
            _currentGroup++;
            AddDivider(fight, _fights, lastWithTankingTime);
          }

          fight.GroupId = _currentGroup;
          AddFight(fight, _fights);
          lastWithTankingTime = double.IsNaN(lastWithTankingTime) ? fight.LastTime : Math.Max(lastWithTankingTime, fight.LastTime);
        });

        NewRowsAdded(_fights);
      }

      if (processNonTankingList != null)
      {
        var lastNonTankingTime = double.NaN;

        var searchAttempts = 0;
        foreach (var fight in _nonTankingFights.Reverse())
        {
          if (searchAttempts++ == 30 || fight.IsInactivity)
          {
            break;
          }

          lastNonTankingTime = double.IsNaN(lastNonTankingTime) ? fight.LastDamageTime : Math.Max(lastNonTankingTime, fight.LastDamageTime);
        }

        processNonTankingList.ForEach(fight =>
        {
          if (!double.IsNaN(lastNonTankingTime) && fight.DamageHits > 0 && fight.BeginTime - lastNonTankingTime >= GroupTimeout)
          {
            _currentNonTankingGroup++;
            AddDivider(fight, _nonTankingFights, lastNonTankingTime);
          }

          fight.NonTankingGroupId = _currentNonTankingGroup;
          AddFight(fight, _nonTankingFights);
          lastNonTankingTime = double.IsNaN(lastNonTankingTime) ? fight.LastDamageTime : Math.Max(lastNonTankingTime, fight.LastDamageTime);
        });

        NewRowsAdded(_nonTankingFights);
      }

      if (_needRefresh && ((processList == null && dataGrid.ItemsSource == _fights) || (processNonTankingList == null && dataGrid.ItemsSource == _nonTankingFights)) &&
        (Keyboard.GetKeyStates(Key.LeftShift) & KeyStates.Down) == 0 && (Keyboard.GetKeyStates(Key.LeftCtrl) & KeyStates.Down) == 0)
      {
        dataGrid?.View?.RefreshFilter();
        _needRefresh = false;
      }
    }

    private void AddFight(Fight fight, ObservableCollection<Fight> list)
    {
      fight.SortId = _currentSortId++;
      list.Add(fight);
    }

    private void AddDivider(Fight fight, ObservableCollection<Fight> list, double lastTime)
    {
      var seconds = fight.BeginTime - lastTime;
      var divider = new Fight
      {
        LastTime = fight.BeginTime,
        BeginTime = lastTime,
        IsInactivity = true,
        BeginTimeString = Fight.Breaktime,
        Name = "Inactivity > " + DateUtil.FormatGeneralTime(seconds),
        TooltipText = "No Data During This Time",
        SortId = _currentSortId++
      };

      list.Add(divider);
    }

    private void NewRowsAdded(ObservableCollection<Fight> list)
    {
      if (dataGrid != null)
      {
        if (Parent is ContentControl control && DockingManager.GetState(control) != DockState.Hidden &&
          !dataGrid.IsMouseOver && dataGrid.View?.Records?.Count > 1)
        {
          Dispatcher.InvokeAsync(() => dataGrid.ScrollInView(new RowColumnIndex(dataGrid.View.Records.Count, 0)));
        }
      }
    }

    internal void DataGridSelectionChanged()
    {
      _needSelectionChange = false;
      // adds a delay where a drag-select doesn't keep sending events
      _selectionTimer.Stop();
      _selectionTimer.Start();

      var items = dataGrid.View.Records;
      menuItemClear.IsEnabled = menuItemSelectFight.IsEnabled = menuItemUnselectFight.IsEnabled = items.Count > 0;

      var selected = dataGrid.SelectedItem as Fight;
      menuItemSetPet.IsEnabled = dataGrid.SelectedItems.Count == 1 && selected?.IsInactivity == false;
      menuItemSetPlayer.IsEnabled = dataGrid.SelectedItems.Count == 1 && selected?.IsInactivity == false &&
        PlayerManager.IsPossiblePlayerName((dataGrid.SelectedItem as Fight)?.Name);
      menuItemRefresh.IsEnabled = dataGrid.SelectedItems.Count > 0;
    }

    private void RefreshClick(object sender, RoutedEventArgs e)
    {
      MainActions.FireFightSelectionChanged(dataGrid.SelectedItems?.Cast<Fight>().ToList());
    }

    private void SelectGroupClick(object sender, RoutedEventArgs e)
    {
      _needSelectionChange = false;
      foreach (var fight in GetFightGroup())
      {
        if (!dataGrid.SelectedItems.Contains(fight))
        {
          dataGrid.SelectedItems.Add(fight);
        }
      }
    }

    private void UnselectGroupClick(object sender, RoutedEventArgs e)
    {
      _needSelectionChange = false;
      foreach (var fight in GetFightGroup())
      {
        dataGrid.SelectedItems.Remove(fight);
      }
    }

    private void ShowBreakChanged(object sender, RoutedEventArgs e)
    {
      if (dataGrid?.View != null)
      {
        _currentShowBreaks = fightShowBreaks.IsChecked == true;
        ConfigUtil.SetSetting("NpcShowInactivityBreaks", _currentShowBreaks);
        dataGrid.View.RefreshFilter();
      }
    }

    private void ShowTankingChanged(object sender, RoutedEventArgs e)
    {
      if (dataGrid?.View != null)
      {
        dataGrid.ItemsSource = (fightShowTanking.IsChecked == true) ? _fights : _nonTankingFights;
        ConfigUtil.SetSetting("NpcShowTanking", fightShowTanking.IsChecked == true);
        dataGrid.View.RefreshFilter();
      }
    }

    private void ShowHitPointsChanged(object sender, RoutedEventArgs e)
    {
      if (dataGrid?.View != null)
      {
        _currentShowHp = !_currentShowHp;
        dataGrid.Columns[1].IsHidden = !_currentShowHp;
        ConfigUtil.SetSetting("NpcShowHitPoints", _currentShowHp);
      }
    }

    private void FightSearchBoxGotFocus(object sender, RoutedEventArgs e)
    {
      if (fightSearchBox.Text == Resource.NPC_SEARCH_TEXT)
      {
        fightSearchBox.Text = "";
        fightSearchBox.FontStyle = FontStyles.Normal;
      }
    }

    private void FightSearchBoxLostFocus(object sender, RoutedEventArgs e)
    {
      if (fightSearchBox.Text.Length == 0)
      {
        fightSearchBox.Text = Resource.NPC_SEARCH_TEXT;
        fightSearchBox.FontStyle = FontStyles.Italic;
      }
    }

    internal void FightSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
      if (fightSearchBox.IsFocused)
      {
        if (e.Key == Key.Escape)
        {
          fightSearchBox.Text = Resource.NPC_SEARCH_TEXT;
          fightSearchBox.FontStyle = FontStyles.Italic;
          if (_currentSearchEntry != null)
          {
            _currentSearchEntry.IsSearchResult = false;
          }
          dataGrid.Focus();
        }
        else if (e.Key == Key.Enter)
        {
          SearchForNpc(e.KeyboardDevice.IsKeyDown(Key.RightShift) || e.KeyboardDevice.IsKeyDown(Key.LeftShift));
        }
      }
    }

    private IEnumerable<Fight> GetFightGroup()
    {
      if (dataGrid.CurrentItem is Fight { IsInactivity: false } npc)
      {
        if (dataGrid.ItemsSource == _fights)
        {
          return _fights.Where(fight => fight.GroupId == npc.GroupId);
        }

        if (dataGrid.ItemsSource == _nonTankingFights)
        {
          return _nonTankingFights.Where(fight => fight.NonTankingGroupId == npc.NonTankingGroupId);
        }
      }

      return new List<Fight>();
    }

    private void SearchForNpc(bool backwards = false)
    {
      if (_currentSearchEntry != null)
      {
        _currentSearchEntry.IsSearchResult = false;
      }

      var records = dataGrid.View.Records;
      if (fightSearchBox.Text.Length > 0 && records.Count > 0)
      {
        int checksNeeded;
        int direction;
        if (backwards)
        {
          direction = -1;
          if (_currentFightSearchDirection != direction)
          {
            _currentFightSearchIndex -= 2;
          }

          if (_currentFightSearchIndex < 0)
          {
            _currentFightSearchIndex = records.Count - 1;
          }

          // 1 check/loop from start to finish or add a 2nd to continue from the middle to element - 1
          checksNeeded = _currentFightSearchIndex == (records.Count - 1) ? 1 : 2;
        }
        else
        {
          direction = 1;
          if (_currentFightSearchDirection != direction)
          {
            _currentFightSearchIndex += 2;
          }

          if (_currentFightSearchIndex >= records.Count)
          {
            _currentFightSearchIndex = 0;
          }

          // 1 check/loop from start to finish or add a 2nd to continue from the middle to element - 1
          checksNeeded = _currentFightSearchIndex == 0 ? 1 : 2;
        }

        _currentFightSearchDirection = direction;

        while (checksNeeded-- > 0)
        {
          for (var i = _currentFightSearchIndex; i < records.Count && i >= 0; i += 1 * direction)
          {
            if (records.GetItemAt(i) is Fight { Name: not null } npc && npc.Name.IndexOf(fightSearchBox.Text, StringComparison.OrdinalIgnoreCase) > -1)
            {
              npc.IsSearchResult = true;
              _currentSearchEntry = npc;
              _currentFightSearchIndex = i + (1 * direction);
              Dispatcher.InvokeAsync(() => dataGrid.ScrollInView(new RowColumnIndex(dataGrid.ResolveToRowIndex(i), 0)));
              return;
            }
          }

          if (checksNeeded == 1)
          {
            _currentFightSearchIndex = (direction == 1) ? _currentFightSearchIndex = 0 : _currentFightSearchIndex = records.Count - 1;
          }
        }
      }
    }

    private void FightSearchBoxTextChanged(object sender, TextChangedEventArgs e)
    {
      _searchTextTimer?.Stop();

      if (e.Changes.FirstOrDefault(change => change.AddedLength > 0) != null)
      {
        _searchTextTimer?.Start();
      }
    }

    private void EventsClearedActiveData(bool cleared)
    {
      _nonTankingFights.Clear();
      _nonTankingFightsToProcess.Clear();
      _fights.Clear();
      _fightsToProcess.Clear();
      _currentGroup = 1;
      _currentNonTankingGroup = 1;
      _currentSearchEntry = null;
    }

    private void AutoGeneratingColumn(object sender, AutoGeneratingColumnArgs e)
    {
      if (e.Column.MappingName == "SortId")
      {
        e.Column.SortMode = DataReflectionMode.Value;
        e.Column.DisplayBinding = new Binding
        {
          Path = new PropertyPath("BeginTimeString")
        };
        e.Column.TextAlignment = TextAlignment.Center;
        e.Column.ShowToolTip = true;
        e.Column.ToolTipTemplate = (DataTemplate)Application.Current.Resources["TemplateToolTip"];
        e.Column.HeaderText = "Initial Hit Time";
        e.Column.Width = MainActions.CurrentDateTimeWidth;
      }
      else if (e.Column.MappingName == "DamageTotal")
      {
        e.Column.IsHidden = !_currentShowHp;
        e.Column.DisplayBinding = new Binding
        {
          Path = new PropertyPath(e.Column.MappingName),
          Converter = new TotalFormatConverter()
        };
        e.Column.TextAlignment = TextAlignment.Right;
        e.Column.ShowToolTip = true;
        e.Column.ToolTipTemplate = (DataTemplate)Application.Current.Resources["TemplateToolTip"];
        e.Column.HeaderText = "HP";
        e.Column.ColumnSizer = GridLengthUnitType.Auto;
        e.Column.Padding = new Thickness(4, 0, 4, 0);
      }
      else if (e.Column.MappingName == "Name")
      {
        e.Column.ShowToolTip = true;
        e.Column.ToolTipTemplate = (DataTemplate)Application.Current.Resources["TemplateToolTip"];
        e.Column.ColumnSizer = GridLengthUnitType.AutoLastColumnFill;
        e.Column.Padding = new Thickness(4, 0, 0, 0);
      }
      else
      {
        e.Cancel = true;
      }
    }
  }
}
