using Syncfusion.Windows.PropertyGrid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
  public partial class TriggersView : IDisposable
  {
    private readonly Dictionary<string, Window> PreviewWindows = new();
    private TriggerConfig TheConfig;
    private readonly FileSystemWatcher Watcher;
    private readonly PatternEditor PatternEditor;
    private readonly PatternEditor EndEarlyPatternEditor;
    private readonly PatternEditor EndEarlyPattern2Editor;
    private readonly RangeEditor TopEditor;
    private readonly RangeEditor LeftEditor;
    private readonly RangeEditor HeightEditor;
    private readonly RangeEditor WidthEditor;
    private readonly SpeechSynthesizer TestSynth;
    private string CurrentCharacterId;
    private readonly GridLength CharacterViewWidth;
    private readonly bool Ready;

    public TriggersView()
    {
      InitializeComponent();
      CharacterViewWidth = mainGrid.ColumnDefinitions[0].Width;
      var config = TriggerStateManager.Instance.GetConfig();
      characterView.SetConfig(config);
      UpdateConfig(config);

      if ((TestSynth = TriggerUtil.GetSpeechSynthesizer()) != null)
      {
        voices.ItemsSource = TestSynth.GetInstalledVoices().Select(voice => voice.VoiceInfo.Name).ToList();
      }

      if (ConfigUtil.IfSet("TriggersWatchForQuickShare"))
      {
        watchQuickShare.IsChecked = true;
      }

      var selectedVoice = TriggerUtil.GetSelectedVoice();
      if (voices.ItemsSource is List<string> populated && populated.IndexOf(selectedVoice) is var found and > -1)
      {
        voices.SelectedIndex = found;
      }

      rateOption.SelectedIndex = TriggerUtil.GetVoiceRate();
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
      AddEditor<WrapTextEditor>("EndEarlyTextToDisplay", "EndTextToDisplay", "TextToDisplay", "TextToShare",
        "WarningTextToDisplay", "Comments", "OverlayComments");
      AddEditorInstance(new RangeEditor(typeof(double), 0.2, 2.0), "DurationSeconds");
      AddEditorInstance(new TextSoundEditor(fileList), "SoundOrText");
      AddEditorInstance(new TextSoundEditor(fileList), "EndEarlySoundOrText");
      AddEditorInstance(new TextSoundEditor(fileList), "EndSoundOrText");
      AddEditorInstance(new TextSoundEditor(fileList), "WarningSoundOrText");
      AddEditorInstance(new RangeEditor(typeof(long), 1, 5), "Priority");
      AddEditorInstance(new RangeEditor(typeof(long), 0, 99999), "WarningSeconds");
      AddEditorInstance(new RangeEditor(typeof(long), 1, 99999), "TimesToLoop");
      AddEditorInstance(new RangeEditor(typeof(double), 0, 99999), "RepeatedResetTime");
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

      theTreeView.Init(CurrentCharacterId, IsCancelSelection, !config.IsAdvanced);
      theTreeView.TreeSelectionChangedEvent += TreeSelectionChangedEvent;
      theTreeView.ClosePreviewOverlaysEvent += ClosePreviewOverlaysEvent;
      TriggerStateManager.Instance.DeleteEvent += TriggerOverlayDeleteEvent;
      TriggerStateManager.Instance.TriggerUpdateEvent += TriggerUpdateEvent;
      TriggerStateManager.Instance.TriggerConfigUpdateEvent += TriggerConfigUpdateEvent;
      TriggerStateManager.Instance.TriggerImportEvent += TriggerImportEvent;
      characterView.SelectedCharacterEvent += CharacterSelectedCharacterEvent;
      Ready = true;
    }

    private void TriggerImportEvent(bool _)
    {
      theTreeView.RefreshTriggers();
      // in case of merge
      TriggerManager.Instance.TriggersUpdated();
    }

    internal bool IsCancelSelection()
    {
      dynamic model = thePropertyGrid?.SelectedObject;
      var cancel = false;
      if (saveButton.IsEnabled)
      {
        if (model is TriggerPropertyModel || model is TextOverlayPropertyModel || model is TimerOverlayPropertyModel)
        {
          if (model.Node?.Name is string name)
          {
            var msgDialog = new MessageWindow("Do you want to save changes to " + name + "?", Resource.UNSAVED,
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

    private void TriggerConfigUpdateEvent(TriggerConfig config) => UpdateConfig(config);

    private void BasicChecked(object sender, RoutedEventArgs e)
    {
      if (Ready && sender is CheckBox checkBox)
      {
        TheConfig.IsEnabled = checkBox.IsChecked == true;
        TriggerStateManager.Instance.UpdateConfig(TheConfig);
      }
    }

    private void CharacterSelectedCharacterEvent(TriggerCharacter character)
    {
      if (character == null)
      {
        if (CurrentCharacterId != null)
        {
          CurrentCharacterId = null;
          thePropertyGrid.SelectedObject = null;
          theTreeView.EnableAndRefreshTriggers(false, CurrentCharacterId);
        }
      }
      else
      {
        if (CurrentCharacterId != character.Id)
        {
          CurrentCharacterId = character.Id;
          thePropertyGrid.SelectedObject = null;
          theTreeView.EnableAndRefreshTriggers(true, CurrentCharacterId);
        }
      }
    }

    private void ToggleAdvancedPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
      if (advancedText != null)
      {
        if (advancedText.Text == "Switch to Advanced Settings")
        {
          TheConfig.IsAdvanced = true;
          basicCheckBox.Visibility = Visibility.Collapsed;
        }
        else
        {
          TheConfig.IsAdvanced = false;
          basicCheckBox.Visibility = Visibility.Visible;
        }

        TriggerStateManager.Instance.UpdateConfig(TheConfig);
      }
    }

    private void UpdateConfig(TriggerConfig config)
    {
      TheConfig = config; // use latest
      theTreeView.SetConfig(TheConfig);
      basicCheckBox.Visibility = !TheConfig.IsAdvanced ? Visibility.Visible : Visibility.Collapsed;
      basicCheckBox.IsChecked = TheConfig.IsEnabled;

      if (TheConfig.IsAdvanced)
      {
        CharacterSelectedCharacterEvent(characterView.GetSelectedCharacter());

        if (TheConfig.Characters.Count(user => user.IsEnabled) is var count and > 0)
        {
          titleLabel.SetResourceReference(ForegroundProperty, "EQGoodForegroundBrush");
          var updatedTitle = $"Triggers Active for {count} Character";
          if (count > 1)
          {
            updatedTitle = $"{updatedTitle}s";
          }
          titleLabel.Content = updatedTitle;
        }
        else
        {
          titleLabel.SetResourceReference(ForegroundProperty, "EQStopForegroundBrush");
          titleLabel.Content = "No Triggers Active";
        }

        advancedText.Text = "Switch to Basic Settings";
        mainGrid.ColumnDefinitions[0].Width = CharacterViewWidth;
        mainGrid.ColumnDefinitions[1].Width = new GridLength(2);
      }
      else
      {
        if (CurrentCharacterId != TriggerStateManager.DefaultUser)
        {
          CurrentCharacterId = TriggerStateManager.DefaultUser;
          thePropertyGrid.SelectedObject = null;
          theTreeView.EnableAndRefreshTriggers(true, CurrentCharacterId);
        }

        if (TheConfig.IsEnabled)
        {
          titleLabel.SetResourceReference(ForegroundProperty, "EQGoodForegroundBrush");
          titleLabel.Content = "Triggers Active";
        }
        else
        {
          titleLabel.SetResourceReference(ForegroundProperty, "EQStopForegroundBrush");
          titleLabel.Content = "Check to Activate Triggers";
        }

        advancedText.Text = "Switch to Advanced Settings";
        mainGrid.ColumnDefinitions[0].Width = new GridLength(0);
        mainGrid.ColumnDefinitions[1].Width = new GridLength(0);
      }

      advancedText.UpdateLayout();
      advancedText.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity));
      advancedText.Arrange(new Rect(advancedText.DesiredSize));
      underlineRect.Width = advancedText.ActualWidth;
    }

    private void ClosePreviewOverlaysEvent(bool _)
    {
      PreviewWindows.Values.ToList().ForEach(window => window.Close());
      PreviewWindows.Clear();
    }

    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
      if (Ready)
      {
        if (Equals(sender, watchQuickShare))
        {
          ConfigUtil.SetSetting("TriggersWatchForQuickShare", watchQuickShare.IsChecked.Value);
        }
        else if (Equals(sender, voices))
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
        else if (Equals(sender, rateOption))
        {
          ConfigUtil.SetSetting("TriggersVoiceRate", rateOption.SelectedIndex);
          TriggerManager.Instance.SetVoiceRate(rateOption.SelectedIndex);

          if (TestSynth != null)
          {
            TestSynth.Rate = rateOption.SelectedIndex;
            if (TriggerUtil.GetSelectedVoice() is { } voice && !string.IsNullOrEmpty(voice))
            {
              TestSynth.SelectVoice(voice);
            }
            var rateText = rateOption.SelectedIndex == 0 ? "Default Voice Rate" : "Voice Rate " + rateOption.SelectedIndex;
            TestSynth.SpeakAsync(rateText);
          }
        }
      }
    }

    private void TriggerOverlayDeleteEvent(string id)
    {
      if (PreviewWindows.Remove(id, out var window))
      {
        window?.Close();
      }

      thePropertyGrid.SelectedObject = null;
      thePropertyGrid.IsEnabled = false;
    }

    private void EnableCategories(bool trigger, int timerType, bool overlay, bool overlayTimer,
      bool overlayAssigned, bool overlayText, bool cooldownTimer)
    {
      PropertyGridUtil.EnableCategories(thePropertyGrid, new dynamic[]
      {
        new { Name = patternItem.CategoryName, IsEnabled = trigger },
        new { Name = timerDurationItem.CategoryName, IsEnabled = timerType > 0 },
        new { Name = endEarlyPatternItem.CategoryName, IsEnabled = timerType > 0 && timerType != 2 },
        new { Name = warningSecondsItem.CategoryName, IsEnabled = timerType > 0 && timerType != 2 },
        new { Name = fontSizeItem.CategoryName, IsEnabled = overlay },
        new { Name = activeBrushItem.CategoryName, IsEnabled = overlayTimer },
        new { Name = idleBrushItem.CategoryName, IsEnabled = cooldownTimer },
        new { Name = assignedOverlaysItem.CategoryName, IsEnabled = overlayAssigned },
        new { Name = fadeDelayItem.CategoryName, IsEnabled = overlayText }
      });

      resetDurationItem.Visibility = (timerType > 0 && timerType != 2 && timerType != 4) ? Visibility.Visible : Visibility.Collapsed;
      timerDurationItem.Visibility = (timerType > 0 && timerType != 2) ? Visibility.Visible : Visibility.Collapsed;
      timerShortDurationItem.Visibility = timerType == 2 ? Visibility.Visible : Visibility.Collapsed;
      loopingTimerItem.Visibility = timerType == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ValueChanged(object sender, ValueChangedEventArgs args)
    {
      if (args.Property.SelectedObject is TriggerPropertyModel trigger)
      {
        var triggerChange = true;
        var isValid = TriggerUtil.TestRegexProperty(trigger.UseRegex, trigger.Pattern, PatternEditor);
        isValid = isValid && TriggerUtil.TestRegexProperty(trigger.EndUseRegex, trigger.EndEarlyPattern, EndEarlyPatternEditor);
        isValid = isValid && TriggerUtil.TestRegexProperty(trigger.EndUseRegex2, trigger.EndEarlyPattern2, EndEarlyPattern2Editor);
        isValid = isValid && !string.IsNullOrEmpty(trigger.Pattern);

        if (args.Property.Name == patternItem.PropertyName)
        {
          trigger.WorstEvalTime = -1;
        }
        else if (args.Property.Name == timerTypeItem.PropertyName && args.Property.Value is int timerType)
        {
          EnableCategories(true, timerType, false, false, true, false, false);
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
              (trigger.TriggerActiveBrush?.Color.ToHexString() != original.ActiveColor);
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
              (trigger.TriggerFontBrush?.Color.ToHexString() != original.FontColor);
          }
        }
        else if (args.Property.Name == "DurationTimeSpan" && timerDurationItem.Visibility == Visibility.Collapsed)
        {
          triggerChange = false;
        }

        // make sure there is a pattern
        if (string.IsNullOrEmpty(trigger.Pattern?.Trim()))
        {
          isValid = false;
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
          textChange = textOverlay.OverlayBrush.Color.ToHexString() != original.OverlayColor;
          Application.Current.Resources["OverlayBrushColor-" + textOverlay.Node.Id] = textOverlay.OverlayBrush;
        }
        else if (args.Property.Name == fontBrushItem.PropertyName)
        {
          textChange = textOverlay.FontBrush.Color.ToHexString() != original.FontColor;
          Application.Current.Resources["TextOverlayFontColor-" + textOverlay.Node.Id] = textOverlay.FontBrush;
        }
        else if (args.Property.Name == fontFamilyItem.PropertyName)
        {
          textChange = textOverlay.FontFamily != original.FontFamily;
          Application.Current.Resources["TextOverlayFontFamily-" + textOverlay.Node.Id] = new FontFamily(textOverlay.FontFamily);
        }
        else if (args.Property.Name == fontSizeItem.PropertyName && textOverlay.FontSize.Split("pt") is { Length: 2 } split
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
          timerChange = timerOverlay.OverlayBrush.Color.ToHexString() != original.OverlayColor;
          Application.Current.Resources["OverlayBrushColor-" + timerOverlay.Node.Id] = timerOverlay.OverlayBrush;
        }
        else if (args.Property.Name == activeBrushItem.PropertyName)
        {
          timerChange = timerOverlay.ActiveBrush.Color.ToHexString() != original.ActiveColor;
          Application.Current.Resources["TimerBarActiveColor-" + timerOverlay.Node.Id] = timerOverlay.ActiveBrush;
        }
        else if (args.Property.Name == idleBrushItem.PropertyName)
        {
          timerChange = timerOverlay.IdleBrush.Color.ToHexString() != original.IdleColor;
          Application.Current.Resources["TimerBarIdleColor-" + timerOverlay.Node.Id] = timerOverlay.IdleBrush;
        }
        else if (args.Property.Name == resetBrushItem.PropertyName)
        {
          timerChange = timerOverlay.ResetBrush.Color.ToHexString() != original.ResetColor;
          Application.Current.Resources["TimerBarResetColor-" + timerOverlay.Node.Id] = timerOverlay.ResetBrush;
        }
        else if (args.Property.Name == backgroundBrushItem.PropertyName)
        {
          timerChange = timerOverlay.BackgroundBrush.Color.ToHexString() != original.BackgroundColor;
          Application.Current.Resources["TimerBarTrackColor-" + timerOverlay.Node.Id] = timerOverlay.BackgroundBrush;
        }
        else if (args.Property.Name == fontBrushItem.PropertyName)
        {
          timerChange = timerOverlay.FontBrush.Color.ToHexString() != original.FontColor;
          Application.Current.Resources["TimerBarFontColor-" + timerOverlay.Node.Id] = timerOverlay.FontBrush;
        }
        else if (args.Property.Name == fontSizeItem.PropertyName && timerOverlay.FontSize.Split("pt") is { Length: 2 } split
                                                                 && double.TryParse(split[0], out var newFontSize))
        {
          timerChange = timerOverlay.FontSize != original.FontSize;
          Application.Current.Resources["TimerBarFontSize-" + timerOverlay.Node.Id] = newFontSize;
          Application.Current.Resources["TimerBarHeight-" + timerOverlay.Node.Id] = TriggerUtil.GetTimerBarHeight(newFontSize);
        }
        else if (args.Property.Name == timerModeItem.PropertyName)
        {
          PropertyGridUtil.EnableCategories(thePropertyGrid,
            new dynamic[] { new { Name = idleBrushItem.CategoryName, IsEnabled = (int)args.Property.Value == 1 } });
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
      if ((model is TimerOverlayPropertyModel || model is TextOverlayPropertyModel) && model.Node?.Id is string id)
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
        TriggerManager.Instance.CloseOverlay(model.Node.Id);

        // if this overlay is changing to default and it wasn't previously then need to refresh Overlay tree
        var old = model.Node.OverlayData as Overlay;
        var needRefresh = model.IsDefault && (old?.IsDefault != model.IsDefault);

        TriggerUtil.Copy(model.Node.OverlayData, model);
        TriggerStateManager.Instance.Update(model.Node);

        // if this node is a default then refresh 
        if (needRefresh)
        {
          theTreeView.RefreshOverlays();
        }
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
        EnableCategories(true, timerType, false, false, true, false, false);
      }
      else if (model is TimerOverlayPropertyModel || model is TextOverlayPropertyModel)
      {
        TriggerUtil.Copy(model, model.Node.OverlayData);
      }

      thePropertyGrid?.RefreshPropertygrid();
      Dispatcher.InvokeAsync(() => cancelButton.IsEnabled = saveButton.IsEnabled = false, DispatcherPriority.Background);
    }

    private void TriggerUpdateEvent(TriggerNode node)
    {
      if (node?.OverlayData is { } overlay)
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

    private void TreeSelectionChangedEvent(Tuple<TriggerTreeViewNode, object> data)
    {
      var isTimerOverlay = data.Item1?.SerializedData?.OverlayData?.IsTimerOverlay == true;
      var isCooldownOverlay = isTimerOverlay && (data.Item1?.SerializedData?.OverlayData?.TimerMode == 1);

      saveButton.IsEnabled = false;
      cancelButton.IsEnabled = false;
      thePropertyGrid.SelectedObject = data.Item2;
      thePropertyGrid.IsEnabled = thePropertyGrid.SelectedObject != null;
      thePropertyGrid.DescriptionPanelVisibility = (data.Item1?.IsTrigger() == true || data.Item1?.IsOverlay() == true) ? Visibility.Visible : Visibility.Collapsed;
      showButton.Visibility = data.Item1?.IsOverlay() == true ? Visibility.Visible : Visibility.Collapsed;

      if (data.Item1?.IsTrigger() == true)
      {
        var timerType = data.Item1.SerializedData?.TriggerData.TimerType ?? 0;
        EnableCategories(true, timerType, false, false, true, false, false);
      }
      else if (data.Item1?.IsOverlay() == true)
      {
        if (isTimerOverlay)
        {
          EnableCategories(false, 0, true, true, false, false, isCooldownOverlay);
        }
        else
        {
          EnableCategories(false, 0, true, false, false, true, false);
        }
      }
    }

    #region IDisposable Support
    private bool DisposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!DisposedValue)
      {
        DisposedValue = true;
        PreviewWindows.Values.ToList().ForEach(window => window.Close());
        PreviewWindows.Clear();
        TriggerStateManager.Instance.TriggerUpdateEvent -= TriggerUpdateEvent;
        TriggerStateManager.Instance.TriggerConfigUpdateEvent -= TriggerConfigUpdateEvent;
        TriggerStateManager.Instance.DeleteEvent -= TriggerOverlayDeleteEvent;
        theTreeView.TreeSelectionChangedEvent -= TreeSelectionChangedEvent;
        TestSynth?.Dispose();
        Watcher?.Dispose();
        thePropertyGrid?.Dispose();
        theTreeView.Dispose();
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
