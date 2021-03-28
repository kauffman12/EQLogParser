using FontAwesome.WPF;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for EQLogViewer.xaml
  /// </summary>
  public partial class EQLogViewer : UserControl, IDisposable
  {
    private static readonly List<double> FontSizeList = new List<double>() { 10, 12, 14, 16, 18, 20, 22, 24 };
    private static readonly List<string> Times = new List<string>() { "< 1 hrs", "< 12 hrs", "< 24 hrs", "< 1 wks", "< 2 wks", "Any Time" };
    private static readonly DateUtil DateUtil = new DateUtil();
    private static bool Complete = true;
    private static bool Running = false;

    public EQLogViewer()
    {
      InitializeComponent();
      fontSize.ItemsSource = FontSizeList;
      logSearchTime.ItemsSource = Times;

      string fgColor = ConfigUtil.GetSetting("EQLogViewerFontFgColor");
      if (fontFgColor.ItemsSource is List<ColorItem> colors)
      {
        fontFgColor.SelectedItem = (colors.Find(item => item.Name == fgColor) is ColorItem found) ? found : colors.Find(item => item.Name == "#ffffff");
      }

      string family = ConfigUtil.GetSetting("EQLogViewerFontFamily");
      fontFamily.SelectedItem = (family != null) ? new FontFamily(family) : logBox.FontFamily;

      string size = ConfigUtil.GetSetting("EQLogViewerFontSize");
      if (size != null && double.TryParse(size, out double dsize))
      {
        fontSize.SelectedItem = dsize;
      }
      else
      {
        fontSize.SelectedValue = logBox.FontSize;
      }

      logSearch.Text = Properties.Resources.LOG_SEARCH_TEXT;
      logSearch2.Text = Properties.Resources.LOG_SEARCH_TEXT;
      progress.Foreground = MainWindow.WARNING_BRUSH;
      searchButton.Focus();
    }

    private void SearchClick(object sender, MouseButtonEventArgs e)
    {
      if (!Running && Complete)
      {
        Running = true;
        progress.Visibility = Visibility.Visible;
        logBox.Document.Blocks.Clear();
        statusCount.Text = logBox.Document.Blocks.Count + " Lines";
        progress.Content = "Searching";
        searchIcon.Icon = FontAwesomeIcon.Close;
        var logSearchText = logSearch.Text;
        var logSearchText2 = logSearch2.Text;
        var modifierIndex = logSearchModifier.SelectedIndex;
        var logTimeIndex = logSearchTime.SelectedIndex;

        Task.Delay(75).ContinueWith(task =>
        {
          if (MainWindow.CurrentLogFile != null)
          {
            using (var f = File.OpenRead(MainWindow.CurrentLogFile))
            {
              SetStartingPosition(f, logTimeIndex);
              var s = new StreamReader(f);
              var list = new List<string>();
              int lastPercent = -1;

              while (!s.EndOfStream && Running)
              {
                string line = s.ReadLine();

                if (TimeCheck(line, logTimeIndex))
                {
                  bool match = true;
                  int secondIndex = -2;
                  int firstIndex = line.IndexOf(logSearchText, StringComparison.OrdinalIgnoreCase);
                  if (logSearchText2 != Properties.Resources.LOG_SEARCH_TEXT && logSearchText2.Length > 1)
                  {
                    secondIndex = line.IndexOf(logSearchText2, StringComparison.OrdinalIgnoreCase);
                  }

                  // AND
                  if (modifierIndex == 0 && (firstIndex == -1 || secondIndex == -1))
                  {
                    match = false;
                  }
                  // OR
                  else if (modifierIndex == 1 && firstIndex == -1 && secondIndex < 0)
                  {
                    match = false;
                  }
                  // Excluding
                  else if (modifierIndex == 2 && (firstIndex == -1 || secondIndex > -1))
                  {
                    match = false;
                  }

                  if (match)
                  {
                    list.Add(line);
                  }
                }

                var percent = Math.Min(Convert.ToInt32((double)f.Position / f.Length * 100), 100);
                if (percent % 5 == 0 && percent != lastPercent)
                {
                  lastPercent = percent;
                  Dispatcher.Invoke(() =>
                  {
                    progress.Content = "Searching (" + percent + "% Complete)";
                  }, System.Windows.Threading.DispatcherPriority.Background);
                }
              }

              list.Reverse();
              list = list.Take(5000).ToList();
              list.Reverse();

              Dispatcher.Invoke(() =>
              {
                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 0), Padding = new Thickness(4, 0, 0, 4) };

                list.ForEach(line =>
                {
                  paragraph.Inlines.Add(line);

                  if (list.Last() != line)
                  {
                    paragraph.Inlines.Add(Environment.NewLine);
                  }
                });

                logBox.Document.Blocks.Add(paragraph);
                statusCount.Text = list.Count + " Lines";
                if (list.Count == 5000)
                {
                  statusCount.Text += " (Maximum Reached)";
                }

                searchButton.IsEnabled = true;
                searchIcon.Icon = FontAwesomeIcon.Search;
                progress.Visibility = Visibility.Hidden;
                logScroller.ScrollToEnd();
                Running = false;
                Complete = true;
              });

              f.Close();
            }
          }
        }, TaskScheduler.Default);
      }
      else
      {
        searchButton.IsEnabled = false;
        Running = false;
      }
    }

    private void SetStartingPosition(FileStream f, int index)
    {
      if (f.Length > 100000000)
      {
        f.Seek(-50000000, SeekOrigin.End);
        var s = new StreamReader(f);
        s.ReadLine();
        var test = s.ReadLine();
        s.DiscardBufferedData();

        if (TimeCheck(test, index))
        {
          var mid = f.Length / 2;
          if (mid > 100000000)
          {
            f.Seek(mid, SeekOrigin.Begin);
            s = new StreamReader(f);
            s.ReadLine();
            test = s.ReadLine();
            s.DiscardBufferedData();

            if (TimeCheck(test, index))
            {
              f.Seek(0, SeekOrigin.Begin);
            }
          }
          else
          {
            f.Seek(0, SeekOrigin.Begin);
          }
        }
      }
    }

    private bool TimeCheck(string line, int index)
    {
      bool pass = true;

      if (!string.IsNullOrEmpty(line) && line.Length >= 24 && index >= 0 && index < 5)
      {
        var timeString = line.Substring(1, 24);
        var logTime = DateUtil.ParseDate(timeString);
        var currentTime = DateUtil.ToDouble(DateTime.Now);
        switch (index)
        {
          case 0:
            pass = (currentTime - logTime) < (60 * 60);
            break;
          case 1:
            pass = (currentTime - logTime) < (60 * 60) * 12;
            break;
          case 2:
            pass = (currentTime - logTime) < (60 * 60) * 24;
            break;
          case 3:
            pass = (currentTime - logTime) < (60 * 60) * 24 * 7;
            break;
          case 4:
            pass = (currentTime - logTime) < (60 * 60) * 24 * 7 * 2;
            break;
        }
      }

      return pass;
    }

    private void LogKeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.PageDown)
      {
        var offset = Math.Min(logScroller.ExtentHeight, logScroller.VerticalOffset + logScroller.ViewportHeight);
        logScroller.ScrollToVerticalOffset(offset);
      }
      else if (e.Key == Key.PageUp)
      {
        var offset = Math.Max(0, logScroller.VerticalOffset - logScroller.ViewportHeight);
        logScroller.ScrollToVerticalOffset(offset);
      }
    }

    private void LogMouseWheel(object sender, MouseWheelEventArgs e)
    {
      if ((Keyboard.Modifiers & ModifierKeys.Control) > 0)
      {
        if (e.Delta < 0 && fontSize.SelectedIndex > 0)
        {
          fontSize.SelectedIndex--;
        }
        else if (e.Delta > 0 && fontSize.SelectedIndex < (fontSize.Items.Count - 1))
        {
          fontSize.SelectedIndex++;
        }
      }
    }

    private void SearchTextChange(object sender, RoutedEventArgs e)
    {
      searchButton.IsEnabled = (logSearch.Text != Properties.Resources.LOG_SEARCH_TEXT && (logSearch.Text.Length > 1) ||
        logSearch2.Text != Properties.Resources.LOG_SEARCH_TEXT && logSearch2.Text.Length > 1);
    }

    private void SearchKeyDown(object sender, KeyEventArgs e)
    {
      var textBox = sender as TextBox;
      if (e.Key == Key.Escape)
      {
        if (searchButton.Focus())
        {
          textBox.Text = Properties.Resources.LOG_SEARCH_TEXT;
          textBox.FontStyle = FontStyles.Italic;
        }
      }
    }

    private void SearchGotFocus(object sender, RoutedEventArgs e)
    {
      var textBox = sender as TextBox;
      if (textBox.Text == Properties.Resources.LOG_SEARCH_TEXT)
      {
        textBox.Text = "";
        textBox.FontStyle = FontStyles.Normal;
      }
    }

    private void SearchLostFocus(object sender, RoutedEventArgs e)
    {
      var textBox = sender as TextBox;
      if (string.IsNullOrEmpty(textBox.Text))
      {
        textBox.Text = Properties.Resources.LOG_SEARCH_TEXT;
        textBox.FontStyle = FontStyles.Italic;
      }
    }

    private void FontFgColor_Changed(object sender, SelectionChangedEventArgs e)
    {
      if (fontFgColor.SelectedItem != null)
      {
        var item = fontFgColor.SelectedItem as ColorItem;
        logBox.Foreground = item.Brush;
        ConfigUtil.SetSetting("EQLogViewerFontFgColor", item.Name);
      }
    }

    private void FontSize_Changed(object sender, SelectionChangedEventArgs e)
    {
      if (fontSize.SelectedItem != null)
      {
        logBox.FontSize = (double)fontSize.SelectedItem;
        ConfigUtil.SetSetting("EQLogViewerFontSize", fontSize.SelectedItem.ToString());
      }
    }

    private void FontFamily_Changed(object sender, SelectionChangedEventArgs e)
    {
      if (fontFamily.SelectedItem != null)
      {
        var family = fontFamily.SelectedItem as FontFamily;
        logBox.FontFamily = family;
        ConfigUtil.SetSetting("EQLogViewerFontFamily", family.ToString());
      }
    }

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        if (disposing)
        {
          // TODO: dispose managed state (managed objects).
        }

        disposedValue = true;
      }
    }

    // This code added to correctly implement the disposable pattern.
    public void Dispose()
    {
      // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
      Dispose(true);
      // TODO: uncomment the following line if the finalizer is overridden above.
      GC.SuppressFinalize(this);
    }
    #endregion
  }
}
