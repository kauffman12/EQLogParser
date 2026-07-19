using EQLogParser.Audio;
using Syncfusion.Windows.PropertyGrid;
using Syncfusion.Windows.Shared;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EQLogParser
{
  public partial class TriggersView : IDocumentContent
  {
    // Binding properties for Variable Actions tab (internal before private per CodingStandards.md)
    internal List<VariableActionType> ActionTypes { get; } = [VariableActionType.Set, VariableActionType.Clear];
    internal string[] ActionTypeLabels { get; } = ["Set Value", "Clear Value"];
    internal List<VariableDataType> DataTypes { get; } = [VariableDataType.Text, VariableDataType.Counter];
    internal ObservableCollection<string> CaptureGroups { get; } = [];
    internal ObservableCollection<VariableAction> VariableActions => _variableActions;

    private readonly Dictionary<string, Window> _previewWindows = [];
    private TriggerConfig _theConfig;
    private readonly PatternEditor _closePatternEditor;
    private readonly PatternEditor _patternEditor;
    private readonly PatternEditor _previousPatternEditor;
    private readonly PatternEditor _endEarlyPatternEditor;
    private readonly PatternEditor _endEarlyPattern2Editor;
    private readonly PatternEditor _endEarlyPattern3Editor;
    private readonly RangeEditor _topEditor;
    private readonly RangeEditor _leftEditor;
    private readonly RangeEditor _heightEditor;
    private readonly RangeEditor _widthEditor;
    private readonly GridLength _characterViewWidth;
    private readonly FileSystemWatcher _soundWatcher;
    private List<string> _deviceIdList;
    private List<string> _deviceNameList;
    private string _currentCharacterId;
    private bool _ready;
    private readonly ObservableCollection<VariableAction> _variableActions = [];

    public TriggersView()
    {
      InitializeComponent();

      _characterViewWidth = mainGrid.ColumnDefinitions[0].Width;
      voices.ItemsSource = AudioManager.Instance.GetVoiceList();

      // Update volume
      var volume = ConfigUtil.GetSettingAsInteger("DefaultAudioVolume", 100);
      AudioManager.Instance.SetVolume(volume);

      // Update audio device list
      var (idList, nameList) = AudioManager.GetDeviceList();
      _deviceIdList = idList;
      _deviceNameList = nameList;
      deviceList.ItemsSource = _deviceNameList;

      var savedDevice = ConfigUtil.GetSetting("DefaultAudioDevice");
      var selectedIndex = 0;
      if (!string.IsNullOrEmpty(savedDevice))
      {
        if (_deviceIdList.FindIndex(item => item == savedDevice) is var index && index > -1)
        {
          selectedIndex = index;
        }
      }

      deviceList.SelectedIndex = selectedIndex;

      if (selectedIndex > 0)
      {
        AudioManager.Instance.SelectDevice(_deviceIdList[selectedIndex]);
      }

      // watch file system for new sounds
      var fileList = new ObservableCollection<string>();
      // watcher needs to stay referenced i think
      _soundWatcher = TriggerUtil.CreateSoundsWatcher(fileList);

      _topEditor = (RangeEditor)AddEditorInstance(new RangeEditor(typeof(long), 0, 9999), "Top");
      _heightEditor = (RangeEditor)AddEditorInstance(new RangeEditor(typeof(long), 0, 9999), "Height");
      _leftEditor = (RangeEditor)AddEditorInstance(new RangeEditor(typeof(long), 0, 9999), "Left");
      _widthEditor = (RangeEditor)AddEditorInstance(new RangeEditor(typeof(long), 0, 9999), "Width");
      _closePatternEditor = (PatternEditor)AddEditorInstance(new PatternEditor(), "ClosePattern");
      _patternEditor = (PatternEditor)AddEditorInstance(new PatternEditor(), "Pattern");
      _previousPatternEditor = (PatternEditor)AddEditorInstance(new PatternEditor(), "PreviousPattern");
      _endEarlyPatternEditor = (PatternEditor)AddEditorInstance(new PatternEditor(), "EndEarlyPattern");
      _endEarlyPattern2Editor = (PatternEditor)AddEditorInstance(new PatternEditor(), "EndEarlyPattern2");
      _endEarlyPattern3Editor = (PatternEditor)AddEditorInstance(new PatternEditor(), "EndEarlyPattern3");
      AddEditor<CheckComboBoxEditor>("SelectedTextOverlays", "SelectedTimerOverlays");
      AddEditor<ColorEditor>("OverlayBrush", "FontBrush", "ActiveBrush", "IdleBrush", "ResetBrush", "BackgroundBrush");
      AddEditor<DurationEditor>("ResetDurationTimeSpan", "IdleTimeoutTimeSpan");
      AddEditor<ExampleTimerBar>("TimerBarPreview");
      AddEditor<OptionalColorEditor>("TriggerActiveBrush", "TriggerFontBrush", "TriggerIdleBrush", "TriggerResetBrush");
      AddEditor<OptionalIconEditor>("IconSource");
      AddEditor<TriggerListsEditor>("TriggerAgainOption", "FontSize", "FontFamily", "FontWeight", "SortBy", "TimerMode",
        "TimerType", "HorizontalAlignment", "VerticalAlignment", "VoiceRate", "Volume");
      AddEditor<WrapTextEditor>("EndEarlyTextToDisplay", "EndTextToDisplay", "TextToDisplay", "TextToShare",
        "WarningTextToDisplay", "Comments", "OverlayComments", "TextToSendToChat", "ChatWebhook");
      AddEditorInstance(new RangeEditor(typeof(double), 0.2, 2.0), "DurationSeconds");
      AddEditorInstance(new TextSoundEditor(fileList), "SoundOrText");
      AddEditorInstance(new TextSoundEditor(fileList), "EndEarlySoundOrText");
      AddEditorInstance(new TextSoundEditor(fileList), "EndSoundOrText");
      AddEditorInstance(new TextSoundEditor(fileList), "WarningSoundOrText");
      AddEditorInstance(new RangeEditor(typeof(long), 1, 5), "Priority");
      AddEditorInstance(new RangeEditor(typeof(long), 0, 99999), "EndEarlyRepeatedCount");
      AddEditorInstance(new RangeEditor(typeof(long), 0, 99999), "WarningSeconds");
      AddEditorInstance(new RangeEditor(typeof(long), 1, 99999), "TimesToLoop");
      AddEditorInstance(new RangeEditor(typeof(double), 0, 99999), "RepeatedResetTime");
      AddEditorInstance(new RangeEditor(typeof(double), 0, 99999), "LockoutTime");
      AddEditorInstance(new DurationEditor(2), "DurationTimeSpan");
      AddEditorInstance(new RangeEditor(typeof(long), 1, 99999), "FadeDelay");

      // don't disconnect these so tree stays in-sync if hidden
      TriggerStateDB.Instance.OverlayImportEvent += OverlayImportEvent;
      TriggerStateDB.Instance.TriggerImportEvent += TriggerImportEvent;
      AudioManager.Instance.DeviceListChanged += AudioDeviceListChanged;
      ThemeConfig.EventsThemeChanged += EventsThemeChanged;
      return;

      ITypeEditor AddEditorInstance(ITypeEditor typeEditor, string propName)
      {
        if (generalPropertyGrid.CustomEditorCollection == null)
        {
          generalPropertyGrid.CustomEditorCollection = new CustomEditorCollection();
        }
        var editor = new CustomEditor { Editor = typeEditor };
        editor.Properties.Add(propName);
        generalPropertyGrid.CustomEditorCollection.Add(editor);
        secondaryPropertyGrid.CustomEditorCollection?.Add(editor);
        return editor.Editor;
      }

      void AddEditor<T>(params string[] propNames) where T : new()
      {
        foreach (var name in propNames)
        {
          var editor = new CustomEditor { Editor = (ITypeEditor)new T() };
          editor.Properties.Add(name);
          generalPropertyGrid.CustomEditorCollection.Add(editor);
          secondaryPropertyGrid.CustomEditorCollection.Add(editor);
        }
      }
    }

    private async void AudioDeviceListChanged(bool _)
    {
      var (idList, nameList) = AudioManager.GetDeviceList();
      if (idList.SequenceEqual(_deviceIdList))
      {
        return;
      }

      await UiUtil.InvokeAsync(() =>
      {
        var id = deviceList.SelectedIndex > -1 ? _deviceIdList[deviceList.SelectedIndex] : Guid.Empty.ToString();
        _deviceIdList = idList;
        _deviceNameList = nameList;
        deviceList.ItemsSource = _deviceNameList;

        if (_deviceIdList.FindIndex(item => item == id) is var index && index > -1)
        {
          deviceList.SelectedIndex = index;
        }
        else
        {
          deviceList.SelectedIndex = 0;
        }
      });
    }

    private void EventsThemeChanged(string _)
    {
      generalPropertyGrid.PropertyNameColumnDefinition = new GridLength(200 + ((ThemeConfig.CurrentFontSize - 12) * 10));
      secondaryPropertyGrid.PropertyNameColumnDefinition = new GridLength(200 + ((ThemeConfig.CurrentFontSize - 12) * 10));
    }

    private async void TriggersViewOnInitialized(object sender, EventArgs e)
    {
      if (await TriggerStateDB.Instance.GetConfig() is { } config)
      {
        characterView.SetConfig(config);
        await UpdateConfig(config);
        var selectedVoice = _theConfig.Voice;

        if (voices.ItemsSource is List<string> { } populated)
        {
          if (populated.IndexOf(selectedVoice) is var found and > -1)
          {
            voices.SelectedIndex = found;
          }
          else if (populated.IndexOf(AudioManager.Instance.GetDefaultVoice()) is var found2 and > -1)
          {
            voices.SelectedIndex = found2;
          }
        }

        rateOption.SelectedIndex = config.VoiceRate;
        await theTreeView.Init(_currentCharacterId, IsCancelSelection, !config.IsAdvanced);
      }
    }

    private void VolumeSliderChanged(object sender, MouseButtonEventArgs mouseButtonEventArgs) => HandleChanged(sender);

    private void VolumeButtonClick(object sender, RoutedEventArgs e)
    {
      volumeSlider.Value = AudioManager.Instance.GetVolume();
      volumePopup.IsOpen = true;
      // workaround to get labels to display
      volumeSlider.LabelOrientation = Orientation.Vertical;
      volumeSlider.LabelOrientation = Orientation.Horizontal;
    }

    private async void OverlayImportEvent(bool _)
    {
      await Dispatcher.InvokeAsync(async () =>
      {
        generalPropertyGrid.SelectedObject = null;
        secondaryPropertyGrid.SelectedObject = null;
        await theTreeView.RefreshOverlays();
      });
    }

    private async void TriggerImportEvent(bool _)
    {
      await Dispatcher.InvokeAsync(async () =>
      {
        generalPropertyGrid.SelectedObject = null;
        secondaryPropertyGrid.SelectedObject = null;
        await theTreeView.RefreshTriggers();
      });
    }

    private async void EventsSelectTrigger(TriggerLogEntry entry)
    {
      await Dispatcher.InvokeAsync(() =>
      {
        if (characterView?.Visibility == Visibility.Visible)
        {
          if (characterView.dataGrid.ItemsSource is List<TriggerCharacter> characters)
          {
            var index = characters.FindIndex(character => character.Id == entry.CharacterId);
            if (index > -1)
            {
              characterView.dataGrid.SelectedIndex = index;
            }
          }
        }

        Dispatcher.InvokeAsync(async () => await theTreeView.SelectNode(entry.NodeId));
      });
    }

    private void EventsUpdatingTriggers(bool updating)
    {
      Dispatcher.InvokeAsync(() =>
      {
        triggerStatus.Visibility = updating ? Visibility.Visible : Visibility.Collapsed;
      });
    }

    internal bool IsCancelSelection()
    {
      var model = generalPropertyGrid?.SelectedObject ?? secondaryPropertyGrid?.SelectedObject;
      var cancel = false;

      if (saveButton.IsEnabled)
      {
        TriggerNode node = null;
        if (model is TriggerPropertyModel triggerModel)
        {
          node = triggerModel.Node;
        }
        else if (model is TextOverlayPropertyModel textModel)
        {
          node = textModel.Node;
        }
        else if (model is TimerOverlayPropertyModel timerModel)
        {
          node = timerModel.Node;
        }

        if (!string.IsNullOrEmpty(node?.Name))
        {
          var msgDialog = new MessageWindow($"Do you want to save changes to {node.Name}?", Resource.UNSAVED,
            MessageWindow.IconType.Question, "Don't Save", "Save");
          msgDialog.ShowDialog();
          if (msgDialog.IsYes2Clicked)
          {
            SaveClick(this, null);
          }
          else if (msgDialog.IsYes1Clicked && node.OverlayData != null)
          {
            _ = TriggerUtil.LoadOverlayStyle(node, node.OverlayData);
          }

          cancel = !msgDialog.IsYes1Clicked && !msgDialog.IsYes2Clicked;
        }
      }

      return cancel;
    }

    private async void TriggerConfigUpdateEvent(TriggerConfig config) => await UpdateConfig(config);

    private async void BasicChecked(object sender, RoutedEventArgs e)
    {
      if (_ready && sender is CheckBox checkBox)
      {
        _theConfig.IsEnabled = checkBox.IsChecked == true;
        await TriggerManager.Instance.StopTriggersAsync();
        await TriggerStateDB.Instance.UpdateConfig(_theConfig);
      }
    }

    private async void CharacterSelectedCharacterEvent(List<TriggerCharacter> characters)
    {
      if (characters == null)
      {
        if (_currentCharacterId != null)
        {
          _currentCharacterId = null;
          var current = generalPropertyGrid.SelectedObject;
          generalPropertyGrid.SelectedObject = null;
          secondaryPropertyGrid.SelectedObject = null;
          await theTreeView.EnableAndRefreshTriggers(false, _currentCharacterId);
          generalPropertyGrid.SelectedObject = current;
          secondaryPropertyGrid.SelectedObject = current;
        }
      }
      else
      {
        if (characters.Count > 0)
        {
          _currentCharacterId = characters[0].Id;
          var current = generalPropertyGrid.SelectedObject;
          generalPropertyGrid.SelectedObject = null;
          secondaryPropertyGrid.SelectedObject = null;
          await theTreeView.EnableAndRefreshTriggers(true, _currentCharacterId, characters);
          generalPropertyGrid.SelectedObject = current;
          secondaryPropertyGrid.SelectedObject = current;
        }
      }
    }

    private async void ToggleAdvancedPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
      if (advancedText != null)
      {
        if (advancedText.Text == "Switch to Advanced")
        {
          _theConfig.IsAdvanced = true;
          basicCheckBox.Visibility = Visibility.Collapsed;
        }
        else
        {
          _theConfig.IsAdvanced = false;
          basicCheckBox.Visibility = Visibility.Visible;
        }

        await TriggerStateDB.Instance.UpdateConfig(_theConfig);
      }
    }

    private async Task UpdateConfig(TriggerConfig config)
    {
      _theConfig = config; // use latest
      theTreeView.SetConfig(_theConfig);
      basicCheckBox.Visibility = !_theConfig.IsAdvanced ? Visibility.Visible : Visibility.Collapsed;
      basicCheckBox.IsChecked = _theConfig.IsEnabled;

      if (_theConfig.IsAdvanced)
      {
        voices.Visibility = Visibility.Collapsed;
        rateOption.Visibility = Visibility.Collapsed;

        var selected = characterView.GetSelectedCharacter();
        List<TriggerCharacter> characters = selected == null ? null : [selected];
        CharacterSelectedCharacterEvent(characters);

        if (_theConfig.Characters.Count(user => user.IsEnabled) is var count and > 0)
        {
          titleLabel.SetResourceReference(ForegroundProperty, "EQGoodForegroundBrush");
          var updatedTitle = $"Triggers Enabled for {count} Character";
          if (count > 1)
          {
            updatedTitle = $"{updatedTitle}s";
          }
          titleLabel.Content = updatedTitle;
          stopBtn.Visibility = Visibility.Visible;
        }
        else
        {
          titleLabel.SetResourceReference(ForegroundProperty, "EQStopForegroundBrush");
          titleLabel.Content = "No Triggers Enabled";
          stopBtn.Visibility = Visibility.Collapsed;
        }

        advancedText.Text = "Switch to Basic";
        mainGrid.ColumnDefinitions[0].Width = _characterViewWidth;
        mainGrid.ColumnDefinitions[1].Width = new GridLength(2);
      }
      else
      {
        voices.Visibility = Visibility.Visible;
        rateOption.Visibility = Visibility.Visible;
        if (_currentCharacterId != TriggerStateDB.DefaultUser)
        {
          _currentCharacterId = TriggerStateDB.DefaultUser;
          generalPropertyGrid.SelectedObject = null;
          secondaryPropertyGrid.SelectedObject = null;
          await theTreeView.EnableAndRefreshTriggers(true, _currentCharacterId);
        }

        if (_theConfig.IsEnabled)
        {
          titleLabel.SetResourceReference(ForegroundProperty, "EQGoodForegroundBrush");
          titleLabel.Content = "Triggers Enabled";
          stopBtn.Visibility = Visibility.Visible;
        }
        else
        {
          titleLabel.SetResourceReference(ForegroundProperty, "EQStopForegroundBrush");
          titleLabel.Content = "Check to Enable Triggers";
          stopBtn.Visibility = Visibility.Collapsed;
        }

        advancedText.Text = "Switch to Advanced";
        mainGrid.ColumnDefinitions[0].Width = new GridLength(0);
        mainGrid.ColumnDefinitions[1].Width = new GridLength(0);
      }
    }

    private void ClosePreviewOverlaysEvent(bool _)
    {
      _previewWindows.Values.ToList().ForEach(window => window.Close());
      _previewWindows.Clear();
    }

    private void OptionsChanged(object sender, RoutedEventArgs e) => HandleChanged(sender);

    private async void HandleChanged(object sender)
    {
      if (_ready)
      {
        if (Equals(sender, deviceList))
        {
          if (deviceList.SelectedIndex > -1)
          {
            AudioManager.Instance.SelectDevice(_deviceIdList[deviceList.SelectedIndex]);
            ConfigUtil.SetSetting("DefaultAudioDevice", _deviceIdList[deviceList.SelectedIndex]);
          }
        }
        else
        {
          string tts = null;
          if (Equals(sender, voices))
          {
            if (voices.SelectedValue is string voiceName)
            {
              _theConfig.Voice = voiceName;
              await TriggerStateDB.Instance.UpdateConfig(_theConfig);
              tts = voiceName;
            }
          }
          else if (Equals(sender, rateOption))
          {
            _theConfig.VoiceRate = rateOption.SelectedIndex;
            await TriggerStateDB.Instance.UpdateConfig(_theConfig);
            tts = rateOption.SelectedIndex == 0 ? "Default Voice Rate" : "Voice Rate " + rateOption.SelectedIndex;
          }
          else if (Equals(sender, volumeSlider))
          {
            var volume = (int)Math.Round(volumeSlider.Value);
            tts = "Volume " + volume + " Percent";
            AudioManager.Instance.SetVolume(volume);
            ConfigUtil.SetSetting("DefaultAudioVolume", volume);
          }

          AudioManager.Instance.TestSpeakTtsAsync(tts, _theConfig.Voice, _theConfig.VoiceRate);
        }
      }
    }

    private void TriggerOverlayDeleteEvent(string id)
    {
      if (_previewWindows.Remove(id, out var window))
      {
        window?.Close();
      }

      generalPropertyGrid.SelectedObject = null;
      generalPropertyGrid.IsEnabled = false;
      secondaryPropertyGrid.SelectedObject = null;
      secondaryPropertyGrid.IsEnabled = false;
    }

    private void EnableCategories(bool trigger, int timerType, bool timerOverlay, bool cooldownTimer)
    {
      var isTimerOverlay = !trigger && timerOverlay;
      var isTextOverlay = !trigger && !timerOverlay;
      var isOverlay = !trigger;

      // Tab 2 visibility
      if (trigger)
      {
        generalPropertyGrid.EnableGrouping = true;
        secondaryPropertyGrid.EnableGrouping = true;
        if (timerType == 0 && secondaryPropertyGridTab.Visibility == Visibility.Visible)
        {
          secondaryPropertyGridTab.Visibility = Visibility.Collapsed;
          propertyTabControl.SelectedIndex = 0;
        }
        else if (timerType != 0 && secondaryPropertyGridTab.Visibility != Visibility.Visible)
        {
          secondaryPropertyGridTab.Visibility = Visibility.Visible;
        }
      }
      else
      {
        generalPropertyGrid.EnableGrouping = false;
        secondaryPropertyGrid.EnableGrouping = false;
        // Always show Tab 2 for overlays
        secondaryPropertyGridTab.Visibility = cooldownTimer ? Visibility.Visible : Visibility.Collapsed;
      }

      // Tab 1 header
      if (trigger)
      {
        generalPropertyGridTab.Header = "Trigger Properties";
      }
      else if (isTimerOverlay)
      {
        generalPropertyGridTab.Header = "Timer Overlay Properties";
      }
      else if (isTextOverlay)
      {
        generalPropertyGridTab.Header = "Text Overlay Properties";
      }

      // Tab 2 header
      if (trigger)
      {
        secondaryPropertyGridTab.Header = $"{TriggerListOptionLabels.TimerTypes[timerType]} Timer";
      }
      else
      {
        secondaryPropertyGridTab.Header = "Cooldown Mode";
      }

      PropertyGridUtil.EnableCategories(generalPropertyGrid,
      [
        new { Name = patternItem.CategoryName, IsEnabled = trigger },
        new { Name = triggerVolumeItem.CategoryName, IsEnabled = trigger },
        new { Name = triggerTextToShareItem.CategoryName, IsEnabled = trigger },
        new { Name = fontSizeItem.CategoryName, IsEnabled = isOverlay },
        new { Name = assignedOverlaysItem.CategoryName, IsEnabled = trigger },
        new { Name = activeBrushItem.CategoryName, IsEnabled = isTimerOverlay },
        new { Name = fadeDelayItem.CategoryName, IsEnabled = isTextOverlay }
      ]);

      PropertyGridUtil.EnableCategories(secondaryPropertyGrid,
      [
        // Trigger timer categories
        new { Name = timerDurationItem.CategoryName, IsEnabled = trigger && timerType > 0 },
        new { Name = endEarlyPatternItem.CategoryName, IsEnabled = trigger && timerType > 0 && timerType != 2 },
        new { Name = warningSecondsItem.CategoryName, IsEnabled = trigger && timerType > 0 && timerType != 2 },
        // Overlay categories
        new { Name = idleBrushItem.CategoryName, IsEnabled = isTimerOverlay && cooldownTimer }
      ]);

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
        var isValid = TriggerUtil.TestRegexProperty(trigger.UseRegex, trigger.Pattern, _patternEditor);
        isValid = isValid && TriggerUtil.TestRegexProperty(trigger.PreviousUseRegex, trigger.PreviousPattern, _previousPatternEditor);
        isValid = isValid && TriggerUtil.TestRegexProperty(trigger.EndUseRegex, trigger.EndEarlyPattern, _endEarlyPatternEditor);
        isValid = isValid && TriggerUtil.TestRegexProperty(trigger.EndUseRegex2, trigger.EndEarlyPattern2, _endEarlyPattern2Editor);
        isValid = isValid && TriggerUtil.TestRegexProperty(trigger.EndUseRegex3, trigger.EndEarlyPattern3, _endEarlyPattern3Editor);

        if (args.Property.Name == patternItem.PropertyName || args.Property.Name == previousPatternItem.PropertyName)
        {
          trigger.WorstEvalTime = -1;
        }
        else if (args.Property.Name == timerTypeItem.PropertyName && args.Property.Value is int timerType)
        {
          EnableCategories(true, timerType, false, false);
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
        else if (args.Property.Name == triggerIdleBrushItem.PropertyName)
        {
          var original = trigger.Node.TriggerData;
          if (trigger.TriggerIdleBrush == null && original.IdleColor == null)
          {
            triggerChange = false;
          }
          else
          {
            triggerChange = (trigger.TriggerIdleBrush == null && original.IdleColor != null) ||
              (trigger.TriggerIdleBrush != null && original.IdleColor == null) ||
              (trigger.TriggerIdleBrush?.Color.ToHexString() != original.IdleColor);
          }
        }
        else if (args.Property.Name == triggerResetBrushItem.PropertyName)
        {
          var original = trigger.Node.TriggerData;
          if (trigger.TriggerResetBrush == null && original.ResetColor == null)
          {
            triggerChange = false;
          }
          else
          {
            triggerChange = (trigger.TriggerResetBrush == null && original.ResetColor != null) ||
              (trigger.TriggerResetBrush != null && original.ResetColor == null) ||
              (trigger.TriggerResetBrush?.Color.ToHexString() != original.ResetColor);
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

        var isValid = TriggerUtil.TestRegexProperty(textOverlay.UseCloseRegex, textOverlay.ClosePattern, _closePatternEditor);

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
        else if (args.Property.Name == fontSizeItem.PropertyName && textOverlay.FontSize.Split("pt") is { Length: 2 } split &&
          double.TryParse(split[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var newFontSize))
        {
          textChange = textOverlay.FontSize != original.FontSize;
          Application.Current.Resources["TextOverlayFontSize-" + textOverlay.Node.Id] = newFontSize;
        }
        else if (args.Property.Name == fontWeightItem.PropertyName)
        {
          textChange = textOverlay.FontWeight != original.FontWeight;
          Application.Current.Resources["TextOverlayFontWeight-" + textOverlay.Node.Id] = UiElementUtil.GetFontWeightByName(textOverlay.FontWeight);
        }
        else if (args.Property.Name == horizontalAlignmentItem.PropertyName)
        {
          textChange = textOverlay.HorizontalAlignment != original.HorizontalAlignment;
          Application.Current.Resources["OverlayHorizontalAlignment-" + textOverlay.Node.Id] = (HorizontalAlignment)textOverlay.HorizontalAlignment;
        }
        else if (args.Property.Name == verticalAlignmentItem.PropertyName)
        {
          textChange = textOverlay.VerticalAlignment != original.VerticalAlignment;
          Application.Current.Resources["OverlayVerticalAlignment-" + textOverlay.Node.Id] = (VerticalAlignment)textOverlay.VerticalAlignment;
        }
        else if (args.Property.Name == textDropShadowItem.PropertyName)
        {
          textChange = textOverlay.UseTextDropShadow != original.UseTextDropShadow;
          Application.Current.Resources["OverlayTextEffect-" + textOverlay.Node.Id] = textOverlay.UseTextDropShadow ? ThemeConfig.OverlayTextEffect : null;
        }

        if (textChange)
        {
          saveButton.IsEnabled = isValid;
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
        else if (args.Property.Name == fontFamilyItem.PropertyName)
        {
          timerChange = timerOverlay.FontFamily != original.FontFamily;
          var family = new FontFamily(timerOverlay.FontFamily);
          Application.Current.Resources["TimerBarFontFamily-" + timerOverlay.Node.Id] = family;
          var fontSize = UiElementUtil.ParseFontSize(timerOverlay.FontSize);
          Application.Current.Resources["TimerBarHeight-" + timerOverlay.Node.Id] = TriggerUtil.CalculateTimerBarHeight(fontSize, family);
        }
        else if (args.Property.Name == fontSizeItem.PropertyName)
        {
          var newFontSize = UiElementUtil.ParseFontSize(timerOverlay.FontSize);
          timerChange = timerOverlay.FontSize != original.FontSize;
          var family = !string.IsNullOrEmpty(timerOverlay.FontFamily) ? new FontFamily(timerOverlay.FontFamily) : null;
          Application.Current.Resources["TimerBarFontSize-" + timerOverlay.Node.Id] = newFontSize;
          Application.Current.Resources["TimerBarHeight-" + timerOverlay.Node.Id] = TriggerUtil.CalculateTimerBarHeight(newFontSize, family);
        }
        else if (args.Property.Name == fontWeightItem.PropertyName)
        {
          timerChange = timerOverlay.FontWeight != original.FontWeight;
          var weight = UiElementUtil.GetFontWeightByName(timerOverlay.FontWeight);
          Application.Current.Resources["TimerBarFontWeight-" + timerOverlay.Node.Id] = weight;
          // resize if needed?
          var fontSize = UiElementUtil.ParseFontSize(timerOverlay.FontSize);
          var family = new FontFamily(timerOverlay.FontFamily);
          Application.Current.Resources["TimerBarHeight-" + timerOverlay.Node.Id] = TriggerUtil.CalculateTimerBarHeight(fontSize, family);
        }
        else if (args.Property.Name == timerModeItem.PropertyName)
        {
          var isCooldownOverlay = (int)args.Property.Value == 1;
          EnableCategories(false, 0, true, isCooldownOverlay);
        }
        else if (args.Property.Name == horizontalAlignmentItem.PropertyName)
        {
          // NOTE: not currently implement for Timers
          timerChange = timerOverlay.HorizontalAlignment != original.HorizontalAlignment;
          Application.Current.Resources["OverlayHorizontalAlignment-" + timerOverlay.Node.Id] = (HorizontalAlignment)timerOverlay.HorizontalAlignment;
        }
        else if (args.Property.Name == verticalAlignmentItem.PropertyName)
        {
          timerChange = timerOverlay.VerticalAlignment != original.VerticalAlignment;
          Application.Current.Resources["OverlayVerticalAlignment-" + timerOverlay.Node.Id] = (VerticalAlignment)timerOverlay.VerticalAlignment;
        }
        else if (args.Property.Name == textDropShadowItem.PropertyName)
        {
          timerChange = timerOverlay.UseTextDropShadow != original.UseTextDropShadow;
          Application.Current.Resources["OverlayTextEffect-" + timerOverlay.Node.Id] = timerOverlay.UseTextDropShadow ? ThemeConfig.OverlayTextEffect : null;
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
      TriggerNode node = null;
      var model = generalPropertyGrid?.SelectedObject ?? secondaryPropertyGrid?.SelectedObject;
      var isTimer = false;
      if (model is TimerOverlayPropertyModel timerModel)
      {
        isTimer = true;
        node = timerModel.Node;
      }
      else if (model is TextOverlayPropertyModel textModel)
      {
        node = textModel.Node;
      }

      if (node?.Id is { } id)
      {
        if (!_previewWindows.TryGetValue(id, out var window))
        {
          _previewWindows[id] = isTimer ? new TimerOverlayWindow(node, _previewWindows)
            : new TextOverlayWindow(node, _previewWindows);
          _previewWindows[id].Show();
        }
        else
        {
          window.Close();
          _previewWindows.Remove(id, out _);
        }
      }
    }

    private async void SaveClick(object sender, RoutedEventArgs e)
    {
      var model = generalPropertyGrid?.SelectedObject ?? secondaryPropertyGrid?.SelectedObject;
      if (model is TriggerPropertyModel triggerModel)
      {
        // Sync variable actions from UI to model before saving (filter out entries with no name)
        triggerModel.VariableActions = _variableActions
          .Where(va => !string.IsNullOrWhiteSpace(va.VariableName))
          .ToList();
        await TriggerUtil.Copy(triggerModel.Node.TriggerData, model);
        await TriggerStateDB.Instance.Update(triggerModel.Node);
      }
      else
      {
        TriggerNode node = null;
        var isDefault = false;
        if (model is TextOverlayPropertyModel textModel)
        {
          node = textModel.Node;
          isDefault = textModel.IsDefault;
        }
        else if (model is TimerOverlayPropertyModel timerModel)
        {
          node = timerModel.Node;
          isDefault = timerModel.IsDefault;
        }

        if (node != null)
        {
          var wasDefault = node.OverlayData?.IsDefault == true;
          await TriggerUtil.Copy(node.OverlayData, model);
          await TriggerStateDB.Instance.Update(node);

          // if this overlay is changing to default, and it wasn't previously
          // then need to refresh Overlay tree
          if (isDefault && !wasDefault)
          {
            await theTreeView.RefreshOverlays();
          }
        }
      }

      cancelButton.IsEnabled = false;
      saveButton.IsEnabled = false;
    }

    private void RegexClick(object sender, RoutedEventArgs e)
    {
      MainActions.OpenFileWithDefault($"{App.ParserHome}/documentation.html#regex-101");
    }

    private async void CancelClick(object sender, RoutedEventArgs e)
    {
      var model = generalPropertyGrid?.SelectedObject ?? secondaryPropertyGrid?.SelectedObject;
      if (model is TriggerPropertyModel triggerModel)
      {
        await TriggerUtil.Copy(model, triggerModel.Node.TriggerData);
        var timerType = triggerModel.Node.TriggerData.TimerType;
        EnableCategories(true, timerType, false, false);

        // Restore variable actions from original data
        _variableActions.Clear();
        if (triggerModel.Node.TriggerData?.VariableActions is { Count: > 0 } originalActions)
        {
          foreach (var action in originalActions)
          {
            _variableActions.Add(action);
          }
        }
        else
        {
          // Add a starter variable card so the UI isn't empty
          _variableActions.Add(new VariableAction { VariableName = "gVariable1" });
        }
      }
      else if (model is TimerOverlayPropertyModel timerModel)
      {
        await TriggerUtil.Copy(model, timerModel.Node.OverlayData);
        var isCooldownOverlay = timerModel.TimerMode == 1;
        EnableCategories(false, 0, true, isCooldownOverlay);
      }
      else if (model is TextOverlayPropertyModel textModel)
      {
        await TriggerUtil.Copy(model, textModel.Node.OverlayData);
        EnableCategories(false, 0, false, false);
      }

      generalPropertyGrid?.RefreshPropertygrid();
      secondaryPropertyGrid?.RefreshPropertygrid();
      Dispatcher.Invoke(() => cancelButton.IsEnabled = saveButton.IsEnabled = false, DispatcherPriority.DataBind);
    }

    private async void StopBtnClick(object sender, MouseButtonEventArgs e)
    {
      await TriggerManager.Instance.StopTriggersAsync();
    }

    private void TriggerUpdateEvent(TriggerNode node)
    {
      if (node?.OverlayData is { } overlay)
      {
        var wasEnabled = saveButton.IsEnabled;
        _topEditor.Update(overlay.Top);
        _leftEditor.Update(overlay.Left);
        _widthEditor.Update(overlay.Width);
        _heightEditor.Update(overlay.Height);

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
      generalPropertyGrid.SelectedObject = data.Item2;
      generalPropertyGrid.IsEnabled = generalPropertyGrid.SelectedObject != null;
      generalPropertyGrid.DescriptionPanelVisibility = (data.Item1?.IsTrigger() == true || data.Item1?.IsOverlay() == true) ? Visibility.Visible : Visibility.Collapsed;
      showButton.Visibility = data.Item1?.IsOverlay() == true ? Visibility.Visible : Visibility.Collapsed;

      secondaryPropertyGrid.SelectedObject = data.Item2;
      secondaryPropertyGrid.IsEnabled = secondaryPropertyGrid.SelectedObject != null;
      secondaryPropertyGrid.DescriptionPanelVisibility = (data.Item1?.IsTrigger() == true || data.Item1?.IsOverlay() == true) ? Visibility.Visible : Visibility.Collapsed;

      if (data.Item1?.IsTrigger() == true)
      {
        var timerType = data.Item1.SerializedData?.TriggerData.TimerType ?? 0;
        EnableCategories(true, timerType, false, false);

        // Initialize variable actions for this trigger
        var trigger = data.Item1.SerializedData?.TriggerData;
        _variableActions.Clear();
        if (trigger?.VariableActions is { Count: > 0 } actions)
        {
          foreach (var action in actions)
          {
            _variableActions.Add(action);
          }
        }
        else
        {
          // Add a starter variable card so the UI isn't empty
          _variableActions.Add(new VariableAction { VariableName = "gVariable1" });
        }

        variableActionsList.ItemsSource = _variableActions;
        UpdateCaptureGroups(trigger?.Pattern);
        variableActionsTab.Visibility = Visibility.Visible;
        UpdateVariableActionsEmptyState();
      }
      else if (data.Item1?.IsOverlay() == true)
      {
        variableActionsTab.Visibility = Visibility.Collapsed;

        if (isTimerOverlay)
        {
          EnableCategories(false, 0, true, isCooldownOverlay);
        }
        else
        {
          EnableCategories(false, 0, false, false);
        }
      }
    }

    private void DictionaryClick(object sender, RoutedEventArgs e)
    {
      var window = new TriggerDictionaryWindow(theTreeView);
      window.ShowDialog();
    }

    private void QuickShareClick(object sender, RoutedEventArgs e)
    {
      var window = new QuickShareWindow();
      window.ShowDialog();
    }

    private void ContentLoaded(object sender, RoutedEventArgs e)
    {
      if (VisualParent != null && !_ready)
      {
        TriggerStateDB.Instance.DeleteEvent += TriggerOverlayDeleteEvent;
        TriggerStateDB.Instance.TriggerUpdateEvent += TriggerUpdateEvent;
        TriggerStateDB.Instance.TriggerConfigUpdateEvent += TriggerConfigUpdateEvent;
        TriggerManager.Instance.EventsSelectTrigger += EventsSelectTrigger;
        TriggerManager.Instance.EventsUpdatingTriggers += EventsUpdatingTriggers;
        characterView.SelectedCharacterEvent += CharacterSelectedCharacterEvent;
        theTreeView.TreeSelectionChangedEvent += TreeSelectionChangedEvent;
        theTreeView.ClosePreviewOverlaysEvent += ClosePreviewOverlaysEvent;
        _ready = true;
      }
    }

    public void HideContent()
    {
      _previewWindows.Values.ToList().ForEach(window => window.Close());
      _previewWindows.Clear();
      TriggerStateDB.Instance.DeleteEvent -= TriggerOverlayDeleteEvent;
      TriggerStateDB.Instance.TriggerUpdateEvent -= TriggerUpdateEvent;
      TriggerStateDB.Instance.TriggerConfigUpdateEvent -= TriggerConfigUpdateEvent;
      TriggerManager.Instance.EventsSelectTrigger -= EventsSelectTrigger;
      TriggerManager.Instance.EventsUpdatingTriggers -= EventsUpdatingTriggers;
      characterView.SelectedCharacterEvent -= CharacterSelectedCharacterEvent;
      theTreeView.TreeSelectionChangedEvent -= TreeSelectionChangedEvent;
      theTreeView.ClosePreviewOverlaysEvent -= ClosePreviewOverlaysEvent;
      _ready = false;
    }

    private void UpdateCaptureGroups(string pattern)
    {
      CaptureGroups.Clear();

      if (string.IsNullOrEmpty(pattern))
      {
        return;
      }

      // Extract EQLP capture group tokens: {s1}, {s2}... and {n1}, {n2}...
      // These are converted to named regex groups at runtime by UpdatePattern()
      foreach (Match m in System.Text.RegularExpressions.Regex.Matches(pattern, @"\{([sn]\d+)\}"))
      {
        var token = "{" + m.Groups[1].Value + "}";
        if (!CaptureGroups.Contains(token))
        {
          CaptureGroups.Add(token);
        }
      }

      // Also extract standard named regex groups (?<name>...)
      foreach (Match m in System.Text.RegularExpressions.Regex.Matches(pattern, @"\(\?<([a-zA-Z0-9_]+)>"))
      {
        var token = "{" + m.Groups[1].Value + "}";
        if (!CaptureGroups.Contains(token))
        {
          CaptureGroups.Add(token);
        }
      }

      // Add default EQLP placeholders if none found
      if (CaptureGroups.Count == 0)
      {
        CaptureGroups.Add("{s1}");
        CaptureGroups.Add("{n1}");
      }
    }

    private void UpdateVariableActionsEmptyState()
    {
      var isEmpty = _variableActions.Count == 0;
      variableActionsEmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void VariableActionRowLoaded(object sender, RoutedEventArgs e)
    {
      // ItemsControl DataTemplates break the visual tree, so RelativeSource bindings
      // can't reach the parent DataContext. Set ItemsSource directly instead.
      if (sender is Border border && border.Child is StackPanel panel && border.DataContext is VariableAction va)
      {
        var isCounter = va.DataType == VariableDataType.Counter;
        var isSet = va.ActionType == VariableActionType.Set;

        // Set brace text for variable name display (curly braces can't be literal in XAML)
        if (UiElementUtil.FindChild<TextBlock>(panel, "varBraceLeft") is TextBlock leftBrace)
          leftBrace.Text = "{";
        if (UiElementUtil.FindChild<TextBlock>(panel, "varBraceRight") is TextBlock rightBrace)
          rightBrace.Text = "}";

        // Find all controls by name using recursive search
        var typePanel = UiElementUtil.FindChild<StackPanel>(panel, "typePanel");
        var valuePanel = UiElementUtil.FindChild<StackPanel>(panel, "valuePanel");
        var counterFieldsPanel = UiElementUtil.FindChild<StackPanel>(panel, "counterFieldsPanel");
        var ttlPanel = UiElementUtil.FindChild<StackPanel>(panel, "ttlPanel");
        var actionTypeCombo = UiElementUtil.FindChild<ComboBox>(panel, "actionTypeCombo");
        var dataTypeCombo = UiElementUtil.FindChild<ComboBox>(panel, "dataTypeCombo");
        var valueTextBox = UiElementUtil.FindChild<TextBox>(panel, "valueTextBox");
        var variableNameBox = UiElementUtil.FindChild<TextBox>(panel, "variableNameBox");
        var valueLabel = UiElementUtil.FindChild<TextBlock>(panel, "valueLabel");

        // Set visibility for panels
        if (typePanel is not null)
        {
          typePanel.Visibility = isSet ? Visibility.Visible : Visibility.Collapsed;
        }

        if (valuePanel is not null)
        {
          valuePanel.Visibility = (isSet && !isCounter) ? Visibility.Visible : Visibility.Collapsed;
        }

        if (counterFieldsPanel is not null)
        {
          counterFieldsPanel.Visibility = (isSet && isCounter) ? Visibility.Visible : Visibility.Collapsed;
        }

        // TTL panel visible when Action=Set (for both Text and Counter)
        if (ttlPanel is not null)
        {
          ttlPanel.Visibility = isSet ? Visibility.Visible : Visibility.Collapsed;
        }

        // Set initial value label text
        if (valueLabel is not null)
        {
          valueLabel.Text = isCounter ? "Increment By" : "Text Value";
        }

        // Variable name text box with validation — mark dirty on change
        if (variableNameBox is not null)
        {
          variableNameBox.TextChanged += (_, _) =>
          {
            // Validate variable name: must start with letter or underscore, contain only alphanumeric + underscore
            var isValid = string.IsNullOrWhiteSpace(variableNameBox.Text) ||
              Regex.IsMatch(variableNameBox.Text, @"^[a-zA-Z_][a-zA-Z0-9_]*$");
            variableNameBox.Foreground = isValid ?
              (Brush)FindResource("ContentForeground") :
              (Brush)FindResource("EQStopForegroundBrush");
            EnableSaveCancel();
          };
        }

        // Action type combo with descriptive labels, bound to enum via index
        if (actionTypeCombo is not null)
        {
          actionTypeCombo.ItemsSource = ActionTypeLabels;
          actionTypeCombo.SelectedIndex = (int)va.ActionType;
          actionTypeCombo.SelectionChanged += (_, _) =>
          {
            if (actionTypeCombo.SelectedIndex >= 0 && actionTypeCombo.SelectedIndex < ActionTypes.Count)
            {
              va.ActionType = ActionTypes[actionTypeCombo.SelectedIndex];
            }
            UpdateVariablePanelVisibility(va, typePanel, valuePanel, counterFieldsPanel, ttlPanel, valueLabel);
            EnableSaveCancel();
          };
        }

        // Data type combo
        if (dataTypeCombo is not null)
        {
          dataTypeCombo.ItemsSource = DataTypes;
          dataTypeCombo.SelectedIndex = (int)va.DataType;
          dataTypeCombo.SelectionChanged += (_, _) =>
          {
            if (dataTypeCombo.SelectedIndex >= 0)
            {
              va.DataType = (VariableDataType)dataTypeCombo.SelectedIndex;
            }
            UpdateVariablePanelVisibility(va, typePanel, valuePanel, counterFieldsPanel, ttlPanel, valueLabel);
            EnableSaveCancel();
          };
        }

        // Value TextBox with placeholder functionality for Text type
        if (valueTextBox is not null)
        {
          const string placeholder = "example value {s1}";

          // Initialize placeholder state if Value is empty
          if (string.IsNullOrWhiteSpace(va.Value))
          {
            valueTextBox.Text = placeholder;
            valueTextBox.Foreground = (Brush)FindResource("PlaceholderForeground");
            valueTextBox.FontStyle = FontStyles.Italic;
          }

          // Focus: clear placeholder
          valueTextBox.GotFocus += (_, _) =>
          {
            if (valueTextBox.Text == placeholder)
            {
              valueTextBox.Text = "";
              valueTextBox.Foreground = (Brush)FindResource("ContentForeground");
              valueTextBox.FontStyle = FontStyles.Normal;
            }
          };

          // Lost focus: restore placeholder if empty
          valueTextBox.LostFocus += (_, _) =>
          {
            if (string.IsNullOrWhiteSpace(valueTextBox.Text))
            {
              valueTextBox.Text = placeholder;
              valueTextBox.Foreground = (Brush)FindResource("PlaceholderForeground");
              valueTextBox.FontStyle = FontStyles.Italic;
              va.Value = ""; // Clear actual value
            }
            else
            {
              va.Value = valueTextBox.Text.Trim();
            }
          };

          // Text change: mark dirty
          valueTextBox.TextChanged += (_, _) => EnableSaveCancel();
        }

        // Counter UpDown controls — mark dirty on value change
        var initialValueBox = UiElementUtil.FindChild<UpDown>(panel, "initialValueBox");
        var stepBox = UiElementUtil.FindChild<UpDown>(panel, "stepBox");

        if (initialValueBox is not null)
        {
          initialValueBox.ValueChanged += (_, _) => EnableSaveCancel();
        }

        if (stepBox is not null)
        {
          stepBox.ValueChanged += (_, _) => EnableSaveCancel();
        }

        // TTL UpDown control — mark dirty on value change
        var ttlBox = UiElementUtil.FindChild<UpDown>(panel, "ttlBox");
        if (ttlBox is not null)
        {
          ttlBox.ValueChanged += (_, _) => EnableSaveCancel();
        }
      }
    }

    private void EnableSaveCancel()
    {
      saveButton.IsEnabled = true;
      cancelButton.IsEnabled = true;
    }

    private static void UpdateVariablePanelVisibility(VariableAction va, StackPanel typePanel, StackPanel valuePanel, StackPanel counterFieldsPanel, StackPanel ttlPanel, TextBlock valueLabel)
    {
      var isCounter = va.DataType == VariableDataType.Counter;
      var isSet = va.ActionType == VariableActionType.Set;

      if (typePanel is not null)
      {
        typePanel.Visibility = isSet ? Visibility.Visible : Visibility.Collapsed;
      }

      if (valuePanel is not null)
      {
        valuePanel.Visibility = (isSet && !isCounter) ? Visibility.Visible : Visibility.Collapsed;
      }

      if (counterFieldsPanel is not null)
      {
        counterFieldsPanel.Visibility = (isSet && isCounter) ? Visibility.Visible : Visibility.Collapsed;
      }

      // TTL panel visible for both Text and Counter when Action=Set
      if (ttlPanel is not null)
      {
        ttlPanel.Visibility = isSet ? Visibility.Visible : Visibility.Collapsed;
      }

      // Update label text based on current type
      if (valueLabel is not null)
      {
        valueLabel.Text = isCounter ? "Increment By" : "Text Value";
      }
    }

    private void AddVariableActionClick(object sender, RoutedEventArgs e)
    {
      _variableActions.Add(new VariableAction { VariableName = "gVariable1" });
      EnableSaveCancel();
      UpdateVariableActionsEmptyState();
    }

    private void DeleteVariableActionClick(object sender, RoutedEventArgs e)
    {
      if (sender is Button button && button.Tag is VariableAction action)
      {
        _variableActions.Remove(action);
        EnableSaveCancel();
        UpdateVariableActionsEmptyState();

        // If last variable was removed, add a fresh starter card
        if (_variableActions.Count == 0)
        {
          _variableActions.Add(new VariableAction { VariableName = "gVariable1" });
          UpdateVariableActionsEmptyState();
        }
      }
    }

  }
}
