using Syncfusion.UI.Xaml.Grid;
using Syncfusion.UI.Xaml.ScrollAxis;
using Syncfusion.Windows.Tools.Controls;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
  public partial class FightTable : UserControl
  {
    // events
    public event EventHandler<IList> EventsSelectionChange;

    // time before creating new group
    public const int GroupTimeout = 120;

    // NPC Search
    private static int CurrentFightSearchIndex = 0;
    private static int CurrentFightSearchDirection = 1;
    private static Fight CurrentSearchEntry = null;
    private static bool NeedSelectionChange = false;

    private readonly ObservableCollection<Fight> Fights = new ObservableCollection<Fight>();
    private readonly ObservableCollection<Fight> NonTankingFights = new ObservableCollection<Fight>();
    private bool CurrentShowBreaks;
    private int CurrentGroup = 1;
    private int CurrentNonTankingGroup = 1;
    private uint CurrentSortId = 1;
    private bool NeedRefresh = false;
    private bool IsEveryOther = false;

    private readonly List<Fight> FightsToProcess = new List<Fight>();
    private readonly List<Fight> NonTankingFightsToProcess = new List<Fight>();
    private readonly DispatcherTimer SelectionTimer;
    private readonly DispatcherTimer SearchTextTimer;
    private readonly DispatcherTimer UpdateTimer;

    public FightTable()
    {
      InitializeComponent();

      // fight search box
      fightSearchBox.FontStyle = FontStyles.Italic;
      fightSearchBox.Text = EQLogParser.Resource.NPC_SEARCH_TEXT;

      menuItemClear.IsEnabled = menuItemSelectFight.IsEnabled = menuItemUnselectFight.IsEnabled =
        menuItemSetPet.IsEnabled = menuItemSetPlayer.IsEnabled = false;

      SelectionTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 750) };
      SelectionTimer.Tick += (sender, e) =>
      {
        if (!rightClickMenu.IsOpen)
        {
          EventsSelectionChange(this, dataGrid.SelectedItems);
        }
        else
        {
          NeedSelectionChange = true;
        }

        SelectionTimer.Stop();
      };

      SearchTextTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1500) };
      SearchTextTimer.Tick += (sender, e) =>
      {
        if (fightSearchBox.Text.Length > 0)
        {
          SearchForNPC();
        }

        SearchTextTimer.Stop();
      };

      UpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1250) };
      UpdateTimer.Tick += (sender, e) => ProcessFights();
      UpdateTimer.Start();

      // read show hp setting
      fightShowHitPoints.IsChecked = ConfigUtil.IfSet("NpcShowHitPoints");
      dataGrid.Columns[1].IsHidden = !fightShowHitPoints.IsChecked.Value;

      // read show breaks and spells setting
      fightShowBreaks.IsChecked = CurrentShowBreaks = ConfigUtil.IfSet("NpcShowInactivityBreaks", null, true);
      fightShowTanking.IsChecked = ConfigUtil.IfSet("NpcShowTanking", null, true);
      dataGrid.ItemsSource = fightShowTanking.IsChecked.Value ? Fights : NonTankingFights;

      // default these columns to descending
      string[] desc = new string[] { "SortId" };
      dataGrid.SortColumnsChanging += (object s, GridSortColumnsChangingEventArgs e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      dataGrid.SortColumnsChanged += (object s, GridSortColumnsChangedEventArgs e) => DataGridUtil.SortColumnsChanged(s, e, desc);

      DataManager.Instance.EventsClearedActiveData += Instance_EventsCleardActiveData;
      DataManager.Instance.EventsRemovedFight += EventsRemovedFight;
      DataManager.Instance.EventsNewFight += EventsNewFight;
      DataManager.Instance.EventsUpdateFight += EventsUpdateFight;
      DataManager.Instance.EventsNewNonTankingFight += EventsNewNonTankingFight;
      (Application.Current.MainWindow as MainWindow).EventsThemeChanged += EventsThemeChanged;
    }

    internal IEnumerable<Fight> GetSelectedFights() => dataGrid.SelectedItems.Cast<Fight>().Where(item => !item.IsInactivity);
    internal IEnumerable<Fight> GetFights() => Fights.Where(item => !item.IsInactivity);

    private void EventsUpdateFight(object sender, Fight fight) => NeedRefresh = true;
    private void EventsRemovedFight(object sender, string name) => RemoveFight(name);
    private void EventsNewFight(object sender, Fight fight) => AddFight(fight);
    private void EventsNewNonTankingFight(object sender, Fight fight) => AddNonTankingFight(fight);

    private void EventsThemeChanged(object sender, string e)
    {
      // just toggle row style to get it to refresh
      var style = dataGrid.RowStyle;
      dataGrid.RowStyle = null;
      dataGrid.RowStyle = style;
    }

    private void ItemsSourceChanged(object sender, GridItemsSourceChangedEventArgs e)
    {
      dataGrid.View.Filter = new Predicate<object>(item => !(CurrentShowBreaks == false && ((Fight)item).IsInactivity));
    }

    private static void RemoveFight(ObservableCollection<Fight> fights, string name)
    {
      for (int i = fights.Count - 1; i >= 0; i--)
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
        EventsSelectionChange(this, dataGrid.SelectedItems);
        NeedSelectionChange = false;
      }
    }

    private void AddFight(Fight fight)
    {
      lock (FightsToProcess)
      {
        FightsToProcess.Add(fight);
      }
    }

    private void AddNonTankingFight(Fight fight)
    {
      lock (FightsToProcess)
      {
        NonTankingFightsToProcess.Add(fight);
      }
    }

    private void ProcessFights()
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
        double lastWithTankingTime = double.NaN;

        int searchAttempts = 0;
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
          if (!double.IsNaN(lastWithTankingTime) && fight.BeginTime - lastWithTankingTime >= GroupTimeout)
          {
            CurrentGroup++;
            AddDivider(fight, Fights, lastWithTankingTime);
          }

          fight.GroupId = CurrentGroup;
          fight.SortId = CurrentSortId++;
          Fights.Add(fight);
          lastWithTankingTime = double.IsNaN(lastWithTankingTime) ? fight.LastTime : Math.Max(lastWithTankingTime, fight.LastTime);
        });

        NewRowsAdded(Fights);
      }

      if (processNonTankingList != null)
      {
        double lastNonTankingTime = double.NaN;

        int searchAttempts = 0;
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
          if (!double.IsNaN(lastNonTankingTime) && fight.DamageHits > 0 && fight.BeginTime - lastNonTankingTime >= GroupTimeout)
          {
            CurrentNonTankingGroup++;
            AddDivider(fight, NonTankingFights, lastNonTankingTime);
          }

          fight.NonTankingGroupId = CurrentNonTankingGroup;
          fight.SortId = CurrentSortId++;
          NonTankingFights.Add(fight);
          lastNonTankingTime = double.IsNaN(lastNonTankingTime) ? fight.LastDamageTime : Math.Max(lastNonTankingTime, fight.LastDamageTime);
        });

        NewRowsAdded(NonTankingFights);
      }

      if (NeedRefresh && (processList == null && dataGrid.ItemsSource == Fights || processNonTankingList == null && dataGrid.ItemsSource == NonTankingFights) &&
        (Keyboard.GetKeyStates(Key.LeftShift) & KeyStates.Down) == 0 && (Keyboard.GetKeyStates(Key.LeftCtrl) & KeyStates.Down) == 0)
      {
        dataGrid.View.RefreshFilter();
        NeedRefresh = false;
      }
    }

    private void AddDivider(Fight fight, ObservableCollection<Fight> list, double lastTime)
    {
      var seconds = fight.BeginTime - lastTime;
      Fight divider = new Fight
      {
        LastTime = fight.BeginTime,
        BeginTime = lastTime,
        IsInactivity = true,
        BeginTimeString = Fight.BREAKTIME,
        Name = "Inactivity > " + DateUtil.FormatGeneralTime(seconds),
        TooltipText = "No Data During This Time"
      };

      divider.SortId = CurrentSortId++;
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

    private void RemoveFight(string name)
    {
      Dispatcher.InvokeAsync(() =>
      {
        RemoveFight(Fights, name);
        RemoveFight(NonTankingFights, name);
      }, DispatcherPriority.DataBind);
    }

    private void ClearClick(object sender, RoutedEventArgs e) => DataManager.Instance.Clear();
    private void SelectionChanged(object sender, GridSelectionChangedEventArgs e) => DataGridSelectionChanged();

    private void SetPetClick(object sender, RoutedEventArgs e)
    {
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      var callingDataGrid = menu.PlacementTarget as SfDataGrid;
      if (callingDataGrid.SelectedItem is Fight npc && !npc.IsInactivity)
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
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      var callingDataGrid = menu.PlacementTarget as SfDataGrid;
      if (callingDataGrid.SelectedItem is Fight npc && !npc.IsInactivity)
      {
        var name = npc.Name;
        Task.Delay(120).ContinueWith(_ => PlayerManager.Instance.AddVerifiedPlayer(name, DateUtil.ToDouble(DateTime.Now)), TaskScheduler.Default);
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
      menuItemSetPet.IsEnabled = dataGrid.SelectedItems.Count == 1 && !selected.IsInactivity;
      menuItemSetPlayer.IsEnabled = dataGrid.SelectedItems.Count == 1 && !selected.IsInactivity &&
        PlayerManager.IsPossiblePlayerName((dataGrid.SelectedItem as Fight)?.Name);
    }

    private void SelectGroupClick(object sender, RoutedEventArgs e)
    {
      NeedSelectionChange = false;
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      SfDataGrid callingDataGrid = menu.PlacementTarget as SfDataGrid;
      foreach (var fight in GetFightGroup())
      {
        if (!callingDataGrid.SelectedItems.Contains(fight))
        {
          callingDataGrid.SelectedItems.Add(fight);
        }
      }
    }

    private void UnselectGroupClick(object sender, RoutedEventArgs e)
    {
      NeedSelectionChange = false;
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      SfDataGrid callingDataGrid = menu.PlacementTarget as SfDataGrid;
      foreach (var fight in GetFightGroup())
      {
        callingDataGrid.SelectedItems.Remove(fight);
      }
    }

    private void ShowBreakChange(object sender, RoutedEventArgs e)
    {
      if (dataGrid?.ItemsSource is ObservableCollection<Fight> list)
      {
        CurrentShowBreaks = fightShowBreaks.IsChecked.Value;
        ConfigUtil.SetSetting("NpcShowInactivityBreaks", CurrentShowBreaks.ToString(CultureInfo.CurrentCulture));
        dataGrid.View.RefreshFilter();
      }
    }

    private void ShowTankingChange(object sender, RoutedEventArgs e)
    {
      if (dataGrid?.ItemsSource is ObservableCollection<Fight>)
      {
        dataGrid.ItemsSource = fightShowTanking.IsChecked.Value ? Fights : NonTankingFights;
        ConfigUtil.SetSetting("NpcShowTanking", fightShowTanking.IsChecked.Value.ToString(CultureInfo.CurrentCulture));
        dataGrid.View.RefreshFilter();
      }
    }

    private void ShowHitPointsChange(object sender, RoutedEventArgs e)
    {
      if (dataGrid != null)
      {
        dataGrid.Columns[1].IsHidden = !dataGrid.Columns[1].IsHidden;
        ConfigUtil.SetSetting("NpcShowHitPoints", (!dataGrid.Columns[1].IsHidden).ToString(CultureInfo.CurrentCulture));
      }
    }

    private void FightSearchBoxGotFocus(object sender, RoutedEventArgs e)
    {
      if (fightSearchBox.Text == EQLogParser.Resource.NPC_SEARCH_TEXT)
      {
        fightSearchBox.Text = "";
        fightSearchBox.FontStyle = FontStyles.Normal;
      }
    }

    private void FightSearchBoxLostFocus(object sender, RoutedEventArgs e)
    {
      if (fightSearchBox.Text.Length == 0)
      {
        fightSearchBox.Text = EQLogParser.Resource.NPC_SEARCH_TEXT;
        fightSearchBox.FontStyle = FontStyles.Italic;
      }
    }

    internal void FightSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
      if (fightSearchBox.IsFocused)
      {
        if (e.Key == Key.Escape)
        {
          fightSearchBox.Text = EQLogParser.Resource.NPC_SEARCH_TEXT;
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
      if (dataGrid.CurrentItem is Fight npc && !npc.IsInactivity)
      {
        if (dataGrid.ItemsSource == Fights)
        {
          return Fights.Where(fight => fight.GroupId == npc.GroupId);
        }
        else if (dataGrid.ItemsSource == NonTankingFights)
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
          for (int i = CurrentFightSearchIndex; i < records.Count && i >= 0; i += 1 * direction)
          {
            if (records.GetItemAt(i) is Fight npc && npc.Name != null && npc.Name.IndexOf(fightSearchBox.Text, StringComparison.OrdinalIgnoreCase) > -1)
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
