using log4net;
using log4net.Appender;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for SplashWindow.xaml
  /// </summary>
  public partial class SplashWindow
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private bool _isClosed;

    internal SplashWindow()
    {
      InitializeComponent();
      ConfigUtil.EventsLoadingText += ConfigUtilEventsLoadingText;
    }

    public void SetErrorState()
    {
      if (!_isClosed)
      {
        data.Visibility = Visibility.Collapsed;
        error.Visibility = Visibility.Visible;
      }
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
              HorizontalAlignment = HorizontalAlignment.Center,
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

    private void CloseButtonOnClick(object sender, RoutedEventArgs e)
    {
      if (!_isClosed)
      {
        _isClosed = true;
        Application.Current.Shutdown();
      }
    }

    private void ViewLogButtonOnClick(object sender, RoutedEventArgs e)
    {
      if (Log.Logger.Repository.GetAppenders().FirstOrDefault() is { } appender)
      {
        MainActions.OpenFileWithDefault("\"" + ((FileAppender)appender).File + "\"");
      }
    }
  }
}
