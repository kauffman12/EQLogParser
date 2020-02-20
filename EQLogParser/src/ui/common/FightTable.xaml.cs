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
    private static SolidColorBrush NORMAL_BRUSH = new SolidColorBrush(Color.FromRgb(37, 37, 38));
    private static SolidColorBrush SEARCH_BRUSH = new SolidColorBrush(Color.FromRgb(58, 84, 63));

    // NPC Search
    private static int CurrentFightSearchIndex = 0;
    private static int CurrentFightSearchDirection = 1;
    private static DataGridRow CurrentSearchRow = null;
    private static bool NeedScroll = false;
    private static bool NeedRefresh = false;

    private ObservableCollection<Fight> Fights = new ObservableCollection<Fight>();
    private bool CurrentShowBreaks;

    private DispatcherTimer SelectionTimer;
    private DispatcherTimer SearchTextTimer;
    private DispatcherTimer UpdateTimer;
    private Fight LastNpc;

    public FightTable()
    {
      InitializeComponent();

      // fight search box
      fightSearchBox.FontStyle = FontStyles.Italic;
      fightSearchBox.Text = Properties.Resources.NPC_SEARCH_TEXT;

      fightMenuItemClear.IsEnabled = fightMenuItemSelectAll.IsEnabled = fightMenuItemUnselectAll.IsEnabled = fightMenuItemSelectFight.IsEnabled = false;
      fightMenuItemSetPet.IsEnabled = fightMenuItemSetPlayer.IsEnabled = false;

      var view = CollectionViewSource.GetDefaultView(Fights);
      view.Filter = new Predicate<object>(item =>
      {
        var fightItem = (Fight)item;
        return (CurrentShowBreaks ? fightItem.GroupID >= -1 : fightItem.GroupID > -1) && (fightItem.GroupID == -1 || fightItem.IsNpc == true);
      });

      fightDataGrid.ItemsSource = view;

      SelectionTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 800) };
      SelectionTimer.Tick += (sender, e) =>
      {
        EventsSelectionChange(this, fightDataGrid.SelectedItems);
        SelectionTimer.Stop();
      };

      SearchTextTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 400) };
      SearchTextTimer.Tick += (sender, e) =>
      {
        HandleSearchTextChanged();
        SearchTextTimer.Stop();
      };

      UpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 500) };
      UpdateTimer.Tick += (sender, e) =>
      {
        if (NeedScroll)
        {
          if (NeedRefresh)
          {
            (fightDataGrid.ItemsSource as ICollectionView).Refresh();
            NeedRefresh = false;
          }

          var last = Fights.LastOrDefault(fight => fight.IsNpc);

          if (last != null)
          {
            fightDataGrid.ScrollIntoView(last);
          }

          NeedScroll = false;
        }
      };

      UpdateTimer.Start();

      // read show breaks setting
      string showBreaks = ConfigUtil.GetApplicationSetting("NpcShowInactivityBreaks");
      fightShowBreaks.IsChecked = CurrentShowBreaks = (showBreaks == null || (bool.TryParse(showBreaks, out bool bValue) && bValue));
    }

    public List<Fight> GetSelectedItems()
    {
      return fightDataGrid.SelectedItems.Cast<Fight>().Where(item => !item.Name.Contains("Inactivity >")).ToList();
    }

    private void AddFight(Fight fight)
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (LastNpc != null && fight.IsNpc)
        {
          double seconds = fight.BeginTime - LastNpc.LastTime;
          if (seconds >= 120)
          {
            Fight divider = new Fight() { LastTime = fight.BeginTime, BeginTime = LastNpc.LastTime, GroupID = -1, BeginTimeString = Fight.BREAKTIME, Name = string.Intern(FormatTime(seconds)) };
            Fights.Add(divider);
          }
        }

        Fights.Add(fight);

        if (fight.IsNpc)
        {
          LastNpc = fight;
        }

        if ((Parent as ToolWindow).IsOpen && !fightDataGrid.IsMouseOver && !NeedScroll)
        {
          NeedScroll = true;
        }
      }, DispatcherPriority.Background);
    }

    private void RemoveFight(string name)
    {
      Dispatcher.InvokeAsync(() =>
      {
        foreach (var fight in Fights.Where(fight => fight.GroupID > -1 && fight.IsNpc && !string.IsNullOrEmpty(fight.Name) && fight.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
          fight.IsNpc = false;
        }

        (fightDataGrid.ItemsSource as ICollectionView).Refresh();
      }, DispatcherPriority.Background);
    }

    private void ClearClick(object sender, RoutedEventArgs e)
    {
      DataManager.Instance.Clear();
    }

    private void SelectAllClick(object sender, RoutedEventArgs e)
    {
      Helpers.DataGridSelectAll(sender as FrameworkElement);
    }

    private void UnselectAllClick(object sender, RoutedEventArgs e)
    {
      Helpers.DataGridUnselectAll(sender as FrameworkElement);
    }

    private void SetPetClick(object sender, RoutedEventArgs e)
    {
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      DataGrid callingDataGrid = menu.PlacementTarget as DataGrid;
      if (callingDataGrid.SelectedItem is Fight npc && npc.GroupID > -1)
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
      if (callingDataGrid.SelectedItem is Fight npc && npc.GroupID > -1)
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
        fightMenuItemSelectFight.IsEnabled = true;
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
      fightMenuItemSetPet.IsEnabled = callingDataGrid.SelectedItems.Count == 1 && selected.GroupID != -1;
      fightMenuItemSetPlayer.IsEnabled = callingDataGrid.SelectedItems.Count == 1 && selected.GroupID != -1 && Helpers.IsPossiblePlayerName((callingDataGrid.SelectedItem as Fight)?.Name);
    }

    private void SelectGroupClick(object sender, RoutedEventArgs e)
    {
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      DataGrid callingDataGrid = menu.PlacementTarget as DataGrid;
      if (callingDataGrid.SelectedItem is Fight npc && npc.GroupID > -1)
      {
        Parallel.ForEach(Fights, (one) =>
        {
          if (one.GroupID == npc.GroupID)
          {
            Dispatcher.InvokeAsync(() => callingDataGrid.SelectedItems.Add(one), DispatcherPriority.Background);
          }
        });
      }
    }

    private void ShowBreakChange(object sender, RoutedEventArgs e)
    {
      if (fightDataGrid?.ItemsSource is ICollectionView view)
      {
        CurrentShowBreaks = fightShowBreaks.IsChecked.Value;
        ConfigUtil.SetApplicationSetting("NpcShowInactivityBreaks", CurrentShowBreaks.ToString(CultureInfo.CurrentCulture));

        view.Refresh();
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

    private static string FormatTime(double seconds)
    {
      TimeSpan diff = TimeSpan.FromSeconds(seconds);
      string result = "Inactivity > ";

      if (diff.Days >= 1)
      {
        result += diff.Days + " days";
      }
      else if (diff.Hours >= 1)
      {
        result += diff.Hours + " hours";
      }
      else
      {
        result += diff.Minutes + " minutes";
      }

      return result;
    }

    private void Instance_EventsCleardActiveData(object sender, bool cleared)
    {
      Fights.Clear();
      LastNpc = null;
      CurrentSearchRow = null;
    }

    private void Instance_EventsRemovedFight(object sender, string name)
    {
      RemoveFight(name);
    }

    private void Instance_EventsNewFight(object sender, Fight fight)
    {
      AddFight(fight);
    }

    private void Instance_EventsRefreshFights(object sender, Fight fight)
    {
      if (NeedRefresh == false)
      {
        NeedRefresh = true;
      }

      Dispatcher.InvokeAsync(() =>
      {
        int index = Fights.IndexOf(fight);

        if (index > 0 && Fights[index - 1].GroupID == -1 && Fights[index - 1].LastTime != fight.BeginTime)
        {
          double seconds = fight.BeginTime - Fights[index - 1].BeginTime;
          if (seconds < 120)
          {
            Fights.RemoveAt(index - 1);
          }
          else
          {
            Fights[index - 1].LastTime = fight.BeginTime;
            Fights[index - 1].Name = string.Intern(FormatTime(seconds));
          }
        }
        else if ((index + 1) < Fights.Count && Fights[index + 1].GroupID == -1 && Fights[index + 1].BeginTime != fight.LastTime)
        {
          double seconds = Fights[index + 1].LastTime - fight.BeginTime;
          if (seconds < 120)
          {
            Fights.RemoveAt(index + 1);
          }
          else
          {
            Fights[index + 1].BeginTime = fight.LastTime;
            Fights[index + 1].Name = string.Intern(FormatTime(seconds));
          }
        }
      }, DispatcherPriority.Background);
    }

    private void TableUnloaded(object sender, RoutedEventArgs e)
    {
      DataManager.Instance.EventsClearedActiveData -= Instance_EventsCleardActiveData;
      DataManager.Instance.EventsRemovedFight -= Instance_EventsRemovedFight;
      DataManager.Instance.EventsNewFight -= Instance_EventsNewFight;
      DataManager.Instance.EventsRefreshFights -= Instance_EventsRefreshFights;
    }

    private void TableLoaded(object sender, RoutedEventArgs e)
    {
      DataManager.Instance.EventsClearedActiveData += Instance_EventsCleardActiveData;
      DataManager.Instance.EventsRemovedFight += Instance_EventsRemovedFight;
      DataManager.Instance.EventsNewFight += Instance_EventsNewFight;
      DataManager.Instance.EventsRefreshFights += Instance_EventsRefreshFights;
    }
  }
}
