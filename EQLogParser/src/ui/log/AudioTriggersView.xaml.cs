using FontAwesome5;
using Syncfusion.UI.Xaml.TreeView;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
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

      if (ConfigUtil.IfSetOrElse("AudioTriggersWatchForGINA", false))
      {
        watchGina.IsChecked = true;
      }

      if (MainWindow.CurrentLogFile == null )
      {
        SetPlayer("Activate Triggers", "EQDisabledBrush", EFontAwesomeIcon.Solid_Play, false);
      }
      else
      {
        EventsLogLoadingComplete(this, true);
      }

      treeView.Nodes.Add(AudioTriggerManager.Instance.GetTreeView());
      AudioTriggerManager.Instance.EventsUpdateTree += EventsUpdateTree;
      (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete += EventsLogLoadingComplete;
    }

    private void EventsLogLoadingComplete(object sender, bool e)
    {
      if (AudioTriggerManager.Instance.IsActive())
      {
        SetPlayer("Deactivate Triggers", "EQStopForegroundBrush", EFontAwesomeIcon.Solid_Square);
      }
      else
      {
        SetPlayer("Activate Triggers", "EQMenuIconBrush", EFontAwesomeIcon.Solid_Play);
      }
    }

    private void EventsUpdateTree(object sender, bool e)
    {
      Dispatcher.InvokeAsync(() =>
      {
        treeView.Nodes.Clear();
        treeView.Nodes.Add(AudioTriggerManager.Instance.GetTreeView());
      });
    }

    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
      // one way to see if UI has been initialized
      if (startIcon?.Icon != FontAwesome5.EFontAwesomeIcon.None)
      {
        ConfigUtil.SetSetting("AudioTriggersWatchForGINA", watchGina.IsChecked.Value.ToString(CultureInfo.CurrentCulture));
      }
    }

    private void SetPlayer(string title, string brush, EFontAwesomeIcon icon, bool hitTest = true)
    {
      startIcon.Icon = icon;
      startIcon.SetResourceReference(ImageAwesome.ForegroundProperty, brush);
      titleLabel.SetResourceReference(Label.ForegroundProperty, brush);
      titleLabel.Content = title;
      startButton.IsHitTestVisible= hitTest;
    }

    private void PlayButtonClick(object sender, RoutedEventArgs e)
    {
      if (startIcon.Icon == EFontAwesomeIcon.Solid_Play)
      {
        SetPlayer("Deactivate Triggers", "EQStopForegroundBrush", EFontAwesomeIcon.Solid_Square);
        AudioTriggerManager.Instance.Start();
      }
      else
      {
        SetPlayer("Activate Triggers", "EQMenuIconBrush", EFontAwesomeIcon.Solid_Play);
        AudioTriggerManager.Instance.Stop();
      }
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

      var targetParent = target.ParentNode as AudioTriggerTreeViewNode;
      var sourceParent = source.ParentNode as AudioTriggerTreeViewNode;
      
      if (e.DropPosition == DropPosition.DropAbove)
      {
        sourceParent.SerializedData.Nodes.Remove(source.SerializedData);
        int index = targetParent.SerializedData.Nodes.IndexOf(target.SerializedData) - 1;
        index = (index >= 0 ? index : 0);
        targetParent.SerializedData.Nodes.Insert(index, source.SerializedData);
      }
      if (e.DropPosition == DropPosition.DropBelow)
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
      else if (e.DropPosition == DropPosition.DropAsChild)
      {
        sourceParent.SerializedData.Nodes.Remove(source.SerializedData);

        if (target.SerializedData.Nodes == null)
        {
          target.SerializedData.Nodes = new List<AudioTriggerData>();
        }

        target.SerializedData.Nodes.Add(source.SerializedData);
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
        parent.SerializedData.Nodes.Remove(node.SerializedData);
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
        thePropertyGrid.SelectedObject = node.IsTrigger ? node.SerializedData.TriggerData : null;
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

    private void SaveExpanded(List<AudioTriggerTreeViewNode> nodes)
    {
      foreach (var node in nodes)
      {
        node.SerializedData.IsExpanded = node.IsExpanded;
        
        if (!node.IsTrigger)
        {
          SaveExpanded(node.ChildNodes.Cast<AudioTriggerTreeViewNode>().ToList());
        }
      }
    }

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete -= EventsLogLoadingComplete;
        SaveExpanded(treeView.Nodes.Cast<AudioTriggerTreeViewNode>().ToList());
        AudioTriggerManager.Instance.EventsUpdateTree -= EventsUpdateTree;
        AudioTriggerManager.Instance.Update();
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
