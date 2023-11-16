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
using System.Windows.Input;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for NpcTable.xaml
  /// </summary>
  public partial class FightTable
  {
    // time before creating new group
    public const int GROUP_TIMEOUT = 120;

    // NPC Search
    private static int CurrentFightSearchIndex;
    private static int CurrentFightSearchDirection = 1;
    private static Fight CurrentSearchEntry;
    private static bool NeedSelectionChange;

    private readonly ObservableCollection<Fight> Fights = new();
    private readonly ObservableCollection<Fight> NonTankingFights = new();
    private bool CurrentShowBreaks;
    private int CurrentGroup = 1;
    private int CurrentNonTankingGroup = 1;
    private uint CurrentSortId = 1;
    private bool NeedRefresh;
    private bool IsEveryOther;

    private readonly List<Fight> FightsToProcess = new();
    private readonly List<Fight> NonTankingFightsToProcess = new();
    private readonly DispatcherTimer SelectionTimer;
    private readonly DispatcherTimer SearchTextTimer;
    private readonly DispatcherTimer UpdateTimer;

    public FightTable()
    {
      InitializeComponent();

      // fight search box
      fightSearchBox.FontStyle = FontStyles.Italic;
      fightSearchBox.Text = Resource.NPC_SEARCH_TEXT;

      menuItemClear.IsEnabled = menuItemSelectFight.IsEnabled = menuItemUnselectFight.IsEnabled =
        menuItemSetPet.IsEnabled = menuItemSetPlayer.IsEnabled = menuItemRefresh.IsEnabled = false;

      SelectionTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 750) };
      SelectionTimer.Tick += (_, _) =>
      {
        if (!rightClickMenu.IsOpen)
        {
          MainActions.FireFightSelectionChanged(dataGrid.SelectedItems?.Cast<Fight>().ToList());
        }
        else
        {
          NeedSelectionChange = true;
        }

        SelectionTimer.Stop();
      };

      SearchTextTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 750) };
      SearchTextTimer.Tick += (_, _) =>
      {
        if (fightSearchBox.Text.Length > 0)
        {
          SearchForNPC();
        }

        SearchTextTimer.Stop();
      };

      UpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1000) };
      UpdateTimer.Tick += (_, _) => DoProcessFights();
      UpdateTimer.Start();

      // read show hp setting
      fightShowHitPoints.IsChecked = ConfigUtil.IfSet("NpcShowHitPoints");
      dataGrid.Columns[1].IsHidden = !fightShowHitPoints.IsChecked.Value;

      // read show breaks and spells setting
      fightShowBreaks.IsChecked = CurrentShowBreaks = ConfigUtil.IfSet("NpcShowInactivityBreaks", null, true);
      fightShowTanking.IsChecked = ConfigUtil.IfSet("NpcShowTanking", null, true);
      dataGrid.ItemsSource = fightShowTanking.IsChecked.Value ? Fights : NonTankingFights;

      // default these columns to descending
      var desc = new[] { "SortId" };
      dataGrid.SortColumnsChanging += (s, e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      dataGrid.SortColumnsChanged += (s, e) => DataGridUtil.SortColumnsChanged(s, e, desc);

      DataManager.Instance.EventsClearedActiveData += Instance_EventsCleardActiveData;
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

      return new List<Fight>();
    }

    internal List<Fight> GetFights()
    {
      if (dataGrid?.ItemsSource is ObservableCollection<Fight> fights)
      {
        return fights.Where(item => !item.IsInactivity).ToList();
      }

      return new List<Fight>();
    }

    private void EventsUpdateFight(object sender, Fight fight) => NeedRefresh = true;
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
    }

    private void ItemsSourceChanged(object sender, GridItemsSourceChangedEventArgs e)
    {
      dataGrid.View.Filter = item => !(CurrentShowBreaks == false && ((Fight)item).IsInactivity);
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
      if (NeedSelectionChange)
      {
        MainActions.FireFightSelectionChanged(dataGrid.SelectedItems?.Cast<Fight>().ToList());
        NeedSelectionChange = false;
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
        RemoveFight(Fights, name);
        RemoveFight(NonTankingFights, name);
      }, DispatcherPriority.DataBind);
    }

    private void SetPetClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItem is Fight { IsInactivity: false } npc)
      {
        var name = npc.Name;
        Task.Delay(120).ContinueWith(_ =>
        {
          PlayerManager.Instance.AddVerifiedPet(name);
          PlayerManager.Instance.AddPetToPlayer(name, Labels.UNASSIGNED);
        }, TaskScheduler.Default);
      }
    }

    private void SetPlayerClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid.SelectedItem is Fight { IsInactivity: false } npc)
      {
        var name = npc.Name;
        Task.Delay(120).ContinueWith(_ => PlayerManager.Instance.AddVerifiedPlayer(name, DateUtil.ToDouble(DateTime.Now)), TaskScheduler.Default);
      }
    }

    private void ProcessFight(Fight fight)
    {
      lock (FightsToProcess)
      {
        FightsToProcess.Add(fight);
      }
    }

    private void ProcessNonTankingFight(Fight fight)
    {
      lock (FightsToProcess)
      {
        NonTankingFightsToProcess.Add(fight);
      }
    }

    private void DoProcessFights()
    {
      IsEveryOther = !IsEveryOther;

      List<Fight> processList = null;
      List<Fight> processNonTankingList = null;
      lock (FightsToProcess)
      {
        if (FightsToProcess.Count > 0)
        {
          processList = new List<Fight>();
          processList.AddRange(FightsToProcess);
          FightsToProcess.Clear();
        }

        if (NonTankingFightsToProcess.Count > 0)
        {
          processNonTankingList = new List<Fight>();
          processNonTankingList.AddRange(NonTankingFightsToProcess);
          NonTankingFightsToProcess.Clear();
        }
      }

      if (processList != null)
      {
        var lastWithTankingTime = double.NaN;

        var searchAttempts = 0;
        foreach (var fight in Fights.Reverse())
        {
          if (searchAttempts++ == 30 || fight.IsInactivity)
          {
            break;
          }

          lastWithTankingTime = double.IsNaN(lastWithTankingTime) ? fight.LastTime : Math.Max(lastWithTankingTime, fight.LastTime);
        }

        processList.ForEach(fight =>
        {
          if (!double.IsNaN(lastWithTankingTime) && fight.BeginTime - lastWithTankingTime >= GROUP_TIMEOUT)
          {
            CurrentGroup++;
            AddDivider(fight, Fights, lastWithTankingTime);
          }

          fight.GroupId = CurrentGroup;
          AddFight(fight, Fights);
          lastWithTankingTime = double.IsNaN(lastWithTankingTime) ? fight.LastTime : Math.Max(lastWithTankingTime, fight.LastTime);
        });

        NewRowsAdded(Fights);
      }

      if (processNonTankingList != null)
      {
        var lastNonTankingTime = double.NaN;

        var searchAttempts = 0;
        foreach (var fight in NonTankingFights.Reverse())
        {
          if (searchAttempts++ == 30 || fight.IsInactivity)
          {
            break;
          }

          lastNonTankingTime = double.IsNaN(lastNonTankingTime) ? fight.LastDamageTime : Math.Max(lastNonTankingTime, fight.LastDamageTime);
        }

        processNonTankingList.ForEach(fight =>
        {
          if (!double.IsNaN(lastNonTankingTime) && fight.DamageHits > 0 && fight.BeginTime - lastNonTankingTime >= GROUP_TIMEOUT)
          {
            CurrentNonTankingGroup++;
            AddDivider(fight, NonTankingFights, lastNonTankingTime);
          }

          fight.NonTankingGroupId = CurrentNonTankingGroup;
          AddFight(fight, NonTankingFights);
          lastNonTankingTime = double.IsNaN(lastNonTankingTime) ? fight.LastDamageTime : Math.Max(lastNonTankingTime, fight.LastDamageTime);
        });

        NewRowsAdded(NonTankingFights);
      }

      if (NeedRefresh && ((processList == null && dataGrid.ItemsSource == Fights) || (processNonTankingList == null && dataGrid.ItemsSource == NonTankingFights)) &&
        (Keyboard.GetKeyStates(Key.LeftShift) & KeyStates.Down) == 0 && (Keyboard.GetKeyStates(Key.LeftCtrl) & KeyStates.Down) == 0)
      {
        dataGrid.View.RefreshFilter();
        NeedRefresh = false;
      }
    }

    private void AddFight(Fight fight, ObservableCollection<Fight> list)
    {
      fight.SortId = CurrentSortId++;
      var ttl = fight.LastTime - fight.BeginTime + 1;
      fight.TooltipText = $"#Hits To Players: {fight.TankHits}, #Hits From Players: {fight.DamageHits}, Time Alive: {ttl}s";
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
        BeginTimeString = Fight.BREAKTIME,
        Name = "Inactivity > " + DateUtil.FormatGeneralTime(seconds),
        TooltipText = "No Data During This Time",
        SortId = CurrentSortId++
      };

      list.Add(divider);
    }

    private void NewRowsAdded(ObservableCollection<Fight> list)
    {
      if (dataGrid.ItemsSource == list)
      {
        NeedRefresh = false;
      }

      if (DockingManager.GetState(Parent as ContentControl) != DockState.Hidden && !dataGrid.IsMouseOver && dataGrid.View.Records.Count > 1)
      {
        Dispatcher.InvokeAsync(() => dataGrid.ScrollInView(new RowColumnIndex(dataGrid.View.Records.Count, 0)));
      }
    }

    internal void DataGridSelectionChanged()
    {
      NeedSelectionChange = false;
      // adds a delay where a drag-select doesn't keep sending events
      SelectionTimer.Stop();
      SelectionTimer.Start();

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
      NeedSelectionChange = false;
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
      NeedSelectionChange = false;
      foreach (var fight in GetFightGroup())
      {
        dataGrid.SelectedItems.Remove(fight);
      }
    }

    private void ShowBreakChange(object sender, RoutedEventArgs e)
    {
      if (dataGrid?.ItemsSource is ObservableCollection<Fight>)
      {
        CurrentShowBreaks = fightShowBreaks.IsChecked.Value;
        ConfigUtil.SetSetting("NpcShowInactivityBreaks", CurrentShowBreaks);
        dataGrid.View.RefreshFilter();
      }
    }

    private void ShowTankingChange(object sender, RoutedEventArgs e)
    {
      if (dataGrid?.ItemsSource is ObservableCollection<Fight>)
      {
        dataGrid.ItemsSource = fightShowTanking.IsChecked.Value ? Fights : NonTankingFights;
        ConfigUtil.SetSetting("NpcShowTanking", fightShowTanking.IsChecked.Value);
        dataGrid.View.RefreshFilter();
      }
    }

    private void ShowHitPointsChange(object sender, RoutedEventArgs e)
    {
      if (dataGrid != null)
      {
        dataGrid.Columns[1].IsHidden = !dataGrid.Columns[1].IsHidden;
        ConfigUtil.SetSetting("NpcShowHitPoints", !dataGrid.Columns[1].IsHidden);
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
          if (CurrentSearchEntry != null)
          {
            CurrentSearchEntry.IsSearchResult = false;
          }
          dataGrid.Focus();
        }
        else if (e.Key == Key.Enter)
        {
          SearchForNPC(e.KeyboardDevice.IsKeyDown(Key.RightShift) || e.KeyboardDevice.IsKeyDown(Key.LeftShift));
        }
      }
    }

    private IEnumerable<Fight> GetFightGroup()
    {
      if (dataGrid.CurrentItem is Fight { IsInactivity: false } npc)
      {
        if (dataGrid.ItemsSource == Fights)
        {
          return Fights.Where(fight => fight.GroupId == npc.GroupId);
        }

        if (dataGrid.ItemsSource == NonTankingFights)
        {
          return NonTankingFights.Where(fight => fight.NonTankingGroupId == npc.NonTankingGroupId);
        }
      }

      return new List<Fight>();
    }

    private void SearchForNPC(bool backwards = false)
    {
      if (CurrentSearchEntry != null)
      {
        CurrentSearchEntry.IsSearchResult = false;
      }

      var records = dataGrid.View.Records;
      if (fightSearchBox.Text.Length > 0 && records.Count > 0)
      {
        int checksNeeded;
        int direction;
        if (backwards)
        {
          direction = -1;
          if (CurrentFightSearchDirection != direction)
          {
            CurrentFightSearchIndex -= 2;
          }

          if (CurrentFightSearchIndex < 0)
          {
            CurrentFightSearchIndex = records.Count - 1;
          }

          // 1 check/loop from start to finish or add a 2nd to continue from the middle to element - 1
          checksNeeded = CurrentFightSearchIndex == (records.Count - 1) ? 1 : 2;
        }
        else
        {
          direction = 1;
          if (CurrentFightSearchDirection != direction)
          {
            CurrentFightSearchIndex += 2;
          }

          if (CurrentFightSearchIndex >= records.Count)
          {
            CurrentFightSearchIndex = 0;
          }

          // 1 check/loop from start to finish or add a 2nd to continue from the middle to element - 1
          checksNeeded = CurrentFightSearchIndex == 0 ? 1 : 2;
        }

        CurrentFightSearchDirection = direction;

        while (checksNeeded-- > 0)
        {
          for (var i = CurrentFightSearchIndex; i < records.Count && i >= 0; i += 1 * direction)
          {
            if (records.GetItemAt(i) is Fight { Name: not null } npc && npc.Name.IndexOf(fightSearchBox.Text, StringComparison.OrdinalIgnoreCase) > -1)
            {
              npc.IsSearchResult = true;
              CurrentSearchEntry = npc;
              CurrentFightSearchIndex = i + (1 * direction);
              Dispatcher.InvokeAsync(() => dataGrid.ScrollInView(new RowColumnIndex(dataGrid.ResolveToRowIndex(i), 0)));
              return;
            }
          }

          if (checksNeeded == 1)
          {
            CurrentFightSearchIndex = (direction == 1) ? CurrentFightSearchIndex = 0 : CurrentFightSearchIndex = records.Count - 1;
          }
        }
      }
    }

    private void FightSearchBoxTextChanged(object sender, TextChangedEventArgs e)
    {
      SearchTextTimer?.Stop();

      if (e.Changes.FirstOrDefault(change => change.AddedLength > 0) != null)
      {
        SearchTextTimer?.Start();
      }
    }

    private void Instance_EventsCleardActiveData(object sender, bool cleared)
    {
      NonTankingFights.Clear();
      NonTankingFightsToProcess.Clear();
      Fights.Clear();
      FightsToProcess.Clear();
      CurrentGroup = 1;
      CurrentNonTankingGroup = 1;
      CurrentSearchEntry = null;
    }
  }
}
