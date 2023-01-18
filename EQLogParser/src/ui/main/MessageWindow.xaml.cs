﻿using FontAwesome5;
using Syncfusion.Windows.Shared;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for MessageWindow.xaml
  /// </summary>
  public partial class MessageWindow : ChromelessWindow
  {
    public bool IsYes2Clicked = false;
    public bool IsYes1Clicked = false;
    public enum IconType { Question, Save, Warn }

    public MessageWindow(string text, string caption, IconType type = IconType.Warn, string yes1 = null, string yes2 = null)
    {
      MainActions.SetTheme(this, MainWindow.CurrentTheme);
      InitializeComponent();
      textBlock.Text = text;
      Title = caption;

      string brush = "";
      EFontAwesomeIcon image = EFontAwesomeIcon.None;
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
        Height -= 15;
        Width -= 30;
      }

      cancelButton.Content = "Ok";

      if (!string.IsNullOrEmpty(yes1))
      {
        yesButton1.Content = yes1;
        yesButton1.Visibility = System.Windows.Visibility.Visible;
        cancelButton.Content = "Cancel";
      }

      if (!string.IsNullOrEmpty(yes2))
      {
        yesButton2.Content = yes2;
        yesButton2.Visibility = System.Windows.Visibility.Visible;
        cancelButton.Content = "Cancel";
      }

      iconImage.SetResourceReference(ImageAwesome.ForegroundProperty, brush);
      iconImage.Icon = image;
    }

    private void ButtonCancelClick(object sender, System.Windows.RoutedEventArgs e)
    {
      Close();
    }

    private void ButtonYes1Click(object sender, System.Windows.RoutedEventArgs e)
    {
      IsYes1Clicked = true;
      Close();
    }

    private void ButtonYes2Click(object sender, System.Windows.RoutedEventArgs e)
    {
      IsYes2Clicked = true;
      Close();
    }
  }
}
