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
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1009:DeclareEventHandlersCorrectly")]
    public event EventHandler<IList> EventsSelectionChange;

    // brushes
    private static SolidColorBrush BREAK_TIME_BRUSH = new SolidColorBrush(Color.FromRgb(150, 65, 13));
    private static SolidColorBrush NORMAL_BRUSH = new SolidColorBrush(Color.FromRgb(35, 35, 37));
    private static SolidColorBrush SEARCH_BRUSH = new SolidColorBrush(Color.FromRgb(58, 84, 63));

    // time before creating new group
    private const int GROUP_TIMEOUT = 120;

    // NPC Search
    private static int CurrentFightSearchIndex = 0;
    private static int CurrentFightSearchDirection = 1;
    private static DataGridRow CurrentSearchRow = null;
    private static bool NeedScroll = false;
    private static bool NeedRefresh = false;
    private static bool NeedSelectionChange = false;

    private ObservableCollection<Fight> Fights = new ObservableCollection<Fight>();
    private bool CurrentShowBreaks;
    private bool CurrentShowSpells;
    private int CurrentGroup = 0;

    private DispatcherTimer RefreshTimer;
    private DispatcherTimer SelectionTimer;
    private DispatcherTimer SearchTextTimer;
    private DispatcherTimer UpdateTimer;
    private Fight LastNpc;
    private Fight LastNpcOrSpell;

    public FightTable()
    {
      InitializeComponent();

      // fight search box
      fightSearchBox.FontStyle = FontStyles.Italic;
      fightSearchBox.Text = Properties.Resources.NPC_SEARCH_TEXT;

      fightMenuItemClear.IsEnabled = fightMenuItemSelectAll.IsEnabled = fightMenuItemUnselectAll.IsEnabled = 
      fightMenuItemSelectFight.IsEnabled = fightMenuItemUnselectFight.IsEnabled = fightMenuItemSetPet.IsEnabled = fightMenuItemSetPlayer.IsEnabled = false;

      var view = CollectionViewSource.GetDefaultView(Fights);
      view.Filter = new Predicate<object>(item =>
      {
        bool display = true;
        var fight = (Fight) item;

        if (CurrentShowSpells == false && fight.IsSpell && fight.DamageHits <= 0)
        {
          display = false;
        }
        else if (CurrentShowSpells == false && fight.GroupId == -1)
        {
          display = false;
        }
        else if (CurrentShowSpells == true && fight.GroupId == -2)
        {
          display = false;
        }
        else if (CurrentShowBreaks == false && fight.GroupId < 0)
        {
          display = false;
        }

        return display;
      });

      fightDataGrid.ItemsSource = view;

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

      RefreshTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 5000) };
      RefreshTimer.Tick += (sender, e) =>
      {
        (fightDataGrid.ItemsSource as ICollectionView).Refresh();
        NeedRefresh = false;
        RefreshTimer.Stop();
      };

      UpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 500) };
      UpdateTimer.Tick += (sender, e) =>
      {
        // get state so it can't be modified outside this thread
        var currentNeedRefresh = NeedRefresh;

        if (NeedScroll)
        {
          (fightDataGrid.ItemsSource as ICollectionView).Refresh();
          currentNeedRefresh = false;
          NeedRefresh = false;

          var last = Fights.LastOrDefault(fight => fight.GroupId > -1);

          if (last != null)
          {
            fightDataGrid.ScrollIntoView(last);
          }

          NeedScroll = false;
        }

        if (currentNeedRefresh && !Keyboard.IsKeyDown(Key.LeftShift) && !RefreshTimer.IsEnabled)
        {
          RefreshTimer.Start();
        }
      };

      UpdateTimer.Start();

      // read show hp setting
      fightShowHitPoints.IsChecked = ConfigUtil.IfSet("NpcShowHitPoints");
      fightDataGrid.Columns[1].Visibility = fightShowHitPoints.IsChecked.Value ? Visibility.Visible : Visibility.Hidden;

      // read show breaks and spells setting
      fightShowBreaks.IsChecked = CurrentShowBreaks = ConfigUtil.IfSet("NpcShowInactivityBreaks", null, true);
      fightShowSpells.IsChecked = CurrentShowSpells = ConfigUtil.IfSet("NpcShowSpells", null, true);
    }

    internal IEnumerable<Fight> GetSelectedItems() => fightDataGrid.SelectedItems.Cast<Fight>().Where(item => item.GroupId > -1);
    internal bool HasSelected() => fightDataGrid.SelectedItems.Cast<Fight>().FirstOrDefault(item => item.GroupId > -1) != null;

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
      Dispatcher.InvokeAsync(() =>
      {
        if (LastNpcOrSpell != null && fight.BeginTime - LastNpcOrSpell.LastTime >= GROUP_TIMEOUT)
        {
          CurrentGroup++;

          var seconds = fight.BeginTime - LastNpcOrSpell.LastTime;
          Fight divider = new Fight()
          {
            LastTime = fight.BeginTime,
            BeginTime = LastNpcOrSpell.LastTime,
            GroupId = -1,
            BeginTimeString = Fight.BREAKTIME,
            Name = "Inactivity > " + DateUtil.FormatGeneralTime(seconds)
          };

          Fights.Add(divider);
        }

        if (LastNpc != null && !fight.IsSpell && fight.BeginTime - LastNpc.LastTime >= GROUP_TIMEOUT)
        {
          var seconds = fight.BeginTime - LastNpc.LastTime;
          Fight divider = new Fight()
          {
            LastTime = fight.BeginTime,
            BeginTime = LastNpc.LastTime,
            GroupId = -2,
            BeginTimeString = Fight.BREAKTIME,
            Name = "Inactivity > " + DateUtil.FormatGeneralTime(seconds)
          };

          Fights.Add(divider);
        }

        fight.GroupId = CurrentGroup;
        Fights.Add(fight);

        if (fight.GroupId > -1)
        {
          LastNpcOrSpell = fight;
        }

        if (fight.GroupId > -1 && !fight.IsSpell)
        {
          LastNpc = fight;
        }

        if ((Parent as ToolWindow).IsOpen && !fightDataGrid.IsMouseOver && !NeedScroll)
        {
          NeedScroll = true;
        }
      }, DispatcherPriority.DataBind);
    }

    private void RemoveFight(string name)
    {
      Dispatcher.InvokeAsync(() =>
      {
        for (int i = Fights.Count - 1; i >= 0; i--)
        {
          if (Fights[i].GroupId > -1 && !string.IsNullOrEmpty(Fights[i].Name) && Fights[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
          {
            Fights.RemoveAt(i);
          }
        }
      }, DispatcherPriority.DataBind);
    }

    private void ClearClick(object sender, RoutedEventArgs e) => DataManager.Instance.Clear();
    private void SelectAllClick(object sender, RoutedEventArgs e) => DataGridUtils.SelectAll(sender as FrameworkElement);
    private void UnselectAllClick(object sender, RoutedEventArgs e) => DataGridUtils.UnselectAll(sender as FrameworkElement);

    private void SetPetClick(object sender, RoutedEventArgs e)
    {
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      DataGrid callingDataGrid = menu.PlacementTarget as DataGrid;
      if (callingDataGrid.SelectedItem is Fight npc && npc.GroupId > -1)
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
      if (callingDataGrid.SelectedItem is Fight npc && npc.GroupId > -1)
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
      RefreshTimer.Stop();

      // adds a delay where a drag-select doesn't keep sending events
      SelectionTimer.Stop();
      SelectionTimer.Start();

      DataGrid callingDataGrid = sender as DataGrid;
      fightMenuItemSelectAll.IsEnabled = (callingDataGrid.SelectedItems.Count < callingDataGrid.Items.Count) && callingDataGrid.Items.Count > 0;
      fightMenuItemUnselectAll.IsEnabled = callingDataGrid.SelectedItems.Count > 0 && callingDataGrid.Items.Count > 0;
      fightMenuItemClear.IsEnabled = callingDataGrid.Items.Count > 0;

      var selected = callingDataGrid.SelectedItem as Fight;
      fightMenuItemSetPet.IsEnabled = callingDataGrid.SelectedItems.Count == 1 && selected.GroupId != -1;
      fightMenuItemSetPlayer.IsEnabled = callingDataGrid.SelectedItems.Count == 1 && selected.GroupId != -1 && 
        PlayerManager.Instance.IsPossiblePlayerName((callingDataGrid.SelectedItem as Fight)?.Name);
    }

    private void SelectGroupClick(object sender, RoutedEventArgs e)
    {
      NeedSelectionChange = false;

      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      DataGrid callingDataGrid = menu.PlacementTarget as DataGrid;
      if (callingDataGrid.CurrentItem is Fight npc && npc.GroupId > -1)
      {
        foreach (var one in Fights)
        {
          if (one.GroupId == npc.GroupId)
          {
            Dispatcher.InvokeAsync(() => callingDataGrid.SelectedItems.Add(one), DispatcherPriority.Normal);
          }
        }
      }
    }

    private void UnselectGroupClick(object sender, RoutedEventArgs e)
    {
      NeedSelectionChange = false;

      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      DataGrid callingDataGrid = menu.PlacementTarget as DataGrid;
      if (callingDataGrid.CurrentItem is Fight npc && npc.GroupId > -1)
      {
        foreach (var one in Fights)
        {
          if (one.GroupId == npc.GroupId)
          {
            Dispatcher.InvokeAsync(() => callingDataGrid.SelectedItems.Remove(one), DispatcherPriority.Normal);
          }
        }
      }
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

    private void ShowSpellsChange(object sender, RoutedEventArgs e)
    {
      if (fightDataGrid?.ItemsSource is ICollectionView view)
      {
        CurrentShowSpells = fightShowSpells.IsChecked.Value;
        ConfigUtil.SetSetting("NpcShowSpells", CurrentShowSpells.ToString(CultureInfo.CurrentCulture));
        view.Refresh();
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
      Fights.Clear();
      CurrentGroup = 0;
      LastNpc = null;
      LastNpcOrSpell = null;
      CurrentSearchRow = null;
    }

    private void Instance_EventsRemovedFight(object sender, string name) => RemoveFight(name);
    private void Instance_EventsNewFight(object sender, Fight fight) => AddFight(fight);
    private void Instance_EventsRefreshFight(object sender, Fight fight) => NeedRefresh = true;

    private void TableUnloaded(object sender, RoutedEventArgs e)
    {
      DataManager.Instance.EventsClearedActiveData -= Instance_EventsCleardActiveData;
      DataManager.Instance.EventsRemovedFight -= Instance_EventsRemovedFight;
      DataManager.Instance.EventsNewFight -= Instance_EventsNewFight;
      DataManager.Instance.EventsRefreshFight -= Instance_EventsRefreshFight;
    }

    private void TableLoaded(object sender, RoutedEventArgs e)
    {
      DataManager.Instance.EventsClearedActiveData += Instance_EventsCleardActiveData;
      DataManager.Instance.EventsRemovedFight += Instance_EventsRemovedFight;
      DataManager.Instance.EventsNewFight += Instance_EventsNewFight;
      DataManager.Instance.EventsRefreshFight += Instance_EventsRefreshFight;
    }
  }
}
