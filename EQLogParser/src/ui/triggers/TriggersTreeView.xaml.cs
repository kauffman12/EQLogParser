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
    internal event Action<Tuple<TriggerTreeViewNode, object>> TreeSelectionChangedEvent;
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private const string LABEL_NEW_TEXT_OVERLAY = "New Text Overlay";
    private const string LABEL_NEW_TIMER_OVERLAY = "New Timer Overlay";
    private const string LABEL_NEW_TRIGGER = "New Trigger";
    private const string LABEL_NEW_FOLDER = "New Folder";
    private TriggerTreeViewNode CopiedNode = null;
    private bool CutNode = false;
    private string CurrentCharacterId = null;
    private Func<bool> IsCancelSelection = null;

    public TriggersTreeView()
    {
      InitializeComponent();

      treeView.DragDropController = new TreeViewDragDropController();
      treeView.DragDropController.CanAutoExpand = true;
      treeView.DragDropController.AutoExpandDelay = new TimeSpan(0, 0, 1);
      TriggerManager.Instance.EventsSelectTrigger += EventsSelectTrigger;
    }

    internal void RefreshOverlays() => RefreshOverlayNode();

    internal void Init(string characterId, Func<bool> isCanceled)
    {
      CurrentCharacterId = characterId;
      IsCancelSelection = isCanceled;
      treeView.Nodes.Add(TriggerStateManager.Instance.GetTriggerTreeView(CurrentCharacterId));
      treeView.Nodes.Add(TriggerStateManager.Instance.GetOverlayTreeView());
    }

    internal void EnableAndRefreshTriggers(bool enable)
    {
      treeView.IsEnabled = enable;
      RefreshTriggerNode();
    }

    private void CloseOverlaysClick(object sender, RoutedEventArgs e) => TriggerManager.Instance.CloseOverlays();
    private void CreateTextOverlayClick(object sender, RoutedEventArgs e) => CreateOverlay(true);
    private void CreateTimerOverlayClick(object sender, RoutedEventArgs e) => CreateOverlay(false);
    private void ExportClick(object sender, RoutedEventArgs e) => TriggerUtil.Export(treeView?.SelectedItems?.Cast<TriggerTreeViewNode>());
    private void EventsSelectTrigger(Trigger e) => Dispatcher.InvokeAsync(() => SelectNode(e));
    private void NodeExpanded(object sender, NodeExpandedCollapsedEventArgs e) => TriggerStateManager.Instance.SetExpanded(e.Node as TriggerTreeViewNode);
    private void RenameClick(object sender, RoutedEventArgs e) => treeView?.BeginEdit(treeView.SelectedItem as TriggerTreeViewNode);
    private void AddOverlayClick(object sender, RoutedEventArgs e) => SetOverlay(sender);
    private void RemoveOverlayClick(object sender, RoutedEventArgs e) => SetOverlay(sender, true);
    private void SelectionChanging(object sender, ItemSelectionChangingEventArgs e) => IsCancelSelection();

    private void CollapseAllClick(object sender, RoutedEventArgs e)
    {
      treeView.CollapseAll();
      TriggerStateManager.Instance.SetAllExpanded(false);
    }

    private void ExpandAllClick(object sender, RoutedEventArgs e)
    {
      treeView.ExpandAll();
      TriggerStateManager.Instance.SetAllExpanded(true);
    }

    private void NodeChecked(object sender, NodeCheckedEventArgs e)
    {
      if (e.Node is TriggerTreeViewNode viewNode)
      {
        TriggerStateManager.Instance.SetState(CurrentCharacterId, viewNode);
        TriggerManager.Instance.TriggersUpdated();
      }
    }

    private void DeleteClick(object sender, RoutedEventArgs e)
    {
      CutNode = false;
      CopiedNode = null;
      Delete(treeView.SelectedItems?.Cast<TriggerTreeViewNode>().ToList());
    }

    private void ImportClick(object sender, RoutedEventArgs e)
    {
      if (treeView?.SelectedItem is TriggerTreeViewNode node)
      {
        var found = node;
        while (found.ParentNode != null)
        {
          found = found.ParentNode as TriggerTreeViewNode;
        }

        if (treeView.Nodes[0] == found)
        {
          TriggerUtil.ImportTriggers(node.SerializedData);
          RefreshTriggerNode();
        }
        else if (treeView.Nodes[1] == found)
        {
          TriggerUtil.ImportOverlays(node.SerializedData);
          RefreshOverlayNode();
        }
      }
    }

    private void RefreshTriggerNode()
    {
      if (treeView.Nodes.Count == 2)
      {
        treeView.Nodes.Remove(treeView.Nodes[0]);
        treeView.Nodes.Insert(0, TriggerStateManager.Instance.GetTriggerTreeView(CurrentCharacterId));
      }
    }

    private void RefreshOverlayNode()
    {
      if (treeView.Nodes.Count == 2)
      {
        treeView.Nodes.Remove(treeView.Nodes[1]);
        treeView.Nodes.Add(TriggerStateManager.Instance.GetOverlayTreeView());
      }
    }

    private void SelectNode(object file)
    {
      if (file != null && IsCancelSelection != null && !IsCancelSelection())
      {
        var node = (file is Trigger ? treeView.Nodes[0] : treeView.Nodes[1]) as TriggerTreeViewNode;
        if (FindAndExpandNode(node, file) is TriggerTreeViewNode found)
        {
          treeView.SelectedItems?.Clear();
          treeView.SelectedItem = found;
          SelectionChanged(found);
        }
      }
    }

    private TriggerTreeViewNode FindAndExpandNode(TriggerTreeViewNode node, object file)
    {
      if (node.SerializedData?.TriggerData == file || node.SerializedData?.OverlayData == file)
      {
        return node;
      }

      foreach (var child in node.ChildNodes.Cast<TriggerTreeViewNode>())
      {
        if (FindAndExpandNode(child, file) is TriggerTreeViewNode found && found != null)
        {
          treeView.ExpandNode(node);
          return found;
        }
      }

      return null;
    }

    private void CreateNodeClick(object sender, RoutedEventArgs e)
    {
      if (treeView.SelectedItem is TriggerTreeViewNode parent && parent.SerializedData?.Id is string id)
      {
        var newNode = TriggerStateManager.Instance.CreateFolder(id, LABEL_NEW_FOLDER);
        parent.ChildNodes.Add(newNode);
      }
    }

    private void CreateOverlay(bool isTextOverlay)
    {
      if (treeView.SelectedItem is TriggerTreeViewNode parent)
      {
        var label = isTextOverlay ? LABEL_NEW_TEXT_OVERLAY : LABEL_NEW_TIMER_OVERLAY;
        if (TriggerStateManager.Instance.CreateOverlay(parent.SerializedData.Id, label, isTextOverlay) is TriggerTreeViewNode newNode)
        {
          parent.ChildNodes.Add(newNode);
          SelectNode(newNode.SerializedData.OverlayData);
        }
      }
    }

    private void CreateTriggerClick(object sender, RoutedEventArgs e)
    {
      if (treeView.SelectedItem is TriggerTreeViewNode parent)
      {
        if (TriggerStateManager.Instance.CreateTrigger(parent.SerializedData.Id, LABEL_NEW_TRIGGER) is TriggerTreeViewNode newNode)
        {
          parent.ChildNodes.Add(newNode);
          SelectNode(newNode.SerializedData.TriggerData);
        }
      }
    }

    private void CopyClick(object sender, RoutedEventArgs e)
    {
      if (treeView.SelectedItem != null && treeView.SelectedItem is TriggerTreeViewNode node)
      {
        CopiedNode = node;
        CutNode = false;
      }
    }

    private void CutClick(object sender, RoutedEventArgs e)
    {
      if (treeView.SelectedItem != null && treeView.SelectedItem is TriggerTreeViewNode node)
      {
        CopiedNode = node;
        CutNode = true;
      }
    }

    private void PasteClick(object sender, RoutedEventArgs e)
    {
      if (treeView.SelectedItem is TriggerTreeViewNode node && CopiedNode != null)
      {
        if (CopiedNode.IsDir())
        {
          if (CopiedNode.SerializedData.Parent != node.SerializedData.Id)
          {
            CopiedNode.SerializedData.Parent = node.SerializedData.Id;
            TriggerStateManager.Instance.Update(CopiedNode.SerializedData, true);
            RefreshTriggerNode();
          }
        }
        else
        {
          if (CutNode)
          {
            Delete(new List<TriggerTreeViewNode> { CopiedNode });
          }

          TriggerStateManager.Instance.Copy(CopiedNode.SerializedData, node.SerializedData);

          if (CopiedNode.IsTrigger())
          {
            RefreshTriggerNode();
          }
          else if (CopiedNode.IsOverlay())
          {
            RefreshOverlayNode();
          }
        }

        CopiedNode = null;
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
          TriggerStateManager.Instance.Delete(id);

          if (node.IsOverlay())
          {
            overlayDelete = true;
            TriggerManager.Instance.CloseOverlay(id);
          }
          else if (node.IsTrigger() && node.IsChecked == true)
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
      else
      {
        var list = e.DraggingNodes.Cast<TriggerTreeViewNode>().ToList();
        if ((target == treeView.Nodes[1] || target.ParentNode == treeView.Nodes[1]) && list.Any(item => !item.IsOverlay()))
        {
          e.Handled = true;
        }
        else if (target != treeView.Nodes[1] && target.ParentNode != treeView.Nodes[1] && list.Any(item => item.IsOverlay()))
        {
          e.Handled = true;
        }
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

    private void ItemContextMenuOpening(object sender, ItemContextMenuOpeningEventArgs e)
    {
      var node = treeView.SelectedItem as TriggerTreeViewNode;
      var count = (treeView.SelectedItems != null) ? treeView.SelectedItems.Count : 0;


      if (node != null)
      {
        var anyTriggers = treeView.SelectedItems.Cast<TriggerTreeViewNode>().Any(node => !node.IsOverlay() && node != treeView.Nodes[1]);
        var anyOverlays = treeView.SelectedItems.Cast<TriggerTreeViewNode>().Any(node => node.IsOverlay() || node == treeView.Nodes[1]);
        setTriggerMenuItem.Visibility = anyTriggers ? Visibility.Visible : Visibility.Collapsed;
        exportMenuItem.IsEnabled = !(anyTriggers && anyOverlays);
        deleteTriggerMenuItem.IsEnabled = (node != treeView.Nodes[0] && node != treeView.Nodes[1]) || count > 1;
        renameMenuItem.IsEnabled = node != treeView.Nodes[0] && node != treeView.Nodes[1] && count == 1;
        importMenuItem.IsEnabled = node.IsDir() && count == 1;
        newMenuItem.IsEnabled = node.IsDir() && count == 1;
        copyItem.IsEnabled = !node.IsDir() && count == 1;
        cutItem.IsEnabled = copyItem.IsEnabled || (node.IsDir() && node != treeView.Nodes[1] && node != treeView.Nodes[0] && count == 1);
        pasteItem.IsEnabled = node.IsDir() && count == 1 && CopiedNode != null &&
          ((CopiedNode.IsOverlay() && node == treeView.Nodes[1]) || (node != treeView.Nodes[1]));
      }
      else
      {
        setTriggerMenuItem.Visibility = Visibility.Collapsed;
        deleteTriggerMenuItem.IsEnabled = false;
        renameMenuItem.IsEnabled = false;
        importMenuItem.IsEnabled = false;
        exportMenuItem.IsEnabled = false;
        newMenuItem.IsEnabled = false;
        copyItem.IsEnabled = false;
        cutItem.IsEnabled = false;
        pasteItem.IsEnabled = false;
      }

      importMenuItem.Header = importMenuItem.IsEnabled ? "Import to Folder (" + node.Content.ToString() + ")" : "Import";

      if (newMenuItem.IsEnabled)
      {
        newFolder.Visibility = node == treeView.Nodes[1] ? Visibility.Collapsed : Visibility.Visible;
        newTrigger.Visibility = node == treeView.Nodes[1] ? Visibility.Collapsed : Visibility.Visible;
        newTimerOverlay.Visibility = node == treeView.Nodes[1] ? Visibility.Visible : Visibility.Collapsed;
        newTextOverlay.Visibility = node == treeView.Nodes[1] ? Visibility.Visible : Visibility.Collapsed;
      }

      if (setPriorityMenuItem.IsEnabled)
      {
        UIElementUtil.ClearMenuEvents(setPriorityMenuItem.Items, SetPriorityClick);
      }

      setPriorityMenuItem.Items.Clear();

      for (var i = 1; i <= 5; i++)
      {
        var menuItem = new MenuItem { Header = "Priority " + i, Tag = i };
        menuItem.Click += SetPriorityClick;
        setPriorityMenuItem.Items.Add(menuItem);
      }

      if (setTriggerMenuItem.Visibility == Visibility.Visible)
      {
        UIElementUtil.ClearMenuEvents(addTextOverlaysMenuItem.Items, AddOverlayClick);
        UIElementUtil.ClearMenuEvents(addTimerOverlaysMenuItem.Items, AddOverlayClick);
        UIElementUtil.ClearMenuEvents(removeTextOverlaysMenuItem.Items, RemoveOverlayClick);
        UIElementUtil.ClearMenuEvents(removeTimerOverlaysMenuItem.Items, RemoveOverlayClick);
        addTextOverlaysMenuItem.Items.Clear();
        addTimerOverlaysMenuItem.Items.Clear();
        removeTextOverlaysMenuItem.Items.Clear();
        removeTimerOverlaysMenuItem.Items.Clear();

        foreach (var overlay in TriggerStateManager.Instance.GetAllOverlays())
        {
          var addMenuItem = CreateMenuItem(overlay, AddOverlayClick);
          var removeMenuItem = CreateMenuItem(overlay, RemoveOverlayClick);
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

    private void SetPriorityClick(object sender, RoutedEventArgs e)
    {
      if (sender is MenuItem menuItem && int.TryParse(menuItem.Tag.ToString(), out var newPriority))
      {
        var selected = treeView.SelectedItems.Cast<TriggerTreeViewNode>().ToList();
        var anyFolders = selected.Any(node => node.IsDir() && node != treeView.Nodes[1]);
        if (!anyFolders)
        {
          TriggerStateManager.Instance.AssignPriority(newPriority, selected.Select(treeView => treeView.SerializedData));
        }
        else
        {
          var msgDialog = new MessageWindow($"Are you sure? This will Set Priority {newPriority} to all selected Triggers and those in all sub folders.",
            EQLogParser.Resource.ASSIGN_PRIORITY, MessageWindow.IconType.Warn, "Yes");
          msgDialog.ShowDialog();
          if (msgDialog.IsYes1Clicked)
          {
            TriggerStateManager.Instance.AssignPriority(newPriority, selected.Select(treeView => treeView.SerializedData));
          }
        }

        RefreshTriggerNode();
        SelectionChanged(treeView.SelectedItem as TriggerTreeViewNode);
        TriggerManager.Instance.TriggersUpdated();
      }
    }

    private void SetOverlay(object sender, bool remove = false)
    {
      if (sender is MenuItem menuItem)
      {
        string name;
        string id;
        var selected = treeView.SelectedItems.Cast<TriggerTreeViewNode>().ToList();
        var anyFolders = selected.Any(node => node.IsDir() && node != treeView.Nodes[1]);

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
            var msgDialog = new MessageWindow($"Are you sure? This will {action} {name} from all selected Triggers and those in all sub folders.",
              EQLogParser.Resource.ASSIGN_OVERLAY, MessageWindow.IconType.Question, "Yes");
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
        SelectionChanged(treeView.SelectedItem as TriggerTreeViewNode);
        TriggerManager.Instance.TriggersUpdated();
      }
    }

    private void SelectionChanged(object sender, ItemSelectionChangedEventArgs e)
    {
      if (e.AddedItems.Count > 0 && e.AddedItems[0] is TriggerTreeViewNode node)
      {
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

    private void TreeViewPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
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

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        disposedValue = true;
        TriggerManager.Instance.EventsSelectTrigger -= EventsSelectTrigger;
        treeView?.DragDropController.Dispose();
        treeView?.Dispose();
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