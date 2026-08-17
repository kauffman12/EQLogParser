using Syncfusion.UI.Xaml.TreeView;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace EQLogParser
{
  public partial class TriggersCharacterView : IDisposable
  {
    internal event Action<List<TriggerCharacter>> SelectedCharacterEvent;
    private const string LabelNewFolder = "New Folder";
    private readonly DispatcherTimer _statusTimer;
    private TriggerConfig _lastConfig;
    private bool _suppressChecked;
    private bool _enableFlushQueued;

    public TriggersCharacterView()
    {
      InitializeComponent();
      characterTreeView.DragDropController = new TreeViewDragDropController
      {
        CanAutoExpand = true,
        AutoExpandDelay = new TimeSpan(0, 0, 1)
      };

      _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
      {
        Interval = new TimeSpan(0, 0, 0, 2, 500),
      };

      _statusTimer.Tick += StatusTimerTick;
      TriggerStateDB.Instance.TriggerConfigUpdateEvent += TriggerConfigUpdateEvent;
      MainActions.EventsWindowStateChanged += EventsWindowStateChanged;
    }

    internal void SetConfig(TriggerConfig config)
    {
      BuildTree(config);

      if (config.IsAdvanced)
      {
        _statusTimer.Start();
      }

      _lastConfig = config;
    }

    internal TriggerCharacter GetSelectedCharacter()
    {
      if (characterTreeView?.SelectedItem is CharacterTreeViewNode { Character: { } character })
      {
        return character;
      }

      return null;
    }

    /* Expand parents and select the character used by a trigger log entry. */
    internal void SelectCharacter(string characterId)
    {
      if (string.IsNullOrEmpty(characterId))
      {
        return;
      }

      foreach (var node in EnumerateNodes())
      {
        if (node.Character?.Id == characterId)
        {
          ExpandParents(node);
          characterTreeView.SelectedItem = node;
          characterTreeView.BringIntoView(node);
          break;
        }
      }
    }

    private void StatusTimerTick(object sender, EventArgs e) => UpdateStatus();

    private async void EventsWindowStateChanged(WindowState newState)
    {
      await Dispatcher.InvokeAsync(() =>
      {
        if (newState == WindowState.Minimized)
        {
          _statusTimer?.Stop();

          if (characterTreeView != null)
          {
            characterTreeView.Visibility = Visibility.Collapsed;
          }
        }
        else
        {
          if (characterTreeView != null)
          {
            characterTreeView.Visibility = Visibility.Visible;
          }

          if (_lastConfig?.IsAdvanced == true && !_statusTimer.IsEnabled)
          {
            _statusTimer.Start();
          }
        }
      });
    }

    private async void UpdateStatus()
    {
      if (_lastConfig?.Characters == null)
      {
        return;
      }

      var byId = _lastConfig.Characters.ToDictionary(character => character.Id);
      foreach (var reader in await TriggerManager.Instance.GetLogReadersAsync())
      {
        if (reader.GetProcessor() is TriggerProcessor processor && byId.TryGetValue(processor.CurrentCharacterId, out var character))
        {
          bool? update;
          if (reader.IsWaiting())
          {
            update = true;
          }
          else
          {
            var diff = DateTime.Now.Ticks - processor.GetActivityLastTicks();
            update = (diff / TimeSpan.TicksPerSecond > 120) ? null : false;
          }

          if (character.IsWaiting != update)
          {
            character.IsWaiting = update;
          }
        }
      }
    }

    private void TriggerConfigUpdateEvent(TriggerConfig config)
    {
      if (config.IsAdvanced)
      {
        PreserveWaiting(config);
        if (NeedsRebuild(config))
        {
          var selectedId = GetSelectedCharacter()?.Id;
          BuildTree(config);
          if (selectedId != null)
          {
            SelectCharacter(selectedId);
          }
        }
        else
        {
          SyncEnabledFromConfig(config);
        }

        if (!_statusTimer.IsEnabled)
        {
          _statusTimer.Start();
        }
      }
      else
      {
        _statusTimer.Stop();
      }

      _lastConfig = config;
    }

    private void AddClick(object sender, RoutedEventArgs e)
    {
      var configWindow = new TriggerPlayerConfigWindow(null, GetTargetFolderId());
      configWindow.ShowDialog();
    }

    private async void FolderClick(object sender, RoutedEventArgs e) => await CreateAndRenameFolderAsync(GetTargetFolderId());

    /* Context menu on empty tree space: create a character at the root. */
    private void RootCharacterClick(object sender, RoutedEventArgs e)
    {
      var configWindow = new TriggerPlayerConfigWindow(null, "");
      configWindow.ShowDialog();
    }

    /* Context menu on empty tree space: create a folder at the root. */
    private async void RootFolderClick(object sender, RoutedEventArgs e) => await CreateAndRenameFolderAsync("");

    /* Creates a folder under parentId (empty = root) and starts an inline rename on it. */
    private async Task CreateAndRenameFolderAsync(string parentId)
    {
      if (await TriggerStateDB.Instance.CreateCharacterFolder(parentId, LabelNewFolder) is { } folder)
      {
        await Dispatcher.InvokeAsync(() =>
        {
          foreach (var node in EnumerateNodes())
          {
            if (node.Folder?.Id == folder.Id)
            {
              BeginRename(node);
              break;
            }
          }
        }, DispatcherPriority.Background);
      }
    }

    private async void DeleteClick(object sender, RoutedEventArgs e)
    {
      if (characterTreeView?.SelectedItem is not CharacterTreeViewNode node)
      {
        return;
      }

      if (node.Character is { } character)
      {
        /* Same wording/icon/button as the other delete confirmations (TriggersTreeView). */
        var msgDialog = new MessageWindow($"Are you sure you want to delete {character.Name}?",
          Resource.TRIGGER_CHARACTER_DELETE, MessageWindow.IconType.Question, "Delete");
        msgDialog.ShowDialog();
        if (msgDialog.IsYes1Clicked)
        {
          await TriggerStateDB.Instance.DeleteCharacter(character.Id);
        }
      }
      else if (node.Folder is { } folder)
      {
        var msgDialog = new MessageWindow(
          $"Are you sure you want to delete '{folder.Name}'? Contents will be moved to the parent.",
          Resource.FOLDER_DELETE, MessageWindow.IconType.Warn, "Delete");
        msgDialog.ShowDialog();
        if (msgDialog.IsYes1Clicked)
        {
          await TriggerStateDB.Instance.DeleteCharacterFolder(folder.Id);
        }
      }
    }

    private void ModifyClick(object sender, RoutedEventArgs e)
    {
      if (characterTreeView?.SelectedItem is CharacterTreeViewNode { Folder: not null } folderNode)
      {
        BeginRename(folderNode);
        return;
      }

      if (GetSelectedCharacter() is { } character)
      {
        var configWindow = new TriggerPlayerConfigWindow(character);
        configWindow.ShowDialog();
      }
    }

    /* Context menu items follow the selection: a single row enables them, folders rename inline, characters edit in the settings dialog. */
    private void CharacterItemContextMenuOpening(object sender, ItemContextMenuOpeningEventArgs e)
    {
      var selected = characterTreeView?.SelectedItems?.OfType<CharacterTreeViewNode>().ToList() ?? [];
      var single = selected.Count == 1 ? selected[0] : null;

      newCharacterMenuItem.IsEnabled = single != null;
      newFolderMenuItem.IsEnabled = single != null;
      editMenuItem.IsEnabled = single != null;
      deleteMenuItem.IsEnabled = single != null;
      editMenuItem.Header = single?.IsFolder() == true ? "Rename" : "Modify";
    }

    /* F2: folders rename inline, characters open the settings dialog (where the name lives). */
    private void CharacterTreePreviewKeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key != Key.F2 || characterTreeView?.SelectedItem is not CharacterTreeViewNode node)
      {
        return;
      }

      if (node.IsFolder())
      {
        BeginRename(node);
      }
      else if (node.IsCharacter())
      {
        var configWindow = new TriggerPlayerConfigWindow(node.Character);
        configWindow.ShowDialog();
      }

      e.Handled = true;
    }

    /* Right-click a row without modifiers to single-select it; right-click empty space for the root folder menu. */
    private void CharacterTreePreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
      if (characterTreeView == null)
      {
        return;
      }

      if (e.OriginalSource is FrameworkElement { DataContext: CharacterTreeViewNode node })
      {
        var hasModifier = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) ||
          Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        if (!hasModifier && (characterTreeView.SelectedItems?.Count != 1 || !ReferenceEquals(characterTreeView.SelectedItem, node)))
        {
          characterTreeView.SelectedItems?.Clear();
          characterTreeView.SelectedItem = node;
        }
      }
      else
      {
        var newCharacterItem = new MenuItem { Header = "New Character" };
        newCharacterItem.Click += RootCharacterClick;
        var newFolderItem = new MenuItem { Header = "New Folder" };
        newFolderItem.Click += RootFolderClick;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, PlacementTarget = characterTreeView };
        menu.Items.Add(newCharacterItem);
        menu.Items.Add(newFolderItem);
        menu.IsOpen = true;
      }
    }

    private void BeginRename(CharacterTreeViewNode folderNode)
    {
      characterTreeView.SelectedItem = folderNode;
      characterTreeView.BringIntoView(folderNode);
      characterTreeView.BeginEdit(folderNode);
    }

    private void CharacterSelectionChanged(object sender, ItemSelectionChangedEventArgs e)
    {
      var selected = characterTreeView?.SelectedItems?.OfType<CharacterTreeViewNode>().ToList() ?? [];
      var characters = selected.Where(node => node.IsCharacter()).Select(node => node.Character).ToList();
      var folders = selected.Where(node => node.IsFolder()).ToList();

      modifyCharacter.IsEnabled = (characters.Count == 1 && folders.Count == 0) || (folders.Count == 1 && characters.Count == 0);
      modifyCharacter.Content = folders.Count == 1 && characters.Count == 0 ? "Rename" : "Modify";
      deleteCharacter.IsEnabled = selected.Count == 1;

      if (characters.Count > 0)
      {
        SelectedCharacterEvent?.Invoke(characters);
      }
      else
      {
        SelectedCharacterEvent?.Invoke(null);
      }
    }

    private async void NodeChecked(object sender, NodeCheckedEventArgs e)
    {
      if (_suppressChecked || e.Node is not CharacterTreeViewNode node)
      {
        return;
      }

      if (node.Character != null)
      {
        node.Character.IsEnabled = node.IsChecked == true;
        if (node.Character.IsEnabled)
        {
          node.Character.IsWaiting = true;
        }
      }

      SyncFolderCheckStates();

      if (!_enableFlushQueued)
      {
        _enableFlushQueued = true;
        await Dispatcher.InvokeAsync(FlushCharacterEnabled, DispatcherPriority.Background);
      }
    }

    private async void FlushCharacterEnabled()
    {
      _enableFlushQueued = false;
      var updates = EnumerateNodes()
        .Where(node => node.Character != null)
        .Select(node => (node.Character.Id, node.IsChecked == true))
        .ToList();
      await TriggerStateDB.Instance.UpdateCharactersEnabled(updates);
    }

    private async void NodeExpanded(object sender, NodeExpandedCollapsedEventArgs e)
    {
      if (e.Node is CharacterTreeViewNode { Folder: { } folder } node)
      {
        await TriggerStateDB.Instance.SetCharacterFolderExpanded(folder.Id, node.IsExpanded);
      }
    }

    private void ItemDropping(object sender, TreeViewItemDroppingEventArgs e)
    {
      if (e.TargetNode is not CharacterTreeViewNode target)
      {
        e.Handled = true;
        return;
      }

      if (e.DropPosition == DropPosition.None ||
          (e.DropPosition == DropPosition.DropAsChild && !target.IsFolder()))
      {
        e.Handled = true;
        return;
      }

      if (e.DraggingNodes?.OfType<CharacterTreeViewNode>().Any(dragged =>
            dragged.IsFolder() && IsFolderDescendantOrSelf(target, dragged)) == true)
      {
        e.Handled = true;
      }
    }

    private async void ItemDropped(object sender, TreeViewItemDroppedEventArgs e)
    {
      if (e.TargetNode is not CharacterTreeViewNode target)
      {
        return;
      }

      var parent = (target.IsFolder() && e.DropPosition == DropPosition.DropAsChild)
        ? target
        : target.ParentNode as CharacterTreeViewNode;

      var parentId = parent?.Folder?.Id ?? "";
      var positions = new List<(string Id, bool IsFolder, string Parent, int Index)>();

      if (parent == null)
      {
        for (var i = 0; i < characterTreeView.Nodes.Count; i++)
        {
          if (characterTreeView.Nodes[i] is CharacterTreeViewNode node)
          {
            positions.Add((node.NodeId, node.IsFolder(), "", i));
          }
        }
      }
      else
      {
        for (var i = 0; i < parent.ChildNodes.Count; i++)
        {
          if (parent.ChildNodes[i] is CharacterTreeViewNode node)
          {
            positions.Add((node.NodeId, node.IsFolder(), parentId, i));
          }
        }
      }

      if (positions.Count > 0)
      {
        await TriggerStateDB.Instance.UpdateCharacterTreePositions(positions);
      }

      SyncFolderCheckStates();
    }

    private void ItemBeginEdit(object sender, TreeViewItemBeginEditEventArgs e)
    {
      if (e.Node is CharacterTreeViewNode node && !node.IsFolder())
      {
        e.Cancel = true;
      }
    }

    private void ItemEndEdit(object sender, TreeViewItemEndEditEventArgs e)
    {
      if (!e.Cancel && e.Node is CharacterTreeViewNode { Folder: { } folder } node)
      {
        Dispatcher.InvokeAsync(async () =>
        {
          if (node.Content is string content && !string.IsNullOrEmpty(content) && content.Trim().Length > 0 && folder.Name != content)
          {
            folder.Name = content.Trim();
            await TriggerStateDB.Instance.RenameCharacterFolder(folder.Id, folder.Name);
          }
          else
          {
            node.Content = folder.Name;
          }
        });
      }
      else if (e.Node is CharacterTreeViewNode { Character: { } character } characterNode)
      {
        characterNode.Content = character.Name;
        e.Cancel = true;
      }
    }

    private void BuildTree(TriggerConfig config)
    {
      if (characterTreeView == null || config == null)
      {
        return;
      }

      _suppressChecked = true;
      config.CharacterFolders ??= [];

      var folderNodes = new Dictionary<string, CharacterTreeViewNode>();
      var allNodes = new List<CharacterTreeViewNode>();
      foreach (var folder in config.CharacterFolders)
      {
        var node = new CharacterTreeViewNode
        {
          Folder = folder,
          Content = folder.Name,
          IsExpanded = folder.IsExpanded
        };
        folderNodes[folder.Id] = node;
        allNodes.Add(node);
      }

      foreach (var character in config.Characters)
      {
        allNodes.Add(new CharacterTreeViewNode
        {
          Character = character,
          Content = character.Name,
          IsChecked = character.IsEnabled
        });
      }

      foreach (var node in allNodes.OrderBy(GetNodeIndex).ThenBy(item => item.Content?.ToString()))
      {
        var parentId = node.Folder != null
          ? TriggerStateDB.NormalizeParent(node.Folder.Parent)
          : TriggerStateDB.NormalizeParent(node.Character?.Parent);

        if (!string.IsNullOrEmpty(parentId) && folderNodes.TryGetValue(parentId, out var parent))
        {
          parent.ChildNodes.Add(node);
        }
      }

      characterTreeView.Nodes.Clear();
      foreach (var node in allNodes.Where(item =>
                 string.IsNullOrEmpty(item.Folder != null
                   ? item.Folder.Parent
                   : item.Character?.Parent))
               .OrderBy(GetNodeIndex).ThenBy(item => item.Content?.ToString()))
      {
        characterTreeView.Nodes.Add(node);
      }

      _suppressChecked = false;
      SyncFolderCheckStates();
    }

    private static int GetNodeIndex(CharacterTreeViewNode node) => node.Folder?.Index ?? node.Character?.Index ?? 0;

    private string GetTargetFolderId()
    {
      if (characterTreeView?.SelectedItem is CharacterTreeViewNode node)
      {
        if (node.IsFolder())
        {
          return node.Folder.Id;
        }

        return TriggerStateDB.NormalizeParent(node.Character?.Parent);
      }

      return "";
    }

    private IEnumerable<CharacterTreeViewNode> EnumerateNodes()
    {
      foreach (var node in characterTreeView.Nodes.OfType<CharacterTreeViewNode>())
      {
        foreach (var child in EnumerateNodes(node))
        {
          yield return child;
        }
      }
    }

    private static IEnumerable<CharacterTreeViewNode> EnumerateNodes(CharacterTreeViewNode node)
    {
      yield return node;
      foreach (var child in node.ChildNodes.OfType<CharacterTreeViewNode>())
      {
        foreach (var nested in EnumerateNodes(child))
        {
          yield return nested;
        }
      }
    }

    private static void ExpandParents(CharacterTreeViewNode node)
    {
      var parent = node.ParentNode as CharacterTreeViewNode;
      while (parent != null)
      {
        parent.IsExpanded = true;
        parent = parent.ParentNode as CharacterTreeViewNode;
      }
    }

    private static bool IsFolderDescendantOrSelf(CharacterTreeViewNode target, CharacterTreeViewNode draggedFolder)
    {
      var current = target;
      while (current != null)
      {
        if (current == draggedFolder || (current.Folder != null && current.Folder.Id == draggedFolder.Folder?.Id))
        {
          return true;
        }

        current = current.ParentNode as CharacterTreeViewNode;
      }

      return false;
    }

    private void PreserveWaiting(TriggerConfig config)
    {
      if (_lastConfig?.Characters == null)
      {
        return;
      }

      var waiting = _lastConfig.Characters.ToDictionary(character => character.Id, character => character.IsWaiting);
      foreach (var character in config.Characters)
      {
        if (waiting.TryGetValue(character.Id, out var value))
        {
          character.IsWaiting = value;
        }
      }
    }

    private bool NeedsRebuild(TriggerConfig config)
    {
      if (_lastConfig == null)
      {
        return true;
      }

      var oldFolders = _lastConfig.CharacterFolders ?? [];
      var newFolders = config.CharacterFolders ?? [];
      if (oldFolders.Count != newFolders.Count || _lastConfig.Characters.Count != config.Characters.Count)
      {
        return true;
      }

      if (oldFolders.Select(folder => folder.Id + folder.Parent + folder.Index + folder.Name)
            .SequenceEqual(newFolders.Select(folder => folder.Id + folder.Parent + folder.Index + folder.Name)) == false)
      {
        return true;
      }

      return !_lastConfig.Characters.Select(character => character.Id + character.Parent + character.Index + character.Name)
        .SequenceEqual(config.Characters.Select(character => character.Id + character.Parent + character.Index + character.Name));
    }

    private void SyncEnabledFromConfig(TriggerConfig config)
    {
      var byId = config.Characters.ToDictionary(character => character.Id);
      _suppressChecked = true;
      foreach (var node in EnumerateNodes())
      {
        if (node.Character != null && byId.TryGetValue(node.Character.Id, out var character))
        {
          node.Character.IsEnabled = character.IsEnabled;
          node.IsChecked = character.IsEnabled;
        }
      }

      SyncFolderCheckStates();
      _suppressChecked = false;
    }

    /* Folder checkbox reflects its characters: all enabled = checked, none = unchecked, mixed = indeterminate. */
    private void SyncFolderCheckStates()
    {
      var suppressed = _suppressChecked;
      _suppressChecked = true;
      foreach (var node in EnumerateNodes().Where(node => node.IsFolder()).ToList())
      {
        var (total, checkedCount) = CountCharacterStates(node);
        bool? state;
        if (total > 0 && checkedCount == total)
        {
          state = true;
        }
        else if (checkedCount > 0)
        {
          state = null;
        }
        else
        {
          state = false;
        }

        node.IsChecked = state;
      }

      _suppressChecked = suppressed;
    }

    private static (int Total, int Checked) CountCharacterStates(CharacterTreeViewNode folder)
    {
      var total = 0;
      var checkedCount = 0;
      var stack = new Stack<CharacterTreeViewNode>();
      foreach (var child in folder.ChildNodes.OfType<CharacterTreeViewNode>())
      {
        stack.Push(child);
      }

      while (stack.Count > 0)
      {
        var node = stack.Pop();
        if (node.IsFolder())
        {
          foreach (var child in node.ChildNodes.OfType<CharacterTreeViewNode>())
          {
            stack.Push(child);
          }
        }
        else
        {
          total++;
          if (node.IsChecked == true)
          {
            checkedCount++;
          }
        }
      }

      return (total, checkedCount);
    }

    #region IDisposable Support
    private bool _disposedValue;

    protected virtual void Dispose(bool disposing)
    {
      if (!_disposedValue)
      {
        _statusTimer.Stop();
        _statusTimer.Tick -= StatusTimerTick;
        TriggerStateDB.Instance.TriggerConfigUpdateEvent -= TriggerConfigUpdateEvent;
        MainActions.EventsWindowStateChanged -= EventsWindowStateChanged;
        _disposedValue = true;
        characterTreeView?.Dispose();
      }
    }

    public void Dispose()
    {
      Dispose(true);
      GC.SuppressFinalize(this);
    }
    #endregion
  }
}
