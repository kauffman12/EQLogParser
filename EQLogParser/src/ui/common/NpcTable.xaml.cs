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
  public partial class NpcTable : UserControl
  {
    // events
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1009:DeclareEventHandlersCorrectly")]
    public event EventHandler<IList> EventsSelectionChange;

    // brushes
    private static SolidColorBrush BREAK_TIME_BRUSH = new SolidColorBrush(Color.FromRgb(150, 65, 13));
    private static SolidColorBrush NORMAL_BRUSH = new SolidColorBrush(Color.FromRgb(37, 37, 38));
    private static SolidColorBrush SEARCH_BRUSH = new SolidColorBrush(Color.FromRgb(58, 84, 63));

    // NPC Search
    private static int CurrentNpcSearchIndex = 0;
    private static int CurrentNpcSearchDirection = 1;
    private static DataGridRow CurrentSearchRow = null;

    private ObservableCollection<NonPlayer> NonPlayers = new ObservableCollection<NonPlayer>();
    private bool CurrentShowBreaks;

    private DispatcherTimer SelectionTimer;
    private DispatcherTimer SearchTextTimer;
    private DispatcherTimer UpdateTimer;

    public NpcTable()
    {
      InitializeComponent();

      // npc search box
      npcSearchBox.FontStyle = FontStyles.Italic;
      npcSearchBox.Text = Properties.Resources.NPC_SEARCH_TEXT;

      npcMenuItemClear.IsEnabled = npcMenuItemSelectAll.IsEnabled = npcMenuItemUnselectAll.IsEnabled = npcMenuItemSelectFight.IsEnabled = false;
      npcMenuItemSetPet.IsEnabled = npcMenuItemSetPlayer.IsEnabled = false;

      var view = CollectionViewSource.GetDefaultView(NonPlayers);
      view.Filter = new Predicate<object>(item =>
      {
        var npcItem = (NonPlayer)item;
        return CurrentShowBreaks ? npcItem.GroupID >= -1 : npcItem.GroupID > -1;
      });

      npcDataGrid.ItemsSource = view;

      SelectionTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1250) };
      SelectionTimer.Tick += (sender, e) =>
      {
        SelectionTimer.Stop();
        EventsSelectionChange(this, npcDataGrid.SelectedItems);
      };

      SearchTextTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 500) };
      SearchTextTimer.Tick += (sender, e) =>
      {
        SearchTextTimer.Stop();
        HandleSearchTextChanged();
      };

      UpdateTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 350) };
      UpdateTimer.Tick += (sender, e) =>
      {
        npcDataGrid.ScrollIntoView(NonPlayers.Last());
        UpdateTimer.Stop();
      };

      // read show breaks setting
      string showBreaks = ConfigUtil.GetApplicationSetting("NpcShowInactivityBreaks");
      npcShowBreaks.IsChecked = CurrentShowBreaks = (showBreaks == null || (bool.TryParse(showBreaks, out bool bValue) && bValue));
    }

    public List<NonPlayer> GetSelectedItems()
    {
      return npcDataGrid.SelectedItems.Cast<NonPlayer>().Where(item => !item.Name.Contains("Inactivity >")).ToList();
    }

    private void AddNonPlayer(NonPlayer npc)
    {
      Dispatcher.InvokeAsync(() =>
      {
        NonPlayers.Add(npc);
        if ((Parent as ToolWindow).IsOpen && !npcDataGrid.IsMouseOver && !UpdateTimer.IsEnabled)
        {
          UpdateTimer.Start();
        }
      }, DispatcherPriority.Background);
    }

    private void RemoveNonPlayer(string name)
    {
      Dispatcher.InvokeAsync(() =>
      {
        for (int i = NonPlayers.Count - 1; i >= 0; i--)
        {
          if (NonPlayers[i].GroupID > -1 && !string.IsNullOrEmpty(NonPlayers[i].Name) && NonPlayers[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
          {
            NonPlayers.RemoveAt(i);
          }
        }
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
      if (callingDataGrid.SelectedItem is NonPlayer npc && npc.GroupID > -1)
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
      if (callingDataGrid.SelectedItem is NonPlayer npc && npc.GroupID > -1)
      {
        Task.Delay(120).ContinueWith(_ => PlayerManager.Instance.AddVerifiedPlayer(npc.Name), TaskScheduler.Default);
      }
    }

    private void LoadingRow(object sender, DataGridRowEventArgs e)
    {
      // set header count
      e.Row.Header = (e.Row.GetIndex() + 1).ToString(CultureInfo.CurrentCulture);

      if (e.Row.Item is NonPlayer npc && npc.BeginTimeString == NonPlayer.BREAKTIME)
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

      if (npcMenuItemSelectFight.IsEnabled == false)
      {
        npcMenuItemSelectFight.IsEnabled = true;
      }
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      // adds a delay where a drag-select doesn't keep sending events
      SelectionTimer.Stop();
      SelectionTimer.Start();

      DataGrid callingDataGrid = sender as DataGrid;
      npcMenuItemSelectAll.IsEnabled = (callingDataGrid.SelectedItems.Count < callingDataGrid.Items.Count) && callingDataGrid.Items.Count > 0;
      npcMenuItemUnselectAll.IsEnabled = callingDataGrid.SelectedItems.Count > 0 && callingDataGrid.Items.Count > 0;
      npcMenuItemClear.IsEnabled = callingDataGrid.Items.Count > 0;

      var selected = callingDataGrid.SelectedItem as NonPlayer;
      npcMenuItemSetPet.IsEnabled = callingDataGrid.SelectedItems.Count == 1 && selected.GroupID != -1;
      npcMenuItemSetPlayer.IsEnabled = callingDataGrid.SelectedItems.Count == 1 && selected.GroupID != -1 && Helpers.IsPossiblePlayerName((callingDataGrid.SelectedItem as NonPlayer)?.Name);
    }

    private void SelectGroupClick(object sender, RoutedEventArgs e)
    {
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      DataGrid callingDataGrid = menu.PlacementTarget as DataGrid;
      if (callingDataGrid.SelectedItem is NonPlayer npc && npc.GroupID > -1)
      {
        Parallel.ForEach(NonPlayers, (one) =>
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
      if (npcDataGrid?.ItemsSource is ICollectionView view)
      {
        CurrentShowBreaks = npcShowBreaks.IsChecked.Value;
        ConfigUtil.SetApplicationSetting("NpcShowInactivityBreaks", CurrentShowBreaks.ToString(CultureInfo.CurrentCulture));

        view.Refresh();
      }
    }

    private void HandleSearchTextChanged()
    {
      if (npcSearchBox.Text.Length > 0)
      {
        SearchForNPC();
      }
    }

    private void NPCSearchBoxGotFocus(object sender, RoutedEventArgs e)
    {
      if (npcSearchBox.Text == Properties.Resources.NPC_SEARCH_TEXT)
      {
        npcSearchBox.Text = "";
        npcSearchBox.FontStyle = FontStyles.Normal;
      }
    }

    private void NPCSearchBoxLostFocus(object sender, RoutedEventArgs e)
    {
      if (npcSearchBox.Text.Length == 0)
      {
        npcSearchBox.Text = Properties.Resources.NPC_SEARCH_TEXT;
        npcSearchBox.FontStyle = FontStyles.Italic;
      }
    }

    // internal for workaround with event being lost
    internal void NPCSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
      if (npcSearchBox.IsFocused)
      {
        if (e.Key == Key.Escape)
        {
          npcSearchBox.Text = Properties.Resources.NPC_SEARCH_TEXT;
          npcSearchBox.FontStyle = FontStyles.Italic;
          if (CurrentSearchRow != null)
          {
            CurrentSearchRow.Background = null;
          }
          npcDataGrid.Focus();
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

      if (npcSearchBox.Text.Length > 0 && npcDataGrid.Items.Count > 0)
      {
        int checksNeeded;
        int direction;
        if (backwards)
        {
          direction = -1;
          if (CurrentNpcSearchDirection != direction)
          {
            CurrentNpcSearchIndex -= 2;
          }

          if (CurrentNpcSearchIndex < 0)
          {
            CurrentNpcSearchIndex = npcDataGrid.Items.Count - 1;
          }

          // 1 check/loop from start to finish or add a 2nd to continue from the middle to element - 1
          checksNeeded = CurrentNpcSearchIndex == (npcDataGrid.Items.Count - 1) ? 1 : 2;
        }
        else
        {
          direction = 1;
          if (CurrentNpcSearchDirection != direction)
          {
            CurrentNpcSearchIndex += 2;
          }

          if (CurrentNpcSearchIndex >= npcDataGrid.Items.Count)
          {
            CurrentNpcSearchIndex = 0;
          }

          // 1 check/loop from start to finish or add a 2nd to continue from the middle to element - 1
          checksNeeded = CurrentNpcSearchIndex == 0 ? 1 : 2;
        }

        CurrentNpcSearchDirection = direction;

        while (checksNeeded-- > 0)
        {
          for (int i = CurrentNpcSearchIndex; i < npcDataGrid.Items.Count && i >= 0; i += (1 * direction))
          {
            if (npcDataGrid.Items[i] is NonPlayer npc && npc.Name != null && npc.Name.IndexOf(npcSearchBox.Text, StringComparison.OrdinalIgnoreCase) > -1)
            {
              npcDataGrid.ScrollIntoView(npc);
              var row = npcDataGrid.ItemContainerGenerator.ContainerFromItem(npc) as DataGridRow;
              row.Background = SEARCH_BRUSH;
              CurrentSearchRow = row;
              CurrentNpcSearchIndex = i + (1 * direction);
              return;
            }
          }

          if (checksNeeded == 1)
          {
            CurrentNpcSearchIndex = (direction == 1) ? CurrentNpcSearchIndex = 0 : CurrentNpcSearchIndex = npcDataGrid.Items.Count - 1;
          }
        }
      }
    }

    private void NPCSearchBoxTextChanged(object sender, TextChangedEventArgs e)
    {
      SearchTextTimer?.Stop();

      if (e.Changes.FirstOrDefault(change => change.AddedLength > 0) != null)
      {
        SearchTextTimer?.Start();
      }
    }

    private void Instance_EventsCleardActiveData(object sender, bool cleared)
    {
      NonPlayers.Clear();
      CurrentSearchRow = null;
    }

    private void Instance_EventsRemovedNonPlayer(object sender, string name)
    {
      RemoveNonPlayer(name);
    }

    private void Instance_EventsNewNonPlayer(object sender, NonPlayer npc)
    {
      AddNonPlayer(npc);
    }

    private void TableUnloaded(object sender, RoutedEventArgs e)
    {
      DataManager.Instance.EventsClearedActiveData -= Instance_EventsCleardActiveData;
      DataManager.Instance.EventsRemovedNonPlayer -= Instance_EventsRemovedNonPlayer;
      DataManager.Instance.EventsNewNonPlayer -= Instance_EventsNewNonPlayer;
    }

    private void TableLoaded(object sender, RoutedEventArgs e)
    {
      DataManager.Instance.EventsClearedActiveData += Instance_EventsCleardActiveData;
      DataManager.Instance.EventsRemovedNonPlayer += Instance_EventsRemovedNonPlayer;
      DataManager.Instance.EventsNewNonPlayer += Instance_EventsNewNonPlayer;
    }
  }
}
