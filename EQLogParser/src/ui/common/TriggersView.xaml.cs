using FontAwesome5;
using Syncfusion.UI.Xaml.TreeView;
using Syncfusion.Windows.PropertyGrid;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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
    private WrapTextEditor ErrorEditor;
    private List<TriggerNode> Removed;
    private SpeechSynthesizer TestSynth = null;

    public TriggersView()
    {
      InitializeComponent();

      try
      {
        TestSynth = new SpeechSynthesizer();
        TestSynth.SetOutputToDefaultAudioDevice();
        voices.ItemsSource = TestSynth.GetInstalledVoices().Select(voice => voice.VoiceInfo.Name).ToList();
      }
      catch (Exception)
      {
        // may not initialize on all systems
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
      Helpers.AddToCollection(priorityEditor.Properties, "Priority");
      thePropertyGrid.CustomEditorCollection.Add(priorityEditor);

      var textWrapEditor = new CustomEditor();
      textWrapEditor.Editor = new WrapTextEditor();
      Helpers.AddToCollection(textWrapEditor.Properties, "Comments", "OverlayComments", "Pattern", "EndPattern",
        "CancelPattern", "EndTextToSpeak", "TextToSpeak", "WarningToSpeak");
      thePropertyGrid.CustomEditorCollection.Add(textWrapEditor);

      // Need to only have one ErrorEditor since the background is updated
      var errorEditor = new CustomEditor();
      errorEditor.Editor = ErrorEditor = new WrapTextEditor();
      Helpers.AddToCollection(errorEditor.Properties, "Errors");
      thePropertyGrid.CustomEditorCollection.Add(errorEditor);

      var durationEditor = new CustomEditor();
      durationEditor.Editor = new DurationEditor();
      Helpers.AddToCollection(durationEditor.Properties, "DurationTimeSpan", "ResetDurationTimeSpan");
      thePropertyGrid.CustomEditorCollection.Add(durationEditor);

      var colorEditor = new CustomEditor();
      colorEditor.Editor = new ColorEditor();
      Helpers.AddToCollection(colorEditor.Properties, "OverlayBrush", "FontBrush", "PrimaryBrush", "SecondaryBrush");
      thePropertyGrid.CustomEditorCollection.Add(colorEditor);

      var overlayEditor = new CustomEditor();
      overlayEditor.Editor = new CheckComboBoxEditor();
      overlayEditor.Properties.Add("SelectedTextOverlays");
      overlayEditor.Properties.Add("SelectedTimerOverlays");
      thePropertyGrid.CustomEditorCollection.Add(overlayEditor);

      var listEditor = new CustomEditor();
      listEditor.Editor = new TriggerListsEditor();
      Helpers.AddToCollection(listEditor.Properties, "TriggerAgainOption", "FontSize", "SortBy");
      thePropertyGrid.CustomEditorCollection.Add(listEditor);

      var timeEditor = new CustomEditor();
      timeEditor.Editor = new RangeEditor(1, 60);
      timeEditor.Properties.Add("FadeDelay");
      thePropertyGrid.CustomEditorCollection.Add(timeEditor);

      var exampleEditor = new CustomEditor();
      exampleEditor.Editor = new ExampleTimerBar();
      exampleEditor.Properties.Add("TimerBarPreview");
      thePropertyGrid.CustomEditorCollection.Add(exampleEditor);

      treeView.Nodes.Add(TriggerManager.Instance.GetTriggerTreeView());
      treeView.Nodes.Add(TriggerOverlayManager.Instance.GetOverlayTreeView());

      TriggerManager.Instance.EventsUpdateTree += EventsUpdateTriggerTree;
      TriggerManager.Instance.EventsSelectTrigger += EventsSelectTrigger;
      (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete += EventsLogLoadingComplete;
    }

    private void AssignTextOverlayClick(object sender, RoutedEventArgs e) => AssignOverlay(sender);
    private void AssignTimerOverlayClick(object sender, RoutedEventArgs e) => AssignOverlay(sender);
    private void CreateTextOverlayClick(object sender, RoutedEventArgs e) => CreateOverlay(false);
    private void CreateTimerOverlayClick(object sender, RoutedEventArgs e) => CreateOverlay(true);
    private void EventsSelectTrigger(object sender, Trigger e) => SelectFile(e);
    private void EventsUpdateTriggerTree(object sender, bool e) => Dispatcher.InvokeAsync(() => RefreshTriggerNode());
    private void ExportClick(object sender, RoutedEventArgs e) => TriggerUtil.Export(treeView?.Nodes, treeView.SelectedItems?.Cast<TriggerTreeViewNode>().ToList());
    private void ImportClick(object sender, RoutedEventArgs e) => TriggerUtil.Import(treeView?.SelectedItem as TriggerTreeViewNode);
    private void RenameClick(object sender, RoutedEventArgs e) => treeView?.BeginEdit(treeView.SelectedItem as TriggerTreeViewNode);

    private void CollapseAllClick(object sender, RoutedEventArgs e)
    {
      treeView.CollapseAll();
      SaveNodeExpanded(treeView.Nodes.Cast<TriggerTreeViewNode>().ToList());
    }

    private void ExpandAllClick(object sender, RoutedEventArgs e)
    {
      treeView.ExpandAll();
      SaveNodeExpanded(treeView.Nodes.Cast<TriggerTreeViewNode>().ToList());
    }

    private void RefreshTriggerNode()
    {
      treeView.Nodes.Remove(treeView.Nodes[0]);
      treeView.Nodes.Insert(0, TriggerManager.Instance.GetTriggerTreeView());
    }

    private void RefreshOverlayNode()
    {
      treeView.Nodes.Remove(treeView.Nodes[1]);
      treeView.Nodes.Add(TriggerOverlayManager.Instance.GetOverlayTreeView());
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

    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
      // one way to see if UI has been initialized
      if (startIcon?.Icon != FontAwesome5.EFontAwesomeIcon.None)
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
            TestSynth.SelectVoice(TriggerUtil.GetSelectedVoice());
            var rateText = rateOption.SelectedIndex == 0 ? "Default Voice Rate" : "Voice Rate " + rateOption.SelectedIndex.ToString();
            TestSynth.SpeakAsync(rateText);
          }
        }
      }
    }

    private void SelectFile(object file)
    {
      if (file != null)
      {
        bool selectFile = false;
        bool isTrigger = file is Trigger;

        if (treeView.SelectedItem == null)
        {
          selectFile = true;
        }
        else if (treeView.SelectedItem is TriggerTreeViewNode node && node.SerializedData != null)
        {
          if (node.SerializedData.TriggerData != file && node.SerializedData.OverlayData != file)
          {
            selectFile = true;
          }
        }

        if (selectFile && 
          TriggerUtil.FindAndExpandNode(treeView, (isTrigger ? treeView.Nodes[0] : treeView.Nodes[1]) as TriggerTreeViewNode, file) is TriggerTreeViewNode found)
        {
          treeView.SelectedItems?.Clear();
          treeView.SelectedItem = found;
          SelectionChanged(found);
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

      if (e.DropPosition == DropPosition.None)
      {
        e.Handled = true;
        return;
      }

      if (target.Level == 0 && e.DropPosition != DropPosition.DropAsChild)
      {
        e.Handled = true;
        return;
      }

      // fix drag and drop that wants to reverse the order for some reason
      var list = e.DraggingNodes.Cast<TriggerTreeViewNode>().ToList();
      list.Reverse();

      if ((target == treeView.Nodes[1] || target.ParentNode == treeView.Nodes[1]) && list.Any(item => !item.IsOverlay))
      {
        e.Handled = true;
        return;
      }

      if ((target != treeView.Nodes[1] && target.ParentNode != treeView.Nodes[1]) && list.Any(item => item.IsOverlay))
      {
        e.Handled = true;
        return;
      }

      e.DraggingNodes.Clear();
      list.ForEach(node => e.DraggingNodes.Add(node));
      target = ((!target.IsTrigger && !target.IsOverlay) && e.DropPosition == DropPosition.DropAsChild) ? target : target.ParentNode as TriggerTreeViewNode;

      Removed = new List<TriggerNode>();
      foreach (var node in e.DraggingNodes.Cast<TriggerTreeViewNode>())
      {
        if (node.ParentNode != target)
        {
          if (node.ParentNode is TriggerTreeViewNode parent && parent.SerializedData != null && parent.SerializedData.Nodes != null)
          {
            parent.SerializedData.Nodes.Remove(node.SerializedData);
            Removed.Add(node.SerializedData);
          }
        }
      }
    }

    private void ItemDropped(object sender, TreeViewItemDroppedEventArgs e)
    {
      var target = e.TargetNode as TriggerTreeViewNode;
      target = ((!target.IsTrigger && !target.IsOverlay) && e.DropPosition == DropPosition.DropAsChild) ? target : target.ParentNode as TriggerTreeViewNode;

      if (target.SerializedData != null)
      {
        if (target.SerializedData.Nodes == null || target.SerializedData.Nodes.Count == 0)
        {
          target.SerializedData.Nodes = e.DraggingNodes.Cast<TriggerTreeViewNode>().Select(node => node.SerializedData).ToList();
          target.SerializedData.IsExpanded = true;
        }
        else
        {
          var newList = new List<TriggerNode>();
          var sources = target.SerializedData.Nodes.ToList();

          if (Removed != null)
          {
            sources.AddRange(Removed);
          }

          foreach (var viewNode in target.ChildNodes.Cast<TriggerTreeViewNode>())
          {
            var found = sources.Find(source => source == viewNode.SerializedData);
            if (found != null)
            {
              newList.Add(found);
              sources.Remove(found);
            }
          }

          if (sources.Count > 0)
          {
            newList.AddRange(sources);
          }

          target.SerializedData.Nodes = newList;
        }
      }

      TriggerManager.Instance.UpdateTriggers(false);
      TriggerOverlayManager.Instance.UpdateOverlays();
      RefreshTriggerNode();
      SelectionChanged(null);
    }

    private void CreateNodeClick(object sender, RoutedEventArgs e)
    {
      if (treeView.SelectedItem != null && treeView.SelectedItem is TriggerTreeViewNode node)
      {
        var newNode = new TriggerNode { Name = LABEL_NEW_FOLDER };
        node.SerializedData.Nodes = (node.SerializedData.Nodes == null) ? new List<TriggerNode>() : node.SerializedData.Nodes;
        node.SerializedData.IsExpanded = true;
        node.SerializedData.Nodes.Add(newNode);
        TriggerManager.Instance.UpdateTriggers();
        RefreshTriggerNode();
      }
    }

    private void CreateOverlay(bool isTimer)
    {
      if (treeView.SelectedItem != null && treeView.SelectedItem is TriggerTreeViewNode node)
      {
        var label = isTimer ? LABEL_NEW_TIMER_OVERLAY : LABEL_NEW_TEXT_OVERLAY;
        var newNode = new TriggerNode
        {
          Name = label,
          IsEnabled = true,
          OverlayData = new Overlay { Name = label, Id = Guid.NewGuid().ToString(), IsTimerOverlay = isTimer, IsTextOverlay = !isTimer }
        };

        if (!isTimer)
        {
          // give a better default for text overlays
          newNode.OverlayData.FontSize = "20pt";
        }

        node.SerializedData.Nodes = (node.SerializedData.Nodes == null) ? new List<TriggerNode>() : node.SerializedData.Nodes;
        node.SerializedData.IsExpanded = true;
        node.SerializedData.Nodes.Add(newNode);
        node.ChildNodes.Add(new TriggerTreeViewNode { Content = newNode.Name, IsChecked = true, IsOverlay = true, SerializedData = newNode });
        TriggerOverlayManager.Instance.UpdateOverlays();
        RefreshOverlayNode();
        SelectFile(newNode.OverlayData);
      }
    }

    private void CreateTriggerClick(object sender, RoutedEventArgs e)
    {
      if (treeView.SelectedItem != null && treeView.SelectedItem is TriggerTreeViewNode node)
      {
        var newNode = new TriggerNode { Name = LABEL_NEW_TRIGGER, IsEnabled = true, TriggerData = new Trigger { Name = LABEL_NEW_TRIGGER } };
        node.SerializedData.Nodes = (node.SerializedData.Nodes == null) ? new List<TriggerNode>() : node.SerializedData.Nodes;
        node.SerializedData.IsExpanded = true;
        node.SerializedData.Nodes.Add(newNode);
        node.ChildNodes.Add(new TriggerTreeViewNode { Content = newNode.Name, IsChecked = true, IsTrigger = true, SerializedData = newNode });
        TriggerManager.Instance.UpdateTriggers();
        RefreshTriggerNode();
        SelectFile(newNode.TriggerData);
      }
    }

    private void DeleteClick(object sender, RoutedEventArgs e)
    {
      if (treeView.SelectedItems != null)
      {
        bool updateTriggers = false;
        bool updateOverlays = false;
        foreach (var node in treeView.SelectedItems.Cast<TriggerTreeViewNode>())
        {
          if (node.ParentNode is TriggerTreeViewNode parent)
          {
            parent.SerializedData.Nodes.Remove(node.SerializedData);
            if (parent.SerializedData.Nodes.Count == 0)
            {
              parent.SerializedData.IsEnabled = false;
              parent.SerializedData.IsExpanded = false;
            }

            if (parent == treeView.Nodes[1])
            {
              updateOverlays = true;
              updateTriggers = true;
              if (node.SerializedData?.OverlayData != null)
              {
                TriggerOverlayManager.Instance.ClosePreviewTimerOverlay(node.SerializedData.OverlayData.Id);
                RemoveOverlayFromTriggers(treeView.Nodes[0] as TriggerTreeViewNode, node.SerializedData.OverlayData.Id);
              }
            }
            else
            {
              updateTriggers = true;
            }
          }
        }

        thePropertyGrid.SelectedObject = null;
        thePropertyGrid.IsEnabled = false;

        if (updateTriggers)
        {
          TriggerManager.Instance.UpdateTriggers();
          RefreshTriggerNode();
        }

        if (updateOverlays)
        {
          TriggerOverlayManager.Instance.UpdateOverlays();
          RefreshOverlayNode();
        }
      }
    }

    private void RemoveOverlayFromTriggers(TriggerTreeViewNode node, string id)
    {
      if (node.IsTrigger)
      {
        if (node.SerializedData != null && node.SerializedData.TriggerData != null)
        {
          node.SerializedData.TriggerData.SelectedOverlays.Remove(id);
        }
      }
      else if (!node.IsOverlay && node.ChildNodes != null)
      {
        foreach (var child in node.ChildNodes.Cast<TriggerTreeViewNode>())
        {
          RemoveOverlayFromTriggers(child, id);
        }
      }
    }

    private void DisableNodes(TriggerNode node)
    {
      if (node.TriggerData == null && node.OverlayData == null)
      {
        node.IsEnabled = false;
        node.IsExpanded = false;
        if (node.Nodes != null)
        {
          foreach (var child in node.Nodes)
          {
            DisableNodes(child);
          }
        }
      }
    }

    private void NodeExpanded(object sender, NodeExpandedCollapsedEventArgs e)
    {
      if (e.Node is TriggerTreeViewNode node)
      {
        node.SerializedData.IsExpanded = node.IsExpanded;
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
            else if (node.IsOverlay && node.SerializedData.OverlayData != null)
            {
              node.SerializedData.OverlayData.Name = node.Content as string;
              Application.Current.Resources["OverlayText-" + node.SerializedData.OverlayData.Id] = node.SerializedData.OverlayData.Name;
            }

            TriggerManager.Instance.UpdateTriggers(false);
            TriggerOverlayManager.Instance.UpdateOverlays();
          }
        }, System.Windows.Threading.DispatcherPriority.Normal);
      }
    }

    private void ItemContextMenuOpening(object sender, ItemContextMenuOpeningEventArgs e)
    {
      var node = treeView.SelectedItem as TriggerTreeViewNode;
      var count = (treeView.SelectedItems != null) ? treeView.SelectedItems.Count : 0;


      if (node != null)
      {
        var anyTriggers = treeView.SelectedItems.Cast<TriggerTreeViewNode>().Any(node => !node.IsOverlay && node != treeView.Nodes[1]);
        assignOverlayMenuItem.IsEnabled = anyTriggers;
        exportMenuItem.IsEnabled = anyTriggers;
        deleteTriggerMenuItem.IsEnabled = (node != treeView.Nodes[0] && node != treeView.Nodes[1]) || count > 1;
        renameMenuItem.IsEnabled = node != treeView.Nodes[0] && node != treeView.Nodes[1] && count == 1;
        importMenuItem.IsEnabled = !node.IsTrigger && !node.IsOverlay && node != treeView.Nodes[1] && count == 1;
        newMenuItem.IsEnabled = !node.IsTrigger && !node.IsOverlay && count == 1;
      }
      else
      {
        deleteTriggerMenuItem.IsEnabled = false;
        renameMenuItem.IsEnabled = false;
        importMenuItem.IsEnabled = false;
        exportMenuItem.IsEnabled = false;
        newMenuItem.IsEnabled = false;
        assignOverlayMenuItem.IsEnabled = false;
      }

      importMenuItem.Header = importMenuItem.IsEnabled ? "Import to Folder (" + node.Content.ToString() + ")" : "Import";

      if (newMenuItem.IsEnabled)
      {
        newFolder.Visibility = node == treeView.Nodes[1] ? Visibility.Collapsed : Visibility.Visible;
        newTrigger.Visibility = node == treeView.Nodes[1] ? Visibility.Collapsed : Visibility.Visible;
        newTimerOverlay.Visibility = node == treeView.Nodes[1] ? Visibility.Visible : Visibility.Collapsed;
        newTextOverlay.Visibility = node == treeView.Nodes[1] ? Visibility.Visible : Visibility.Collapsed;
      }

      if (assignOverlayMenuItem.IsEnabled)
      {
        foreach (var previous in assignTextOverlaysMenuItem.Items)
        {
          if (previous is MenuItem m)
          {
            m.Click -= AssignTextOverlayClick;
          }
        }

        assignTextOverlaysMenuItem.Items.Clear();
        foreach (var overlay in TriggerOverlayManager.Instance.GetTextOverlays())
        {
          var menuItem = new MenuItem { Header = overlay.Name + " (" + overlay.Id + ")" };
          menuItem.Click += AssignTextOverlayClick;
          menuItem.Tag = overlay.Id;
          assignTextOverlaysMenuItem.Items.Add(menuItem);
        }

        foreach (var previous in assignTimerOverlaysMenuItem.Items)
        {
          if (previous is MenuItem m)
          {
            m.Click -= AssignTimerOverlayClick;
          }
        }

        assignTimerOverlaysMenuItem.Items.Clear();
        foreach (var overlay in TriggerOverlayManager.Instance.GetTimerOverlays())
        {
          var menuItem = new MenuItem { Header = overlay.Name + " (" + overlay.Id + ")" };
          menuItem.Click += AssignTimerOverlayClick;
          menuItem.Tag = overlay.Id;
          assignTimerOverlaysMenuItem.Items.Add(menuItem);
        }
      }
    }

    private void AssignOverlay(object sender)
    {
      if (sender is MenuItem menuItem)
      {
        var id = menuItem.Tag.ToString();
        var anyFolders = treeView.SelectedItems.Cast<TriggerTreeViewNode>().Any(node => !node.IsOverlay && !node.IsTrigger && node != treeView.Nodes[1]);
        if (!anyFolders)
        {
          treeView.SelectedItems.Cast<TriggerTreeViewNode>().ToList().ForEach(node =>
          {
            if (node.IsTrigger && node.SerializedData != null)
            {
              if (!node.SerializedData.TriggerData.SelectedOverlays.Contains(id))
              {
                node.SerializedData.TriggerData.SelectedOverlays.Add(id);
              }
            }
          });
        }
        else
        {
          var msgDialog = new MessageWindow("Are you sure? This will Assign all selected Triggers and those in all sub folders.",
            EQLogParser.Resource.ASSIGN_TIMER_OVERLAY, MessageWindow.IconType.Question, "Yes");
          msgDialog.ShowDialog();
          if (msgDialog.IsYes1Clicked)
          {
            treeView.SelectedItems.Cast<TriggerTreeViewNode>().ToList().ForEach(node => AssignOverlay(node.SerializedData, id));
          }
        }

        TriggerManager.Instance.UpdateTriggers();
        SelectionChanged(treeView.SelectedItem as TriggerTreeViewNode);
      }
    }

    private void AssignOverlay(TriggerNode node, string id)
    {
      if (node.TriggerData != null)
      {
        if (!node.TriggerData.SelectedOverlays.Contains(id))
        {
          node.TriggerData.SelectedOverlays.Add(id);
        }
      }
      else if (node.OverlayData == null)
      {
        node.Nodes.ForEach(node => AssignOverlay(node, id));
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
      var isTrigger = (node?.IsTrigger == true);
      var isOverlay = (node?.IsOverlay == true);
      var isTimerOverlay = (node?.SerializedData?.OverlayData?.IsTimerOverlay == true);

      if (isTrigger || isOverlay)
      {
        if (isTrigger)
        {
          model = new TriggerPropertyModel { Original = node.SerializedData.TriggerData };
          TriggerUtil.Copy(model, node.SerializedData.TriggerData);
        }
        else if (isOverlay)
        {
          if (!isTimerOverlay)
          {
            model = new TextOverlayPropertyModel { Original = node.SerializedData.OverlayData };
            TriggerUtil.Copy(model, node.SerializedData.OverlayData);
          }
          else if (isTimerOverlay)
          {
            model = new TimerOverlayPropertyModel { Original = node.SerializedData.OverlayData };
            TriggerUtil.Copy(model, node.SerializedData.OverlayData);
            model.TimerBarPreview = model.Id;
          }
        }

        saveButton.IsEnabled = false;
        cancelButton.IsEnabled = false;
      }

      thePropertyGrid.SelectedObject = model;
      thePropertyGrid.IsEnabled = (thePropertyGrid.SelectedObject != null);
      thePropertyGrid.DescriptionPanelVisibility = (isTrigger || isOverlay) ? Visibility.Visible : Visibility.Collapsed;
      showButton.Visibility = isOverlay ? Visibility.Visible : Visibility.Collapsed;

      if (isTrigger)
      {
        EnableCategories(true, node.SerializedData.TriggerData.EnableTimer, true, false, false, true, false);
      }
      else if (isOverlay)
      {
        if (isTimerOverlay)
        {
          EnableCategories(false, false, false, true, true, false, false);
        }
        else
        {
          EnableCategories(false, false, false, true, false, false, true);
        }
      }
    }

    private void EnableCategories(bool trigger, bool triggerTimer, bool status, bool overlay, bool overlayTimer, bool overlayAssigned, bool overlayText)
    {
      PropertyGridUtil.EnableCategories(thePropertyGrid, new[]
      {
        new { Name = patternItem.CategoryName, IsEnabled = trigger },
        new { Name = timerDurationItem.CategoryName, IsEnabled = triggerTimer },
        new { Name = evalTimeItem.CategoryName, IsEnabled = status },
        new { Name = fontSizeItem.CategoryName, IsEnabled = overlay },
        new { Name = primaryBrushItem.CategoryName, IsEnabled = overlayTimer },
        new { Name = assignedOverlaysItem.CategoryName, IsEnabled = overlayAssigned },
        new { Name = fadeDelayItem.CategoryName, IsEnabled = overlayText }
      });
    }

    private void NodeChecked(object sender, NodeCheckedEventArgs e)
    {
      if (e.Node is TriggerTreeViewNode node)
      {
        node.SerializedData.IsEnabled = node.IsChecked;

        if (!node.IsTrigger && !node.IsOverlay)
        {
          CheckParent(node);
          CheckChildren(node, node.IsChecked);
        }

        TriggerManager.Instance.UpdateTriggers();
        TriggerOverlayManager.Instance.UpdateOverlays();
      }
    }

    private void CheckChildren(TriggerTreeViewNode node, bool? value)
    {
      foreach (var child in node.ChildNodes.Cast<TriggerTreeViewNode>())
      {
        child.SerializedData.IsEnabled = value;
        if (!child.IsTrigger && !child.IsOverlay)
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

    private void SaveNodeExpanded(List<TriggerTreeViewNode> nodes)
    {
      foreach (var node in nodes)
      {
        node.SerializedData.IsExpanded = node.IsExpanded;

        if (!node.IsTrigger && !node.IsOverlay)
        {
          SaveNodeExpanded(node.ChildNodes.Cast<TriggerTreeViewNode>().ToList());
        }
      }
    }

    private void ValueChanged(object sender, ValueChangedEventArgs args)
    {
      if (args.Property.Name != errorsItem.PropertyName && args.Property.Name != evalTimeItem.PropertyName &&
        args.Property.SelectedObject is TriggerPropertyModel trigger)
      {
        var list = thePropertyGrid.Properties.ToList();
        var errorsProp = PropertyGridUtil.FindProperty(list, errorsItem.PropertyName);
        var longestProp = PropertyGridUtil.FindProperty(list, evalTimeItem.PropertyName);

        bool isValid = trigger.UseRegex ? TestRegexProperty(trigger, trigger.Pattern, errorsProp) : true;
        isValid = (isValid && trigger.EndUseRegex) ? TestRegexProperty(trigger, trigger.EndEarlyPattern, errorsProp) : isValid;
        isValid = (isValid && trigger.EndUseRegex2) ? TestRegexProperty(trigger, trigger.EndEarlyPattern2, errorsProp) : isValid;

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
          PropertyGridUtil.EnableCategories(thePropertyGrid, new[] { new { Name = timerDurationItem.CategoryName, IsEnabled = (bool)args.Property.Value } });
        }

        saveButton.IsEnabled = (trigger.Errors == "None");
        cancelButton.IsEnabled = true;
      }
      else if (args.Property.SelectedObject is TextOverlayPropertyModel textOverlay)
      {
        var textChange = true;

        if (args.Property.Name == overlayBrushItem.PropertyName)
        {
          textChange = !(textOverlay.OverlayBrush.Color == (Color)ColorConverter.ConvertFromString(textOverlay.Original.OverlayColor));
          Application.Current.Resources["OverlayBrushColor-" + textOverlay.Id] = textOverlay.OverlayBrush;
        }
        else if (args.Property.Name == fontBrushItem.PropertyName)
        {
          textChange = !(textOverlay.FontBrush.Color == (Color)ColorConverter.ConvertFromString(textOverlay.Original.FontColor));
          Application.Current.Resources["TextOverlayFontColor-" + textOverlay.Id] = textOverlay.FontBrush;
        }
        else if (args.Property.Name == fontSizeItem.PropertyName && textOverlay.FontSize.Split("pt") is string[] split && split.Length == 2
         && double.TryParse(split[0], out double newFontSize))
        {
          textChange = textOverlay.FontSize != textOverlay.Original.FontSize;
          Application.Current.Resources["TextOverlayFontSize-" + textOverlay.Id] = newFontSize;
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

        if (args.Property.Name == overlayBrushItem.PropertyName)
        {
          timerChange = !(timerOverlay.OverlayBrush.Color == (Color)ColorConverter.ConvertFromString(timerOverlay.Original.OverlayColor));
          Application.Current.Resources["OverlayBrushColor-" + timerOverlay.Id] = timerOverlay.OverlayBrush;
        }
        else if (args.Property.Name == primaryBrushItem.PropertyName)
        {
          timerChange = !(timerOverlay.PrimaryBrush.Color == (Color)ColorConverter.ConvertFromString(timerOverlay.Original.PrimaryColor));
          Application.Current.Resources["TimerBarProgressColor-" + timerOverlay.Id] = timerOverlay.PrimaryBrush;
        }
        else if (args.Property.Name == secondaryBrushItem.PropertyName)
        {
          timerChange = !(timerOverlay.SecondaryBrush.Color == (Color)ColorConverter.ConvertFromString(timerOverlay.Original.SecondaryColor));
          Application.Current.Resources["TimerBarTrackColor-" + timerOverlay.Id] = timerOverlay.SecondaryBrush;
        }
        else if (args.Property.Name == fontBrushItem.PropertyName)
        {
          timerChange = !(timerOverlay.FontBrush.Color == (Color)ColorConverter.ConvertFromString(timerOverlay.Original.FontColor));
          Application.Current.Resources["TimerBarFontColor-" + timerOverlay.Id] = timerOverlay.FontBrush;
        }
        else if (args.Property.Name == fontSizeItem.PropertyName && timerOverlay.FontSize.Split("pt") is string[] split && split.Length == 2
         && double.TryParse(split[0], out double newFontSize))
        {
          timerChange = timerOverlay.FontSize != timerOverlay.Original.FontSize;
          Application.Current.Resources["TimerBarFontSize-" + timerOverlay.Id] = newFontSize;
          Application.Current.Resources["TimerBarHeight-" + timerOverlay.Id] = TriggerUtil.GetTimerBarHeight(newFontSize);
        }

        if (timerChange)
        {
          saveButton.IsEnabled = true;
          cancelButton.IsEnabled = true;
        }
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

    private void ShowClick(object sender, RoutedEventArgs e)
    {
      if (thePropertyGrid.SelectedObject is TimerOverlayPropertyModel timerModel)
      {
        TriggerOverlayManager.Instance.PreviewTimerOverlay(timerModel.Id);
      }
      else if (thePropertyGrid.SelectedObject is TextOverlayPropertyModel textModel)
      {
        TriggerOverlayManager.Instance.PreviewTextOverlay(textModel.Id);
      }
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
      if (thePropertyGrid.SelectedObject is TriggerPropertyModel triggerModel)
      {
        TriggerUtil.Copy(triggerModel.Original, triggerModel);
        TriggerManager.Instance.UpdateTriggers();
      }
      else if (thePropertyGrid.SelectedObject is TextOverlayPropertyModel textModel)
      {
        TriggerUtil.Copy(textModel.Original, textModel);
        TriggerOverlayManager.Instance.UpdateOverlays();
      }
      else if (thePropertyGrid.SelectedObject is TimerOverlayPropertyModel timerModel)
      {
        TriggerUtil.Copy(timerModel.Original, timerModel);
        TriggerOverlayManager.Instance.UpdateOverlays();
      }

      cancelButton.IsEnabled = false;
      saveButton.IsEnabled = false;
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
      if (thePropertyGrid.SelectedObject is TriggerPropertyModel triggerModel)
      {
        TriggerUtil.Copy(triggerModel, triggerModel.Original);
        PropertyGridUtil.EnableCategories(thePropertyGrid,
          new[] { new { Name = timerDurationItem.CategoryName, IsEnabled = triggerModel.Original.EnableTimer } });
      }
      else if (thePropertyGrid.SelectedObject is TimerOverlayPropertyModel timerModel)
      {
        TriggerUtil.Copy(timerModel, timerModel.Original);
      }
      else if (thePropertyGrid.SelectedObject is TextOverlayPropertyModel textModel)
      {
        TriggerUtil.Copy(textModel, textModel.Original);
      }

      thePropertyGrid.RefreshPropertygrid();
      Dispatcher.InvokeAsync(() =>
      {
        cancelButton.IsEnabled = false;
        saveButton.IsEnabled = false;
      }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void TreeViewPreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
      if (e.OriginalSource is FrameworkElement element && element.DataContext is TriggerTreeViewNode node)
      {
        if (System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LeftCtrl) ||
          System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RightCtrl))
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
        (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete -= EventsLogLoadingComplete;
        TriggerManager.Instance.EventsUpdateTree -= EventsUpdateTriggerTree;
        TriggerManager.Instance.EventsSelectTrigger -= EventsSelectTrigger;
        treeView.DragDropController.Dispose();
        treeView.Dispose();
        thePropertyGrid?.Dispose();
        TestSynth?.Dispose();
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
