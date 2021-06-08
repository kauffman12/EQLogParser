using FontAwesome.WPF;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for EQLogViewer.xaml
  /// </summary>
  public partial class EQLogViewer : UserControl, IDisposable
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly List<double> FontSizeList = new List<double>() { 10, 12, 14, 16, 18, 20, 22, 24 };
    private static readonly List<string> Times = new List<string>() { "Last Hour", "Last 8 Hours", "Last 24 Hours", "Last 7 Days", "Last 14 Days", "Last 30 Days", "Everything" };
    private static readonly DateUtil DateUtil = new DateUtil();
    private static bool Complete = true;
    private static bool Running = false;
    private readonly DispatcherTimer FilterTimer;
    private int UnFilteredCount;
    private Paragraph UnFiltered;

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

      logFilter.Text = Properties.Resources.LOG_FILTER_TEXT;
      FilterTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1000) };
      FilterTimer.Tick += (sender, e) =>
      {
        UpdateUI();
        FilterTimer.Stop();
      };
    }

    private void UpdateUI()
    {
      if (logBox.Document.Blocks.FirstBlock is Paragraph para)
      {
        if (logFilter.Text == Properties.Resources.LOG_FILTER_TEXT && para != UnFiltered)
        {
          logBox.Document.Blocks.Clear();
          logBox.Document.Blocks.Add(UnFiltered);
          statusCount.Text = UnFilteredCount + " Lines";
          if (UnFilteredCount == 5000)
          {
            statusCount.Text += " (Maximum Reached)";
          }
        }
        else if (logFilter.Text != Properties.Resources.LOG_FILTER_TEXT && logFilter.Text.Length > 1)
        {
          int count = 0;
          var filtered = new Paragraph { Margin = new Thickness(0, 0, 0, 0), Padding = new Thickness(4, 0, 0, 4) };
          UnFiltered.Inlines.ToList().ForEach(inline =>
          {
            if (inline is Run run && run.Text != Environment.NewLine)
            {
              if (logFilterModifier.SelectedIndex == 0 && run.Text.IndexOf(logFilter.Text, StringComparison.OrdinalIgnoreCase) > -1 ||
              logFilterModifier.SelectedIndex == 1 && run.Text.IndexOf(logFilter.Text, StringComparison.OrdinalIgnoreCase) < 0)
              {
                count++;
                filtered.Inlines.Add(new Run(run.Text));
                filtered.Inlines.Add(new Run(Environment.NewLine));
              }
            }
          });

          // get rid of last new line character
          if (filtered.Inlines.Count > 0)
          {
            filtered.Inlines.Remove(filtered.Inlines.LastInline);
          }

          statusCount.Text = string.Format(CultureInfo.CurrentCulture, "{0} {1}", count, Properties.Resources.LINES_FILTERED);
          logBox.Document.Blocks.Clear();
          logBox.Document.Blocks.Add(filtered);
          logScroller.ScrollToEnd();
        }
      }
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
              StreamReader s;
              if (!f.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
              {
                if (f.Length > 100000000)
                {
                  SetStartingPosition(f, logTimeIndex);
                }

                s = new StreamReader(f);
              }
              else
              {
                var gs = new GZipStream(f, CompressionMode.Decompress);
                s = new StreamReader(gs, System.Text.Encoding.UTF8, true, 4096);
              }

              var list = new List<string>();
              int lastPercent = -1;

              while (!s.EndOfStream && Running)
              {
                string line = s.ReadLine();

                if (TimeCheck(line, logTimeIndex))
                {
                  bool match = true;
                  int firstIndex = -2;
                  int secondIndex = -2;

                  if (logSearchText != Properties.Resources.LOG_SEARCH_TEXT && logSearchText.Length > 1)
                  {
                    firstIndex = line.IndexOf(logSearchText, StringComparison.OrdinalIgnoreCase);
                  }

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
                  else if (modifierIndex == 1 && firstIndex < 0 && secondIndex < 0)
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
                  }, DispatcherPriority.Background);
                }
              }

              list.Reverse();
              list = list.Take(5000).ToList();
              list.Reverse();

              Dispatcher.Invoke(() =>
              {
                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 0), Padding = new Thickness(4, 0, 0, 4) };

                int lines = 0;
                list.ForEach(line =>
                {
                  lines++;
                  paragraph.Inlines.Add(line);

                  if (lines < list.Count)
                  {
                    paragraph.Inlines.Add(Environment.NewLine);
                  }
                });

                logBox.Document.Blocks.Add(paragraph);
                statusCount.Text = list.Count + " Lines";
                UnFilteredCount = list.Count;
                if (list.Count == 5000)
                {
                  statusCount.Text += " (Maximum Reached)";
                }

                UnFiltered = paragraph;

                // reset filter
                logFilter.Text = Properties.Resources.LOG_FILTER_TEXT;
                logFilter.FontStyle = FontStyles.Italic;
              });

              f.Close();
            }
          }

          Dispatcher.Invoke(() =>
          {
            searchButton.IsEnabled = true;
            searchIcon.Icon = FontAwesomeIcon.Search;
            progress.Visibility = Visibility.Hidden;
            logScroller.ScrollToEnd();
            Running = false;
            Complete = true;
          });
        }, TaskScheduler.Default);
      }
      else
      {
        searchButton.IsEnabled = false;
        Running = false;
      }
    }

    private void SetStartingPosition(FileStream f, int index, long left = 0, long right = 0, long good = 0, int count = 0)
    {
      if (count <= 5)
      {
        if (f.Position == 0)
        {
          right = f.Length;
          f.Seek(f.Length / 2, SeekOrigin.Begin);
        }

        try
        {
          var s = new StreamReader(f);
          s.ReadLine();
          var check = TimeCheck(s.ReadLine(), index);
          s.DiscardBufferedData();

          long pos = 0;
          if (check)
          {
            pos = left + (f.Position - left) / 2;
            right = f.Position;
          }
          else
          {
            pos = right - (right - f.Position) / 2;
            good = left = f.Position;
          }

          f.Seek(pos, SeekOrigin.Begin);
          SetStartingPosition(f, index, left, right, good, count + 1);
        }
        catch (IOException ioe)
        {
          LOG.Error("Problem searching log file", ioe);
        }
        catch (OutOfMemoryException ome)
        {
          LOG.Debug("Out of memory", ome);
        }
      }
      else if (f.Position != good)
      {
        f.Seek(good, SeekOrigin.Begin);
      }
    }

    private static bool TimeCheck(string line, int index)
    {
      bool pass = true;

      if (!string.IsNullOrEmpty(line) && line.Length > 24 && index >= 0 && index < 5)
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
            pass = (currentTime - logTime) < (60 * 60) * 8;
            break;
          case 2:
            pass = (currentTime - logTime) < (60 * 60) * 24;
            break;
          case 3:
            pass = (currentTime - logTime) < (60 * 60) * 24 * 7;
            break;
          case 4:
            pass = (currentTime - logTime) < (60 * 60) * 24 * 14;
            break;
          case 5:
            pass = (currentTime - logTime) < (60 * 60) * 24 * 30;
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
      else if (e.Key == Key.Enter)
      {
        searchButton.Focus();
        SearchClick(sender, null);
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

    private void FilterKeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.Escape)
      {
        logFilter.Text = Properties.Resources.LOG_FILTER_TEXT;
        logFilter.FontStyle = FontStyles.Italic;
        logBox.Focus();
      }
    }

    private void FilterGotFocus(object sender, RoutedEventArgs e)
    {
      if (logFilter.Text == Properties.Resources.LOG_FILTER_TEXT)
      {
        logFilter.Text = "";
        logFilter.FontStyle = FontStyles.Normal;
      }
    }

    private void FilterLostFocus(object sender, RoutedEventArgs e)
    {
      if (string.IsNullOrEmpty(logFilter.Text))
      {
        logFilter.Text = Properties.Resources.LOG_FILTER_TEXT;
        logFilter.FontStyle = FontStyles.Italic;
      }
    }

    private void FilterTextChanged(object sender, TextChangedEventArgs e)
    {
      FilterTimer?.Stop();
      FilterTimer?.Start();
    }

    private void OptionsChange(object sender, EventArgs e) => UpdateUI();

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
