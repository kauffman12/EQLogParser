using FontAwesome5;
using Syncfusion.Windows.Shared;
using System.Windows;
using System.Windows.Input;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for MessageWindow.xaml
  /// </summary>
  public partial class MessageWindow : ChromelessWindow
  {
    public bool IsYes2Clicked;
    public bool IsYes1Clicked;
    public bool MergeOption;
    public enum IconType { Question, Save, Warn, Info }
    private readonly string CopyData;

    public MessageWindow(string text, string caption, string copyData) : this(text, caption, IconType.Info)
    {
      CopyData = copyData;
      copyLink.Visibility = Visibility.Visible;
      Clipboard.SetText(copyData);
    }

    public MessageWindow(string text, string caption, IconType type = IconType.Warn, string yes1 = null, string yes2 = null, bool extra = false)
    {
      MainActions.SetTheme(this, MainWindow.CurrentTheme);
      InitializeComponent();
      textBox.Text = text;
      Title = caption;

      var brush = "";
      var image = EFontAwesomeIcon.None;
      if (type == IconType.Warn)
      {
        brush = "EQWarnForegroundBrush";
        image = EFontAwesomeIcon.Solid_ExclamationTriangle;
      }
      else if (type == IconType.Question)
      {
        brush = "EQMenuIconBrush";
        image = EFontAwesomeIcon.Regular_QuestionCircle;
      }
      else if (type == IconType.Save)
      {
        brush = "EQMenuIconBrush";
        image = EFontAwesomeIcon.Solid_Save;
      }
      else if (type == IconType.Info)
      {
        brush = "EQMenuIconBrush";
        image = EFontAwesomeIcon.Solid_InfoCircle;
      }

      cancelButton.Content = "Ok";

      if (!string.IsNullOrEmpty(yes1))
      {
        yesButton1.Content = yes1;
        yesButton1.Visibility = Visibility.Visible;
        cancelButton.Content = "Cancel";
      }

      if (!string.IsNullOrEmpty(yes2))
      {
        yesButton2.Content = yes2;
        yesButton2.Visibility = Visibility.Visible;
        cancelButton.Content = "Cancel";

        if (extra)
        {
          mergeLabel.Visibility = Visibility.Visible;
          mergeOption.Visibility = Visibility.Visible;
        }
      }

      iconImage.SetResourceReference(ImageAwesome.ForegroundProperty, brush);
      iconImage.Icon = image;
    }

    private void ButtonCancelClick(object sender, RoutedEventArgs e)
    {
      Close();
    }

    private void ButtonYes1Click(object sender, RoutedEventArgs e)
    {
      MergeOption = mergeOption.IsChecked == true;
      IsYes1Clicked = true;
      Close();
    }

    private void ButtonYes2Click(object sender, RoutedEventArgs e)
    {
      MergeOption = mergeOption.IsChecked == true;
      IsYes2Clicked = true;
      Close();
    }

    private void CopyLinkPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
      if (CopyData != null)
      {
        Clipboard.SetText(CopyData);
      }
    }
  }
}
