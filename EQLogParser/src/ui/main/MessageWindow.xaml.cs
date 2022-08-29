using FontAwesome5;
using Syncfusion.Windows.Shared;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for MessageWindow.xaml
  /// </summary>
  public partial class MessageWindow : ChromelessWindow
  {
    public bool IsYesClicked = false;

    public MessageWindow(string text, string caption, bool question = false, bool save = false)
    {
      MainActions.SetTheme(this, MainWindow.CurrentTheme);
      InitializeComponent();
      textBlock.Text = text;
      Title = caption;

      var brush = "EQWarnForegroundBrush";
      var image = EFontAwesomeIcon.Solid_ExclamationTriangle;

      if (question)
      {
        brush = "EQMenuIconBrush";
        image = EFontAwesomeIcon.Regular_QuestionCircle;
        okButton.Content = "No";
        yesButton.Visibility = System.Windows.Visibility.Visible;
      }

      if (save)
      {
        brush = "EQMenuIconBrush";
        image = EFontAwesomeIcon.Solid_Save;
        okButton.Visibility = System.Windows.Visibility.Hidden;
        Height -= 15;
        Width -= 30;
      }

      iconImage.SetResourceReference(ImageAwesome.ForegroundProperty, brush);
      iconImage.Icon = image;
    }

    private void ButtonOkClick(object sender, System.Windows.RoutedEventArgs e)
    {
      Close();
    }

    private void ButtonYesClick(object sender, System.Windows.RoutedEventArgs e)
    {
      IsYesClicked = true;
      Close();
    }
  }
}
