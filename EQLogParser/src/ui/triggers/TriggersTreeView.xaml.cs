using FontAwesome5;
using Syncfusion.UI.Xaml.TreeView;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TriggersTreeView.xaml
  /// </summary>
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
    private bool _shiftDown = false;

    public TriggersTreeView()
    {
      InitializeComponent();
      SetupDragNDrop(triggerTreeView);
      SetupDragNDrop(overlayTreeView);
      TriggerManager.Instance.EventsSelectTrigger += EventsSelectTrigger;
      findTrigger.Text = Resource.TRIGGER_SEARCH_TEXT;
      _findTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 750) };
      _findTimer.Tick += (_, _) =>
      {
        _findTimer.Stop();
        if (findTrigger.FontStyle != FontStyles.Italic && findTrigger.Text != Resource.TRIGGER_SEARCH_TEXT && !string.IsNullOrEmpty(findTrigger.Text))
        {
          if (triggerTreeView?.Nodes.Count > 0 && triggerTreeView?.Nodes[0] is TriggerTreeViewNode node)
          {
            _findTriggerEnumerator = FindNodesByName(triggerTreeView, node, findTrigger.Text).GetEnumerator();
            ExpandNextTrigger();
          }
        }
      };
    }

    internal void RefreshOverlays() => RefreshOverlayNode();
    internal void RefreshTriggers() => RefreshTriggerNode();
    internal void SetConfig(TriggerConfig config) => _theConfig = config;

    internal void Init(string characterId, Func<bool> isCanceled, bool enable)
    {
      _isCancelSelection = isCanceled;
      overlayTreeView.Nodes.Add(TriggerStateManager.Instance.GetOverlayTreeView());
      EnableAndRefreshTriggers(enable, characterId);
    }

    internal void EnableAndRefreshTriggers(bool enable, string characterId, List<TriggerCharacter> characters = null)
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
        RefreshTriggerNode();
      }
    }

    private void CreateTextOverlayClick(object sender, RoutedEventArgs e) => CreateOverlay(true);
    private void CreateTimerOverlayClick(object sender, RoutedEventArgs e) => CreateOverlay(false);
    private void EventsSelectTrigger(string id) => Dispatcher.InvokeAsync(() => SelectNode(triggerTreeView, id));
    private void NodeExpanded(object sender, NodeExpandedCollapsedEventArgs e) => TriggerStateManager.Instance.SetExpanded(e.Node as TriggerTreeViewNode);
    private void AssignOverlayClick(object sender, RoutedEventArgs e) => SetOverlay(sender);
    private void UnassignOverlayClick(object sender, RoutedEventArgs e) => SetOverlay(sender, true);

    private void ClearRecentlyMergedClick(object sender, RoutedEventArgs e)
    {
      TriggerStateManager.Instance.RecentlyMerged.Clear();
      RefreshTriggers();
    }

    private void ExpandNextTrigger()
    {
      if (_findTriggerEnumerator?.MoveNext() == true)
      {
        var node = _findTriggerEnumerator.Current;
        triggerTreeView.ExpandNode(node?.IsTrigger() == true ? node.ParentNode : node);
        triggerTreeView.SelectedItems?.Clear();
        triggerTreeView.SelectedItem = node;
        SelectionChanged(node);
      }
      else if (triggerTreeView?.Nodes.Count > 0 && triggerTreeView?.Nodes[0] is TriggerTreeViewNode node)
      {
        _findTriggerEnumerator = FindNodesByName(triggerTreeView, node, findTrigger.Text).GetEnumerator();
      }
    }

    private static void SetupDragNDrop(SfTreeView treeView)
    {
      treeView.DragDropController = new TreeViewDragDropController
      {
        CanAutoExpand = true,
        AutoExpandDelay = new TimeSpan(0, 0, 1)
      };
    }

    private void CloseOverlaysClick(object sender, RoutedEventArgs e)
    {
      TriggerManager.Instance.CloseOverlays();
      ClosePreviewOverlaysEvent?.Invoke(true);
      e.Handled = true;
    }

    private void CollapseAllClick(object sender, RoutedEventArgs e)
    {
      if (GetTreeViewFromMenu(sender) is { } treeView)
      {
        Dispatcher.InvokeAsync(() =>
        {
          treeView.CollapseAll();
          TriggerStateManager.Instance.SetAllExpanded(false);
        });
      }
    }

    private void ExpandAllClick(object sender, RoutedEventArgs e)
    {
      if (GetTreeViewFromMenu(sender) is { } treeView)
      {
        Dispatcher.InvokeAsync(() =>
        {
          treeView.ExpandAll();
          TriggerStateManager.Instance.SetAllExpanded(true);
        });
      }
    }

    private void ExportClick(object sender, RoutedEventArgs e)
    {
      if (GetTreeViewFromMenu(sender) is { } treeView)
      {
        TriggerUtil.Export(treeView.SelectedItems?.Cast<TriggerTreeViewNode>());
      }
    }

    private void ShareTriggersClick(object sender, RoutedEventArgs e)
    {
      if (GetTreeViewFromMenu(sender) is { } treeView)
      {
        var nodes = treeView.SelectedItems?.Cast<TriggerTreeViewNode>().ToList();
        Dispatcher.InvokeAsync(() => TriggerUtil.ShareAsync(nodes));
      }
    }

    private void SelectionChanging(object sender, ItemSelectionChangingEventArgs e)
    {
      e.Cancel = _isCancelSelection();
    }

    private void NodeChecked(object sender, NodeCheckedEventArgs e)
    {
      if (e.Node is TriggerTreeViewNode viewNode)
      {
        var ids = _selectedCharacters?.Select(x => x.Id).ToList() ?? [_currentCharacterId];
        TriggerStateManager.Instance.SetState(ids, viewNode);
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

    private void DeleteClick(object sender, RoutedEventArgs e)
    {
      if (GetTreeViewFromMenu(sender) is { } treeView)
      {
        _triggerCutNode = false;
        _triggerCopiedNode = null;
        Delete(treeView.SelectedItems?.Cast<TriggerTreeViewNode>().ToList());
      }
    }

    private void ImportClick(object sender, RoutedEventArgs e)
    {
      if (GetTreeViewFromMenu(sender) is { SelectedItem: TriggerTreeViewNode node } treeView)
      {
        if (treeView == triggerTreeView)
        {
          TriggerUtil.ImportTriggers(node.SerializedData);
          RefreshTriggerNode();
        }
        else if (treeView == overlayTreeView)
        {
          TriggerUtil.ImportOverlays(node.SerializedData);
          RefreshOverlayNode();
        }
      }
    }

    private void RefreshTriggerNode()
    {
      triggerTreeView?.Nodes?.Clear();
      triggerTreeView?.Nodes?.Add(TriggerStateManager.Instance.GetTriggerTreeView(_currentCharacterId));
    }

    private void RefreshOverlayNode()
    {
      if (overlayTreeView?.Nodes.Count > 0)
      {
        overlayTreeView.Nodes.Clear();
        overlayTreeView.Nodes.Add(TriggerStateManager.Instance.GetOverlayTreeView());
      }
    }

    private void SelectNode(SfTreeView treeView, string id)
    {
      if (id != null && _isCancelSelection != null && !_isCancelSelection())
      {
        if (treeView?.Nodes.Count > 0 && treeView.Nodes[0] is TriggerTreeViewNode node)
        {
          if (FindAndExpandNodeById(treeView, node, id) is { } found)
          {
            treeView.SelectedItems?.Clear();
            treeView.SelectedItem = found;
            SelectionChanged(found);
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


    private void CreateNodeClick(object sender, RoutedEventArgs e)
    {
      if (GetTreeViewFromMenu(sender) is { SelectedItem: TriggerTreeViewNode { SerializedData.Id: { } id } parent })
      {
        var newNode = TriggerStateManager.Instance.CreateFolder(id, LabelNewFolder, _currentCharacterId);
        parent.ChildNodes.Add(newNode);
      }
    }

    private void CreateOverlay(bool isTextOverlay)
    {
      if (overlayTreeView.SelectedItem is TriggerTreeViewNode parent)
      {
        var label = isTextOverlay ? LabelNewTextOverlay : LabelNewTimerOverlay;
        if (TriggerStateManager.Instance.CreateOverlay(parent.SerializedData.Id, label, isTextOverlay) is { } newNode)
        {
          parent.ChildNodes.Add(newNode);
          SelectNode(overlayTreeView, newNode.SerializedData.Id);
        }
      }
    }

    private void CreateTriggerClick(object sender, RoutedEventArgs e)
    {
      if (triggerTreeView.SelectedItem is TriggerTreeViewNode parent)
      {
        if (TriggerStateManager.Instance.CreateTrigger(parent.SerializedData.Id, LabelNewTrigger, _currentCharacterId) is { } newNode)
        {
          parent.ChildNodes.Add(newNode);
          SelectNode(triggerTreeView, newNode.SerializedData.Id);
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

    private void PasteClick(object sender, RoutedEventArgs e)
    {
      var treeView = GetTreeViewFromMenu(sender);
      if (treeView == triggerTreeView)
      {
        HandlePaste(triggerTreeView, _triggerCopiedNode, _triggerCutNode);
        _triggerCopiedNode = null;
      }
      else if (treeView == overlayTreeView)
      {
        HandlePaste(overlayTreeView, _overlayCopiedNode, _overlayCutNode);
        _overlayCopiedNode = null;
      }

      void HandlePaste(SfTreeView tree, TriggerTreeViewNode copiedNode, bool isCutNode)
      {
        if (tree.SelectedItem is TriggerTreeViewNode node && copiedNode != null)
        {
          if (copiedNode.IsDir())
          {
            if (copiedNode.SerializedData.Parent != node.SerializedData.Id)
            {
              copiedNode.SerializedData.Parent = node.SerializedData.Id;
              TriggerStateManager.Instance.Update(copiedNode.SerializedData, true);
              RefreshTriggerNode();
            }
          }
          else
          {
            if (isCutNode)
            {
              Delete([copiedNode]);
            }

            TriggerStateManager.Instance.Copy(copiedNode.SerializedData, node.SerializedData);

            if (copiedNode.IsTrigger())
            {
              RefreshTriggerNode();
            }
            else if (copiedNode.IsOverlay())
            {
              RefreshOverlayNode();
            }
          }
        }
      }
    }

    private void Delete(List<TriggerTreeViewNode> nodes)
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

          TriggerStateManager.Instance.Delete(id);

          if (node.IsOverlay())
          {
            overlayDelete = true;
            TriggerManager.Instance.CloseOverlay(id);
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
        RefreshTriggerNode();
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

    private void ItemDropped(object sender, TreeViewItemDroppedEventArgs e)
    {
      if (e.TargetNode as TriggerTreeViewNode is { } target)
      {
        target = (target.IsDir() && e.DropPosition == DropPosition.DropAsChild) ? target : target.ParentNode as TriggerTreeViewNode;

        for (var i = 0; i < target?.ChildNodes.Count; i++)
        {
          if (target.ChildNodes[i] is TriggerTreeViewNode node)
          {
            if (target.SerializedData.Id != node.SerializedData.Parent || node.SerializedData.Index != i)
            {
              node.SerializedData.Parent = target.SerializedData.Id;
              node.SerializedData.Index = i;
              TriggerStateManager.Instance.Update(node.SerializedData);

              if (_shiftDown)
              {
                TriggerStateManager.Instance.SetStateFromParent(node.SerializedData.Parent, _currentCharacterId, node);
              }

              RefreshTriggerNode();
            }
          }
        }
      }
    }

    private void ItemEndEdit(object sender, TreeViewItemEndEditEventArgs e)
    {
      if (!e.Cancel && e.Node is TriggerTreeViewNode node && sender is SfTreeView treeView)
      {
        // delay because node still shows old value
        Dispatcher.InvokeAsync(() =>
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
            TriggerStateManager.Instance.Update(node.SerializedData);

            if (node.IsOverlay())
            {
              Application.Current.Resources["OverlayText-" + node.SerializedData.Id] = node.SerializedData.Name;
            }
          }
        }, DispatcherPriority.Normal);
      }
    }

    private void TriggerItemContextMenuOpening(object sender, ItemContextMenuOpeningEventArgs e)
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

        foreach (var overlay in TriggerStateManager.Instance.GetAllOverlays())
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

    private void CopySettingsClick(object sender, RoutedEventArgs e)
    {
      if (sender is MenuItem { Tag: string id })
      {
        TriggerStateManager.Instance.CopyState((TriggerTreeViewNode)triggerTreeView.SelectedItem, _currentCharacterId, id);
      }
    }

    private void SetPriorityClick(object sender, RoutedEventArgs e)
    {
      if (sender is MenuItem menuItem && int.TryParse(menuItem.Tag.ToString(), out var newPriority))
      {
        var selected = triggerTreeView.SelectedItems.Cast<TriggerTreeViewNode>().ToList();
        var anyFolders = selected.Any(node => node.IsDir());
        if (!anyFolders)
        {
          TriggerStateManager.Instance.AssignPriority(newPriority, selected.Select(treeView => treeView.SerializedData));
        }
        else
        {
          var msgDialog = new MessageWindow($"Are you sure? This will Set Priority {newPriority} to all selected\nTriggers and those in all sub folders.",
            Resource.ASSIGN_PRIORITY, MessageWindow.IconType.Warn, "Yes");
          msgDialog.ShowDialog();
          if (msgDialog.IsYes1Clicked)
          {
            TriggerStateManager.Instance.AssignPriority(newPriority, selected.Select(treeView => treeView.SerializedData));
          }
        }

        RefreshTriggerNode();
        SelectionChanged(triggerTreeView.SelectedItem as TriggerTreeViewNode);
        TriggerManager.Instance.TriggersUpdated();
      }
    }

    private void SetOverlay(object sender, bool remove = false)
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
              TriggerStateManager.Instance.AssignOverlay(id, selected.Select(treeView => treeView.SerializedData));
            }
            else
            {
              TriggerStateManager.Instance.UnassignOverlay(id, selected.Select(treeView => treeView.SerializedData));
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
                TriggerStateManager.Instance.AssignOverlay(id, selected.Select(treeView => treeView.SerializedData));
              }
              else
              {
                TriggerStateManager.Instance.UnassignOverlay(id, selected.Select(treeView => treeView.SerializedData));
              }
            }
          }
        }

        RefreshTriggerNode();
        SelectionChanged(triggerTreeView.SelectedItem as TriggerTreeViewNode);
        TriggerManager.Instance.TriggersUpdated();
      }
    }

    private void SelectionChanged(object sender, ItemSelectionChangedEventArgs e)
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

        SelectionChanged(node);
      }
    }

    private void SelectionChanged(TriggerTreeViewNode node)
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
            model = new TriggerPropertyModel { Node = data };
            TriggerUtil.Copy(model, node.SerializedData?.TriggerData);
          }
          else if (node.IsOverlay())
          {
            if (!isTimerOverlay)
            {
              model = new TextOverlayPropertyModel { Node = data };
              TriggerUtil.Copy(model, data?.OverlayData);
            }
            else
            {
              model = new TimerOverlayPropertyModel { Node = data };
              TriggerUtil.Copy(model, data?.OverlayData);
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
        TriggerManager.Instance.EventsSelectTrigger -= EventsSelectTrigger;
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

    private void FindKeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.Escape)
      {
        findTrigger.Text = Resource.TRIGGER_SEARCH_TEXT;
        findTrigger.FontStyle = FontStyles.Italic;
        triggerTreeView.Focus();
      }
      else if (e.Key == Key.Enter)
      {
        ExpandNextTrigger();
      }
    }

    private void FindTextChanged(object sender, TextChangedEventArgs e)
    {
      _findTimer?.Stop();
      _findTimer?.Start();
    }
  }
}