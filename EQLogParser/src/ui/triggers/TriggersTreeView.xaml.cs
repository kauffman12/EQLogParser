using FontAwesome5;
using Syncfusion.UI.Xaml.TreeView;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Application = System.Windows.Application;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace EQLogParser
{
  public partial class TriggersTreeView : IDisposable
  {
    internal event Action<bool> ClosePreviewOverlaysEvent;
    internal event Action<Tuple<TriggerTreeViewNode, object>> TreeSelectionChangedEvent;
    private const string LabelNewTextOverlay = "New Text Overlay";
    private const string LabelNewTimerOverlay = "New Timer Overlay";
    private const string LabelNewTrigger = "New Trigger";
    private const string LabelNewFolder = "New Folder";
    private readonly DispatcherTimer _findTimer;
    private TriggerTreeViewNode _triggerCopiedNode;
    private TriggerTreeViewNode _overlayCopiedNode;
    private bool _triggerCutNode;
    private bool _overlayCutNode;
    private string _currentCharacterId;
    private Func<bool> _isCancelSelection;
    private TriggerConfig _theConfig;
    private List<TriggerCharacter> _selectedCharacters;
    private IEnumerator<TriggerTreeViewNode> _findTriggerEnumerator;
    private bool _shiftDown;

    public TriggersTreeView()
    {
      InitializeComponent();
      SetupDragNDrop(triggerTreeView);
      SetupDragNDrop(overlayTreeView);
      findTrigger.Text = Resource.TRIGGER_SEARCH_TEXT;
      _findTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 750) };
      _findTimer.Tick += async (_, _) =>
      {
        _findTimer.Stop();
        if (findTrigger.FontStyle != FontStyles.Italic && findTrigger.Text != Resource.TRIGGER_SEARCH_TEXT && !string.IsNullOrEmpty(findTrigger.Text))
        {
          if (triggerTreeView?.Nodes.Count > 0 && triggerTreeView?.Nodes[0] is TriggerTreeViewNode node)
          {
            _findTriggerEnumerator = FindNodesByName(triggerTreeView, node, findTrigger.Text).GetEnumerator();
            await ExpandNextTrigger();
          }
        }
      };
    }

    internal async Task RefreshOverlays() => await RefreshOverlayNode();
    internal async Task RefreshTriggers() => await RefreshTriggerNode();
    internal void SetConfig(TriggerConfig config) => _theConfig = config;
    internal async Task SelectNode(string id) => await SelectNode(triggerTreeView, id);

    internal async Task PlayTts(string text, int volume = 4)
    {
      var config = await TriggerStateManager.Instance.GetConfig();
      if (!config.IsAdvanced)
      {
        AudioManager.Instance.TestSpeakTtsAsync(text, config.Voice, config.VoiceRate, volume);
      }
      else if (config.Characters.FirstOrDefault(character => character.Id == _currentCharacterId) is { } found)
      {
        AudioManager.Instance.TestSpeakTtsAsync(text, found.Voice, found.VoiceRate, volume, found.CustomVolume);
      }
      else
      {
        AudioManager.Instance.TestSpeakTtsAsync(text);
      }
    }

    internal async Task Init(string characterId, Func<bool> isCanceled, bool enable)
    {
      _isCancelSelection = isCanceled;
      var nodes = await TriggerStateManager.Instance.GetOverlayTreeView();
      overlayTreeView.Nodes.Add(nodes);
      await EnableAndRefreshTriggers(enable, characterId);
    }

    internal async Task EnableAndRefreshTriggers(bool enable, string characterId, List<TriggerCharacter> characters = null)
    {
      var needRefresh = _currentCharacterId != characterId;
      _selectedCharacters = characters;
      _currentCharacterId = characterId;
      triggerTreeView.IsEnabled = enable;

      if (enable && noCharacterSelected.Visibility == Visibility.Visible)
      {
        noCharacterSelected.Visibility = Visibility.Collapsed;
        triggerTreeView.Visibility = Visibility.Visible;
      }
      else if (!enable && noCharacterSelected.Visibility == Visibility.Collapsed)
      {
        noCharacterSelected.Visibility = Visibility.Visible;
        triggerTreeView.Visibility = Visibility.Collapsed;
      }

      if (needRefresh)
      {
        await RefreshTriggerNode();
      }
    }

    private async void CreateTextOverlayClick(object sender, RoutedEventArgs e) => await CreateOverlay(true);
    private async void CreateTimerOverlayClick(object sender, RoutedEventArgs e) => await CreateOverlay(false);
    private async void AssignOverlayClick(object sender, RoutedEventArgs e) => await SetOverlay(sender);
    private async void UnassignOverlayClick(object sender, RoutedEventArgs e) => await SetOverlay(sender, true);

    private async void ClearRecentlyMergedClick(object sender, RoutedEventArgs e)
    {
      TriggerStateManager.Instance.RecentlyMerged.Clear();
      await RefreshTriggers();
    }

    private async Task ExpandNextTrigger()
    {
      if (_findTriggerEnumerator?.MoveNext() == true)
      {
        var node = _findTriggerEnumerator.Current;
        triggerTreeView.ExpandNode(node?.IsTrigger() == true ? node.ParentNode : node);
        triggerTreeView.SelectedItems?.Clear();
        triggerTreeView.SelectedItem = node;
        await SelectionChanged(node);
      }
      else if (triggerTreeView?.Nodes.Count > 0 && triggerTreeView?.Nodes[0] is TriggerTreeViewNode node)
      {
        _findTriggerEnumerator = FindNodesByName(triggerTreeView, node, findTrigger.Text).GetEnumerator();
      }
    }

    private async void NodeExpanded(object sender, NodeExpandedCollapsedEventArgs e)
    {
      await TriggerStateManager.Instance.SetExpanded(e.Node as TriggerTreeViewNode);
    }

    private static void SetupDragNDrop(SfTreeView treeView)
    {
      treeView.DragDropController = new TreeViewDragDropController
      {
        CanAutoExpand = true,
        AutoExpandDelay = new TimeSpan(0, 0, 1)
      };
    }

    private void HideOverlaysClick(object sender, RoutedEventArgs e)
    {
      ClosePreviewOverlaysEvent?.Invoke(true);
      TriggerOverlayManager.Instance.HideOverlays();
      e.Handled = true;
    }

    private void CollapseAllClick(object sender, RoutedEventArgs e)
    {
      if (GetTreeViewFromMenu(sender) is { } treeView)
      {
        Dispatcher.InvokeAsync(async () =>
        {
          treeView.CollapseAll();
          await TriggerStateManager.Instance.SetAllExpanded(false);
        });
      }
    }

    private void ExpandAllClick(object sender, RoutedEventArgs e)
    {
      if (GetTreeViewFromMenu(sender) is { } treeView)
      {
        Dispatcher.InvokeAsync(async () =>
        {
          treeView.ExpandAll();
          await TriggerStateManager.Instance.SetAllExpanded(true);
        });
      }
    }

    private void ExportClick(object sender, RoutedEventArgs e)
    {
      if (GetTreeViewFromMenu(sender) is { } treeView)
      {
        var nodes = treeView.SelectedItems?.Cast<TriggerTreeViewNode>().ToList();
        Dispatcher.InvokeAsync(() => TriggerUtil.Export(nodes));
      }
    }

    private void ShareOverlayClick(object sender, RoutedEventArgs e)
    {
      if (GetTreeViewFromMenu(sender) is { } treeView)
      {
        var nodes = treeView.SelectedItems?.Cast<TriggerTreeViewNode>().ToList();
        Dispatcher.InvokeAsync(() => TriggerUtil.ShareAsync(nodes, false));
      }
    }

    private void ShareTriggersClick(object sender, RoutedEventArgs e)
    {
      if (GetTreeViewFromMenu(sender) is { } treeView)
      {
        var nodes = treeView.SelectedItems?.Cast<TriggerTreeViewNode>().ToList();
        Dispatcher.InvokeAsync(() => TriggerUtil.ShareAsync(nodes, true));
      }
    }

    private void SelectionChanging(object sender, ItemSelectionChangingEventArgs e)
    {
      e.Cancel = _isCancelSelection();
    }

    private async void NodeChecked(object sender, NodeCheckedEventArgs e)
    {
      if (e.Node is TriggerTreeViewNode viewNode)
      {
        var ids = _selectedCharacters?.Select(x => x.Id).ToList() ?? [_currentCharacterId];
        await TriggerStateManager.Instance.SetState(ids, viewNode);
        TriggerManager.Instance.TriggersUpdated();
      }
    }

    private void RenameClick(object sender, RoutedEventArgs e)
    {
      if (GetTreeViewFromMenu(sender) is { } treeView)
      {
        treeView.BeginEdit(treeView.SelectedItem as TriggerTreeViewNode);
      }
    }

    private async void DeleteClick(object sender, RoutedEventArgs e)
    {
      if (GetTreeViewFromMenu(sender) is { } treeView)
      {
        _triggerCutNode = false;
        _triggerCopiedNode = null;
        var list = treeView.SelectedItems?.Cast<TriggerTreeViewNode>().ToList();
        await Delete(list);
      }
    }

    private async void ImportClick(object sender, RoutedEventArgs e)
    {
      if (GetTreeViewFromMenu(sender) is { SelectedItem: TriggerTreeViewNode node } treeView)
      {
        if (treeView == triggerTreeView)
        {
          await TriggerUtil.ImportTriggers(node.SerializedData);
          await RefreshTriggerNode();
        }
        else if (treeView == overlayTreeView)
        {
          await TriggerUtil.ImportOverlays(node.SerializedData);
          await RefreshOverlayNode();
        }
      }
    }

    private async Task RefreshTriggerNode()
    {
      triggerTreeView?.Nodes?.Clear();
      var nodes = await TriggerStateManager.Instance.GetTriggerTreeView(_currentCharacterId);
      triggerTreeView?.Nodes?.Add(nodes);
    }

    private async Task RefreshOverlayNode()
    {
      if (overlayTreeView?.Nodes.Count > 0)
      {
        overlayTreeView.Nodes.Clear();
        var nodes = await TriggerStateManager.Instance.GetOverlayTreeView();
        overlayTreeView.Nodes.Add(nodes);
      }
    }

    private async Task SelectNode(SfTreeView treeView, string id)
    {
      if (id != null && _isCancelSelection != null && !_isCancelSelection())
      {
        if (treeView?.Nodes.Count > 0 && treeView.Nodes[0] is TriggerTreeViewNode node)
        {
          if (FindAndExpandNodeById(treeView, node, id) is { } found)
          {
            treeView.SelectedItems?.Clear();
            treeView.SelectedItem = found;
            await SelectionChanged(found);
          }
        }
      }
    }

    private static TriggerTreeViewNode FindAndExpandNodeById(SfTreeView treeView, TriggerTreeViewNode node, string id)
    {
      if (node.SerializedData?.Id == id)
      {
        return node;
      }

      foreach (var child in node.ChildNodes.Cast<TriggerTreeViewNode>())
      {
        if (FindAndExpandNodeById(treeView, child, id) is { } found)
        {
          treeView.ExpandNode(node);
          return found;
        }
      }

      return null;
    }

    private static IEnumerable<TriggerTreeViewNode> FindNodesByName(SfTreeView treeView, TriggerTreeViewNode node, string name)
    {
      if (node.SerializedData?.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) == true ||
          node.SerializedData?.TriggerData?.Pattern.Contains(name, StringComparison.OrdinalIgnoreCase) == true)
      {
        // Yield the current node if its name contains the search term
        yield return node;
      }

      foreach (var child in node.ChildNodes.Cast<TriggerTreeViewNode>())
      {
        // Recursively search in child nodes and yield each found node
        foreach (var found in FindNodesByName(treeView, child, name))
        {
          treeView.ExpandNode(node);
          yield return found;
        }
      }
    }


    private async void CreateNodeClick(object sender, RoutedEventArgs e)
    {
      if (GetTreeViewFromMenu(sender) is { SelectedItem: TriggerTreeViewNode { SerializedData.Id: { } id } parent })
      {
        var newNode = await TriggerStateManager.Instance.CreateFolder(id, LabelNewFolder, _currentCharacterId);
        parent.ChildNodes.Add(newNode);
      }
    }

    private async Task CreateOverlay(bool isTextOverlay)
    {
      if (overlayTreeView.SelectedItem is TriggerTreeViewNode parent)
      {
        var label = isTextOverlay ? LabelNewTextOverlay : LabelNewTimerOverlay;
        if (await TriggerStateManager.Instance.CreateOverlay(parent.SerializedData.Id, label, isTextOverlay) is { } newNode)
        {
          parent.ChildNodes.Add(newNode);
          await SelectNode(overlayTreeView, newNode.SerializedData.Id);
        }
      }
    }

    private async void CreateTriggerClick(object sender, RoutedEventArgs e)
    {
      if (triggerTreeView.SelectedItem is TriggerTreeViewNode parent)
      {
        if (await TriggerStateManager.Instance.CreateTrigger(parent.SerializedData.Id, LabelNewTrigger, _currentCharacterId) is { } newNode)
        {
          parent.ChildNodes.Add(newNode);
          await SelectNode(triggerTreeView, newNode.SerializedData.Id);
        }
      }
    }

    private void CopyClick(object sender, RoutedEventArgs e)
    {
      var treeView = GetTreeViewFromMenu(sender);
      if (treeView == triggerTreeView)
      {
        if (triggerTreeView.SelectedItem is TriggerTreeViewNode node)
        {
          _triggerCopiedNode = node;
          _triggerCutNode = false;
        }
      }
      else if (treeView == overlayTreeView)
      {
        if (overlayTreeView.SelectedItem is TriggerTreeViewNode node)
        {
          _overlayCopiedNode = node;
          _overlayCutNode = false;
        }
      }
    }

    private void CutClick(object sender, RoutedEventArgs e)
    {
      var treeView = GetTreeViewFromMenu(sender);
      if (treeView == triggerTreeView)
      {
        if (triggerTreeView.SelectedItem is TriggerTreeViewNode node)
        {
          _triggerCopiedNode = node;
          _triggerCutNode = true;
        }
      }
      else if (treeView == overlayTreeView)
      {
        if (overlayTreeView.SelectedItem is TriggerTreeViewNode node)
        {
          _overlayCopiedNode = node;
          _overlayCutNode = true;
        }
      }
    }

    private async void PasteClick(object sender, RoutedEventArgs e)
    {
      var treeView = GetTreeViewFromMenu(sender);
      if (treeView == triggerTreeView)
      {
        await HandlePaste(triggerTreeView, _triggerCopiedNode, _triggerCutNode);
        _triggerCopiedNode = null;
      }
      else if (treeView == overlayTreeView)
      {
        await HandlePaste(overlayTreeView, _overlayCopiedNode, _overlayCutNode);
        _overlayCopiedNode = null;
      }

      return;

      async Task HandlePaste(SfTreeView tree, TriggerTreeViewNode copiedNode, bool isCutNode)
      {
        if (tree.SelectedItem is TriggerTreeViewNode node && copiedNode != null)
        {
          if (copiedNode.IsDir())
          {
            if (copiedNode.SerializedData.Parent != node.SerializedData.Id)
            {
              copiedNode.SerializedData.Parent = node.SerializedData.Id;
              await TriggerStateManager.Instance.Update(copiedNode.SerializedData, true);
              await RefreshTriggerNode();
            }
          }
          else
          {
            if (isCutNode)
            {
              await Delete([copiedNode]);
            }

            await TriggerStateManager.Instance.Copy(copiedNode.SerializedData, node.SerializedData);

            if (copiedNode.IsTrigger())
            {
              await RefreshTriggerNode();
            }
            else if (copiedNode.IsOverlay())
            {
              await RefreshOverlayNode();
            }
          }
        }
      }
    }

    private async Task Delete(List<TriggerTreeViewNode> nodes)
    {
      var overlayDelete = false;
      var triggerDelete = false;
      foreach (var node in nodes)
      {
        if (node?.ParentNode is TriggerTreeViewNode parent && node.SerializedData.Id is { } id)
        {
          parent.ChildNodes.Remove(node);
          if (!parent.HasChildNodes)
          {
            parent.IsChecked = false;
          }

          await TriggerStateManager.Instance.Delete(id);

          if (node.IsOverlay())
          {
            overlayDelete = true;
          }
          else if ((node.IsDir() || node.IsTrigger()) && node.IsChecked != false)
          {
            triggerDelete = true;
          }
        }
      }

      // if an overlay was deleted then trigger selected overlay may need refresh
      if (overlayDelete)
      {
        await RefreshTriggerNode();
      }

      if (triggerDelete)
      {
        TriggerManager.Instance.TriggersUpdated();
      }
    }

    private void ItemDropping(object sender, TreeViewItemDroppingEventArgs e)
    {
      if (e.TargetNode as TriggerTreeViewNode is { } target)
      {
        if (e.DropPosition == DropPosition.None ||
            (target.Level == 0 && e.DropPosition != DropPosition.DropAsChild) ||
            (e.DropPosition == DropPosition.DropAsChild && !target.IsDir()))
        {
          e.Handled = true;
        }
      }

      _shiftDown = Keyboard.IsKeyDown(Key.LeftShift);
    }

    private async void ItemDropped(object sender, TreeViewItemDroppedEventArgs e)
    {
      if (e.TargetNode as TriggerTreeViewNode is { } target)
      {
        target = (target.IsDir() && e.DropPosition == DropPosition.DropAsChild) ? target : target.ParentNode as TriggerTreeViewNode;

        var needRefresh = false;
        for (var i = 0; i < target?.ChildNodes.Count; i++)
        {
          if (target.ChildNodes[i] is TriggerTreeViewNode node)
          {
            if (target.SerializedData.Id != node.SerializedData.Parent || node.SerializedData.Index != i)
            {
              node.SerializedData.Parent = target.SerializedData.Id;
              node.SerializedData.Index = i;
              await TriggerStateManager.Instance.Update(node.SerializedData);

              if (_shiftDown)
              {
                await TriggerStateManager.Instance.SetStateFromParent(node.SerializedData.Parent, _currentCharacterId, node);
              }

              needRefresh = true;
            }
          }
        }

        if (needRefresh)
        {
          await RefreshTriggerNode();
          TriggerManager.Instance.TriggersUpdated();
        }
      }
    }

    private void ItemEndEdit(object sender, TreeViewItemEndEditEventArgs e)
    {
      if (!e.Cancel && e.Node is TriggerTreeViewNode node && sender is SfTreeView treeView)
      {
        // delay because node still shows old value
        Dispatcher.InvokeAsync(async () =>
        {
          var content = node.Content as string;
          if (string.IsNullOrEmpty(content) || content.Trim().Length == 0 || node.SerializedData.Name == content)
          {
            node.Content = node.SerializedData.Name;
            treeView.SelectedItems?.Clear();
            treeView.SelectedItem = node;
          }
          else
          {
            node.SerializedData.Name = node.Content as string;
            await TriggerStateManager.Instance.Update(node.SerializedData);

            if (node.IsOverlay())
            {
              Application.Current.Resources["OverlayText-" + node.SerializedData.Id] = node.SerializedData.Name;
            }
          }
        }, DispatcherPriority.DataBind);
      }
    }

    private async void TriggerItemContextMenuOpening(object sender, ItemContextMenuOpeningEventArgs e)
    {
      var node = triggerTreeView.SelectedItem as TriggerTreeViewNode;
      var count = triggerTreeView.SelectedItems?.Count ?? 0;


      if (node != null)
      {
        renameTriggerMenuItem.IsEnabled = node.ParentNode != null;
        deleteTriggerMenuItem.IsEnabled = node.ParentNode != null;
        importTriggerMenuItem.IsEnabled = node.IsDir() && count == 1;
        newTriggerMenuItem.IsEnabled = node.IsDir() && count == 1;
        copyTriggerItem.IsEnabled = !node.IsDir() && count == 1;
        cutTriggerItem.IsEnabled = node.ParentNode != null && (copyTriggerItem.IsEnabled || (node.IsDir() && count == 1));
        pasteTriggerItem.IsEnabled = node.IsDir() && count == 1 && _triggerCopiedNode != null;
      }
      else
      {
        renameTriggerMenuItem.IsEnabled = false;
        deleteTriggerMenuItem.IsEnabled = false;
        importTriggerMenuItem.IsEnabled = false;
        newTriggerMenuItem.IsEnabled = false;
        copyTriggerItem.IsEnabled = false;
        cutTriggerItem.IsEnabled = false;
        pasteTriggerItem.IsEnabled = false;
      }

      clearRecentlyMergedMenuItem.IsEnabled = !TriggerStateManager.Instance.RecentlyMerged.IsEmpty;
      importTriggerMenuItem.Header = node != null && importTriggerMenuItem.IsEnabled ? $"Import to ({node.Content})" : "Import";

      UiElementUtil.ClearMenuEvents(copySettingsMenuItem.Items, CopySettingsClick);
      copySettingsMenuItem.Items.Clear();

      if (_theConfig?.IsAdvanced == true)
      {
        copySettingsMenuItem.Visibility = Visibility.Visible;
        if (_theConfig.Characters.Count > 1 && triggerTreeView.SelectedItems?.Count == 1)
        {
          copySettingsMenuItem.IsEnabled = true;
          foreach (var character in _theConfig.Characters.Where(c => c.Id != _currentCharacterId))
          {
            var menuItem = new MenuItem { Header = character.Name, Tag = character.Id };
            menuItem.Click += CopySettingsClick;
            copySettingsMenuItem.Items.Add(menuItem);
          }
        }
        else
        {
          copySettingsMenuItem.IsEnabled = false;
        }
      }
      else
      {
        copySettingsMenuItem.IsEnabled = false;
        copySettingsMenuItem.Visibility = Visibility.Collapsed;
      }

      UiElementUtil.ClearMenuEvents(setPriorityMenuItem.Items, SetPriorityClick);
      setPriorityMenuItem.Items.Clear();

      for (var i = 1; i <= 5; i++)
      {
        var menuItem = new MenuItem { Header = "Priority " + i, Tag = i };
        if (i == 1)
        {
          var icon = new ImageAwesome { Icon = EFontAwesomeIcon.Solid_ArrowUp };
          icon.SetResourceReference(StyleProperty, "EQIconStyle");
          menuItem.Icon = icon;
        }
        else if (i == 5)
        {
          var icon = new ImageAwesome { Icon = EFontAwesomeIcon.Solid_ArrowDown };
          icon.SetResourceReference(StyleProperty, "EQIconStyle");
          menuItem.Icon = icon;
        }

        menuItem.Click += SetPriorityClick;
        setPriorityMenuItem.Items.Add(menuItem);
      }

      if (setTriggerMenuItem.Visibility == Visibility.Visible)
      {
        UiElementUtil.ClearMenuEvents(addTextOverlaysMenuItem.Items, AssignOverlayClick);
        UiElementUtil.ClearMenuEvents(addTimerOverlaysMenuItem.Items, AssignOverlayClick);
        UiElementUtil.ClearMenuEvents(removeTextOverlaysMenuItem.Items, UnassignOverlayClick);
        UiElementUtil.ClearMenuEvents(removeTimerOverlaysMenuItem.Items, UnassignOverlayClick);
        addTextOverlaysMenuItem.Items.Clear();
        addTimerOverlaysMenuItem.Items.Clear();
        removeTextOverlaysMenuItem.Items.Clear();
        removeTimerOverlaysMenuItem.Items.Clear();

        foreach (var overlay in await TriggerStateManager.Instance.GetAllOverlays())
        {
          var addMenuItem = CreateMenuItem(overlay, AssignOverlayClick);
          var removeMenuItem = CreateMenuItem(overlay, UnassignOverlayClick);
          if (overlay.OverlayData?.IsTextOverlay == true)
          {
            addTextOverlaysMenuItem.Items.Add(addMenuItem);
            removeTextOverlaysMenuItem.Items.Add(removeMenuItem);
          }
          else
          {
            addTimerOverlaysMenuItem.Items.Add(addMenuItem);
            removeTimerOverlaysMenuItem.Items.Add(removeMenuItem);
          }
        }
      }

      static MenuItem CreateMenuItem(OtData overlay, RoutedEventHandler eventHandler)
      {
        var menuItem = new MenuItem { Header = overlay.Name, Tag = $"{overlay.Name}={overlay.Id}" };
        menuItem.Click += eventHandler;
        return menuItem;
      }
    }

    private void OverlayItemContextMenuOpening(object sender, ItemContextMenuOpeningEventArgs e)
    {
      var node = overlayTreeView.SelectedItem as TriggerTreeViewNode;
      var count = overlayTreeView.SelectedItems?.Count ?? 0;


      if (node != null)
      {
        renameOverlayMenuItem.IsEnabled = node.ParentNode != null;
        deleteOverlayMenuItem.IsEnabled = node.ParentNode != null;
        importOverlayMenuItem.IsEnabled = node.IsDir() && count == 1;
        newOverlayMenuItem.IsEnabled = node.IsDir() && count == 1;
        copyOverlayItem.IsEnabled = !node.IsDir() && count == 1;
        pasteOverlayItem.IsEnabled = node.IsDir() && count == 1 && _overlayCopiedNode != null;
      }
      else
      {
        renameOverlayMenuItem.IsEnabled = false;
        deleteOverlayMenuItem.IsEnabled = false;
        importOverlayMenuItem.IsEnabled = false;
        copyOverlayItem.IsEnabled = false;
        pasteOverlayItem.IsEnabled = false;
      }

      importOverlayMenuItem.Header = node != null && importOverlayMenuItem.IsEnabled ? $"Import to Folder ({node.Content})" : "Import";
    }

    private async void CopySettingsClick(object sender, RoutedEventArgs e)
    {
      if (sender is MenuItem { Tag: string id })
      {
        await TriggerStateManager.Instance.CopyState((TriggerTreeViewNode)triggerTreeView.SelectedItem, _currentCharacterId, id);
      }
    }

    private async void SetPriorityClick(object sender, RoutedEventArgs e)
    {
      if (sender is MenuItem menuItem && int.TryParse(menuItem.Tag.ToString(), out var newPriority))
      {
        var selected = triggerTreeView.SelectedItems.Cast<TriggerTreeViewNode>().ToList();
        var anyFolders = selected.Any(node => node.IsDir());
        if (!anyFolders)
        {
          await TriggerStateManager.Instance.AssignPriority(newPriority, selected.Select(treeView => treeView.SerializedData));
        }
        else
        {
          var msgDialog = new MessageWindow($"Are you sure? This will Set Priority {newPriority} to all selected\nTriggers and those in all sub folders.",
            Resource.ASSIGN_PRIORITY, MessageWindow.IconType.Warn, "Yes");
          msgDialog.ShowDialog();
          if (msgDialog.IsYes1Clicked)
          {
            await TriggerStateManager.Instance.AssignPriority(newPriority, selected.Select(treeView => treeView.SerializedData));
          }
        }

        await RefreshTriggerNode();
        await SelectionChanged(triggerTreeView.SelectedItem as TriggerTreeViewNode);
        TriggerManager.Instance.TriggersUpdated();
      }
    }

    private async Task SetOverlay(object sender, bool remove = false)
    {
      if (sender is MenuItem menuItem)
      {
        var selected = triggerTreeView.SelectedItems.Cast<TriggerTreeViewNode>().ToList();
        var anyFolders = selected.Any(node => node.IsDir());

        if (menuItem.Tag is string overlayTag && overlayTag.Split('=') is { Length: 2 } overlayData)
        {
          var name = overlayData[0];
          var id = overlayData[1];

          if (!anyFolders)
          {
            if (!remove)
            {
              await TriggerStateManager.Instance.AssignOverlay(id, selected.Select(treeView => treeView.SerializedData));
            }
            else
            {
              await TriggerStateManager.Instance.UnassignOverlay(id, selected.Select(treeView => treeView.SerializedData));
            }
          }
          else
          {
            var action = remove ? "Remove" : "Add";
            var msgDialog = new MessageWindow($"Are you sure? This will {action} {name} from all selected\nTriggers and those in all sub folders.",
              Resource.ASSIGN_OVERLAY, MessageWindow.IconType.Warn, "Yes");
            msgDialog.ShowDialog();
            if (msgDialog.IsYes1Clicked)
            {
              if (!remove)
              {
                await TriggerStateManager.Instance.AssignOverlay(id, selected.Select(treeView => treeView.SerializedData));
              }
              else
              {
                await TriggerStateManager.Instance.UnassignOverlay(id, selected.Select(treeView => treeView.SerializedData));
              }
            }
          }
        }

        await RefreshTriggerNode();
        await SelectionChanged(triggerTreeView.SelectedItem as TriggerTreeViewNode);
        TriggerManager.Instance.TriggersUpdated();
      }
    }

    private async void SelectionChanged(object sender, ItemSelectionChangedEventArgs e)
    {
      if (e.AddedItems.Count > 0 && e.AddedItems[0] is TriggerTreeViewNode node)
      {
        if (Equals(sender, triggerTreeView))
        {
          overlayTreeView.SelectedItems?.Clear();
        }
        else if (Equals(sender, overlayTreeView))
        {
          triggerTreeView.SelectedItems?.Clear();
        }

        await SelectionChanged(node);
      }
    }

    private async Task SelectionChanged(TriggerTreeViewNode node)
    {
      if (node != null)
      {
        dynamic model = null;
        var isTimerOverlay = node.SerializedData?.OverlayData?.IsTimerOverlay == true;

        if (node.IsTrigger() || node.IsOverlay())
        {
          var data = node.SerializedData;
          if (node.IsTrigger())
          {
            model = new TriggerPropertyModel { Node = data, DataContext = this };
            await TriggerUtil.Copy(model, node.SerializedData?.TriggerData);
          }
          else if (node.IsOverlay())
          {
            if (!isTimerOverlay)
            {
              model = new TextOverlayPropertyModel { Node = data };
              await TriggerUtil.Copy(model, data?.OverlayData);
            }
            else
            {
              model = new TimerOverlayPropertyModel { Node = data };
              await TriggerUtil.Copy(model, data?.OverlayData);
              model.TimerBarPreview = data?.Id;
            }
          }
        }

        TreeSelectionChangedEvent?.Invoke(Tuple.Create(node, (object)model));
      }
    }

    private static SfTreeView GetTreeViewFromMenu(object sender)
    {
      if (sender is MenuItem { DataContext: TreeViewItemContextMenuInfo info })
      {
        return info.TreeView;
      }

      return null;
    }

    private void TreeViewPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
      if (sender is SfTreeView treeView)
      {
        if (e.OriginalSource is FrameworkElement { DataContext: TriggerTreeViewNode node })
        {
          if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) ||
            Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
          {
            return;
          }

          treeView.SelectedItems?.Clear();
          treeView.SelectedItem = node;
        }
      }
    }

    #region IDisposable Support
    private bool _disposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!_disposedValue)
      {
        _disposedValue = true;
        triggerTreeView?.DragDropController.Dispose();
        triggerTreeView?.Dispose();
        overlayTreeView?.DragDropController.Dispose();
        overlayTreeView?.Dispose();
      }
    }

    // This code added to correctly implement the disposable pattern.
    public void Dispose()
    {
      // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
      Dispose(true);
      GC.SuppressFinalize(this);
    }
    #endregion

    private void FindLostFocus(object sender, RoutedEventArgs e)
    {
      if (string.IsNullOrEmpty(findTrigger.Text))
      {
        findTrigger.Text = Resource.TRIGGER_SEARCH_TEXT;
        findTrigger.FontStyle = FontStyles.Italic;
      }
    }

    private void FindGotFocus(object sender, RoutedEventArgs e)
    {
      if (findTrigger.Text == Resource.TRIGGER_SEARCH_TEXT)
      {
        findTrigger.Text = "";
        findTrigger.FontStyle = FontStyles.Normal;
      }
    }

    private async void FindKeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.Escape)
      {
        findTrigger.Text = Resource.TRIGGER_SEARCH_TEXT;
        findTrigger.FontStyle = FontStyles.Italic;
        triggerTreeView.Focus();
      }
      else if (e.Key == Key.Enter)
      {
        await ExpandNextTrigger();
      }
    }

    private void FindTextChanged(object sender, TextChangedEventArgs e)
    {
      _findTimer?.Stop();
      _findTimer?.Start();
    }
  }
}