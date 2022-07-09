using FontAwesome5;
using Syncfusion.Windows.Edit;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for EQLogViewer.xaml
  /// </summary>
  public partial class EQLogViewer : UserControl
  {
    private const int CONTEXT = 30000;
    private const int MAX_ROWS = 250000;
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly List<double> FontSizeList = new List<double>() { 10, 12, 14, 16, 18, 20, 22, 24 };
    private static readonly List<string> Times = new List<string>() { "Last Hour", "Last 8 Hours", "Last 24 Hours", "Last 7 Days", "Last 14 Days", "Last 30 Days", "Everything" };
    private static readonly DateUtil DateUtil = new DateUtil();
    private static bool Complete = true;
    private static bool Running = false;
    private readonly DispatcherTimer FilterTimer;
    private List<string> UnFiltered = new List<string>();
    private Dictionary<long, long> FilteredLinePositionMap = new Dictionary<long, long>();
    private Dictionary<long, long> LinePositions = new Dictionary<long, long>();

    public EQLogViewer()
    {
      InitializeComponent();
      fontSize.ItemsSource = FontSizeList;
      logSearchTime.ItemsSource = Times;
      fontFamily.ItemsSource = System.Windows.Media.Fonts.SystemFontFamilies.OrderBy(f => f.Source).ToList();

      try
      {
        string fgColor = ConfigUtil.GetSetting("EQLogViewerFontFgColor");
        colorPicker.Color = (Color)ColorConverter.ConvertFromString(fgColor);
      }
      catch (FormatException)
      {
        colorPicker.Color = Colors.White;
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
      progress.Foreground = Application.Current.Resources["EQWarnForegroundBrush"] as SolidColorBrush;
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
      FilteredLinePositionMap.Clear();

      if (logFilter.Text == Properties.Resources.LOG_FILTER_TEXT)
      {
        logBox.Text = string.Join(Environment.NewLine, UnFiltered);
        UpdateStatusCount(UnFiltered.Count);
        if (logBox.Lines.Count > 0)
        {
          GoToLine(logBox, logBox.Lines.Count);
        }
      }
      else if (logFilter.Text != Properties.Resources.LOG_FILTER_TEXT && logFilter.Text.Length > 1)
      {
        var filtered = new List<string>();
        int lineCount = -1;
        foreach (ref string line in UnFiltered.ToArray().AsSpan())
        {
          lineCount++;
          if (logFilterModifier.SelectedIndex == 0 && line.IndexOf(logFilter.Text, StringComparison.OrdinalIgnoreCase) > -1 ||
          logFilterModifier.SelectedIndex == 1 && line.IndexOf(logFilter.Text, StringComparison.OrdinalIgnoreCase) < 0)
          {
            FilteredLinePositionMap[filtered.Count] = LinePositions[lineCount];
            filtered.Add(line);
          }
        }

        logBox.Text = string.Join(Environment.NewLine, filtered);
        UpdateStatusCount(filtered.Count);
        if (logBox.Lines.Count > 0)
        {
          GoToLine(logBox, logBox.Lines.Count);
        }
      }
    }

    private void LoadContext(long pos, string text)
    {
      Task.Delay(50).ContinueWith(task =>
      {
        if (MainWindow.CurrentLogFile != null)
        {
          using (var f = File.OpenRead(MainWindow.CurrentLogFile))
          {
            f.Seek(Math.Max(0, pos - CONTEXT), SeekOrigin.Begin);
            StreamReader s = GetStreamReader(f);
            var list = new List<string>();

            if (!s.EndOfStream)
            {
              // since position is not the start of a line just read one as junk
              s.ReadLine();
            }

            List<int> FoundLines = new List<int>();
            while (!s.EndOfStream)
            {
              var line = s.ReadLine();
              list.Add(line);

              if (line.Contains(text))
              {
                FoundLines.Add(list.Count);
              }

              if (f.Position >= (pos + CONTEXT))
              {
                break;
              }
            }

            var allText = string.Join(Environment.NewLine, list);
            Dispatcher.InvokeAsync(() =>
            {
              SolidColorBrush highlight = Application.Current.Resources["EQSearchBackgroundBrush"] as SolidColorBrush;
              contextBox.Text = allText;
              contextTab.Visibility = Visibility.Visible;
              FoundLines.ForEach(line => contextBox.SetLineBackground(line, true, highlight));
              tabControl.SelectedItem = contextTab;
              UpdateStatusCount(contextBox.Lines.Count);

              if (FoundLines.Count > 0)
              {
                GoToLine(contextBox, FoundLines[0] + 3);
              }
            });

            f.Close();
          }
        }
      }, TaskScheduler.Default);
    }

    private void GoToLine(EditControl control, int line)
    {
      // GoToLine just doesnt work until UI is fully rendered
      Task.Delay(250).ContinueWith(task => Dispatcher.Invoke(() =>
      {
        try
        {
          control.GoToLine(Math.Min(line, control.Lines.Count));
        }
        catch (NullReferenceException)
        {

        }
      }));
    }

    private void SearchClick(object sender, MouseButtonEventArgs e)
    {
      if (!Running && Complete)
      {
        Running = true;
        progress.Visibility = Visibility.Visible;
        progress.Content = "Searching";
        searchIcon.Icon = EFontAwesomeIcon.Solid_TimesCircle;
        logBox.ClearAllText();
        var logSearchText = logSearch.Text;
        var logSearchText2 = logSearch2.Text;
        var modifierIndex = logSearchModifier.SelectedIndex;
        var logTimeIndex = logSearchTime.SelectedIndex;
        LinePositions.Clear();

        Task.Delay(75).ContinueWith(task =>
        {
          if (MainWindow.CurrentLogFile != null)
          {
            using (var f = File.OpenRead(MainWindow.CurrentLogFile))
            {
              StreamReader s = GetStreamReader(f, logTimeIndex);
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
                    LinePositions[list.Count] = f.Position;
                    list.Add(line);
                  }
                }

                var percent = Math.Min(Convert.ToInt32((double)f.Position / f.Length * 100), 100);
                if (percent % 5 == 0 && percent != lastPercent)
                {
                  lastPercent = percent;
                  Dispatcher.InvokeAsync(() =>
                  {
                    progress.Content = "Searching (" + percent + "% Complete)";
                  }, DispatcherPriority.Background);
                }
              }

              UnFiltered = list.Take(MAX_ROWS).ToList();
              var allData = string.Join(Environment.NewLine, UnFiltered);
              // adding extra new line to be away from scrollbar

              Dispatcher.InvokeAsync(() =>
              {
                if (!string.IsNullOrEmpty(allData))
                {
                  logBox.Text = allData;
                }

                // reset filter
                tabControl.SelectedItem = resultsTab;
                logFilter.Text = Properties.Resources.LOG_FILTER_TEXT;
                logFilter.FontStyle = FontStyles.Italic;
                UpdateStatusCount(UnFiltered.Count);
                if (logBox.Lines.Count > 0)
                {
                  GoToLine(logBox, logBox.Lines.Count);
                }
              });

              f.Close();
            }
          }

          Dispatcher.InvokeAsync(() =>
          {
            searchButton.IsEnabled = true;
            searchIcon.Icon = EFontAwesomeIcon.Solid_Search;
            progress.Visibility = Visibility.Hidden;
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

    private StreamReader GetStreamReader(FileStream f, int logTimeIndex = -1)
    {
      StreamReader s;
      if (!f.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
      {
        if (f.Length > 100000000 && logTimeIndex > -1)
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

      return s;
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

    private void UpdateStatusCount(int count)
    {
      statusCount.Text = count + " Lines";
      if (count == MAX_ROWS)
      {
        statusCount.Text += " (Maximum Reached)";
      }
    }

    private static bool TimeCheck(string line, int index)
    {
      bool pass = true;

      if (!string.IsNullOrEmpty(line) && line.Length > 24 && index >= 0 && index < 5)
      {
        var logTime = DateUtil.ParseDate(line);
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

    private void LogPreviewKeyDown(object sender, KeyEventArgs e)
    {
      // ignore these keys that open the save/options window
      if (e.Key == Key.O && (e.KeyboardDevice.Modifiers & ModifierKeys.Control) != 0)
      {
        e.Handled = true;
      }

      if (e.Key == Key.S && (e.KeyboardDevice.Modifiers & ModifierKeys.Control) != 0)
      {
        e.Handled = true;
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

        e.Handled = true;
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
    private void FontFgColor_Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      logBox.Foreground = new SolidColorBrush(colorPicker.Color);
      contextBox.Foreground = new SolidColorBrush(colorPicker.Color);
      ConfigUtil.SetSetting("EQLogViewerFontFgColor", TextFormatUtils.GetHexString(colorPicker.Color));
    }

    private void FontSize_Changed(object sender, SelectionChangedEventArgs e)
    {
      if (fontSize.SelectedItem != null)
      {
        logBox.FontSize = (double)fontSize.SelectedItem;
        contextBox.FontSize = (double)fontSize.SelectedItem;
        ConfigUtil.SetSetting("EQLogViewerFontSize", fontSize.SelectedItem.ToString());
      }
    }

    private void FontFamily_Changed(object sender, SelectionChangedEventArgs e)
    {
      if (fontFamily.SelectedItem != null)
      {
        var family = fontFamily.SelectedItem as FontFamily;
        logBox.FontFamily = family;
        contextBox.FontFamily = family;
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

    private void logBox_SelectedTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      selectedContext.IsEnabled = !string.IsNullOrEmpty(logBox.SelectedText);
    }

    private void SelectedContext(object sender, RoutedEventArgs e)
    {
      if (logBox.SelectedTextPointer != null && LinePositions.ContainsKey(logBox.SelectedTextPointer.StartLine))
      {
        long start;
        if (FilteredLinePositionMap.ContainsKey(logBox.SelectedTextPointer.StartLine))
        {
          start = FilteredLinePositionMap[logBox.SelectedTextPointer.StartLine];
        }
        else
        {
          start = LinePositions[logBox.SelectedTextPointer.StartLine];
        }

        LoadContext(start, logBox.Lines[logBox.SelectedTextPointer.StartLine].Text);
      }
    }

    private void tabControl_PreviewSelectedItemChangedEvent(object sender, Syncfusion.Windows.Tools.Controls.PreviewSelectedItemChangedEventArgs e)
    {
      if (e.NewSelectedItem == resultsTab)
      {
        UpdateStatusCount(logBox.Lines.Count - 1);
      }
      else if (e.NewSelectedItem == contextTab)
      {
        UpdateStatusCount(contextBox.Lines.Count - 1);
      }
    }

    private void tabControl_TabClosed(object sender, Syncfusion.Windows.Tools.Controls.CloseTabEventArgs e)
    {
      // can only close the context and display the results
      UpdateStatusCount(logBox.Lines.Count - 1);
    }

    private void OptionsChange(object sender, EventArgs e) => UpdateUI();
  }
}
