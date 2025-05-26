using Syncfusion.Windows.PropertyGrid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Application = System.Windows.Application;
using CheckBox = System.Windows.Controls.CheckBox;

namespace EQLogParser
{
  public partial class TriggersView : IDocumentContent
  {
    private readonly Dictionary<string, Window> _previewWindows = [];
    private TriggerConfig _theConfig;
    private readonly PatternEditor _patternEditor;
    private readonly PatternEditor _previousPatternEditor;
    private readonly PatternEditor _endEarlyPatternEditor;
    private readonly PatternEditor _endEarlyPattern2Editor;
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

    public TriggersView()
    {
      InitializeComponent();

      _characterViewWidth = mainGrid.ColumnDefinitions[0].Width;

      if (ConfigUtil.IfSet("TriggersWatchForQuickShare"))
      {
        watchQuickShare.IsChecked = true;
      }

      voices.ItemsSource = AudioManager.Instance.GetVoiceList();

      // Update volume
      var volume = ConfigUtil.GetSettingAsInteger("DefaultAudioVolume", 100);
      AudioManager.Instance.SetVolume(volume);

      // Update audio device list
      var deviceInfo = AudioManager.GetDeviceList();
      _deviceIdList = deviceInfo.idList;
      _deviceNameList = deviceInfo.nameList;
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
      _patternEditor = (PatternEditor)AddEditorInstance(new PatternEditor(), "Pattern");
      _previousPatternEditor = (PatternEditor)AddEditorInstance(new PatternEditor(), "PreviousPattern");
      _endEarlyPatternEditor = (PatternEditor)AddEditorInstance(new PatternEditor(), "EndEarlyPattern");
      _endEarlyPattern2Editor = (PatternEditor)AddEditorInstance(new PatternEditor(), "EndEarlyPattern2");
      AddEditor<CheckComboBoxEditor>("SelectedTextOverlays", "SelectedTimerOverlays");
      AddEditor<ColorEditor>("OverlayBrush", "FontBrush", "ActiveBrush", "IdleBrush", "ResetBrush", "BackgroundBrush");
      AddEditor<DurationEditor>("ResetDurationTimeSpan", "IdleTimeoutTimeSpan");
      AddEditor<ExampleTimerBar>("TimerBarPreview");
      AddEditor<OptionalColorEditor>("TriggerActiveBrush", "TriggerFontBrush");
      AddEditor<OptionalIconEditor>("TriggerIconSource");
      AddEditor<TriggerListsEditor>("TriggerAgainOption", "FontSize", "FontFamily", "SortBy", "TimerMode",
        "TimerType", "VerticalAlignment", "Volume");
      AddEditor<WrapTextEditor>("EndEarlyTextToDisplay", "EndTextToDisplay", "TextToDisplay", "TextToShare",
        "WarningTextToDisplay", "Comments", "OverlayComments", "TextToSendToChat", "ChatWebhook");
      AddEditorInstance(new RangeEditor(typeof(double), 0.2, 2.0), "DurationSeconds");
      AddEditorInstance(new TextSoundEditor(fileList), "SoundOrText");
      AddEditorInstance(new TextSoundEditor(fileList), "EndEarlySoundOrText");
      AddEditorInstance(new TextSoundEditor(fileList), "EndSoundOrText");
      AddEditorInstance(new TextSoundEditor(fileList), "WarningSoundOrText");
      AddEditorInstance(new RangeEditor(typeof(long), 1, 5), "Priority");
      AddEditorInstance(new RangeEditor(typeof(long), 0, 99999), "WarningSeconds");
      AddEditorInstance(new RangeEditor(typeof(long), 1, 99999), "TimesToLoop");
      AddEditorInstance(new RangeEditor(typeof(double), 0, 99999), "RepeatedResetTime");
      AddEditorInstance(new RangeEditor(typeof(double), 0, 99999), "LockoutTime");
      AddEditorInstance(new DurationEditor(2), "DurationTimeSpan");
      AddEditorInstance(new RangeEditor(typeof(long), 1, 60), "FadeDelay");

      // don't disconnect these so tree stays in-sync if hidden
      TriggerStateManager.Instance.TriggerImportEvent += TriggerImportEvent;
      AudioManager.Instance.DeviceListChanged += AudioDeviceListChanged;
      return;

      ITypeEditor AddEditorInstance(ITypeEditor typeEditor, string propName)
      {
        if (thePropertyGrid.CustomEditorCollection == null)
        {
          thePropertyGrid.CustomEditorCollection = new CustomEditorCollection();
        }
        var editor = new CustomEditor { Editor = typeEditor };
        editor.Properties.Add(propName);
        thePropertyGrid.CustomEditorCollection.Add(editor);
        return editor.Editor;
      }

      void AddEditor<T>(params string[] propNames) where T : new()
      {
        foreach (var name in propNames)
        {
          var editor = new CustomEditor { Editor = (ITypeEditor)new T() };
          editor.Properties.Add(name);
          thePropertyGrid.CustomEditorCollection.Add(editor);
        }
      }
    }

