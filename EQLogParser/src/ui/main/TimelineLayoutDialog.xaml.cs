using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EQLogParser
{
  public partial class TimelineLayoutDialog
  {
    private static readonly Regex ValidNamePattern = new(@"^[a-zA-Z0-9 _-]+$");
    private static readonly string[] ReservedNames =
    {
      "CON", "PRN", "AUX", "NUL",
      "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
      "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public string LayoutName { get; private set; }
    public bool IsSaveClicked { get; private set; }

    public TimelineLayoutDialog()
    {
      MainActions.SetCurrentTheme(this);
      InitializeComponent();
      Owner = MainActions.GetOwner();
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
      layoutNameTextBox.Focus();
      layoutNameTextBox.SelectAll();
    }

    private void layoutNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
      errorMessage.Visibility = Visibility.Hidden;
      ValidateAndEnableSave();
    }

    private void layoutNameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.Enter)
      {
        if (IsValidLayoutName())
        {
          SaveLayout();
        }
      }
    }

    private void ValidateAndEnableSave()
    {
      var isValid = IsValidLayoutName();
      saveButton.IsEnabled = isValid;
    }

    private bool IsValidLayoutName()
    {
      var name = layoutNameTextBox.Text?.Trim();

      if (string.IsNullOrEmpty(name))
      {
        return false;
      }

      if (name.Length > 100)
      {
        ShowError("Name too long (max 100 characters)");
        return false;
      }

      if (!ValidNamePattern.IsMatch(name))
      {
        ShowError("Invalid characters. Use simple names.");
        return false;
      }

      if (ReservedNames.Any(n => n == name.ToUpperInvariant()))
      {
        ShowError("That name is reserved. Please choose a different name");
        return false;
      }

      return true;
    }

    private void ShowError(string message)
    {
      errorMessage.Text = message;
      errorMessage.Visibility = Visibility.Visible;
    }

    private void SaveButtonClick(object sender, RoutedEventArgs e)
    {
      SaveLayout();
    }

    private void SaveLayout()
    {
      var name = layoutNameTextBox.Text?.Trim();
      if (string.IsNullOrEmpty(name))
      {
        ShowError("Please enter a name");
        return;
      }

      if (!IsValidLayoutName())
      {
        return;
      }

      LayoutName = name;
      IsSaveClicked = true;
      Close();
    }

    private void CancelButtonClick(object sender, RoutedEventArgs e)
    {
      IsSaveClicked = false;
      Close();
    }
  }
}
