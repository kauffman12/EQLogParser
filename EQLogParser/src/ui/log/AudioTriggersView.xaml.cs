using FontAwesome5;
using Microsoft.Win32;
using Syncfusion.Data.Extensions;
using Syncfusion.UI.Xaml.TreeView;
using Syncfusion.UI.Xaml.TreeView.Helpers;
using Syncfusion.Windows.PropertyGrid;
using Syncfusion.Windows.Shared;
using Syncfusion.Windows.Tools.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
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
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private const string LABEL_NEW_TRIGGER = "New Trigger";
    private const string LABEL_NEW_FOLDER = "New Folder";

    public AudioTriggersView()
    {
      InitializeComponent();

      if (ConfigUtil.IfSetOrElse("AudioTriggersWatchForGINA", false))
      {
        watchGina.IsChecked = true;
      }

      if (MainWindow.CurrentLogFile == null)
      {
        SetPlayer("Activate Triggers", "EQDisabledBrush", EFontAwesomeIcon.Solid_Play, false);
      }
      else
      {
        EventsLogLoadingComplete(this, true);
      }

      treeView.DragDropController = new TreeViewDragDropController();
      treeView.DragDropController.CanAutoExpand = true;
      treeView.DragDropController.AutoExpandDelay = new TimeSpan(0, 0, 1);

      CustomEditor priorityEditor = new CustomEditor();
      priorityEditor.Editor = new RangeEditor(1, 5);
      priorityEditor.Properties.Add("Priority");
      thePropertyGrid.CustomEditorCollection.Add(priorityEditor);

      CustomEditor commentsEditor = new CustomEditor();
      commentsEditor.Editor = new WrapTextEditor();
      commentsEditor.Properties.Add("Comments");
      commentsEditor.Properties.Add("Pattern");
      commentsEditor.Properties.Add("EndEarlyPattern");
      thePropertyGrid.CustomEditorCollection.Add(commentsEditor);

      CustomEditor timeEditor = new CustomEditor();
      timeEditor.Editor = new RangeEditor(0, 60);
      timeEditor.Properties.Add("Seconds");
      timeEditor.Properties.Add("Minutes");
      thePropertyGrid.CustomEditorCollection.Add(timeEditor);

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

    private void ImportClick(object sender, RoutedEventArgs e)
    {
      if (e.Source is MenuItem item && item.DataContext is TreeViewItemContextMenuInfo info && info.Node is AudioTriggerTreeViewNode node)
      {
        try
        {
          // WPF doesn't have its own file chooser so use Win32 Version
          OpenFileDialog dialog = new OpenFileDialog
          {
            // filter to txt files
            DefaultExt = ".scf.gz",
            Filter = "GINA Package File (*.gtp) | *.gtp"
          };

          // show dialog and read result
          if (dialog.ShowDialog().Value)
          {
            // limit to 100 megs just incase
            var fileInfo = new FileInfo(dialog.FileName);
            if (fileInfo.Exists && fileInfo.Length < 100000000)
            {
              var data = new byte[fileInfo.Length];
              fileInfo.OpenRead().Read(data);
              GINAXmlParser.Import(data, node.SerializedData);
            }
          }
        }
        catch (Exception ex)
        {
          new MessageWindow("Problem Importing from GINA Package File. Check Error Log for details.", EQLogParser.Resource.IMPORT_ERROR).ShowDialog();
          LOG.Error("Import Failure", ex);
        }
      }
    }

    private void SetPlayer(string title, string brush, EFontAwesomeIcon icon, bool hitTest = true)
    {
      startIcon.Icon = icon;
      startIcon.SetResourceReference(ImageAwesome.ForegroundProperty, brush);
      titleLabel.SetResourceReference(Label.ForegroundProperty, brush);
      titleLabel.Content = title;
      startButton.IsHitTestVisible = hitTest;
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
        e.Handled = true;
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
      if (e.Source is MenuItem item && item.DataContext is TreeViewItemContextMenuInfo info && info.Node is AudioTriggerTreeViewNode node)
      {
        var newNode = new AudioTriggerTreeViewNode { Content = LABEL_NEW_FOLDER, IsTrigger = false };
        newNode.SerializedData = new AudioTriggerData { Name = LABEL_NEW_FOLDER };

        if (node.SerializedData.Nodes == null)
        {
          node.SerializedData.Nodes = new List<AudioTriggerData>();
        }

        node.SerializedData.Nodes.Add(newNode.SerializedData);
        node.ChildNodes.Add(newNode);
      }
    }

    private void CreateTriggerClick(object sender, RoutedEventArgs e)
    {
      if (e.Source is MenuItem item && item.DataContext is TreeViewItemContextMenuInfo info && info.Node is AudioTriggerTreeViewNode node)
      {
        var newNode = new AudioTriggerTreeViewNode { Content = LABEL_NEW_TRIGGER, IsTrigger = true };
        newNode.SerializedData = new AudioTriggerData { Name = LABEL_NEW_TRIGGER, TriggerData = new AudioTrigger { Name = LABEL_NEW_TRIGGER } };

        if (node.SerializedData.Nodes == null)
        {
          node.SerializedData.Nodes = new List<AudioTriggerData>();
        }

        node.SerializedData.Nodes.Add(newNode.SerializedData);

        // copy entire node for some reason
        var newNodeList = node.ChildNodes.Where(node => node != null).ToList();
        var copyNode = new AudioTriggerTreeViewNode { Content = node.Content, IsTrigger = false, SerializedData = node.SerializedData, 
          IsChecked = node.IsChecked, IsExpanded = true };
        newNodeList.ForEach(node => copyNode.ChildNodes.Add(node));
        copyNode.ChildNodes.Add(newNode);

        var parent = node.ParentNode as AudioTriggerTreeViewNode;
        var index = parent.ChildNodes.IndexOf(node);
        parent.ChildNodes.Remove(node);
        parent.ChildNodes.Insert(index, copyNode);
        AudioTriggerManager.Instance.Update();
      }
    }

    private void DeleteClick(object sender, RoutedEventArgs e)
    {
      if (e.Source is MenuItem item && item.DataContext is TreeViewItemContextMenuInfo info && info.Node.ParentNode is AudioTriggerTreeViewNode parent &&
        info.Node is AudioTriggerTreeViewNode node)
      {
        parent.ChildNodes.Remove(node);
        parent.SerializedData.Nodes.Remove(node.SerializedData);
        thePropertyGrid.SelectedObject = null;
        thePropertyGrid.IsEnabled = false;
        thePropertyGrid.DescriptionPanelVisibility = Visibility.Collapsed;
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
      if (!e.Cancel && e.Node is AudioTriggerTreeViewNode node)
      {
        // delay because node still shows old value
        Dispatcher.InvokeAsync(() =>
        {
          node.SerializedData.Name = node.Content as string;
          if (node.IsTrigger && node.SerializedData.TriggerData != null)
          {
            node.SerializedData.TriggerData.Name = node.Content as string;
          }

          AudioTriggerManager.Instance.Update(false);
        }, System.Windows.Threading.DispatcherPriority.Background);
      }
    }

    private void ItemContextMenuOpening(object sender, ItemContextMenuOpeningEventArgs e)
    {
      if (e.MenuInfo.Node is AudioTriggerTreeViewNode node)
      {
        deleteTriggerMenuItem.IsEnabled = (node.Level > 0);
        renameMenuItem.IsEnabled = (node.Level > 0);
        newMenuItem.Visibility = node.IsTrigger ? Visibility.Collapsed : Visibility.Visible;
        newSeparator.Visibility = node.IsTrigger ? Visibility.Collapsed : Visibility.Visible;
        importMenuItem.Visibility = node.IsTrigger ? Visibility.Collapsed : Visibility.Visible;
        importSeparator.Visibility = node.IsTrigger ? Visibility.Collapsed : Visibility.Visible;
      }
    }

    private void SelectionChanged(object sender, ItemSelectionChangedEventArgs e)
    {
      if (e.AddedItems.Count > 0 && e.AddedItems[0] is AudioTriggerTreeViewNode node)
      {
        SelectionChanged(node);
      }
    }

    private void SelectionChanged(AudioTriggerTreeViewNode node)
    {
      AudioTriggerPropertyModel model = null;

      if (node.IsTrigger)
      {
        model = new AudioTriggerPropertyModel { Original = node.SerializedData.TriggerData };
        AudioTriggerUtil.Copy(model, node.SerializedData.TriggerData);
        saveButton.IsEnabled = false;
        cancelButton.IsEnabled = false;
      }

      thePropertyGrid.SelectedObject = model;
      thePropertyGrid.IsEnabled = (thePropertyGrid.SelectedObject != null);
      thePropertyGrid.DescriptionPanelVisibility = node.IsTrigger ? Visibility.Visible : Visibility.Collapsed;
      buttonPanel.Visibility = node.IsTrigger ? Visibility.Visible : Visibility.Collapsed;
      EnableCategory(timerDurationItem.CategoryName, node.IsTrigger ? node.SerializedData.TriggerData.EnableTimer : false);
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

    private void ValueChanged(object sender, ValueChangedEventArgs args)
    {
      if (args.Property.Name != errorsItem.PropertyName && args.Property.Name != evalTimeItem.PropertyName &&
        args.Property.SelectedObject is AudioTrigger trigger)
      {
        var collection = thePropertyGrid.Properties.ToObservableCollection();
        var errorsProp = FindProperty(collection, errorsItem.PropertyName);
        var longestProp = FindProperty(collection, evalTimeItem.PropertyName);

        if (trigger.UseRegex)
        {
          bool isValid = TextFormatUtils.IsValidRegex(trigger.Pattern);
          if (trigger.Errors != "None" && isValid)
          {
            trigger.Errors = "None";
            errorsProp.Value = "None";
          }
          else if (trigger.Errors == "None" && !isValid)
          {
            trigger.Errors = "Invalid Regex";
            errorsProp.Value = "Invalid Regex";
          }
        }
        else if (trigger.Errors != "None")
        {
          trigger.Errors = "None";
          errorsProp.Value = "None";
        }

        if (args.Property.Name == patternItem.PropertyName || args.Property.Name == useRegexItem.PropertyName)
        {
          trigger.LongestEvalTime = -1;
          longestProp.Value = -1;
        }
        else if (args.Property.Name == enableTimerItem.PropertyName)
        {
          EnableCategory(timerDurationItem.CategoryName, (bool)args.Property.Value);
        }

        saveButton.IsEnabled = (trigger.Errors == "None");
        cancelButton.IsEnabled = true;
      }
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
      if (thePropertyGrid.SelectedObject is AudioTriggerPropertyModel model)
      {
        AudioTriggerUtil.Copy(model.Original, model);
      }

      cancelButton.IsEnabled = false;
      saveButton.IsEnabled = false;
      AudioTriggerManager.Instance.Update();
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
      if (thePropertyGrid.SelectedObject is AudioTriggerPropertyModel model)
      {
        AudioTriggerUtil.Copy(model, model.Original);
        thePropertyGrid.RefreshPropertygrid();
        EnableCategory(timerDurationItem.CategoryName, model.Original.EnableTimer);
      }

      cancelButton.IsEnabled = false;
      saveButton.IsEnabled = false;
    }

    private new void PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
      if (e.OriginalSource is FrameworkElement element && element.DataContext is AudioTriggerTreeViewNode node)
      {
        treeView.SelectedItems?.Clear();
        treeView.SelectedItem = node;
        SelectionChanged(node);
      }
    }

    private void EnableCategory(string category, bool isEnabled)
    {
      foreach (var item in thePropertyGrid.Items)
      {
        if (item.CategoryName == category)
        {
          item.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
        }
      }

      thePropertyGrid.RefreshPropertygrid();
    }

    private PropertyItem FindProperty(ObservableCollection<object> collection, string name)
    {
      foreach (var prop in collection)
      {
        if (prop is PropertyItem item)
        {
          if (item.Name == name)
          {
            return item;
          }
        }
        else if (prop is PropertyCategoryViewItemCollection sub && FindProperty(sub.Properties, name) is PropertyItem found)
        {
          return found;
        }
      }

      return null;
    }

    internal class AudioTriggerPropertyModel : AudioTrigger
    {
      public AudioTrigger Original { get; set; }
    }

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        SaveExpanded(treeView.Nodes.Cast<AudioTriggerTreeViewNode>().ToList());
        (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete -= EventsLogLoadingComplete;
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
