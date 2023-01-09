using Syncfusion.UI.Xaml.TreeView;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for AudioTriggers.xaml
  /// </summary>
  public partial class AudioTriggersView : UserControl, IDisposable
  {
    public AudioTriggersView()
    {
      InitializeComponent();
      treeView.Nodes.Add(AudioTriggerManager.Instance.GetTreeView());
      AudioTriggerManager.Instance.EventsUpdateTree += EventsUpdateTree;
    }

    private void EventsUpdateTree(object sender, bool e)
    {
      Dispatcher.InvokeAsync(() =>
      {
        treeView.Nodes.Clear();
        treeView.Nodes.Add(AudioTriggerManager.Instance.GetTreeView());
      });
    }

    private void ItemDropping(object sender, TreeViewItemDroppingEventArgs e)
    {
      var target = e.TargetNode as AudioTriggerTreeViewNode;
      var source = e.DraggingNodes[0] as AudioTriggerTreeViewNode;

      if (target.Level == 0 && e.DropPosition != DropPosition.DropAsChild)
      {
        e.Handled= true;
        return;
      }

      if (target.IsTrigger && e.DropPosition == DropPosition.DropAsChild)
      {
        e.Handled = true;
        return;
      }

      if ((e.DropPosition == DropPosition.DropAbove || e.DropPosition == DropPosition.DropBelow) &&
        target.IsTrigger != source.IsTrigger)
      {
        e.Handled = true;
        return;
      }

      var targetParent = target.ParentNode as AudioTriggerTreeViewNode;
      var sourceParent = source.ParentNode as AudioTriggerTreeViewNode;
      
      if (e.DropPosition == DropPosition.DropAbove)
      {
        if (source.IsTrigger)
        {
          sourceParent.SerializedData.Triggers.Remove(source.TriggerData);
          int index = targetParent.SerializedData.Triggers.IndexOf(target.TriggerData) - 1;
          index = (index >= 0 ? index : 0);
          targetParent.SerializedData.Triggers.Insert(index, source.TriggerData);
        }
        else
        {
          sourceParent.SerializedData.Nodes.Remove(source.SerializedData);
          int index = targetParent.SerializedData.Nodes.IndexOf(target.SerializedData) - 1;
          index = (index >= 0 ? index : 0);
          targetParent.SerializedData.Nodes.Insert(index, source.SerializedData);
        }
      }
      if (e.DropPosition == DropPosition.DropBelow)
      {
        if (source.IsTrigger)
        {
          sourceParent.SerializedData.Triggers.Remove(source.TriggerData);
          int index = targetParent.SerializedData.Triggers.IndexOf(target.TriggerData) + 1;
          if (index >= targetParent.SerializedData.Triggers.Count)
          {
            targetParent.SerializedData.Triggers.Add(source.TriggerData);
          }
          else
          {
            targetParent.SerializedData.Triggers.Insert(index, source.TriggerData);
          }
        }
        else
        {
          sourceParent.SerializedData.Nodes.Remove(source.SerializedData);
          int index = targetParent.SerializedData.Nodes.IndexOf(target.SerializedData) + 1;
          if (index >= targetParent.SerializedData.Nodes.Count)
          {
            targetParent.SerializedData.Nodes.Add(source.SerializedData);
          }
          else
          {
            targetParent.SerializedData.Nodes.Insert(index, source.SerializedData);
          }
        }
      }
      else if (e.DropPosition == DropPosition.DropAsChild)
      {
        if (source.IsTrigger)
        {
          sourceParent.SerializedData.Triggers.Remove(source.TriggerData);
              
          if (target.SerializedData.Triggers == null)
          {
            target.SerializedData.Triggers = new List<AudioTrigger>();
          }

          target.SerializedData.Triggers.Add(source.TriggerData);
        }
        else
        {
          sourceParent.SerializedData.Nodes.Remove(source.SerializedData);

          if (target.SerializedData.Nodes == null)
          {
            target.SerializedData.Nodes = new List<AudioTriggerData>();
          }

          target.SerializedData.Nodes.Add(source.SerializedData);
        }
      }

      AudioTriggerManager.Instance.Update();
    }

    private void CreateNodeClick(object sender, RoutedEventArgs e)
    {
      if (e.Source is MenuItem item && item.DataContext is TreeViewItemContextMenuInfo info)
      {
        if (info.Node != null)
        {
          info.Node.ChildNodes.Add(new AudioTriggerTreeViewNode { Content = "New Node", IsTrigger = false });
          info.Node.IsExpanded = true;
        }
      }
    }

    private void CreateTriggerClick(object sender, RoutedEventArgs e)
    {
      if (e.Source is MenuItem item && item.DataContext is TreeViewItemContextMenuInfo info)
      {
        info.Node.ChildNodes.Add(new AudioTriggerTreeViewNode { Content = "New Trigger", IsTrigger = true });
        info.Node.IsExpanded = true;
      }
    }

    private void DeleteClick(object sender, RoutedEventArgs e)
    {
      if (e.Source is MenuItem item && item.DataContext is TreeViewItemContextMenuInfo info && info.Node.ParentNode is AudioTriggerTreeViewNode parent &&
        info.Node is AudioTriggerTreeViewNode node)
      {
        parent.ChildNodes.Remove(node);

        if (node.IsTrigger)
        {
          parent.SerializedData.Triggers.Remove(node.TriggerData);
        }
        else
        {
          parent.SerializedData.Nodes.Remove(node.SerializedData);
        }

        AudioTriggerManager.Instance.Update();
      }
    }

    private void RenameClick(object sender, RoutedEventArgs e)
    {
      if (e.Source is MenuItem item && item.DataContext is TreeViewItemContextMenuInfo info)
      {
        treeView.BeginEdit(info.Node);
      }
    }

    private void ItemEndEdit(object sender, TreeViewItemEndEditEventArgs e)
    {

    }

    private void ItemContextMenuOpening(object sender, ItemContextMenuOpeningEventArgs e)
    {
      if (e.MenuInfo.Node is AudioTriggerTreeViewNode node)
      {
        addFolderMenuItem.IsEnabled = false; // !node.IsTrigger;
        addTriggerMenuItem.IsEnabled = false; // (!node.IsTrigger && node.Level > 0);
        deleteTriggerMenuItem.IsEnabled = (node.Level > 0);
        renameMenuItem.IsEnabled = false; // (node.Level > 0);
      }
    }

    private void SelectionChanged(object sender, ItemSelectionChangedEventArgs e)
    {
      if (e.AddedItems.Count > 0 && e.AddedItems[0] is AudioTriggerTreeViewNode node)
      {
        thePropertyGrid.SelectedObject = node.IsTrigger ? node.TriggerData : null;
      }
    }

    private void NodeChecked(object sender, NodeCheckedEventArgs e)
    {
      if (e.Node is AudioTriggerTreeViewNode node && !node.IsTrigger)
      {
        node.SerializedData.IsEnabled = node.IsChecked;

        CheckParent(node);
        CheckChildren(node, node.IsChecked);
        AudioTriggerManager.Instance.Update();
      }
    }

    private void CheckChildren(AudioTriggerTreeViewNode node, bool? value)
    {
      foreach (var child in node.ChildNodes.Cast<AudioTriggerTreeViewNode>())
      {
        if (!child.IsTrigger)
        {
          child.SerializedData.IsEnabled = value;
          CheckChildren(child, value);
        }
      }
    }

    private void CheckParent(AudioTriggerTreeViewNode node)
    {
      if (node.ParentNode is AudioTriggerTreeViewNode parent)
      {
        parent.SerializedData.IsEnabled = parent.IsChecked;
        CheckParent(parent);
      }
    }

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        AudioTriggerManager.Instance.EventsUpdateTree -= EventsUpdateTree;
        treeView.Dispose();
        disposedValue = true;
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
