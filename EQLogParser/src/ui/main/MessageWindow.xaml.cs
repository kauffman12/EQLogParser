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

    public MessageWindow(string text, string caption, bool question = false)
    {
      MainActions.SetTheme(this, MainWindow.CurrentTheme);
      InitializeComponent();
      textBlock.Text = text;
      Title = caption;

      var brush = question ? "EQMenuIconBrush" : "EQWarnForegroundBrush";
      iconImage.SetResourceReference(ImageAwesome.ForegroundProperty, brush);
      iconImage.Icon = question ? EFontAwesomeIcon.Solid_QuestionCircle : EFontAwesomeIcon.Solid_ExclamationTriangle;
      if (question)
      {
        okButton.Content = "No";
        yesButton.Visibility = System.Windows.Visibility.Visible;
      }
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
