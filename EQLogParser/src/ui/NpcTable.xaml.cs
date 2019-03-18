using ActiproSoftware.Windows.Controls.DataGrid;
using ActiproSoftware.Windows.Controls.Docking;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    public static SolidColorBrush BREAK_TIME_BRUSH = new SolidColorBrush(Color.FromRgb(150, 65, 13));
    public static SolidColorBrush NORMAL_BRUSH = new SolidColorBrush(Color.FromRgb(37, 37, 38));
    private static SolidColorBrush SEARCH_BRUSH = new SolidColorBrush(Color.FromRgb(58, 84, 63));
    private const string NPC_SEARCH_TEXT = "NPC Search";

    // NPC Search
    private static int CurrentNpcSearchIndex = 0;
    private static int CurrentNpcSearchDirection = 1;
    private static DataGridRow CurrentSearchRow = null;

    private ObservableCollection<NonPlayer> NonPlayersView = new ObservableCollection<NonPlayer>();
    private CollectionViewSource NonPlayersViewSource;

    private DispatcherTimer SelectionTimer;
    private DispatcherTimer SearchTextTimer;
    private DispatcherTimer UpdateTimer;

    public NpcTable()
    {
      InitializeComponent();

      // npc search box
      npcSearchBox.FontStyle = FontStyles.Italic;
      npcSearchBox.Text = NPC_SEARCH_TEXT;

      npcMenuItemClear.IsEnabled = npcMenuItemSelectAll.IsEnabled = npcMenuItemUnselectAll.IsEnabled = npcMenuItemSelectFight.IsEnabled = false;
      npcMenuItemSetPet.IsEnabled = npcMenuItemSetPlayer.IsEnabled = false;

      NonPlayersViewSource = new CollectionViewSource() { Source = NonPlayersView };
      npcDataGrid.ItemsSource = NonPlayersViewSource.View;

      SelectionTimer = new DispatcherTimer();
      SelectionTimer.Interval = new TimeSpan(0, 0, 0, 0, 400);
      SelectionTimer.Tick += (sender, e) =>
      {
        SelectionTimer.Stop();
        EventsSelectionChange(this, npcDataGrid.SelectedItems);
      };

      SearchTextTimer = new DispatcherTimer();
      SearchTextTimer.Interval = new TimeSpan(0, 0, 0, 0, 400);
      SearchTextTimer.Tick += (sender, e) =>
      {
        SearchTextTimer.Stop();
        HandleSearchTextChanged();
      };

      UpdateTimer = new DispatcherTimer();
      UpdateTimer.Interval = new TimeSpan(0, 0, 0, 0, 250);
      UpdateTimer.Tick += (sender, e) =>
      {
        npcDataGrid.ScrollIntoView(NonPlayersView.Last());
        UpdateTimer.Stop();
      };

      // read show breaks setting
      bool bValue;
      string showBreaks = DataManager.Instance.GetApplicationSetting("NpcShowInactivityBreaks");
      npcShowBreaks.IsChecked = showBreaks == null || (bool.TryParse(showBreaks, out bValue) && bValue);

      // Clear/Reset
      DataManager.Instance.EventsClearedActiveData += (sender, cleared) => NonPlayersView.Clear();
      DataManager.Instance.EventsRemovedNonPlayer += (sender, name) => RemoveNonPlayer(name);
      DataManager.Instance.EventsNewNonPlayer += (sender, npc) => AddNonPlayer(npc);
    }

    public List<NonPlayer> GetSelectedItems()
    {
      return npcDataGrid.SelectedItems.Cast<NonPlayer>().Where(item => !item.Name.Contains("Inactivity >")).ToList();
    }

    public void SelectLastRow()
    {
      npcDataGrid.Items.MoveCurrentToLast();
    }

    private void AddNonPlayer(NonPlayer npc)
    {
      Dispatcher.InvokeAsync(() =>
      {
        NonPlayersView.Add(npc);
        if ((this.Parent as ToolWindow).IsOpen && !npcDataGrid.IsMouseOver && !UpdateTimer.IsEnabled)
        {
          UpdateTimer.Start();
        }
      });
    }

    private void RemoveNonPlayer(string name)
    {
      Dispatcher.InvokeAsync(() =>
      {
        int i = 0;
        foreach (NonPlayer item in NonPlayersView.Reverse())
        {
          i++;
          if (name == item.Name)
          {
            NonPlayersView.Remove(item);
            npcDataGrid.Items.Refresh(); // re-numbers
          }
        }
      });
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
      DataManager.Instance.Clear();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
      Helpers.DataGridSelectAll(sender);
    }

    private void UnselectAll_Click(object sender, RoutedEventArgs e)
    {
      Helpers.DataGridUnselectAll(sender);
    }

    private void SetPet_Click(object sender, RoutedEventArgs e)
    {
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      ThemedDataGrid callingDataGrid = menu.PlacementTarget as ThemedDataGrid;
      NonPlayer npc = callingDataGrid.SelectedItem as NonPlayer;
      if (npc != null && npc.GroupID > -1)
      {
        DataManager.Instance.UpdateVerifiedPets(npc.Name);
        DataManager.Instance.UpdatePetToPlayer(npc.Name, DataManager.UNASSIGNED_PET_OWNER);
      }
    }

    private void SetPlayer_Click(object sender, RoutedEventArgs e)
    {
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      ThemedDataGrid callingDataGrid = menu.PlacementTarget as ThemedDataGrid;
      NonPlayer npc = callingDataGrid.SelectedItem as NonPlayer;
      if (npc != null && npc.GroupID > -1)
      {
        DataManager.Instance.UpdateVerifiedPlayers(npc.Name);
      }
    }

    private void LoadingRow(object sender, DataGridRowEventArgs e)
    {
      // set header count
      e.Row.Header = (e.Row.GetIndex() + 1).ToString();

      NonPlayer npc = e.Row.Item as NonPlayer;
      if (npc != null && npc.BeginTimeString == NonPlayer.BREAK_TIME)
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

      ThemedDataGrid callingDataGrid = sender as ThemedDataGrid;
      npcMenuItemSelectAll.IsEnabled = (callingDataGrid.SelectedItems.Count < callingDataGrid.Items.Count) && callingDataGrid.Items.Count > 0;
      npcMenuItemUnselectAll.IsEnabled = callingDataGrid.SelectedItems.Count > 0 && callingDataGrid.Items.Count > 0;
      npcMenuItemClear.IsEnabled = callingDataGrid.Items.Count > 0;

      var selected = callingDataGrid.SelectedItem as NonPlayer;
      npcMenuItemSetPet.IsEnabled = npcMenuItemSetPlayer.IsEnabled = callingDataGrid.SelectedItems.Count == 1 && selected.GroupID != -1;
    }

    private void SelectGroup_Click(object sender, RoutedEventArgs e)
    {
      ContextMenu menu = (sender as FrameworkElement).Parent as ContextMenu;
      ThemedDataGrid callingDataGrid = menu.PlacementTarget as ThemedDataGrid;
      NonPlayer npc = callingDataGrid.SelectedItem as NonPlayer;
      if (npc != null && npc.GroupID > -1)
      {
        Parallel.ForEach(NonPlayersView, (one) =>
        {
          if (one.GroupID == npc.GroupID)
          {
            Dispatcher.InvokeAsync(() => callingDataGrid.SelectedItems.Add(one));
          }
        });
      }
    }

    private void ShowBreak_Change(object sender, RoutedEventArgs e)
    {
      if (NonPlayersView != null && NonPlayersViewSource != null)
      {
        if (npcShowBreaks.IsChecked.Value)
        {
          NonPlayersViewSource.View.Filter = null;
        }
        else
        {
          NonPlayersViewSource.View.Filter = new Predicate<object>(item => ((NonPlayer) item).GroupID > -1);
        }

        DataManager.Instance.SetApplicationSetting("NpcShowInactivityBreaks", npcShowBreaks.IsChecked.Value.ToString());
      }
    }

    private void HandleSearchTextChanged()
    {
      if (npcSearchBox.Text.Length > 0)
      {
        SearchForNPC();
      }
    }

    private void NPCSearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
      if (npcSearchBox.Text == NPC_SEARCH_TEXT)
      {
        npcSearchBox.Text = "";
        npcSearchBox.FontStyle = FontStyles.Normal;
      }
    }

    private void NPCSearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
      if (npcSearchBox.Text == "")
      {
        npcSearchBox.Text = NPC_SEARCH_TEXT;
        npcSearchBox.FontStyle = FontStyles.Italic;
      }
    }

    private void NPCSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.Escape)
      {
        npcSearchBox.Text = NPC_SEARCH_TEXT;
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
            NonPlayer npc = npcDataGrid.Items[i] as NonPlayer;
            if (npc != null && npc.Name != null && npc.Name.IndexOf(npcSearchBox.Text, StringComparison.OrdinalIgnoreCase) > -1)
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

    private void NPCSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
      SearchTextTimer?.Stop();

      if (e.Changes.FirstOrDefault(change => change.AddedLength > 0) != null)
      {
        SearchTextTimer?.Start();
      }
    }
  }
}
