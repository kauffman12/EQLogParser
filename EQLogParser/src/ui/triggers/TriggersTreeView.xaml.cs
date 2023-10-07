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
  public partial class TriggersTreeView : UserControl, IDisposable
  {
    internal event Action<bool> ClosePreviewOverlaysEvent;
    internal event Action<Tuple<TriggerTreeViewNode, object>> TreeSelectionChangedEvent;
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private const string LABEL_NEW_TEXT_OVERLAY = "New Text Overlay";
    private const string LABEL_NEW_TIMER_OVERLAY = "New Timer Overlay";
    private const string LABEL_NEW_TRIGGER = "New Trigger";
    private const string LABEL_NEW_FOLDER = "New Folder";
    private TriggerTreeViewNode TriggerCopiedNode = null;
    private TriggerTreeViewNode OverlayCopiedNode = null;
    private bool TriggerCutNode = false;
    private bool OverlayCutNode = false;
    private string CurrentCharacterId = null;
    private Func<bool> IsCancelSelection = null;

    public TriggersTreeView()
    {
      InitializeComponent();

      SetupDragNDrop(triggerTreeView);
      SetupDragNDrop(overlayTreeView);
      TriggerManager.Instance.EventsSelectTrigger += EventsSelectTrigger;

      void SetupDragNDrop(SfTreeView treeView)
      {
        treeView.DragDropController = new TreeViewDragDropController();
        treeView.DragDropController.CanAutoExpand = true;
        treeView.DragDropController.AutoExpandDelay = new TimeSpan(0, 0, 1);
      }
    }

    internal void RefreshOverlays() => RefreshOverlayNode();

    internal void Init(string characterId, Func<bool> isCanceled, bool enable)
    {
      IsCancelSelection = isCanceled;
      overlayTreeView.Nodes.Add(TriggerStateManager.Instance.GetOverlayTreeView());
      EnableAndRefreshTriggers(enable, characterId);
    }

    internal void EnableAndRefreshTriggers(bool enable, string characterId)
    {
      CurrentCharacterId = characterId;
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

      RefreshTriggerNode();
    }

    private void CreateTextOverlayClick(object sender, RoutedEventArgs e) => CreateOverlay(true);
    private void CreateTimerOverlayClick(object sender, RoutedEventArgs e) => CreateOverlay(false);
    private void EventsSelectTrigger(Trigger e) => Dispatcher.InvokeAsync(() => SelectNode(triggerTreeView, e));
    private void NodeExpanded(object sender, NodeExpandedCollapsedEventArgs e) => TriggerStateManager.Instance.SetExpanded(e.Node as TriggerTreeViewNode);
    private void AssignOverlayClick(object sender, RoutedEventArgs e) => SetOverlay(sender);
    private void UnassignOverlayClick(object sender, RoutedEventArgs e) => SetOverlay(sender, true);
    private void SelectionChanging(object sender, ItemSelectionChangingEventArgs e) => IsCancelSelection();

    private void CloseOverlaysClick(object sender, RoutedEventArgs e)
    {
      TriggerManager.Instance.CloseOverlays();
      ClosePreviewOverlaysEvent?.Invoke(true);
      e.Handled = true;
    }

    private void CollapseAllClick(object sender, RoutedEventArgs e)
    {
      if (GetTreeViewFromMenu(sender) is SfTreeView treeView)
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
      if (GetTreeViewFromMenu(sender) is SfTreeView treeView)
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
      if (GetTreeViewFromMenu(sender) is SfTreeView treeView)
      {
        TriggerUtil.Export(treeView.SelectedItems?.Cast<TriggerTreeViewNode>());
      }
    }

    private void NodeChecked(object sender, NodeCheckedEventArgs e)
    {
      if (e.Node is TriggerTreeViewNode viewNode)
      {
        TriggerStateManager.Instance.SetState(CurrentCharacterId, viewNode);
        TriggerManager.Instance.TriggersUpdated();
      }
    }

    private void RenameClick(object sender, RoutedEventArgs e)
    {
      if (GetTreeViewFromMenu(sender) is SfTreeView treeView)
      {
        treeView.BeginEdit(treeView.SelectedItem as TriggerTreeViewNode);
      }
    }

    private void DeleteClick(object sender, RoutedEventArgs e)
    {
      if (GetTreeViewFromMenu(sender) is SfTreeView treeView)
      {
        TriggerCutNode = false;
        TriggerCopiedNode = null;
        Delete(treeView.SelectedItems?.Cast<TriggerTreeViewNode>().ToList());
      }
    }

    private void ImportClick(object sender, RoutedEventArgs e)
    {
      if (GetTreeViewFromMenu(sender) is SfTreeView treeView)
      {
        if (treeView.SelectedItem is TriggerTreeViewNode node)
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
    }

    private void RefreshTriggerNode()
    {
      triggerTreeView?.Nodes?.Clear();
      triggerTreeView?.Nodes?.Add(TriggerStateManager.Instance.GetTriggerTreeView(CurrentCharacterId));
    }

    private void RefreshOverlayNode()
    {
      if (overlayTreeView?.Nodes.Count > 0)
      {
        overlayTreeView.Nodes.Clear();
        overlayTreeView.Nodes.Add(TriggerStateManager.Instance.GetOverlayTreeView());
      }
    }

    private void SelectNode(SfTreeView treeView, object file)
    {
      if (file != null && IsCancelSelection != null && !IsCancelSelection())
      {
        if (treeView?.Nodes.Count > 0 && treeView.Nodes[0] is TriggerTreeViewNode node)
        {
          if (FindAndExpandNode(treeView, node, file) is TriggerTreeViewNode found)
          {
            treeView.SelectedItems?.Clear();
            treeView.SelectedItem = found;
            SelectionChanged(found);
          }
        }
      }
    }

    private TriggerTreeViewNode FindAndExpandNode(SfTreeView treeView, TriggerTreeViewNode node, object file)
    {
      if (node.SerializedData?.TriggerData == file || node.SerializedData?.OverlayData == file)
      {
        return node;
      }

      foreach (var child in node.ChildNodes.Cast<TriggerTreeViewNode>())
      {
        if (FindAndExpandNode(treeView, child, file) is TriggerTreeViewNode found && found != null)
        {
          treeView.ExpandNode(node);
          return found;
        }
      }

      return null;
    }

    private void CreateNodeClick(object sender, RoutedEventArgs e)
    {
      if (GetTreeViewFromMenu(sender) is SfTreeView treeView)
      {
        if (treeView.SelectedItem is TriggerTreeViewNode parent && parent.SerializedData?.Id is string id)
        {
          var newNode = TriggerStateManager.Instance.CreateFolder(id, LABEL_NEW_FOLDER);
          parent.ChildNodes.Add(newNode);
        }
      }
    }

    private void CreateOverlay(bool isTextOverlay)
    {
      if (overlayTreeView.SelectedItem is TriggerTreeViewNode parent)
      {
        var label = isTextOverlay ? LABEL_NEW_TEXT_OVERLAY : LABEL_NEW_TIMER_OVERLAY;
        if (TriggerStateManager.Instance.CreateOverlay(parent.SerializedData.Id, label, isTextOverlay) is TriggerTreeViewNode newNode)
        {
          parent.ChildNodes.Add(newNode);
          SelectNode(overlayTreeView, newNode.SerializedData.OverlayData);
        }
      }
    }

    private void CreateTriggerClick(object sender, RoutedEventArgs e)
    {
      if (triggerTreeView.SelectedItem is TriggerTreeViewNode parent)
      {
        if (TriggerStateManager.Instance.CreateTrigger(parent.SerializedData.Id, LABEL_NEW_TRIGGER) is TriggerTreeViewNode newNode)
        {
          parent.ChildNodes.Add(newNode);
          SelectNode(triggerTreeView, newNode.SerializedData.TriggerData);
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
          TriggerCopiedNode = node;
          TriggerCutNode = false;
        }
      }
      else if (treeView == overlayTreeView)
      {
        if (overlayTreeView.SelectedItem is TriggerTreeViewNode node)
        {
          OverlayCopiedNode = node;
          OverlayCutNode = false;
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
          TriggerCopiedNode = node;
          TriggerCutNode = true;
        }
      }
      else if (treeView == overlayTreeView)
      {
        if (overlayTreeView.SelectedItem is TriggerTreeViewNode node)
        {
          OverlayCopiedNode = node;
          OverlayCutNode = true;
        }
      }
    }

    private void PasteClick(object sender, RoutedEventArgs e)
    {
      var treeView = GetTreeViewFromMenu(sender);
      if (treeView == triggerTreeView)
      {
        HandlePaste(triggerTreeView, TriggerCopiedNode, TriggerCutNode);
        TriggerCopiedNode = null;
      }
      else if (treeView == overlayTreeView)
      {
        HandlePaste(overlayTreeView, OverlayCopiedNode, OverlayCutNode);
        OverlayCopiedNode = null;
      }

      void HandlePaste(SfTreeView treeView, TriggerTreeViewNode copiedNode, bool isCutNode)
      {
        if (treeView.SelectedItem is TriggerTreeViewNode node && copiedNode != null)
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
              Delete(new List<TriggerTreeViewNode> { copiedNode });
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
        if (node?.ParentNode is TriggerTreeViewNode parent && node.SerializedData.Id is string id)
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
      var target = e.TargetNode as TriggerTreeViewNode;

      if (e.DropPosition == DropPosition.None ||
        (target.Level == 0 && e.DropPosition != DropPosition.DropAsChild) ||
        (e.DropPosition == DropPosition.DropAsChild && !target.IsDir()))
      {
        e.Handled = true;
      }
    }

    private void ItemDropped(object sender, TreeViewItemDroppedEventArgs e)
    {
      var target = e.TargetNode as TriggerTreeViewNode;
      target = (target.IsDir() && e.DropPosition == DropPosition.DropAsChild) ? target : target.ParentNode as TriggerTreeViewNode;

      for (var i = 0; i < target.ChildNodes.Count; i++)
      {
        if (target.ChildNodes[i] is TriggerTreeViewNode node)
        {
          if (target.SerializedData.Id != node.SerializedData.Parent || node.SerializedData.Index != i)
          {
            node.SerializedData.Parent = target.SerializedData.Id;
            node.SerializedData.Index = i;
            TriggerStateManager.Instance.Update(node.SerializedData);
          }
        }
      }
    }

    private void ItemEndEdit(object sender, TreeViewItemEndEditEventArgs e)
    {
      if (!e.Cancel && e.Node is TriggerTreeViewNode node)
      {
        var previous = node.Content as string;
        // delay because node still shows old value
        Dispatcher.InvokeAsync(() =>
        {
          var content = node.Content as string;
          if (string.IsNullOrEmpty(content) || content.Trim().Length == 0)
          {
            node.Content = previous;
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
      var count = (triggerTreeView.SelectedItems != null) ? triggerTreeView.SelectedItems.Count : 0;


      if (node != null)
      {
        renameTriggerMenuItem.IsEnabled = node?.ParentNode != null;
        deleteTriggerMenuItem.IsEnabled = node?.ParentNode != null;
        importTriggerMenuItem.IsEnabled = node.IsDir() && count == 1;
        newTriggerMenuItem.IsEnabled = node.IsDir() && count == 1;
        copyTriggerItem.IsEnabled = !node.IsDir() && count == 1;
        cutTriggerItem.IsEnabled = node?.ParentNode != null && (copyTriggerItem.IsEnabled || (node.IsDir() && count == 1));
        pasteTriggerItem.IsEnabled = node.IsDir() && count == 1 && TriggerCopiedNode != null;
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

      importTriggerMenuItem.Header = importTriggerMenuItem.IsEnabled ? $"Import to Folder ({node.Content.ToString()})" : "Import";

      if (setPriorityMenuItem.IsEnabled)
      {
        UIElementUtil.ClearMenuEvents(setPriorityMenuItem.Items, SetPriorityClick);
      }

      setPriorityMenuItem.Items.Clear();

      for (var i = 1; i <= 5; i++)
      {
        var menuItem = new MenuItem { Header = "Priority " + i, Tag = i };
        if (i == 1)
        {
          var icon = new FontAwesome5.ImageAwesome { Icon = FontAwesome5.EFontAwesomeIcon.Solid_ArrowUp };
          icon.SetResourceReference(FontAwesome5.ImageAwesome.StyleProperty, "EQIconStyle");
          menuItem.Icon = icon;
        }
        else if (i == 5)
        {
          var icon = new FontAwesome5.ImageAwesome { Icon = FontAwesome5.EFontAwesomeIcon.Solid_ArrowDown };
          icon.SetResourceReference(FontAwesome5.ImageAwesome.StyleProperty, "EQIconStyle");
          menuItem.Icon = icon;
        }

        menuItem.Click += SetPriorityClick;
        setPriorityMenuItem.Items.Add(menuItem);
      }

      if (setTriggerMenuItem.Visibility == Visibility.Visible)
      {
        UIElementUtil.ClearMenuEvents(addTextOverlaysMenuItem.Items, AssignOverlayClick);
        UIElementUtil.ClearMenuEvents(addTimerOverlaysMenuItem.Items, AssignOverlayClick);
        UIElementUtil.ClearMenuEvents(removeTextOverlaysMenuItem.Items, UnassignOverlayClick);
        UIElementUtil.ClearMenuEvents(removeTimerOverlaysMenuItem.Items, UnassignOverlayClick);
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

      MenuItem CreateMenuItem(OTData overlay, RoutedEventHandler eventHandler)
      {
        var menuItem = new MenuItem { Header = overlay.Name, Tag = $"{overlay.Name}={overlay.Id}" };
        menuItem.Click += eventHandler;
        return menuItem;
      }
    }

    private void OverlayItemContextMenuOpening(object sender, ItemContextMenuOpeningEventArgs e)
    {
      var node = overlayTreeView.SelectedItem as TriggerTreeViewNode;
      var count = (overlayTreeView.SelectedItems != null) ? overlayTreeView.SelectedItems.Count : 0;


      if (node != null)
      {
        renameOverlayMenuItem.IsEnabled = node?.ParentNode != null;
        deleteOverlayMenuItem.IsEnabled = node?.ParentNode != null;
        importOverlayMenuItem.IsEnabled = node.IsDir() && count == 1;
        newOverlayMenuItem.IsEnabled = node.IsDir() && count == 1;
        copyOverlayItem.IsEnabled = !node.IsDir() && count == 1;
        pasteOverlayItem.IsEnabled = node.IsDir() && count == 1 && OverlayCopiedNode != null;
      }
      else
      {
        renameOverlayMenuItem.IsEnabled = false;
        deleteOverlayMenuItem.IsEnabled = false;
        importOverlayMenuItem.IsEnabled = false;
        copyOverlayItem.IsEnabled = false;
        pasteOverlayItem.IsEnabled = false;
      }

      importOverlayMenuItem.Header = importOverlayMenuItem.IsEnabled ? $"Import to Folder ({node.Content.ToString()})" : "Import";
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
            EQLogParser.Resource.ASSIGN_PRIORITY, MessageWindow.IconType.Warn, "Yes");
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
        string name;
        string id;
        var selected = triggerTreeView.SelectedItems.Cast<TriggerTreeViewNode>().ToList();
        var anyFolders = selected.Any(node => node.IsDir());

        if (menuItem.Tag is string overlayTag && overlayTag.Split('=') is string[] overlayData && overlayData.Length == 2)
        {
          name = overlayData[0];
          id = overlayData[1];

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
              EQLogParser.Resource.ASSIGN_OVERLAY, MessageWindow.IconType.Warn, "Yes");
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
        if (sender == triggerTreeView)
        {
          overlayTreeView.SelectedItems?.Clear();
        }
        else if (sender == overlayTreeView)
        {
          triggerTreeView.SelectedItems?.Clear();
        }

        SelectionChanged(node);
      }
    }

    private void SelectionChanged(TriggerTreeViewNode node)
    {
      dynamic model = null;
      var isTimerOverlay = node?.SerializedData?.OverlayData?.IsTimerOverlay == true;

      if (node.IsTrigger() || node.IsOverlay())
      {
        var data = node.SerializedData;
        if (node.IsTrigger())
        {
          model = new TriggerPropertyModel { Node = data };
          TriggerUtil.Copy(model, node.SerializedData.TriggerData);
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

      TreeSelectionChangedEvent?.Invoke(Tuple.Create(node, model as object));
    }

    private SfTreeView GetTreeViewFromMenu(object sender)
    {
      if (sender is MenuItem menuItem && menuItem.DataContext is TreeViewItemContextMenuInfo info)
      {
        return info.TreeView;
      }

      return null;
    }

    private void TreeViewPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
      if (sender is SfTreeView treeView)
      {
        if (e.OriginalSource is FrameworkElement element && element.DataContext is TriggerTreeViewNode node)
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
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        disposedValue = true;
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
      // TODO: uncomment the following line if the finalizer is overridden above.
      GC.SuppressFinalize(this);
    }
    #endregion
  }
}