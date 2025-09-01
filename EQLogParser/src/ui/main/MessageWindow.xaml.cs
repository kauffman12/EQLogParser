using FontAwesome5;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace EQLogParser
{
  public partial class MessageWindow
  {
    public bool IsYes2Clicked;
    public bool IsYes1Clicked;
    public bool MergeOption;
    public enum IconType { Question, Save, Warn, Info }
    private readonly string _copyData;
    private readonly Func<Task> _yes1Callback;

    public MessageWindow(string text, string caption, string copyData, string yes1, Func<Task> yes1Callback) : this(text, caption, IconType.Info, yes1)
    {
      _copyData = copyData;
      _yes1Callback = yes1Callback;
      copyLink.Visibility = Visibility.Visible;
      cancelButton.Content = "Ok";
      UiUtil.SetClipboardText(copyData);
    }

    public MessageWindow(string text, string caption, IconType type = IconType.Warn, string yes1 = null, string yes2 = null,
      bool extra = false, bool noButtons = false)
    {
      MainActions.SetCurrentTheme(this);
      InitializeComponent();
      Owner = MainActions.GetOwner();
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

      if (noButtons)
      {
        cancelButton.Visibility = Visibility.Hidden;
      }

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

    private void ButtonCancelClick(object sender, RoutedEventArgs e) => Close();

    private async void CopyLinkPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
      UiUtil.SetClipboardText(_copyData);

      // show some feedback to the user
      copyLink.Cursor = Cursors.Arrow;
      await Task.Delay(250);
      copyLink.Cursor = Cursors.Hand;
    }

    private void ButtonYes1Click(object sender, RoutedEventArgs e)
    {
      // if custom callback the click isnt a close operation
      if (_yes1Callback != null)
      {
        _yes1Callback();
      }
      else
      {
        MergeOption = mergeOption.IsChecked == true;
        IsYes1Clicked = true;
        Close();
      }
    }

    private void ButtonYes2Click(object sender, RoutedEventArgs e)
    {
      MergeOption = mergeOption.IsChecked == true;
      IsYes2Clicked = true;
      Close();
    }

    private const int GwlExstyle = -20;
    private const int WsExNoactivate = 0x08000000;

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private void SetWindowNoActivate()
    {
      var helper = new System.Windows.Interop.WindowInteropHelper(this);
      var exStyle = GetWindowLong(helper.Handle, GwlExstyle);
      _ = SetWindowLong(helper.Handle, GwlExstyle, exStyle | WsExNoactivate);
    }

    private void MessageWindowLoaded(object sender, RoutedEventArgs e)
    {
      SetWindowNoActivate();
    }
  }
}
