﻿using Microsoft.WindowsAPICodePack.Dialogs;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace EQLogParser
{
  public partial class TriggerPlayerConfigWindow
  {
    private const string EnterName = "Enter Character Name";
    private readonly TriggerCharacter _theCharacter;
    private readonly bool _ready;

    internal TriggerPlayerConfigWindow(TriggerCharacter character = null)
    {
      MainActions.SetCurrentTheme(this);
      InitializeComponent();

      Owner = MainActions.GetOwner();
      voices.ItemsSource = AudioManager.Instance.GetVoiceList();

      _theCharacter = character;
      if (_theCharacter != null)
      {
        characterName.Text = _theCharacter.Name;
        characterName.FontStyle = FontStyles.Normal;
        txtFilePath.Text = _theCharacter.FilePath;
        txtFilePath.FontStyle = FontStyles.Normal;
        Title = "Modify Character Settings";

        var selectedVoice = _theCharacter.Voice;
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

        rateOption.SelectedIndex = _theCharacter.VoiceRate;

        if (_theCharacter.ActiveColor != null && UiUtil.GetBrush(_theCharacter.ActiveColor) is { } activeColor)
        {
          activeColorPicker.Color = activeColor.Color;
          activeColorPicker.Visibility = Visibility.Visible;
          activeSelectText.Visibility = Visibility.Collapsed;
        }

        if (_theCharacter.FontColor != null && UiUtil.GetBrush(_theCharacter.FontColor) is { } fontColor)
        {
          fontColorPicker.Color = fontColor.Color;
          fontColorPicker.Visibility = Visibility.Visible;
          fontSelectText.Visibility = Visibility.Collapsed;
        }
      }
      else
      {
        characterName.Text = EnterName;
      }

      _ready = true;
    }

    private void CancelClicked(object sender, RoutedEventArgs e) => Close();
    private void TextChanged(object sender, TextChangedEventArgs e) => EnableSave();

    private void NamePreviewKeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.Escape)
      {
        characterName.Text = "";
        cancelButton.Focus();
      }
    }

    private void NameGotFocus(object sender, RoutedEventArgs e)
    {
      if (characterName?.FontStyle == FontStyles.Italic)
      {
        characterName.Text = "";
        characterName.FontStyle = FontStyles.Normal;
      }
    }

    private void NameLostFocus(object sender, RoutedEventArgs e)
    {
      if (string.IsNullOrEmpty(characterName.Text))
      {
        characterName.FontStyle = FontStyles.Italic;
        characterName.Text = EnterName;
      }
    }

    private async void SaveClicked(object sender, RoutedEventArgs e)
    {
      var activeColor = activeColorPicker.Visibility == Visibility.Visible ? activeColorPicker?.Color.ToHexString() : null;
      var fontColor = fontColorPicker.Visibility == Visibility.Visible ? fontColorPicker?.Color.ToHexString() : null;

      if (_theCharacter == null)
      {
        await TriggerStateManager.Instance.AddCharacter(characterName.Text, txtFilePath.Text, voices.SelectedValue.ToString(),
          rateOption.SelectedIndex, activeColor, fontColor);
      }
      else
      {
        await TriggerStateManager.Instance.UpdateCharacter(_theCharacter.Id, characterName.Text, txtFilePath.Text, voices.SelectedValue.ToString(),
          rateOption.SelectedIndex, activeColor, fontColor);
      }

      Close();
    }

    private void EnableSave()
    {
      if (saveButton != null)
      {
        saveButton.IsEnabled = characterName?.FontStyle != FontStyles.Italic && txtFilePath?.FontStyle != FontStyles.Italic &&
          characterName?.Text.Length > 0 && txtFilePath?.Text.Length > 0;
      }
    }

    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
      if (_ready)
      {
        // default to rate
        var tts = rateOption.SelectedIndex == 0 ? "Default Voice Rate" : "Voice Rate " + rateOption.SelectedIndex;
        string voice = null;
        if (voices.SelectedItem is string name && !string.IsNullOrEmpty(name))
        {
          voice = name;
          if (Equals(sender, voices))
          {
            tts = name;
          }
        }

        AudioManager.Instance.TestSpeakTtsAsync(tts, voice, rateOption.SelectedIndex);
        EnableSave();
      }
    }

    private void ChooseFileClicked(object sender, RoutedEventArgs e)
    {
      var initialPath = string.IsNullOrEmpty(txtFilePath.Text) ? string.Empty : Path.GetDirectoryName(txtFilePath.Text);

      var dialog = new CommonOpenFileDialog
      {
        // Set to false because we're opening a file, not selecting a folder
        IsFolderPicker = false,
        // Set the initial directory
        InitialDirectory = initialPath ?? "",
      };

      // Show dialog and read result
      dialog.Filters.Add(new CommonFileDialogFilter("eqlog_Player_server", "*.txt"));

      var handle = new WindowInteropHelper(this).Handle;
      if (dialog.ShowDialog(handle) == CommonFileDialogResult.Ok)
      {
        txtFilePath.FontStyle = FontStyles.Normal;
        txtFilePath.Text = dialog.FileName;
      }
    }

    private void ResetActiveColorClick(object sender, RoutedEventArgs e)
    {
      activeColorPicker.Visibility = Visibility.Collapsed;
      activeSelectText.Visibility = Visibility.Visible;
      EnableSave();
    }

    private void SelectActiveColorClick(object sender, MouseButtonEventArgs e)
    {
      activeColorPicker.Visibility = Visibility.Visible;
      activeSelectText.Visibility = Visibility.Collapsed;
      EnableSave();
    }

    private void ResetFontColorClick(object sender, RoutedEventArgs e)
    {
      fontColorPicker.Visibility = Visibility.Collapsed;
      fontSelectText.Visibility = Visibility.Visible;
      EnableSave();
    }

    private void SelectFontColorClick(object sender, MouseButtonEventArgs e)
    {
      fontColorPicker.Visibility = Visibility.Visible;
      fontSelectText.Visibility = Visibility.Collapsed;
      EnableSave();
    }

    private void SelectedColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      if (_ready)
      {
        EnableSave();
      }
    }
  }
}
