using FontAwesome5;
using Microsoft.Win32;
using Syncfusion.UI.Xaml.TreeView;
using Syncfusion.Windows.PropertyGrid;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Speech.Synthesis;
using System.Text.Json;
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

    private const string LABEL_NEW_OVERLAY = "New Overlay";
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
      priorityEditor.Properties.Add("Priority");
      thePropertyGrid.CustomEditorCollection.Add(priorityEditor);

      var textWrapEditor = new CustomEditor();
      textWrapEditor.Editor = new WrapTextEditor();
      textWrapEditor.Properties.Add("Comments");
      textWrapEditor.Properties.Add("Pattern");
      textWrapEditor.Properties.Add("EndPattern");
      textWrapEditor.Properties.Add("CancelPattern");
      textWrapEditor.Properties.Add("EndTextToSpeak");
      textWrapEditor.Properties.Add("TextToSpeak");
      textWrapEditor.Properties.Add("WarningToSpeak");
      thePropertyGrid.CustomEditorCollection.Add(textWrapEditor);

      var errorEditor = new CustomEditor();
      errorEditor.Editor = ErrorEditor = new WrapTextEditor();
      errorEditor.Properties.Add("Errors");
      thePropertyGrid.CustomEditorCollection.Add(errorEditor);

      var colorEditor = new CustomEditor();
      colorEditor.Editor = new ColorEditor();
      colorEditor.Properties.Add("FontBrush");
      colorEditor.Properties.Add("PrimaryBrush");
      colorEditor.Properties.Add("SecondaryBrush");
      thePropertyGrid.CustomEditorCollection.Add(colorEditor);

      var listEditor = new CustomEditor();
      listEditor.Editor = new TriggerListsEditor();
      listEditor.Properties.Add("TriggerAgainOption");
      listEditor.Properties.Add("FontSize");
      listEditor.Properties.Add("SortBy");
      thePropertyGrid.CustomEditorCollection.Add(listEditor);

      var timeEditor = new CustomEditor();
      timeEditor.Editor = new RangeEditor(0, 60);
      timeEditor.Properties.Add("Seconds");
      timeEditor.Properties.Add("Minutes");
      thePropertyGrid.CustomEditorCollection.Add(timeEditor);

      treeView.Nodes.Add(TriggerManager.Instance.GetTriggerTreeView());
      treeView.Nodes.Add(TriggerManager.Instance.GetOverlayTreeView());

      TriggerManager.Instance.EventsUpdateTree += EventsUpdateTriggerTree;
      TriggerManager.Instance.EventsSelectTrigger += EventsSelectTrigger;
      (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete += EventsLogLoadingComplete;
    }

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

    private void EventsSelectTrigger(object sender, Trigger e)
    {
      if (e != null && (treeView.SelectedItem == null || (treeView.SelectedItem is TriggerTreeViewNode selected &&
        selected.SerializedData.TriggerData != null && selected.SerializedData.TriggerData != e)))
      {
        var found = FindAndExpandNode(treeView.Nodes[0] as TriggerTreeViewNode, e);
        treeView.SelectedItem = found;
        SelectionChanged(found);
      }
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

    private void EventsUpdateOverlayTree(object sender, bool e)
    {
      Dispatcher.InvokeAsync(() =>
      {
        treeView.Nodes.Remove(treeView.Nodes[1]);
        treeView.Nodes.Add(TriggerManager.Instance.GetOverlayTreeView());
      });
    }

    private void EventsUpdateTriggerTree(object sender, bool e)
    {
      Dispatcher.InvokeAsync(() =>
      {
        treeView.Nodes.Remove(treeView.Nodes[0]);
        treeView.Nodes.Insert(0, TriggerManager.Instance.GetTriggerTreeView());
      });
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
            var rateText = rateOption.SelectedIndex == 0 ? "Default Voice Rate" : "Voice Rate " + rateOption.SelectedIndex.ToString();
            TestSynth.SpeakAsync(rateText);
          }
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
      var list = e.DraggingNodes.ToList();
      list.Reverse();
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

      TriggerManager.Instance.UpdateTriggers(true);
      EventsUpdateTriggerTree(this, true);
      SelectionChanged(null);
    }

    private void CreateNodeClick(object sender, RoutedEventArgs e)
    {
      if (treeView.SelectedItem != null && treeView.SelectedItem is TriggerTreeViewNode node)
      {
        var newNode = new TriggerNode { Name = LABEL_NEW_FOLDER };

        if (node.SerializedData.Nodes == null)
        {
          node.SerializedData.Nodes = new List<TriggerNode>();
        }

        node.SerializedData.IsExpanded = true;
        node.SerializedData.Nodes.Add(newNode);
        TriggerManager.Instance.UpdateTriggers();
        EventsUpdateTriggerTree(this, true);
      }
    }

    private void CreateOverlayClick(object sender, RoutedEventArgs e)
    {
      if (treeView.SelectedItem != null && treeView.SelectedItem is TriggerTreeViewNode node)
      {
        var newOverlay = new TriggerNode
        {
          Name = LABEL_NEW_OVERLAY,
          IsEnabled = true,
          OverlayData = new Overlay { Name = LABEL_NEW_OVERLAY, Id = Guid.NewGuid().ToString() }
        };

        if (node.SerializedData.Nodes == null)
        {
          node.SerializedData.Nodes = new List<TriggerNode>();
        }

        node.SerializedData.IsExpanded = true;
        node.SerializedData.Nodes.Add(newOverlay);
        TriggerManager.Instance.UpdateOverlays();
        EventsUpdateOverlayTree(this, true);
      }
    }

    private void CreateTriggerClick(object sender, RoutedEventArgs e)
    {
      if (treeView.SelectedItem != null && treeView.SelectedItem is TriggerTreeViewNode node)
      {
        var newTrigger = new TriggerNode { Name = LABEL_NEW_TRIGGER, IsEnabled = true, TriggerData = new Trigger { Name = LABEL_NEW_TRIGGER } };

        if (node.SerializedData.Nodes == null)
        {
          node.SerializedData.Nodes = new List<TriggerNode>();
        }

        node.SerializedData.IsExpanded = true;
        node.SerializedData.Nodes.Add(newTrigger);
        TriggerManager.Instance.UpdateTriggers();
        EventsUpdateTriggerTree(this, true);
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
            }
            else
            {
              updateTriggers = true;
            }
          }
        }

        thePropertyGrid.SelectedObject = null;
        thePropertyGrid.IsEnabled = false;
        thePropertyGrid.DescriptionPanelVisibility = Visibility.Collapsed;

        if (updateTriggers)
        {
          EventsUpdateTriggerTree(this, true);
          TriggerManager.Instance.UpdateTriggers();
        }

        if (updateOverlays)
        {
          EventsUpdateOverlayTree(this, true);
          TriggerManager.Instance.UpdateOverlays();
        }
      }
    }

    private void RenameClick(object sender, RoutedEventArgs e)
    {
      if (treeView.SelectedItems?.Count == 1)
      {
        treeView.BeginEdit(treeView.SelectedItem as TriggerTreeViewNode);
      }
    }

    private void ExportClick(object sender, RoutedEventArgs e)
    {
      if (treeView.SelectedItems?.Count > 0)
      {
        try
        {
          var exportList = new List<TriggerNode>();
          foreach (var selected in treeView.SelectedItems.Cast<TriggerTreeViewNode>())
          {
            // if the root is in there just use it
            if (selected == treeView.Nodes[0])
            {
              exportList = new List<TriggerNode>() { selected.SerializedData };
              break;
            }
            else if (selected.IsOverlay || selected == treeView.Nodes[1])
            {
              break;
            }

            var start = selected.ParentNode as TriggerTreeViewNode;
            var child = selected.SerializedData;
            TriggerNode newNode = null;
            while (start != null)
            {
              newNode = new TriggerNode
              {
                Name = start.SerializedData.Name,
                IsEnabled = start.SerializedData.IsEnabled,
                IsExpanded = start.SerializedData.IsExpanded,
                Nodes = new List<TriggerNode>() { child }
              };

              child = newNode;
              start = start.ParentNode as TriggerTreeViewNode;
            }

            if (newNode != null)
            {
              exportList.Add(newNode);
            }
          }

          if (exportList.Count > 0)
          {
            var result = System.Text.Json.JsonSerializer.Serialize(exportList, new JsonSerializerOptions { IncludeFields = true });
            SaveFileDialog saveFileDialog = new SaveFileDialog();
            string filter = "Triggers File (*.tgf.gz)|*.tgf.gz";
            saveFileDialog.Filter = filter;
            if (saveFileDialog.ShowDialog().Value)
            {
              FileInfo gzipFileName = new FileInfo(saveFileDialog.FileName);
              FileStream gzipTargetAsStream = gzipFileName.Create();
              GZipStream gzipStream = new GZipStream(gzipTargetAsStream, CompressionMode.Compress);
              var writer = new StreamWriter(gzipStream);
              writer?.Write(result);
              writer?.Close();
            }
          }
        }
        catch (Exception ex)
        {
          new MessageWindow("Problem Exporting Triggers. Check Error Log for details.", EQLogParser.Resource.EXPORT_ERROR).ShowDialog();
          LOG.Error(ex);
        }
      }
    }

    private void ImportClick(object sender, RoutedEventArgs e)
    {
      if (treeView?.SelectedItem is TriggerTreeViewNode node)
      {
        try
        {
          // WPF doesn't have its own file chooser so use Win32 Version
          OpenFileDialog dialog = new OpenFileDialog
          {
            // filter to txt files
            DefaultExt = ".scf.gz",
            Filter = "All Supported Files|*.tgf.gz;*.gtp"
          };

          // show dialog and read result
          if (dialog.ShowDialog().Value)
          {
            // limit to 100 megs just incase
            var fileInfo = new FileInfo(dialog.FileName);
            if (fileInfo.Exists && fileInfo.Length < 100000000)
            {
              if (dialog.FileName.EndsWith("tgf.gz"))
              {
                GZipStream decompressionStream = new GZipStream(fileInfo.OpenRead(), CompressionMode.Decompress);
                var reader = new StreamReader(decompressionStream);
                string json = reader?.ReadToEnd();
                reader?.Close();
                var data = JsonSerializer.Deserialize<List<TriggerNode>>(json, new JsonSerializerOptions { IncludeFields = true });
                TriggerManager.Instance.MergeTriggers(data, node.SerializedData);
              }
              else if (dialog.FileName.EndsWith(".gtp"))
              {
                var data = new byte[fileInfo.Length];
                fileInfo.OpenRead().Read(data);
                TriggerUtil.ImportFromGina(data, node.SerializedData);
              }
            }
          }
        }
        catch (Exception ex)
        {
          new MessageWindow("Problem Importing Triggers. Check Error Log for details.", EQLogParser.Resource.IMPORT_ERROR).ShowDialog();
          LOG.Error("Import Failure", ex);
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
            }

            TriggerManager.Instance.UpdateTriggers(false);
          }
        }, System.Windows.Threading.DispatcherPriority.Background);
      }
    }

    private void ItemContextMenuOpening(object sender, ItemContextMenuOpeningEventArgs e)
    {
      var node = treeView.SelectedItem as TriggerTreeViewNode;
      var count = (treeView.SelectedItems != null) ? treeView.SelectedItems.Count : 0;


      if (node != null)
      {
        deleteTriggerMenuItem.IsEnabled = (node != treeView.Nodes[0] && node != treeView.Nodes[1]) || count > 1;
        renameMenuItem.IsEnabled = node != treeView.Nodes[0] && node != treeView.Nodes[1] && count == 1;
        importMenuItem.IsEnabled = !node.IsTrigger && !node.IsOverlay && node != treeView.Nodes[1] && count == 1;
        exportMenuItem.IsEnabled = treeView.SelectedItems.Cast<TriggerTreeViewNode>().Any(node => !node.IsOverlay && node != treeView.Nodes[1]);
        newMenuItem.IsEnabled = !node.IsTrigger && !node.IsOverlay && count == 1;
      }
      else
      {
        deleteTriggerMenuItem.IsEnabled = false;
        renameMenuItem.IsEnabled = false;
        importMenuItem.IsEnabled = false;
        exportMenuItem.IsEnabled = false;
        newMenuItem.IsEnabled = false;
      }

      importMenuItem.Header = importMenuItem.IsEnabled ? "Import to " + node.Content.ToString() : "Import";
      if (newMenuItem.IsEnabled)
      {
        newFolder.Visibility = node == treeView.Nodes[0] ? Visibility.Visible : Visibility.Collapsed;
        newTrigger.Visibility = node == treeView.Nodes[0] ? Visibility.Visible : Visibility.Collapsed;
        newOverlay.Visibility = node == treeView.Nodes[1] ? Visibility.Visible : Visibility.Collapsed;
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
      object model = null;
      var isTrigger = (node?.IsTrigger == true);
      var isOverlay = (node?.IsOverlay == true);

      if (isTrigger || isOverlay)
      {
        if (isTrigger)
        {
          model = new TriggerPropertyModel { Original = node.SerializedData.TriggerData };
          TriggerUtil.Copy(model, node.SerializedData.TriggerData);
        }
        else if (isOverlay)
        {
          model = new OverlayPropertyModel { Original = node.SerializedData.OverlayData };
          TriggerUtil.Copy(model, node.SerializedData.OverlayData);

          if (node.SerializedData.OverlayData.FontColor is string fontColor && !string.IsNullOrEmpty(fontColor))
          {
            (model as OverlayPropertyModel).FontBrush = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString(fontColor) };
          }

          if (node.SerializedData.OverlayData.PrimaryColor is string primary && !string.IsNullOrEmpty(primary))
          {
            (model as OverlayPropertyModel).PrimaryBrush = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString(primary) };
          }

          if (node.SerializedData.OverlayData.SecondaryColor is string secondary && !string.IsNullOrEmpty(secondary))
          {
            (model as OverlayPropertyModel).SecondaryBrush = new SolidColorBrush { Color = (Color)ColorConverter.ConvertFromString(secondary) };
          }
        }

        saveButton.IsEnabled = false;
        cancelButton.IsEnabled = false;
      }

      thePropertyGrid.SelectedObject = model;
      thePropertyGrid.IsEnabled = (thePropertyGrid.SelectedObject != null);
      thePropertyGrid.DescriptionPanelVisibility = (isTrigger || isOverlay) ? Visibility.Visible : Visibility.Collapsed;
      buttonPanel.Visibility = (isTrigger || isOverlay) ? Visibility.Visible : Visibility.Collapsed;

      if (isTrigger)
      {
        EnableCategory(timerDurationItem.CategoryName, node.SerializedData.TriggerData.EnableTimer);
        EnableCategory(patternItem.CategoryName, true);
        EnableCategory(evalTimeItem.CategoryName, true);
        EnableCategory(fontSizeItem.CategoryName, false);
      }
      else if (isOverlay)
      {
        EnableCategory(timerDurationItem.CategoryName, false);
        EnableCategory(patternItem.CategoryName, false);
        EnableCategory(evalTimeItem.CategoryName, false);
        EnableCategory(fontSizeItem.CategoryName, true);
      }
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
        args.Property.SelectedObject is Trigger trigger)
      {
        var list = thePropertyGrid.Properties.ToList();
        var errorsProp = FindProperty(list, errorsItem.PropertyName);
        var longestProp = FindProperty(list, evalTimeItem.PropertyName);

        bool isValid = true;
        if (trigger.UseRegex)
        {
          isValid = TestRegexProperty(trigger, trigger.Pattern, errorsProp);
        }

        if (isValid && trigger.EndUseRegex)
        {
          isValid = TestRegexProperty(trigger, trigger.CancelPattern, errorsProp);
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
      else if (args.Property.SelectedObject is Overlay overlay)
      {
        saveButton.IsEnabled = true;
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
      if (thePropertyGrid.SelectedObject is TriggerPropertyModel triggerModel)
      {
        TriggerUtil.Copy(triggerModel.Original, triggerModel);
        TriggerManager.Instance.UpdateTriggers();
      }
      else if (thePropertyGrid.SelectedObject is OverlayPropertyModel overlayModel)
      {
        overlayModel.FontColor = TextFormatUtils.GetHexString(overlayModel.FontBrush.Color);
        overlayModel.PrimaryColor = TextFormatUtils.GetHexString(overlayModel.PrimaryBrush.Color);
        overlayModel.SecondaryColor = TextFormatUtils.GetHexString(overlayModel.SecondaryBrush.Color);
        TriggerUtil.Copy(overlayModel.Original, overlayModel);
        TriggerManager.Instance.UpdateOverlays();
      }

      cancelButton.IsEnabled = false;
      saveButton.IsEnabled = false;
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

    private PropertyItem FindProperty(List<object> list, string name)
    {
      foreach (var prop in list)
      {
        if (prop is PropertyItem item)
        {
          if (item.Name == name)
          {
            return item;
          }
        }
        else if (prop is PropertyCategoryViewItemCollection sub && FindProperty(sub.Properties.ToList(), name) is PropertyItem found)
        {
          return found;
        }
      }

      return null;
    }

    private TriggerTreeViewNode FindAndExpandNode(TriggerTreeViewNode node, Trigger trigger)
    {
      if (node.SerializedData?.TriggerData == trigger)
      {
        return node;
      }

      foreach (var child in node.ChildNodes.Cast<TriggerTreeViewNode>())
      {
        if (FindAndExpandNode(child, trigger) is TriggerTreeViewNode found)
        {
          treeView.ExpandNode(node);
          return found;
        }
      }

      return null;
    }

    internal class OverlayPropertyModel : Overlay
    {
      public SolidColorBrush FontBrush { get; set; }
      public SolidColorBrush PrimaryBrush { get; set; }
      public SolidColorBrush SecondaryBrush { get; set; }
      public Overlay Original { get; set; }
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
        (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete -= EventsLogLoadingComplete;
        TriggerManager.Instance.EventsUpdateTree -= EventsUpdateTriggerTree;
        TriggerManager.Instance.EventsSelectTrigger -= EventsSelectTrigger;
        treeView.DragDropController.Dispose();
        treeView.Dispose();
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
