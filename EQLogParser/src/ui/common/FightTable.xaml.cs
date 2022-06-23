﻿using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
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

    internal static readonly SolidColorBrush SEARCH_BRUSH = new SolidColorBrush(Color.FromRgb(58, 84, 63));

    // time before creating new group
    public const int GroupTimeout = 120;

    // NPC Search
    private static int CurrentFightSearchIndex = 0;
    private static int CurrentFightSearchDirection = 1;
    private static DataGridRow CurrentSearchRow = null;
    private static bool NeedSelectionChange = false;

    private readonly ListCollectionView View;
    private readonly ListCollectionView NonTankingView;
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
      fightSearchBox.Text = Properties.Resources.NPC_SEARCH_TEXT;

      fightMenuItemClear.IsEnabled = fightMenuItemSelectAll.IsEnabled = fightMenuItemUnselectAll.IsEnabled =
      fightMenuItemSelectFight.IsEnabled = fightMenuItemUnselectFight.IsEnabled = fightMenuItemSetPet.IsEnabled = fightMenuItemSetPlayer.IsEnabled = false;

      View = (ListCollectionView)CollectionViewSource.GetDefaultView(Fights);
      NonTankingView = (ListCollectionView)CollectionViewSource.GetDefaultView(NonTankingFights);

      var filter = new Predicate<object>(item => !(CurrentShowBreaks == false && ((Fight)item).IsInactivity));
      View.Filter = filter;
      NonTankingView.Filter = filter;

      SelectionTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 800) };
      SelectionTimer.Tick += (sender, e) =>
      {
        if (!rightClickMenu.IsOpen)
        {
          EventsSelectionChange(this, fightDataGrid.SelectedItems);
        }
        else
        {
          NeedSelectionChange = true;
        }

        SelectionTimer.Stop();
      };

      SearchTextTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 500) };
      SearchTextTimer.Tick += (sender, e) =>
      {
        HandleSearchTextChanged();
        SearchTextTimer.Stop();
      };

      UpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1500) };
      UpdateTimer.Tick += (sender, e) => ProcessFights();
      UpdateTimer.Start();

      // read show hp setting
      fightShowHitPoints.IsChecked = ConfigUtil.IfSet("NpcShowHitPoints");
      fightDataGrid.Columns[1].IsHidden = !fightShowHitPoints.IsChecked.Value;

      // read show breaks and spells setting
      fightShowBreaks.IsChecked = CurrentShowBreaks = ConfigUtil.IfSet("NpcShowInactivityBreaks", null, true);
      fightShowTanking.IsChecked = ConfigUtil.IfSet("NpcShowTanking", null, true);
      fightDataGrid.ItemsSource = fightShowTanking.IsChecked.Value ? View : NonTankingView;

      // default these columns to descending
      string[] desc = new string[] { "SortId" };
      fightDataGrid.SortColumnsChanging += (object s, GridSortColumnsChangingEventArgs e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      fightDataGrid.SortColumnsChanged += (object s, GridSortColumnsChangedEventArgs e) => DataGridUtil.SortColumnsChanged(s, e, desc);

      DataManager.Instance.EventsClearedActiveData += Instance_EventsCleardActiveData;
      DataManager.Instance.EventsRemovedFight += Instance_EventsRemovedFight;
      DataManager.Instance.EventsNewFight += Instance_EventsNewFight;
      DataManager.Instance.EventsUpdateFight += Instance_EventsUpdateFight;
      DataManager.Instance.EventsNewNonTankingFight += Instance_EventsNewNonTankingFight;
    }

    internal IEnumerable<Fight> GetSelectedFights() => fightDataGrid.SelectedItems.Cast<Fight>().Where(item => !item.IsInactivity);
    internal bool HasSelected() => fightDataGrid.SelectedItems.Cast<Fight>().FirstOrDefault(item => !item.IsInactivity) != null;
    internal IEnumerable<Fight> GetFights() => Fights.Where(item => !item.IsInactivity);

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
        EventsSelectionChange(this, fightDataGrid.SelectedItems);
        NeedSelectionChange = false;
      }
    }

    private void UpdateCurrentItem(object sender, MouseButtonEventArgs e)
    {
      var gridPoint = e.GetPosition(fightDataGrid);
      var vis = VisualTreeHelper.HitTest(fightDataGrid, gridPoint);

      if (vis != null && vis.VisualHit is FrameworkElement elem && elem.DataContext != null)
      {
        fightDataGrid.CurrentItem = elem.DataContext;
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

            var seconds = fight.BeginTime - lastWithTankingTime;
            Fight divider = new Fight
            {
              LastTime = fight.BeginTime,
              BeginTime = lastWithTankingTime,
              IsInactivity = true,
              BeginTimeString = Fight.BREAKTIME,
              Name = "Inactivity > " + DateUtil.FormatGeneralTime(seconds)
            };

            divider.SortId = CurrentSortId++;
            Fights.Add(divider);
          }

          fight.GroupId = CurrentGroup;
          fight.SortId = CurrentSortId++;
          Fights.Add(fight);
          lastWithTankingTime = double.IsNaN(lastWithTankingTime) ? fight.LastTime : Math.Max(lastWithTankingTime, fight.LastTime);
        });

        if (fightDataGrid.ItemsSource == View)
        {
          NeedRefresh = false;
        }

        //if ((Parent as ContentControl).IsVisible && fightDataGrid.Items.Count > 0 && !fightDataGrid.IsMouseOver)
        //{
        //  fightDataGrid.ScrollIntoView(fightDataGrid.Items[fightDataGrid.Items.Count - 1]);
        //}
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

            // only set processed for non tanking since its a special case
            var seconds = fight.BeginTime - lastNonTankingTime;
            Fight divider = new Fight
            {
              LastTime = fight.BeginTime,
              BeginTime = lastNonTankingTime,
              IsInactivity = true,
              BeginTimeString = Fight.BREAKTIME,
              Name = "Inactivity > " + DateUtil.FormatGeneralTime(seconds)
            };

            divider.SortId = CurrentSortId++;
            NonTankingFights.Add(divider);
          }

          fight.NonTankingGroupId = CurrentNonTankingGroup;
          fight.SortId = CurrentSortId++;
          NonTankingFights.Add(fight);
          lastNonTankingTime = double.IsNaN(lastNonTankingTime) ? fight.LastDamageTime : Math.Max(lastNonTankingTime, fight.LastDamageTime);
        });

        if (fightDataGrid.ItemsSource == NonTankingView)
        {
          NeedRefresh = false;
        }

        //if ((Parent as ContentControl).IsVisible && fightDataGrid.Items.Count > 0 && !fightDataGrid.IsMouseOver)
        //{
        //  fightDataGrid.ScrollIntoView(fightDataGrid.Items[fightDataGrid.Items.Count - 1]);
        //}
      }

      if (NeedRefresh && (processList == null && fightDataGrid.ItemsSource == View || processNonTankingList == null && fightDataGrid.ItemsSource == NonTankingView) &&
        (Keyboard.GetKeyStates(Key.LeftShift) & KeyStates.Down) == 0 && (Keyboard.GetKeyStates(Key.LeftCtrl) & KeyStates.Down) == 0)
      {
        (fightDataGrid.ItemsSource as ICollectionView).Refresh();
        NeedRefresh = false;
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
    private void SelectAllClick(object sender, RoutedEventArgs e) => DataGridUtil.SelectAll(sender as FrameworkElement);
    private void UnselectAllClick(object sender, RoutedEventArgs e) => DataGridUtil.UnselectAll(sender as FrameworkElement);

    private void SetPetClick(object sender, RoutedEventArgs e)
    {
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      DataGrid callingDataGrid = menu.PlacementTarget as DataGrid;
      if (callingDataGrid.SelectedItem is Fight npc && !npc.IsInactivity)
      {
        Task.Delay(120).ContinueWith(_ =>
        {
          PlayerManager.Instance.AddVerifiedPet(npc.Name);
          PlayerManager.Instance.AddPetToPlayer(npc.Name, Labels.UNASSIGNED);
        }, TaskScheduler.Default);
      }
    }

    private void SetPlayerClick(object sender, RoutedEventArgs e)
    {
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      DataGrid callingDataGrid = menu.PlacementTarget as DataGrid;
      if (callingDataGrid.SelectedItem is Fight npc && !npc.IsInactivity)
      {
        Task.Delay(120).ContinueWith(_ => PlayerManager.Instance.AddVerifiedPlayer(npc.Name, DateUtil.ToDouble(DateTime.Now)), TaskScheduler.Default);
      }
    }

    private void SelectionChanged(object sender, GridSelectionChangedEventArgs e)
    {
      // adds a delay where a drag-select doesn't keep sending events
      SelectionTimer.Stop();
      SelectionTimer.Start();

      var callingDataGrid = sender as SfDataGrid;
      var items = callingDataGrid.ItemsSource as ListCollectionView;
      fightMenuItemSelectAll.IsEnabled = (callingDataGrid.SelectedItems.Count < items.Count) && items.Count > 0;
      fightMenuItemUnselectAll.IsEnabled = callingDataGrid.SelectedItems.Count > 0 && items.Count > 0;
      fightMenuItemClear.IsEnabled = items.Count > 0;

      var selected = callingDataGrid.SelectedItem as Fight;
      fightMenuItemSetPet.IsEnabled = callingDataGrid.SelectedItems.Count == 1 && !selected.IsInactivity;
      fightMenuItemSetPlayer.IsEnabled = callingDataGrid.SelectedItems.Count == 1 && !selected.IsInactivity &&
        PlayerManager.Instance.IsPossiblePlayerName((callingDataGrid.SelectedItem as Fight)?.Name);
    }

    private void SelectGroupClick(object sender, RoutedEventArgs e)
    {
      NeedSelectionChange = false;
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      DataGrid callingDataGrid = menu.PlacementTarget as DataGrid;
      GetFightGroup().ForEach(fight =>
      {
        Dispatcher.InvokeAsync(() => callingDataGrid.SelectedItems.Add(fight), DispatcherPriority.Normal);
      });
    }

    private void UnselectGroupClick(object sender, RoutedEventArgs e)
    {
      NeedSelectionChange = false;
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      DataGrid callingDataGrid = menu.PlacementTarget as DataGrid;
      GetFightGroup().ForEach(fight =>
      {
        Dispatcher.InvokeAsync(() => callingDataGrid.SelectedItems.Remove(fight), DispatcherPriority.Normal);
      });
    }

    private void ShowBreakChange(object sender, RoutedEventArgs e)
    {
      if (fightDataGrid?.ItemsSource is ICollectionView view)
      {
        CurrentShowBreaks = fightShowBreaks.IsChecked.Value;
        ConfigUtil.SetSetting("NpcShowInactivityBreaks", CurrentShowBreaks.ToString(CultureInfo.CurrentCulture));
        view.Refresh();
      }
    }

    private void ShowTankingChange(object sender, RoutedEventArgs e)
    {
      if (fightDataGrid?.ItemsSource is ICollectionView)
      {
        fightDataGrid.ItemsSource = fightShowTanking.IsChecked.Value ? View : NonTankingView;
        ConfigUtil.SetSetting("NpcShowTanking", fightShowTanking.IsChecked.Value.ToString(CultureInfo.CurrentCulture));
        (fightDataGrid.ItemsSource as ICollectionView).Refresh();
      }
    }

    private void ShowHitPointsChange(object sender, RoutedEventArgs e)
    {
      if (fightDataGrid != null)
      {
        fightDataGrid.Columns[1].IsHidden = !fightDataGrid.Columns[1].IsHidden;
        ConfigUtil.SetSetting("NpcShowHitPoints", (!fightDataGrid.Columns[1].IsHidden).ToString(CultureInfo.CurrentCulture));
      }
    }

    private void HandleSearchTextChanged()
    {
      if (fightSearchBox.Text.Length > 0)
      {
        SearchForNPC();
      }
    }

    private void FightSearchBoxGotFocus(object sender, RoutedEventArgs e)
    {
      if (fightSearchBox.Text == Properties.Resources.NPC_SEARCH_TEXT)
      {
        fightSearchBox.Text = "";
        fightSearchBox.FontStyle = FontStyles.Normal;
      }
    }

    private void FightSearchBoxLostFocus(object sender, RoutedEventArgs e)
    {
      if (fightSearchBox.Text.Length == 0)
      {
        fightSearchBox.Text = Properties.Resources.NPC_SEARCH_TEXT;
        fightSearchBox.FontStyle = FontStyles.Italic;
      }
    }

    // internal for workaround with event being lost
    internal void FightSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
      if (fightSearchBox.IsFocused)
      {
        if (e.Key == Key.Escape)
        {
          fightSearchBox.Text = Properties.Resources.NPC_SEARCH_TEXT;
          fightSearchBox.FontStyle = FontStyles.Italic;
          if (CurrentSearchRow != null)
          {
            CurrentSearchRow.Background = null;
          }
          fightDataGrid.Focus();
        }
        else if (e.Key == Key.Enter)
        {
          SearchForNPC(e.KeyboardDevice.IsKeyDown(Key.RightShift) || e.KeyboardDevice.IsKeyDown(Key.LeftShift));
        }
      }
    }

    private List<Fight> GetFightGroup()
    {
      List<Fight> fightGroup = new List<Fight>();
      if (fightDataGrid.CurrentItem is Fight npc && !npc.IsInactivity)
      {
        if (fightDataGrid.ItemsSource == View)
        {
          Fights.Where(fight => fight.GroupId == npc.GroupId).ToList().ForEach(fight => fightGroup.Add(fight));
        }
        else if (fightDataGrid.ItemsSource == NonTankingView)
        {
          NonTankingFights.Where(fight => fight.NonTankingGroupId == npc.NonTankingGroupId).ToList().ForEach(fight => fightGroup.Add(fight));
        }
      }
      return fightGroup;
    }

    private void SearchForNPC(bool backwards = false)
    {
      if (CurrentSearchRow != null)
      {
        CurrentSearchRow.Background = null;
      }

      if (fightDataGrid.ItemsSource is ListCollectionView items && fightSearchBox.Text.Length > 0 && items.Count > 0)
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
            CurrentFightSearchIndex = items.Count - 1;
          }

          // 1 check/loop from start to finish or add a 2nd to continue from the middle to element - 1
          checksNeeded = CurrentFightSearchIndex == (items.Count - 1) ? 1 : 2;
        }
        else
        {
          direction = 1;
          if (CurrentFightSearchDirection != direction)
          {
            CurrentFightSearchIndex += 2;
          }

          if (CurrentFightSearchIndex >= items.Count)
          {
            CurrentFightSearchIndex = 0;
          }

          // 1 check/loop from start to finish or add a 2nd to continue from the middle to element - 1
          checksNeeded = CurrentFightSearchIndex == 0 ? 1 : 2;
        }

        CurrentFightSearchDirection = direction;

        while (checksNeeded-- > 0)
        {
          for (int i = CurrentFightSearchIndex; i < items.Count && i >= 0; i += (1 * direction))
          {
            if (items.GetItemAt(i) is Fight npc && npc.Name != null && npc.Name.IndexOf(fightSearchBox.Text, StringComparison.OrdinalIgnoreCase) > -1)
            {
              //fightDataGrid.ScrollIntoView(npc);
              //var row = fightDataGrid.ItemContainerGenerator.ContainerFromItem(npc) as DataGridRow;
              //row.Background = SEARCH_BRUSH;
              //CurrentSearchRow = row;
              CurrentFightSearchIndex = i + (1 * direction);
              return;
            }
          }

          if (checksNeeded == 1)
          {
            CurrentFightSearchIndex = (direction == 1) ? CurrentFightSearchIndex = 0 : CurrentFightSearchIndex = items.Count - 1;
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
      CurrentSearchRow = null;
    }

    private void Instance_EventsUpdateFight(object sender, Fight fight) => NeedRefresh = true;
    private void Instance_EventsRemovedFight(object sender, string name) => RemoveFight(name);
    private void Instance_EventsNewFight(object sender, Fight fight) => AddFight(fight);
    private void Instance_EventsNewNonTankingFight(object sender, Fight fight) => AddNonTankingFight(fight);
  }
}
