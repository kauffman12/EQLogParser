using FontAwesome5;
using Microsoft.Win32;
using Syncfusion.Data.Extensions;
using Syncfusion.UI.Xaml.TreeView;
using Syncfusion.Windows.PropertyGrid;
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
  /// Interaction logic for TriggersView.xaml
  /// </summary>
  public partial class TriggersView : UserControl, IDisposable
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private const string LABEL_NEW_TRIGGER = "New Trigger";
    private const string LABEL_NEW_FOLDER = "New Folder";
    private WrapTextEditor ErrorEditor;
    private TimerResetEditor TimerResetOptions;

    public TriggersView()
    {
      InitializeComponent();

      if (ConfigUtil.IfSetOrElse("TriggersWatchForGINA", false))
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

      var priorityEditor = new CustomEditor();
      priorityEditor.Editor = new RangeEditor(1, 5);
      priorityEditor.Properties.Add("Priority");
      thePropertyGrid.CustomEditorCollection.Add(priorityEditor);

      var textWrapEditor = new CustomEditor();
      textWrapEditor.Editor = new WrapTextEditor();
      textWrapEditor.Properties.Add("Comments");
      textWrapEditor.Properties.Add("Pattern");
      textWrapEditor.Properties.Add("EndPattern");
      textWrapEditor.Properties.Add("EndEarlyPattern");
      textWrapEditor.Properties.Add("EndTextToSpeak");
      textWrapEditor.Properties.Add("TextToSpeak");
      textWrapEditor.Properties.Add("WarningToSpeak");
      thePropertyGrid.CustomEditorCollection.Add(textWrapEditor);

      ErrorEditor = new WrapTextEditor();
      var errorEdit = new CustomEditor();
      errorEdit.Editor = ErrorEditor;
      errorEdit.Properties.Add("Errors");
      thePropertyGrid.CustomEditorCollection.Add(errorEdit);

      TimerResetOptions = new TimerResetEditor();
      var listEditor = new CustomEditor();
      listEditor.Editor = TimerResetOptions;
      listEditor.Properties.Add("TriggerAgainOption");
      thePropertyGrid.CustomEditorCollection.Add(listEditor);

      var timeEditor = new CustomEditor();
      timeEditor.Editor = new RangeEditor(0, 60);
      timeEditor.Properties.Add("Seconds");
      timeEditor.Properties.Add("Minutes");
      thePropertyGrid.CustomEditorCollection.Add(timeEditor);

      treeView.Nodes.Add(TriggerManager.Instance.GetTreeView());
      TriggerManager.Instance.EventsUpdateTree += EventsUpdateTree;
      (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete += EventsLogLoadingComplete;

      (Application.Current.MainWindow as MainWindow).Closing += TriggersViewClosing;
    }

    private void TriggersViewClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
      SaveExpanded(treeView.Nodes.Cast<TriggerTreeViewNode>().ToList());
    }

    private void EventsLogLoadingComplete(object sender, bool e)
    {
      if (TriggerManager.Instance.IsActive())
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
        treeView.Nodes.Add(TriggerManager.Instance.GetTreeView());
      });
    }

    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
      // one way to see if UI has been initialized
      if (startIcon?.Icon != FontAwesome5.EFontAwesomeIcon.None)
      {
        ConfigUtil.SetSetting("TriggersWatchForGINA", watchGina.IsChecked.Value.ToString(CultureInfo.CurrentCulture));
      }
    }

    private void ImportClick(object sender, RoutedEventArgs e)
    {
      if (e.Source is MenuItem item && item.DataContext is TreeViewItemContextMenuInfo info && info.Node is TriggerTreeViewNode node)
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
        TriggerManager.Instance.Start();
      }
      else
      {
        SetPlayer("Activate Triggers", "EQMenuIconBrush", EFontAwesomeIcon.Solid_Play);
        TriggerManager.Instance.Stop();
      }
    }

    private void ItemDropping(object sender, TreeViewItemDroppingEventArgs e)
    {
      var target = e.TargetNode as TriggerTreeViewNode;
      var source = e.DraggingNodes[0] as TriggerTreeViewNode;

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

      var targetParent = target.ParentNode as TriggerTreeViewNode;
      var sourceParent = source.ParentNode as TriggerTreeViewNode;

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
          target.SerializedData.Nodes = new List<TriggerNode>();
        }

        target.SerializedData.Nodes.Add(source.SerializedData);
      }

      TriggerManager.Instance.Update(true);
    }

    private void CreateNodeClick(object sender, RoutedEventArgs e)
    {
      if (e.Source is MenuItem item && item.DataContext is TreeViewItemContextMenuInfo info && info.Node is TriggerTreeViewNode node)
      {
        var newNode = new TriggerNode { Name = LABEL_NEW_FOLDER };

        if (node.SerializedData.Nodes == null)
        {
          node.SerializedData.Nodes = new List<TriggerNode>();
        }

        node.SerializedData.IsExpanded = true;
        node.SerializedData.Nodes.Add(newNode);
        TriggerManager.Instance.Update();
        EventsUpdateTree(this, true);
      }
    }

    private void CreateTriggerClick(object sender, RoutedEventArgs e)
    {
      if (e.Source is MenuItem item && item.DataContext is TreeViewItemContextMenuInfo info && info.Node is TriggerTreeViewNode node)
      {
        var newTrigger = new TriggerNode { Name = LABEL_NEW_TRIGGER, IsEnabled = true, TriggerData = new Trigger { Name = LABEL_NEW_TRIGGER } };

        if (node.SerializedData.Nodes == null)
        {
          node.SerializedData.Nodes = new List<TriggerNode>();
        }

        node.SerializedData.IsExpanded = true;
        node.SerializedData.Nodes.Add(newTrigger);
        TriggerManager.Instance.Update();
        EventsUpdateTree(this, true);
      }
    }

    private void DeleteClick(object sender, RoutedEventArgs e)
    {
      if (e.Source is MenuItem item && item.DataContext is TreeViewItemContextMenuInfo info && info.Node.ParentNode is TriggerTreeViewNode parent &&
        info.Node is TriggerTreeViewNode node)
      {
        parent.SerializedData.Nodes.Remove(node.SerializedData);
        if (parent.SerializedData.Nodes.Count == 0)
        {
          parent.SerializedData.IsEnabled = false;
        }

        thePropertyGrid.SelectedObject = null;
        thePropertyGrid.IsEnabled = false;
        thePropertyGrid.DescriptionPanelVisibility = Visibility.Collapsed;
        EventsUpdateTree(this, true);
        TriggerManager.Instance.Update();
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
            if (node.IsTrigger && node.SerializedData.TriggerData != null)
            {
              node.SerializedData.TriggerData.Name = node.Content as string;
            }

            TriggerManager.Instance.Update(false);
          }
        }, System.Windows.Threading.DispatcherPriority.Background);
      }
    }

    private void ItemContextMenuOpening(object sender, ItemContextMenuOpeningEventArgs e)
    {
      if (e.MenuInfo.Node is TriggerTreeViewNode node)
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
      if (e.AddedItems.Count > 0 && e.AddedItems[0] is TriggerTreeViewNode node)
      {
        SelectionChanged(node);
      }
    }

    private void SelectionChanged(TriggerTreeViewNode node)
    {
      TriggerPropertyModel model = null;

      if (node.IsTrigger)
      {
        model = new TriggerPropertyModel { Original = node.SerializedData.TriggerData };
        TriggerUtil.Copy(model, node.SerializedData.TriggerData);
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
      if (e.Node is TriggerTreeViewNode node)
      {
        node.SerializedData.IsEnabled = node.IsChecked;

        if (!node.IsTrigger)
        {
          CheckParent(node);
          CheckChildren(node, node.IsChecked);
        }

        TriggerManager.Instance.Update();
      }
    }

    private void CheckChildren(TriggerTreeViewNode node, bool? value)
    {
      foreach (var child in node.ChildNodes.Cast<TriggerTreeViewNode>())
      {
        child.SerializedData.IsEnabled = value;
        if (!child.IsTrigger)
        {          
          CheckChildren(child, value);
        }
      }
    }

    private void CheckParent(TriggerTreeViewNode node)
    {
      if (node.ParentNode is TriggerTreeViewNode parent)
      {
        parent.SerializedData.IsEnabled = parent.IsChecked;
        CheckParent(parent);
      }
    }

    private void SaveExpanded(List<TriggerTreeViewNode> nodes)
    {
      foreach (var node in nodes)
      {
        node.SerializedData.IsExpanded = node.IsExpanded;

        if (!node.IsTrigger)
        {
          SaveExpanded(node.ChildNodes.Cast<TriggerTreeViewNode>().ToList());
        }
      }
    }

    private void ValueChanged(object sender, ValueChangedEventArgs args)
    {
      if (args.Property.Name != errorsItem.PropertyName && args.Property.Name != evalTimeItem.PropertyName &&
        args.Property.SelectedObject is Trigger trigger)
      {
        var collection = thePropertyGrid.Properties.ToObservableCollection();
        var errorsProp = FindProperty(collection, errorsItem.PropertyName);
        var longestProp = FindProperty(collection, evalTimeItem.PropertyName);

        bool isValid = true;
        if (trigger.UseRegex)
        {
          isValid = TestRegexProperty(trigger, trigger.Pattern, errorsProp);
        }
        
        if (isValid && trigger.EndUseRegex)
        {
          isValid = TestRegexProperty(trigger, trigger.EndEarlyPattern, errorsProp);
        }
        
        if (isValid && trigger.Errors != "None")
        {
          trigger.Errors = "None";
          errorsProp.Value = "None";
          ErrorEditor.SetForeground("ContentForeground");
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

    private bool TestRegexProperty(Trigger trigger, string pattern, PropertyItem errorsProp)
    {
      bool isValid = TextFormatUtils.IsValidRegex(pattern);
      if (trigger.Errors == "None" && !isValid)
      {
        trigger.Errors = "Invalid Regex";
        errorsProp.Value = "Invalid Regex";
        ErrorEditor.SetForeground("EQWarnForegroundBrush");
      }

      return isValid;
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
      if (thePropertyGrid.SelectedObject is TriggerPropertyModel model)
      {
        TriggerUtil.Copy(model.Original, model);
      }

      cancelButton.IsEnabled = false;
      saveButton.IsEnabled = false;
      TriggerManager.Instance.Update();
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
      if (thePropertyGrid.SelectedObject is TriggerPropertyModel model)
      {
        TriggerUtil.Copy(model, model.Original);
        thePropertyGrid.RefreshPropertygrid();
        EnableCategory(timerDurationItem.CategoryName, model.Original.EnableTimer);
      }

      cancelButton.IsEnabled = false;
      saveButton.IsEnabled = false;
    }

    private new void PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
      if (e.OriginalSource is FrameworkElement element && element.DataContext is TriggerTreeViewNode node)
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

    internal class TriggerPropertyModel : Trigger
    {
      public Trigger Original { get; set; }
    }

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        SaveExpanded(treeView.Nodes.Cast<TriggerTreeViewNode>().ToList());
        (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete -= EventsLogLoadingComplete;
        (Application.Current.MainWindow as MainWindow).Closing -= TriggersViewClosing;
        TriggerManager.Instance.EventsUpdateTree -= EventsUpdateTree;
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
