using Syncfusion.Data.Extensions;
using Syncfusion.UI.Xaml.TreeView;
using Syncfusion.Windows.PropertyGrid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TriggersView.xaml
  /// </summary>
  public partial class TriggersView : UserControl, IDisposable
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private const string LABEL_NEW_TEXT_OVERLAY = "New Text Overlay";
    private const string LABEL_NEW_TIMER_OVERLAY = "New Timer Overlay";
    private const string LABEL_NEW_TRIGGER = "New Trigger";
    private const string LABEL_NEW_FOLDER = "New Folder";
    private readonly Dictionary<string, Window> PreviewWindows = new Dictionary<string, Window>();
    private TriggerConfig TheConfig;
    private FileSystemWatcher Watcher;
    private PatternEditor PatternEditor;
    private PatternEditor EndEarlyPatternEditor;
    private PatternEditor EndEarlyPattern2Editor;
    private RangeEditor TopEditor;
    private RangeEditor LeftEditor;
    private RangeEditor HeightEditor;
    private RangeEditor WidthEditor;
    private SpeechSynthesizer TestSynth = null;
    private TriggerTreeViewNode CopiedNode = null;
    private bool CutNode = false;
    private string CurrentPlayer = TriggerStateManager.DEFAULT_USER;
    private bool Ready = false;

    public TriggersView()
    {
      InitializeComponent();

      if (TriggerStateManager.Instance.GetConfig() is TriggerConfig config)
      {
        TheConfig = config;

        if (!TheConfig.IsAdvanced)
        {
          basicCheckBox.IsChecked = config.IsEnabled;
          SetTitle(config.IsEnabled);
        }
      }

      if ((TestSynth = TriggerUtil.GetSpeechSynthesizer()) != null)
      {
        voices.ItemsSource = TestSynth.GetInstalledVoices().Select(voice => voice.VoiceInfo.Name).ToList();
      }

      if (ConfigUtil.IfSetOrElse("TriggersWatchForGINA", false))
      {
        watchGina.IsChecked = true;
      }

      var selectedVoice = TriggerUtil.GetSelectedVoice();
      if (voices.ItemsSource is List<string> populated && populated.IndexOf(selectedVoice) is int found && found > -1)
      {
        voices.SelectedIndex = found;
      }

      rateOption.SelectedIndex = TriggerUtil.GetVoiceRate();
      treeView.DragDropController = new TreeViewDragDropController();
      treeView.DragDropController.CanAutoExpand = true;
      treeView.DragDropController.AutoExpandDelay = new TimeSpan(0, 0, 1);

      var fileList = new ObservableCollection<string>();
      Watcher = TriggerUtil.CreateSoundsWatcher(fileList);

      TopEditor = (RangeEditor)AddEditorInstance(new RangeEditor(typeof(long), 0, 9999), "Top");
      HeightEditor = (RangeEditor)AddEditorInstance(new RangeEditor(typeof(long), 0, 9999), "Height");
      LeftEditor = (RangeEditor)AddEditorInstance(new RangeEditor(typeof(long), 0, 9999), "Left");
      WidthEditor = (RangeEditor)AddEditorInstance(new RangeEditor(typeof(long), 0, 9999), "Width");
      PatternEditor = (PatternEditor)AddEditorInstance(new PatternEditor(), "Pattern");
      EndEarlyPatternEditor = (PatternEditor)AddEditorInstance(new PatternEditor(), "EndEarlyPattern");
      EndEarlyPattern2Editor = (PatternEditor)AddEditorInstance(new PatternEditor(), "EndEarlyPattern2");
      AddEditor<CheckComboBoxEditor>("SelectedTextOverlays", "SelectedTimerOverlays");
      AddEditor<ColorEditor>("OverlayBrush", "FontBrush", "ActiveBrush", "IdleBrush", "ResetBrush", "BackgroundBrush");
      AddEditor<DurationEditor>("ResetDurationTimeSpan", "IdleTimeoutTimeSpan");
      AddEditor<ExampleTimerBar>("TimerBarPreview");
      AddEditor<OptionalColorEditor>("TriggerActiveBrush", "TriggerFontBrush");
      AddEditor<TriggerListsEditor>("TriggerAgainOption", "FontSize", "FontFamily", "SortBy", "TimerMode", "TimerType");
      AddEditor<WrapTextEditor>("EndEarlyTextToDisplay", "EndTextToDisplay", "TextToDisplay", "WarningTextToDisplay", "Comments", "OverlayComments");
      AddEditorInstance(new RangeEditor(typeof(double), 0.2, 2.0), "DurationSeconds");
      AddEditorInstance(new TextSoundEditor(fileList), "SoundOrText");
      AddEditorInstance(new TextSoundEditor(fileList), "EndEarlySoundOrText");
      AddEditorInstance(new TextSoundEditor(fileList), "EndSoundOrText");
      AddEditorInstance(new TextSoundEditor(fileList), "WarningSoundOrText");
      AddEditorInstance(new RangeEditor(typeof(long), 1, 5), "Priority");
      AddEditorInstance(new RangeEditor(typeof(long), 0, 99999), "WarningSeconds");
      AddEditorInstance(new DurationEditor(2), "DurationTimeSpan");
      AddEditorInstance(new RangeEditor(typeof(long), 1, 60), "FadeDelay");

      void AddEditor<T>(params string[] propNames) where T : new()
      {
        foreach (var name in propNames)
        {
          var editor = new CustomEditor { Editor = (ITypeEditor)new T() };
          editor.Properties.Add(name);
          thePropertyGrid.CustomEditorCollection.Add(editor);
        }
      }

      ITypeEditor AddEditorInstance(ITypeEditor typeEditor, string propName)
      {
        var editor = new CustomEditor { Editor = typeEditor };
        editor.Properties.Add(propName);
        thePropertyGrid.CustomEditorCollection.Add(editor);
        return editor.Editor;
      }

      treeView.Nodes.Add(TriggerStateManager.Instance.GetTriggerTreeView(CurrentPlayer));
      treeView.Nodes.Add(TriggerStateManager.Instance.GetOverlayTreeView());
      TriggerManager.Instance.EventsSelectTrigger += EventsSelectTrigger;
      TriggerStateManager.Instance.TriggerUpdateEvent += TriggerUpdateEvent;
      Ready = true;
    }

    private void AssignTextOverlayClick(object sender, RoutedEventArgs e) => AssignOverlay(sender, true);
    private void AssignTimerOverlayClick(object sender, RoutedEventArgs e) => AssignOverlay(sender, false);
    private void CloseOverlaysClick(object sender, RoutedEventArgs e) => TriggerManager.Instance.CloseOverlays();
    private void CreateTextOverlayClick(object sender, RoutedEventArgs e) => CreateOverlay(false);
    private void CreateTimerOverlayClick(object sender, RoutedEventArgs e) => CreateOverlay(true);
    private void ExportClick(object sender, RoutedEventArgs e) => TriggerUtil.Export(treeView?.SelectedItems?.Cast<TriggerTreeViewNode>());
    private void EventsSelectTrigger(Trigger e) => Dispatcher.InvokeAsync(() => SelectFile(e));
    private void NodeExpanded(object sender, NodeExpandedCollapsedEventArgs e) => TriggerStateManager.Instance.SetExpanded(e.Node as TriggerTreeViewNode);
    private void RenameClick(object sender, RoutedEventArgs e) => treeView?.BeginEdit(treeView.SelectedItem as TriggerTreeViewNode);
    private void SelectionChanging(object sender, ItemSelectionChangingEventArgs e) => e.Cancel = IsCancelSelection();

    private void BasicChecked(object sender, RoutedEventArgs e)
    {
      if (Ready && sender is CheckBox checkBox)
      {
        if (checkBox?.IsChecked == true)
        {
          SetTitle(true);
          TheConfig.IsEnabled = true;
        }
        else
        {
          SetTitle(false);
          TheConfig.IsEnabled = false;
        }

        TriggerStateManager.Instance.UpdateConfig(TheConfig);
        TriggerManager.Instance.ConfigUpdated();
      }
    }

    private void SetTitle(bool active)
    {
      if (active)
      {
        titleLabel.SetResourceReference(Label.ForegroundProperty, "EQGoodForegroundBrush");
        titleLabel.Content = "Triggers Active";
      }
      else
      {
        titleLabel.SetResourceReference(Label.ForegroundProperty, "EQStopForegroundBrush");
        titleLabel.Content = "Check to Activate Triggers";
      }
    }

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
        TriggerStateManager.Instance.SetState(CurrentPlayer, viewNode);
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
      treeView.Nodes.Remove(treeView.Nodes[0]);
      treeView.Nodes.Insert(0, TriggerStateManager.Instance.GetTriggerTreeView(CurrentPlayer));
    }

    private void RefreshOverlayNode()
    {
      treeView.Nodes.Remove(treeView.Nodes[1]);
      treeView.Nodes.Add(TriggerStateManager.Instance.GetOverlayTreeView());
    }

    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
      if (Ready)
      {
        if (sender == watchGina)
        {
          ConfigUtil.SetSetting("TriggersWatchForGINA", watchGina.IsChecked.Value.ToString(CultureInfo.CurrentCulture));
        }
        else if (sender == voices)
        {
          if (voices.SelectedValue is string voiceName)
          {
            ConfigUtil.SetSetting("TriggersSelectedVoice", voiceName);
            TriggerManager.Instance.SetVoice(voiceName);

            if (TestSynth != null)
            {
              TestSynth.Rate = TriggerUtil.GetVoiceRate();
              TestSynth.SelectVoice(voiceName);
              TestSynth.SpeakAsync(voiceName);
            }
          }
        }
        else if (sender == rateOption)
        {
          ConfigUtil.SetSetting("TriggersVoiceRate", rateOption.SelectedIndex.ToString(CultureInfo.CurrentCulture));
          TriggerManager.Instance.SetVoiceRate(rateOption.SelectedIndex);

          if (TestSynth != null)
          {
            TestSynth.Rate = rateOption.SelectedIndex;
            if (TriggerUtil.GetSelectedVoice() is string voice && !string.IsNullOrEmpty(voice))
            {
              TestSynth.SelectVoice(voice);
            }
            var rateText = rateOption.SelectedIndex == 0 ? "Default Voice Rate" : "Voice Rate " + rateOption.SelectedIndex.ToString();
            TestSynth.SpeakAsync(rateText);
          }
        }
      }
    }

    private void SelectFile(object file)
    {
      if (file != null && !IsCancelSelection())
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

    private void CreateOverlay(bool isTimer)
    {
      if (treeView.SelectedItem is TriggerTreeViewNode parent)
      {
        var label = isTimer ? LABEL_NEW_TIMER_OVERLAY : LABEL_NEW_TEXT_OVERLAY;
        if (TriggerStateManager.Instance.CreateOverlay(parent.SerializedData.Id, label, isTimer) is TriggerTreeViewNode newNode)
        {
          parent.ChildNodes.Add(newNode);
          SelectFile(newNode.SerializedData.OverlayData);
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
          SelectFile(newNode.SerializedData.TriggerData);
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
            if (PreviewWindows.Remove(id, out var window))
            {
              window?.Close();
            }

            TriggerManager.Instance.CloseOverlay(id);
          }
          else if (node.IsTrigger() && node.IsChecked == true)
          {
            triggerDelete = true;
          }
        }
      }

      thePropertyGrid.SelectedObject = null;
      thePropertyGrid.IsEnabled = false;

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
        assignOverlayMenuItem.IsEnabled = anyTriggers;
        assignPriorityMenuItem.IsEnabled = anyTriggers;
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
        deleteTriggerMenuItem.IsEnabled = false;
        renameMenuItem.IsEnabled = false;
        importMenuItem.IsEnabled = false;
        exportMenuItem.IsEnabled = false;
        newMenuItem.IsEnabled = false;
        assignOverlayMenuItem.IsEnabled = false;
        assignPriorityMenuItem.IsEnabled = false;
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

      if (assignPriorityMenuItem.IsEnabled)
      {
        UIElementUtil.ClearMenuEvents(assignPriorityMenuItem.Items, AssignPriorityClick);
      }

      assignPriorityMenuItem.Items.Clear();

      for (var i = 1; i <= 5; i++)
      {
        var menuItem = new MenuItem { Header = "Priority " + i, Tag = i };
        menuItem.Click += AssignPriorityClick;
        assignPriorityMenuItem.Items.Add(menuItem);
      }

      if (assignOverlayMenuItem.IsEnabled)
      {
        UIElementUtil.ClearMenuEvents(assignTextOverlaysMenuItem.Items, AssignTextOverlayClick);
        UIElementUtil.ClearMenuEvents(assignTimerOverlaysMenuItem.Items, AssignTimerOverlayClick);
        assignTextOverlaysMenuItem.Items.Clear();
        assignTimerOverlaysMenuItem.Items.Clear();

        foreach (var overlay in TriggerStateManager.Instance.GetAllOverlays())
        {
          var menuItem = new MenuItem { Header = overlay.Name, Tag = $"{overlay.Name}={overlay.Id}" };
          if (overlay.OverlayData?.IsTextOverlay == true)
          {
            menuItem.Click += AssignTextOverlayClick;
            assignTextOverlaysMenuItem.Items.Add(menuItem);
          }
          else
          {
            menuItem.Click += AssignTimerOverlayClick;
            assignTimerOverlaysMenuItem.Items.Add(menuItem);
          }
        }

        var removeTextOverlays = new MenuItem { Header = "Unassign All Text Overlays" };
        removeTextOverlays.Click += AssignTextOverlayClick;
        assignTextOverlaysMenuItem.Items.Add(removeTextOverlays);
        var removeTimerOverlays = new MenuItem { Header = "Unassign All Timer Overlays" };
        removeTimerOverlays.Click += AssignTimerOverlayClick;
        assignTimerOverlaysMenuItem.Items.Add(removeTimerOverlays);

      }
    }

    private void AssignPriorityClick(object sender, RoutedEventArgs e)
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
          var msgDialog = new MessageWindow($"Are you sure? This will Assign Priority {newPriority} to all selected Triggers and those in all sub folders.",
            EQLogParser.Resource.ASSIGN_PRIORITY, MessageWindow.IconType.Question, "Yes");
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

    private void AssignOverlay(object sender, bool isTextOverlay)
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
            TriggerStateManager.Instance.AssignOverlay(id, selected.Select(treeView => treeView.SerializedData));
          }
          else
          {
            var msgDialog = new MessageWindow($"Are you sure? This will Assign all selected Triggers and those in all sub folders to {name}.",
              EQLogParser.Resource.ASSIGN_OVERLAY, MessageWindow.IconType.Question, "Yes");
            msgDialog.ShowDialog();
            if (msgDialog.IsYes1Clicked)
            {
              TriggerStateManager.Instance.AssignOverlay(id, selected.Select(treeView => treeView.SerializedData));
            }
          }
        }
        else if (menuItem.Tag == null)
        {
          if (!anyFolders)
          {
            TriggerStateManager.Instance.UnassignOverlay(isTextOverlay, selected.Select(treeView => treeView.SerializedData));
          }
          else
          {
            var type = isTextOverlay ? "Text Overlays" : "Timer Overlays";
            var msgDialog = new MessageWindow($"Are you sure? This will Unassign all selected Triggers and those in all sub folders from all {type}.",
              EQLogParser.Resource.UNASSIGN_OVERLAY, MessageWindow.IconType.Question, "Yes");
            msgDialog.ShowDialog();
            if (msgDialog.IsYes1Clicked)
            {
              TriggerStateManager.Instance.UnassignOverlay(isTextOverlay, selected.Select(treeView => treeView.SerializedData));
            }
          }
        }

        RefreshTriggerNode();
        SelectionChanged(treeView.SelectedItem as TriggerTreeViewNode);
        TriggerManager.Instance.TriggersUpdated();
      }
    }

    private bool IsCancelSelection()
    {
      dynamic model = thePropertyGrid?.SelectedObject;
      var cancel = false;
      if (saveButton.IsEnabled)
      {
        if (model is TriggerPropertyModel || model is TextOverlayPropertyModel || model is TimerOverlayPropertyModel)
        {
          if (model?.Node?.Name is string name)
          {
            var msgDialog = new MessageWindow("Do you want to save changes to " + name + "?", EQLogParser.Resource.UNSAVED,
              MessageWindow.IconType.Question, "Don't Save", "Save");
            msgDialog.ShowDialog();
            cancel = !msgDialog.IsYes1Clicked && !msgDialog.IsYes2Clicked;
            if (msgDialog.IsYes2Clicked)
            {
              SaveClick(this, null);
            }
          }
        }
      }

      return cancel;
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
      var isCooldownOverlay = isTimerOverlay && (node?.SerializedData?.OverlayData?.TimerMode == 1);

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

        saveButton.IsEnabled = false;
        cancelButton.IsEnabled = false;
      }

      thePropertyGrid.SelectedObject = model;
      thePropertyGrid.IsEnabled = thePropertyGrid.SelectedObject != null;
      thePropertyGrid.DescriptionPanelVisibility = (node.IsTrigger() || node.IsOverlay()) ? Visibility.Visible : Visibility.Collapsed;
      showButton.Visibility = node.IsOverlay() ? Visibility.Visible : Visibility.Collapsed;

      if (node.IsTrigger())
      {
        var timerType = node.SerializedData.TriggerData.TimerType;
        EnableCategories(true, timerType > 0, timerType == 2, false, false, true, false, false);
      }
      else if (node.IsOverlay())
      {
        if (isTimerOverlay)
        {
          EnableCategories(false, false, false, true, true, false, false, isCooldownOverlay);
        }
        else
        {
          EnableCategories(false, false, false, true, false, false, true, false);
        }
      }
    }

    private void EnableCategories(bool trigger, bool basicTimer, bool shortTimer, bool overlay, bool overlayTimer,
      bool overlayAssigned, bool overlayText, bool cooldownTimer)
    {
      PropertyGridUtil.EnableCategories(thePropertyGrid, new[]
      {
        new { Name = patternItem.CategoryName, IsEnabled = trigger },
        new { Name = timerDurationItem.CategoryName, IsEnabled = basicTimer },
        new { Name = resetDurationItem.CategoryName, IsEnabled = basicTimer && !shortTimer },
        new { Name = endEarlyPatternItem.CategoryName, IsEnabled = basicTimer && !shortTimer },
        new { Name = fontSizeItem.CategoryName, IsEnabled = overlay },
        new { Name = activeBrushItem.CategoryName, IsEnabled = overlayTimer },
        new { Name = idleBrushItem.CategoryName, IsEnabled = cooldownTimer },
        new { Name = assignedOverlaysItem.CategoryName, IsEnabled = overlayAssigned },
        new { Name = fadeDelayItem.CategoryName, IsEnabled = overlayText }
      });

      timerDurationItem.Visibility = (basicTimer && !shortTimer) ? Visibility.Visible : Visibility.Collapsed;
      timerShortDurationItem.Visibility = shortTimer ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ValueChanged(object sender, ValueChangedEventArgs args)
    {
      if (args.Property.Name != evalTimeItem.PropertyName &&
        args.Property.SelectedObject is TriggerPropertyModel trigger)
      {
        var triggerChange = true;
        var list = thePropertyGrid.Properties.ToList();
        var longestProp = PropertyGridUtil.FindProperty(list, evalTimeItem.PropertyName);

        var isValid = TriggerUtil.TestRegexProperty(trigger.UseRegex, trigger.Pattern, PatternEditor);
        isValid = isValid && TriggerUtil.TestRegexProperty(trigger.EndUseRegex, trigger.EndEarlyPattern, EndEarlyPatternEditor);
        isValid = isValid && TriggerUtil.TestRegexProperty(trigger.EndUseRegex2, trigger.EndEarlyPattern2, EndEarlyPattern2Editor);

        if (args.Property.Name == patternItem.PropertyName)
        {
          trigger.WorstEvalTime = -1;
          longestProp.Value = -1;
        }
        else if (args.Property.Name == timerTypeItem.PropertyName && args.Property.Value is int timerType)
        {
          EnableCategories(true, timerType > 0, timerType == 2, false, false, true, false, false);
        }
        else if (args.Property.Name == triggerActiveBrushItem.PropertyName)
        {
          var original = trigger.Node.TriggerData;
          if (trigger.TriggerActiveBrush == null && original.ActiveColor == null)
          {
            triggerChange = false;
          }
          else
          {
            triggerChange = (trigger.TriggerActiveBrush == null && original.ActiveColor != null) ||
              (trigger.TriggerActiveBrush != null && original.ActiveColor == null) ||
              (trigger.TriggerActiveBrush.Color.ToHexString() != original.ActiveColor);
          }
        }
        else if (args.Property.Name == triggerFontBrushItem.PropertyName)
        {
          var original = trigger.Node.TriggerData;
          if (trigger.TriggerFontBrush == null && original.FontColor == null)
          {
            triggerChange = false;
          }
          else
          {
            triggerChange = (trigger.TriggerFontBrush == null && original.FontColor != null) ||
              (trigger.TriggerFontBrush != null && original.FontColor == null) ||
              (trigger.TriggerFontBrush.Color.ToHexString() != original.FontColor);
          }
        }
        else if (args.Property.Name == "DurationTimeSpan" && timerDurationItem.Visibility == Visibility.Collapsed)
        {
          triggerChange = false;
        }

        if (triggerChange)
        {
          saveButton.IsEnabled = isValid;
          cancelButton.IsEnabled = true;
        }
      }
      else if (args.Property.SelectedObject is TextOverlayPropertyModel textOverlay)
      {
        var textChange = true;
        var original = textOverlay.Node.OverlayData;

        if (args.Property.Name == overlayBrushItem.PropertyName)
        {
          textChange = !(textOverlay.OverlayBrush.Color.ToHexString() == original.OverlayColor);
          Application.Current.Resources["OverlayBrushColor-" + textOverlay.Node.Id] = textOverlay.OverlayBrush;
        }
        else if (args.Property.Name == fontBrushItem.PropertyName)
        {
          textChange = !(textOverlay.FontBrush.Color.ToHexString() == original.FontColor);
          Application.Current.Resources["TextOverlayFontColor-" + textOverlay.Node.Id] = textOverlay.FontBrush;
        }
        else if (args.Property.Name == fontFamilyItem.PropertyName)
        {
          textChange = textOverlay.FontFamily != original.FontFamily;
          Application.Current.Resources["TextOverlayFontFamily-" + textOverlay.Node.Id] = new FontFamily(textOverlay.FontFamily);
        }
        else if (args.Property.Name == fontSizeItem.PropertyName && textOverlay.FontSize.Split("pt") is string[] split && split.Length == 2
         && double.TryParse(split[0], out var newFontSize))
        {
          textChange = textOverlay.FontSize != original.FontSize;
          Application.Current.Resources["TextOverlayFontSize-" + textOverlay.Node.Id] = newFontSize;
        }

        if (textChange)
        {
          saveButton.IsEnabled = true;
          cancelButton.IsEnabled = true;
        }
      }
      else if (args.Property.SelectedObject is TimerOverlayPropertyModel timerOverlay)
      {
        var timerChange = true;
        var original = timerOverlay.Node.OverlayData;

        if (args.Property.Name == overlayBrushItem.PropertyName)
        {
          timerChange = !(timerOverlay.OverlayBrush.Color.ToHexString() == original.OverlayColor);
          Application.Current.Resources["OverlayBrushColor-" + timerOverlay.Node.Id] = timerOverlay.OverlayBrush;
        }
        else if (args.Property.Name == activeBrushItem.PropertyName)
        {
          timerChange = !(timerOverlay.ActiveBrush.Color.ToHexString() == original.ActiveColor);
          Application.Current.Resources["TimerBarActiveColor-" + timerOverlay.Node.Id] = timerOverlay.ActiveBrush;
        }
        else if (args.Property.Name == idleBrushItem.PropertyName)
        {
          timerChange = !(timerOverlay.IdleBrush.Color.ToHexString() == original.IdleColor);
          Application.Current.Resources["TimerBarIdleColor-" + timerOverlay.Node.Id] = timerOverlay.IdleBrush;
        }
        else if (args.Property.Name == resetBrushItem.PropertyName)
        {
          timerChange = !(timerOverlay.ResetBrush.Color.ToHexString() == original.ResetColor);
          Application.Current.Resources["TimerBarResetColor-" + timerOverlay.Node.Id] = timerOverlay.ResetBrush;
        }
        else if (args.Property.Name == backgroundBrushItem.PropertyName)
        {
          timerChange = !(timerOverlay.BackgroundBrush.Color.ToHexString() == original.BackgroundColor);
          Application.Current.Resources["TimerBarTrackColor-" + timerOverlay.Node.Id] = timerOverlay.BackgroundBrush;
        }
        else if (args.Property.Name == fontBrushItem.PropertyName)
        {
          timerChange = !(timerOverlay.FontBrush.Color.ToHexString() == original.FontColor);
          Application.Current.Resources["TimerBarFontColor-" + timerOverlay.Node.Id] = timerOverlay.FontBrush;
        }
        else if (args.Property.Name == fontSizeItem.PropertyName && timerOverlay.FontSize.Split("pt") is string[] split && split.Length == 2
         && double.TryParse(split[0], out var newFontSize))
        {
          timerChange = timerOverlay.FontSize != original.FontSize;
          Application.Current.Resources["TimerBarFontSize-" + timerOverlay.Node.Id] = newFontSize;
          Application.Current.Resources["TimerBarHeight-" + timerOverlay.Node.Id] = TriggerUtil.GetTimerBarHeight(newFontSize);
        }
        else if (args.Property.Name == timerModeItem.PropertyName)
        {
          PropertyGridUtil.EnableCategories(thePropertyGrid, new[] { new { Name = idleBrushItem.CategoryName, IsEnabled = (int)args.Property.Value == 1 } });
        }

        if (timerChange)
        {
          saveButton.IsEnabled = true;
          cancelButton.IsEnabled = true;
        }
      }
    }

    private void ShowClick(object sender, RoutedEventArgs e)
    {
      dynamic model = thePropertyGrid?.SelectedObject;
      if ((model is TimerOverlayPropertyModel || model is TextOverlayPropertyModel) && model?.Node?.Id is string id)
      {
        if (!PreviewWindows.TryGetValue(id, out var window))
        {
          PreviewWindows[id] = (model is TimerOverlayPropertyModel) ? new TimerOverlayWindow(model.Node, PreviewWindows)
            : new TextOverlayWindow(model.Node, PreviewWindows);
          PreviewWindows[id].Show();
        }
        else
        {
          window.Close();
          PreviewWindows.Remove(id, out _);
        }
      }
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
      dynamic model = thePropertyGrid?.SelectedObject;
      if (model is TriggerPropertyModel)
      {
        TriggerUtil.Copy(model.Node.TriggerData, model);
        TriggerStateManager.Instance.Update(model.Node);

        // reload triggers if current one is enabled by anyone
        if (TriggerStateManager.Instance.IsAnyEnabled(model.Node.Id))
        {
          TriggerManager.Instance.TriggersUpdated();
        }
      }
      else if (model is TextOverlayPropertyModel || model is TimerOverlayPropertyModel)
      {
        // only close overlay if non-style attributes have changed
        var old = model.Node.OverlayData;
        if (old.Top != model.Top || old.Left != model.Left || old.Height != model.Height || old.Width != model.Width)
        {
          TriggerManager.Instance.CloseOverlay(model.Node.Id);
        }

        TriggerUtil.Copy(model.Node.OverlayData, model);
        TriggerStateManager.Instance.Update(model.Node);
      }

      cancelButton.IsEnabled = false;
      saveButton.IsEnabled = false;
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
      dynamic model = thePropertyGrid?.SelectedObject;
      if (model is TriggerPropertyModel)
      {
        TriggerUtil.Copy(model, model.Node.TriggerData);
        var timerType = model.Node.TriggerData.TimerType;
        EnableCategories(true, timerType > 0, timerType == 2, false, false, true, false, false);
      }
      else if (model is TimerOverlayPropertyModel || model is TextOverlayPropertyModel)
      {
        TriggerUtil.Copy(model, model.Node.OverlayData);
      }

      thePropertyGrid.RefreshPropertygrid();
      Dispatcher.InvokeAsync(() => cancelButton.IsEnabled = saveButton.IsEnabled = false, DispatcherPriority.Background);
    }

    private void TriggerUpdateEvent(TriggerNode node)
    {
      if (node?.OverlayData is Overlay overlay)
      {
        var wasEnabled = saveButton.IsEnabled;
        TopEditor.Update(overlay.Top);
        LeftEditor.Update(overlay.Left);
        WidthEditor.Update(overlay.Width);
        HeightEditor.Update(overlay.Height);

        if (!wasEnabled)
        {
          saveButton.IsEnabled = false;
          cancelButton.IsEnabled = false;
        }
      }
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
        PreviewWindows.Values.ToList().ForEach(window => window.Close());
        PreviewWindows.Clear();
        TriggerStateManager.Instance.TriggerUpdateEvent -= TriggerUpdateEvent;
        TriggerManager.Instance.EventsSelectTrigger -= EventsSelectTrigger;
        TestSynth?.Dispose();
        Watcher?.Dispose();
        thePropertyGrid?.Dispose();
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
