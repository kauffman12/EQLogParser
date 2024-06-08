using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for SplashWindow.xaml
  /// </summary>
  public partial class SplashWindow
  {
    private bool _isClosed = false;

    internal SplashWindow()
    {
      InitializeComponent();
      ConfigUtil.EventsLoadingText += ConfigUtilEventsLoadingText;
    }

    private void ConfigUtilEventsLoadingText(string text) => AddLoadingText(text);

    private void SplashWindowOnClosing(object sender, CancelEventArgs e)
    {
      ConfigUtil.EventsLoadingText -= ConfigUtilEventsLoadingText;
    }

    private void AddLoadingText(string text)
    {
      Dispatcher.InvokeAsync(() =>
      {
        if (_isClosed)
        {
          return;
        }

        if (text == "Done")
        {
          _isClosed = true;
          ConfigUtil.EventsLoadingText -= ConfigUtilEventsLoadingText;
          MainActions.GetOwner().IsEnabled = true;
          Dispatcher.InvokeAsync(Close, DispatcherPriority.Background);
        }
        else
        {
          Dispatcher.Invoke(() =>
          {
            var block = new TextBlock
            {
              HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
              FontSize = 10,
              Text = text
            };

            data.Children.Add(block);

            if (data.Children.Count > 3)
            {
              data.Children.RemoveAt(0);
            }
          }, DispatcherPriority.Render);
        }
      }, DispatcherPriority.Background);
    }
  }
}
