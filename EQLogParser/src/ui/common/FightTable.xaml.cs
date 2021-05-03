using ActiproSoftware.Windows.Controls.Docking;
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

    // brushes
    private static readonly SolidColorBrush BREAK_TIME_BRUSH = new SolidColorBrush(Color.FromRgb(150, 65, 13));
    private static readonly SolidColorBrush NORMAL_BRUSH = new SolidColorBrush(Color.FromRgb(35, 35, 37));
    private static readonly SolidColorBrush SEARCH_BRUSH = new SolidColorBrush(Color.FromRgb(58, 84, 63));

    // time before creating new group
    public const int GROUPTIMEOUT = 120;

    // NPC Search
    private static int CurrentFightSearchIndex = 0;
    private static int CurrentFightSearchDirection = 1;
    private static DataGridRow CurrentSearchRow = null;
    private static bool NeedSelectionChange = false;

    private readonly ICollectionView View;
    private readonly ICollectionView NonTankingView;
    private readonly ObservableCollection<Fight> Fights = new ObservableCollection<Fight>();
    private readonly ObservableCollection<Fight> NonTankingFights = new ObservableCollection<Fight>();
    private bool CurrentShowBreaks;
    private int CurrentGroup = 1;
    private int CurrentNonTankingGroup = 1;
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

      View = CollectionViewSource.GetDefaultView(Fights);
      NonTankingView = CollectionViewSource.GetDefaultView(NonTankingFights);

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
      fightDataGrid.Columns[1].Visibility = fightShowHitPoints.IsChecked.Value ? Visibility.Visible : Visibility.Hidden;

      // read show breaks and spells setting
      fightShowBreaks.IsChecked = CurrentShowBreaks = ConfigUtil.IfSet("NpcShowInactivityBreaks", null, true);
      fightShowTanking.IsChecked = ConfigUtil.IfSet("NpcShowTanking", null, true);

      fightDataGrid.ItemsSource = fightShowTanking.IsChecked.Value ? View : NonTankingView;
    }

    internal IEnumerable<Fight> GetSelectedItems() => fightDataGrid.SelectedItems.Cast<Fight>().Where(item => !item.IsInactivity);
    internal bool HasSelected() => fightDataGrid.SelectedItems.Cast<Fight>().FirstOrDefault(item => !item.IsInactivity) != null;

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
          if (!double.IsNaN(lastWithTankingTime) && fight.BeginTime - lastWithTankingTime >= GROUPTIMEOUT)
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

            Fights.Add(divider);
          }

          fight.GroupId = CurrentGroup;
          Fights.Add(fight);
          lastWithTankingTime = double.IsNaN(lastWithTankingTime) ? fight.LastTime : Math.Max(lastWithTankingTime, fight.LastTime);
        });

        if (fightDataGrid.ItemsSource == View)
        {
          NeedRefresh = false;
        }

        if ((Parent as ToolWindow).IsOpen && fightDataGrid.Items.Count > 0 && !fightDataGrid.IsMouseOver)
        {
          fightDataGrid.ScrollIntoView(fightDataGrid.Items[fightDataGrid.Items.Count - 1]);
        }
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
          if (!double.IsNaN(lastNonTankingTime) && fight.DamageHits > 0 && fight.BeginTime - lastNonTankingTime >= GROUPTIMEOUT)
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

            NonTankingFights.Add(divider);
          }

          fight.NonTankingGroupId = CurrentNonTankingGroup;
          NonTankingFights.Add(fight);
          lastNonTankingTime = double.IsNaN(lastNonTankingTime) ? fight.LastDamageTime : Math.Max(lastNonTankingTime, fight.LastDamageTime);
        });

        if (fightDataGrid.ItemsSource == NonTankingView)
        {
          NeedRefresh = false;
        }

        if ((Parent as ToolWindow).IsOpen && fightDataGrid.Items.Count > 0 && !fightDataGrid.IsMouseOver)
        {
          fightDataGrid.ScrollIntoView(fightDataGrid.Items[fightDataGrid.Items.Count - 1]);
        }
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
    private void SelectAllClick(object sender, RoutedEventArgs e) => DataGridUtils.SelectAll(sender as FrameworkElement);
    private void UnselectAllClick(object sender, RoutedEventArgs e) => DataGridUtils.UnselectAll(sender as FrameworkElement);

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
        Task.Delay(120).ContinueWith(_ => PlayerManager.Instance.AddVerifiedPlayer(npc.Name), TaskScheduler.Default);
      }
    }

    private void LoadingRow(object sender, DataGridRowEventArgs e)
    {
      // set header count
      e.Row.Header = (e.Row.GetIndex() + 1).ToString(CultureInfo.CurrentCulture);

      if (e.Row.Item is Fight npc && npc.BeginTimeString == Fight.BREAKTIME)
      {
        if (e.Row.Background != BREAK_TIME_BRUSH)
        {
          e.Row.Background = BREAK_TIME_BRUSH;
        }
      }
      else if (e.Row.Background != NORMAL_BRUSH)
      {
        e.Row.Background = NORMAL_BRUSH;
      }

      if (fightMenuItemSelectFight.IsEnabled == false)
      {
        fightMenuItemSelectFight.IsEnabled = fightMenuItemUnselectFight.IsEnabled = true;
      }
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      // adds a delay where a drag-select doesn't keep sending events
      SelectionTimer.Stop();
      SelectionTimer.Start();

      DataGrid callingDataGrid = sender as DataGrid;
      fightMenuItemSelectAll.IsEnabled = (callingDataGrid.SelectedItems.Count < callingDataGrid.Items.Count) && callingDataGrid.Items.Count > 0;
      fightMenuItemUnselectAll.IsEnabled = callingDataGrid.SelectedItems.Count > 0 && callingDataGrid.Items.Count > 0;
      fightMenuItemClear.IsEnabled = callingDataGrid.Items.Count > 0;

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
        var show = fightDataGrid.Columns[1].Visibility == Visibility.Hidden;
        fightDataGrid.Columns[1].Visibility = show ? Visibility.Visible : Visibility.Hidden;
        ConfigUtil.SetSetting("NpcShowHitPoints", show.ToString(CultureInfo.CurrentCulture));
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

      if (fightSearchBox.Text.Length > 0 && fightDataGrid.Items.Count > 0)
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
            CurrentFightSearchIndex = fightDataGrid.Items.Count - 1;
          }

          // 1 check/loop from start to finish or add a 2nd to continue from the middle to element - 1
          checksNeeded = CurrentFightSearchIndex == (fightDataGrid.Items.Count - 1) ? 1 : 2;
        }
        else
        {
          direction = 1;
          if (CurrentFightSearchDirection != direction)
          {
            CurrentFightSearchIndex += 2;
          }

          if (CurrentFightSearchIndex >= fightDataGrid.Items.Count)
          {
            CurrentFightSearchIndex = 0;
          }

          // 1 check/loop from start to finish or add a 2nd to continue from the middle to element - 1
          checksNeeded = CurrentFightSearchIndex == 0 ? 1 : 2;
        }

        CurrentFightSearchDirection = direction;

        while (checksNeeded-- > 0)
        {
          for (int i = CurrentFightSearchIndex; i < fightDataGrid.Items.Count && i >= 0; i += (1 * direction))
          {
            if (fightDataGrid.Items[i] is Fight npc && npc.Name != null && npc.Name.IndexOf(fightSearchBox.Text, StringComparison.OrdinalIgnoreCase) > -1)
            {
              fightDataGrid.ScrollIntoView(npc);
              var row = fightDataGrid.ItemContainerGenerator.ContainerFromItem(npc) as DataGridRow;
              row.Background = SEARCH_BRUSH;
              CurrentSearchRow = row;
              CurrentFightSearchIndex = i + (1 * direction);
              return;
            }
          }

          if (checksNeeded == 1)
          {
            CurrentFightSearchIndex = (direction == 1) ? CurrentFightSearchIndex = 0 : CurrentFightSearchIndex = fightDataGrid.Items.Count - 1;
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

    private void TableUnloaded(object sender, RoutedEventArgs e)
    {
      DataManager.Instance.EventsClearedActiveData -= Instance_EventsCleardActiveData;
      DataManager.Instance.EventsRemovedFight -= Instance_EventsRemovedFight;
      DataManager.Instance.EventsNewFight -= Instance_EventsNewFight;
      DataManager.Instance.EventsUpdateFight -= Instance_EventsUpdateFight;
      DataManager.Instance.EventsNewNonTankingFight -= Instance_EventsNewNonTankingFight;
    }

    private void TableLoaded(object sender, RoutedEventArgs e)
    {
      DataManager.Instance.EventsClearedActiveData += Instance_EventsCleardActiveData;
      DataManager.Instance.EventsRemovedFight += Instance_EventsRemovedFight;
      DataManager.Instance.EventsNewFight += Instance_EventsNewFight;
      DataManager.Instance.EventsUpdateFight += Instance_EventsUpdateFight;
      DataManager.Instance.EventsNewNonTankingFight += Instance_EventsNewNonTankingFight;
    }
  }
}