    private async void AudioDeviceListChanged(bool _)
    {
      await UiUtil.InvokeAsync(() =>
      {
        var id = deviceList.SelectedIndex > -1 ? _deviceIdList[deviceList.SelectedIndex] : Guid.Empty.ToString();

        var deviceInfo = AudioManager.GetDeviceList();
        _deviceIdList = deviceInfo.idList;
        _deviceNameList = deviceInfo.nameList;
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

    private async void TriggersViewOnInitialized(object sender, EventArgs e)
    {
      if (await TriggerStateManager.Instance.GetConfig() is { } config)
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
    }

    private async void TriggerImportEvent(bool _)
    {
      await theTreeView.RefreshTriggers();
      // in case of merge
      TriggerManager.Instance.TriggersUpdated();
    }

    private void EventsSelectTrigger(AlertEntry entry)
    {
      Dispatcher.InvokeAsync(() =>
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

    internal bool IsCancelSelection()
    {
      var model = thePropertyGrid?.SelectedObject;
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
        await TriggerStateManager.Instance.UpdateConfig(_theConfig);
      }
    }

    private async void CharacterSelectedCharacterEvent(List<TriggerCharacter> characters)
    {
      if (characters == null)
      {
        if (_currentCharacterId != null)
        {
          _currentCharacterId = null;
          thePropertyGrid.SelectedObject = null;
          await theTreeView.EnableAndRefreshTriggers(false, _currentCharacterId);
        }
      }
      else
      {
        if (characters.Count > 0)
        {
          _currentCharacterId = characters[0].Id;
          thePropertyGrid.SelectedObject = null;
          await theTreeView.EnableAndRefreshTriggers(true, _currentCharacterId, characters);
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

        await TriggerStateManager.Instance.UpdateConfig(_theConfig);
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
        }
        else
        {
          titleLabel.SetResourceReference(ForegroundProperty, "EQStopForegroundBrush");
          titleLabel.Content = "No Triggers Enabled";
        }

        advancedText.Text = "Switch to Basic";
        mainGrid.ColumnDefinitions[0].Width = _characterViewWidth;
        mainGrid.ColumnDefinitions[1].Width = new GridLength(2);
      }
      else
      {
        voices.Visibility = Visibility.Visible;
        rateOption.Visibility = Visibility.Visible;
        if (_currentCharacterId != TriggerStateManager.DefaultUser)
        {
          _currentCharacterId = TriggerStateManager.DefaultUser;
          thePropertyGrid.SelectedObject = null;
          await theTreeView.EnableAndRefreshTriggers(true, _currentCharacterId);
        }

        if (_theConfig.IsEnabled)
        {
          titleLabel.SetResourceReference(ForegroundProperty, "EQGoodForegroundBrush");
          titleLabel.Content = "Triggers Enabled";
        }
        else
        {
          titleLabel.SetResourceReference(ForegroundProperty, "EQStopForegroundBrush");
          titleLabel.Content = "Check to Enable Triggers";
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
        if (Equals(sender, watchQuickShare))
        {
          ConfigUtil.SetSetting("TriggersWatchForQuickShare", watchQuickShare.IsChecked == true);
        }
        else if (Equals(sender, deviceList))
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
              await TriggerStateManager.Instance.UpdateConfig(_theConfig);
              tts = voiceName;
            }
          }
          else if (Equals(sender, rateOption))
          {
            _theConfig.VoiceRate = rateOption.SelectedIndex;
            await TriggerStateManager.Instance.UpdateConfig(_theConfig);
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

      thePropertyGrid.SelectedObject = null;
      thePropertyGrid.IsEnabled = false;
    }

    private void EnableCategories(bool trigger, int timerType, bool overlay, bool overlayTimer,
      bool overlayAssigned, bool overlayText, bool cooldownTimer)
    {
      PropertyGridUtil.EnableCategories(thePropertyGrid,
      [
        new { Name = patternItem.CategoryName, IsEnabled = trigger },
        new { Name = timerDurationItem.CategoryName, IsEnabled = timerType > 0 },
        new { Name = endEarlyPatternItem.CategoryName, IsEnabled = timerType > 0 && timerType != 2 },
        new { Name = warningSecondsItem.CategoryName, IsEnabled = timerType > 0 && timerType != 2 },
        new { Name = fontSizeItem.CategoryName, IsEnabled = overlay },
        new { Name = activeBrushItem.CategoryName, IsEnabled = overlayTimer },
        new { Name = idleBrushItem.CategoryName, IsEnabled = cooldownTimer },
        new { Name = assignedOverlaysItem.CategoryName, IsEnabled = overlayAssigned },
        new { Name = fadeDelayItem.CategoryName, IsEnabled = overlayText }
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

        if (args.Property.Name == patternItem.PropertyName || args.Property.Name == previousPatternItem.PropertyName)
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
        else if (args.Property.Name == triggerIconSourceItem.PropertyName)
        {
          var original = trigger.Node.TriggerData;
          if (trigger.TriggerIconSource == null && original.IconSource == null)
          {
            triggerChange = false;
          }
          else
          {
            triggerChange = (trigger.TriggerIconSource == null && original.IconSource != null) ||
             (trigger.TriggerIconSource != null && original.IconSource == null) ||
             (trigger.TriggerIconSource?.UriSource.OriginalString != original.IconSource);
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
        else if (args.Property.Name == fontSizeItem.PropertyName && textOverlay.FontSize.Split("pt") is { Length: 2 } split &&
          double.TryParse(split[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var newFontSize))
        {
          textChange = textOverlay.FontSize != original.FontSize;
          Application.Current.Resources["TextOverlayFontSize-" + textOverlay.Node.Id] = newFontSize;
        }
        else if (args.Property.Name == verticalAlignment.PropertyName)
        {
          // not currently using vertical alignment for text overlays
          textChange = textOverlay.VerticalAlignment != original.VerticalAlignment;
          Application.Current.Resources["OverlayVerticalAlignment-" + textOverlay.Node.Id] = (VerticalAlignment)textOverlay.VerticalAlignment;
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
        else if (args.Property.Name == fontFamilyItem.PropertyName)
        {
          timerChange = timerOverlay.FontFamily != original.FontFamily;
          var family = new FontFamily(timerOverlay.FontFamily);
          Application.Current.Resources["TimerBarFontFamily-" + timerOverlay.Node.Id] = family;
          var fontSize = TriggerUtil.ParseFontSize(timerOverlay.FontSize);
          Application.Current.Resources["TimerBarHeight-" + timerOverlay.Node.Id] = TriggerUtil.CalculateTimerBarHeight(fontSize, family);
        }
        else if (args.Property.Name == fontSizeItem.PropertyName)
        {
          var newFontSize = TriggerUtil.ParseFontSize(timerOverlay.FontSize);
          timerChange = timerOverlay.FontSize != original.FontSize;
          var family = !string.IsNullOrEmpty(timerOverlay.FontFamily) ? new FontFamily(timerOverlay.FontFamily) : null;
          Application.Current.Resources["TimerBarFontSize-" + timerOverlay.Node.Id] = newFontSize;
          Application.Current.Resources["TimerBarHeight-" + timerOverlay.Node.Id] = TriggerUtil.CalculateTimerBarHeight(newFontSize, family);
        }
        else if (args.Property.Name == timerModeItem.PropertyName)
        {
          PropertyGridUtil.EnableCategories(thePropertyGrid,
            [new { Name = idleBrushItem.CategoryName, IsEnabled = (int)args.Property.Value == 1 }]);
        }
        else if (args.Property.Name == verticalAlignment.PropertyName)
        {
          timerChange = timerOverlay.VerticalAlignment != original.VerticalAlignment;
          Application.Current.Resources["OverlayVerticalAlignment-" + timerOverlay.Node.Id] = (VerticalAlignment)timerOverlay.VerticalAlignment;
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
      var model = thePropertyGrid?.SelectedObject;
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
      var model = thePropertyGrid?.SelectedObject;
      if (model is TriggerPropertyModel triggerModel)
      {
        await TriggerUtil.Copy(triggerModel.Node.TriggerData, model);
        await TriggerStateManager.Instance.Update(triggerModel.Node);

        // reload triggers if current one is enabled by anyone
        if (await TriggerStateManager.Instance.IsAnyEnabled(triggerModel.Node.Id))
        {
          TriggerManager.Instance.TriggersUpdated();
        }
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
          await TriggerStateManager.Instance.Update(node);

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

    private async void CancelClick(object sender, RoutedEventArgs e)
    {
      var model = thePropertyGrid?.SelectedObject;
      if (model is TriggerPropertyModel triggerModel)
      {
        await TriggerUtil.Copy(model, triggerModel.Node.TriggerData);
        var timerType = triggerModel.Node.TriggerData.TimerType;
        EnableCategories(true, timerType, false, false, true, false, false);
      }
      else if (model is TimerOverlayPropertyModel timerModel)
      {
        await TriggerUtil.Copy(model, timerModel.Node.OverlayData);
      }
      else if (model is TextOverlayPropertyModel textModel)
      {
        await TriggerUtil.Copy(model, textModel.Node.OverlayData);
      }

      thePropertyGrid?.RefreshPropertygrid();
      Dispatcher.Invoke(() => cancelButton.IsEnabled = saveButton.IsEnabled = false, DispatcherPriority.DataBind);
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

    private void DictionaryClick(object sender, RoutedEventArgs e)
    {
      var window = new TriggerDictionaryWindow(theTreeView);
      window.ShowDialog();
    }

    private void ContentLoaded(object sender, RoutedEventArgs e)
    {
      if (VisualParent != null && !_ready)
      {
        TriggerStateManager.Instance.DeleteEvent += TriggerOverlayDeleteEvent;
        TriggerStateManager.Instance.TriggerUpdateEvent += TriggerUpdateEvent;
        TriggerStateManager.Instance.TriggerConfigUpdateEvent += TriggerConfigUpdateEvent;
        TriggerManager.Instance.EventsSelectTrigger += EventsSelectTrigger;
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
      TriggerStateManager.Instance.DeleteEvent -= TriggerOverlayDeleteEvent;
      TriggerStateManager.Instance.TriggerUpdateEvent -= TriggerUpdateEvent;
      TriggerStateManager.Instance.TriggerConfigUpdateEvent -= TriggerConfigUpdateEvent;
      TriggerManager.Instance.EventsSelectTrigger -= EventsSelectTrigger;
      characterView.SelectedCharacterEvent -= CharacterSelectedCharacterEvent;
      theTreeView.TreeSelectionChangedEvent -= TreeSelectionChangedEvent;
      theTreeView.ClosePreviewOverlaysEvent -= ClosePreviewOverlaysEvent;
      _ready = false;
    }
  }
}
