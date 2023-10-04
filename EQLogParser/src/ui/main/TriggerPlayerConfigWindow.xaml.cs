using Microsoft.Win32;
using Syncfusion.Windows.Shared;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TriggerPlayerConfig.xaml
  /// </summary>
  public partial class TriggerPlayerConfigWindow : ChromelessWindow
  {
    private const string NONAME = "Enter Character Name";

    private TriggerCharacter TheCharacter;

    internal TriggerPlayerConfigWindow(TriggerCharacter character = null)
    {
      MainActions.SetTheme(this, MainWindow.CurrentTheme);
      InitializeComponent();

      TheCharacter = character;
      if (TheCharacter != null)
      {
        characterName.Text = character.Name;
        characterName.FontStyle = FontStyles.Normal;
        txtFilePath.Text = character.FilePath;
        txtFilePath.FontStyle = FontStyles.Normal;
        Title = "Modify Character Settings";
      }
      else
      {
        characterName.Text = NONAME;
      }
    }

    private void CancelClicked(object sender, RoutedEventArgs e) => Close();

    private void TextChanged(object sender, TextChangedEventArgs e)
    {
      if (saveButton != null)
      {
        saveButton.IsEnabled = characterName?.FontStyle != FontStyles.Italic && txtFilePath?.FontStyle != FontStyles.Italic &&
          characterName?.Text?.Length > 0 && txtFilePath?.Text?.Length > 0;
      }
    }

    private void NamePreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
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
        characterName.Text = NONAME;
      }
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
      if (TheCharacter == null)
      {
        TriggerStateManager.Instance.AddCharacter(characterName.Text, txtFilePath.Text);
      }
      else
      {
        TriggerStateManager.Instance.UpdateCharacter(TheCharacter.Id, characterName.Text, txtFilePath.Text);
      }

      Close();
    }

    private void ChooseFileClicked(object sender, RoutedEventArgs e)
    {
      var openFileDialog = new OpenFileDialog
      {
        // filter to txt files
        DefaultExt = ".txt",
        Filter = "eqlog_Player_server (.txt)|*.txt",
      };

      if (openFileDialog.ShowDialog() == true)
      {
        txtFilePath.FontStyle = FontStyles.Normal;
        txtFilePath.Text = openFileDialog.FileName;
      }
    }
  }
}
