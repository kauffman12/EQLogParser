using Microsoft.Win32;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TriggerPlayerConfig.xaml
  /// </summary>
  public partial class TriggerPlayerConfigWindow
  {
    private const string EnterName = "Enter Character Name";
    private readonly TriggerCharacter _theCharacter;
    private readonly SpeechSynthesizer _testSynth;
    private readonly bool _ready;

    internal TriggerPlayerConfigWindow(TriggerCharacter character = null)
    {
      MainActions.SetTheme(this, MainWindow.CurrentTheme);
      InitializeComponent();

      Owner = Application.Current.MainWindow;

      if ((_testSynth = TriggerUtil.GetSpeechSynthesizer()) != null)
      {
        voices.ItemsSource = _testSynth.GetInstalledVoices().Select(voice => voice.VoiceInfo.Name).ToList();
      }

      _theCharacter = character;
      if (_theCharacter != null)
      {
        characterName.Text = _theCharacter.Name;
        characterName.FontStyle = FontStyles.Normal;
        txtFilePath.Text = _theCharacter.FilePath;
        txtFilePath.FontStyle = FontStyles.Normal;
        Title = "Modify Character Settings";

        var selectedVoice = _theCharacter.Voice;
        if (voices.ItemsSource is List<string> populated && populated.IndexOf(selectedVoice) is var found and > -1)
        {
          voices.SelectedIndex = found;
        }

        rateOption.SelectedIndex = _theCharacter.VoiceRate;
      }
      else
      {
        characterName.Text = EnterName;
      }

      _ready = true;
    }

    private void CancelClicked(object sender, RoutedEventArgs e) => Close();

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

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
      if (_theCharacter == null)
      {
        TriggerStateManager.Instance.AddCharacter(characterName.Text, txtFilePath.Text, voices.SelectedValue.ToString(), rateOption.SelectedIndex);
      }
      else
      {
        TriggerStateManager.Instance.UpdateCharacter(_theCharacter.Id, characterName.Text, txtFilePath.Text, voices.SelectedValue.ToString(), rateOption.SelectedIndex);
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

    private void TextChanged(object sender, TextChangedEventArgs e) => EnableSave();

    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
      if (_ready)
      {
        if (Equals(sender, voices))
        {
          if (voices.SelectedValue is string voiceName)
          {
            if (_testSynth != null)
            {
              _testSynth.Rate = rateOption.SelectedIndex;
              _testSynth.SelectVoice(voiceName);
              _testSynth.SpeakAsync(voiceName);
            }
          }
        }
        else if (Equals(sender, rateOption))
        {
          if (_testSynth != null)
          {
            _testSynth.Rate = rateOption.SelectedIndex;
            if (voices.SelectedItem is string voice && !string.IsNullOrEmpty(voice))
            {
              _testSynth.SelectVoice(voice);
            }

            var rateText = rateOption.SelectedIndex == 0 ? "Default Voice Rate" : "Voice Rate " + rateOption.SelectedIndex;
            _testSynth.SpeakAsync(rateText);
          }
        }

        EnableSave();
      }
    }

    private void ChooseFileClicked(object sender, RoutedEventArgs e)
    {
      var initialPath = string.IsNullOrEmpty(txtFilePath.Text) ? string.Empty : Path.GetDirectoryName(txtFilePath.Text);

      var openFileDialog = new OpenFileDialog
      {
        // filter to txt files
        DefaultExt = ".txt",
        Filter = "eqlog_Player_server (.txt)|*.txt",
        InitialDirectory = initialPath ?? ""
      };

      if (openFileDialog.ShowDialog() == true)
      {
        txtFilePath.FontStyle = FontStyles.Normal;
        txtFilePath.Text = openFileDialog.FileName;
      }
    }

    private void TriggerPlayerConfigWindowOnClosing(object sender, CancelEventArgs e)
    {
      _testSynth?.Dispose();
    }
  }
}
