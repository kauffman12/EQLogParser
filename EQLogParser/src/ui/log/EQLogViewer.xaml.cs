﻿using FontAwesome5;
using Syncfusion.SfSkinManager;
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
  public partial class EQLogViewer : UserControl, IDisposable
  {
    private const int MAX_ROWS = 250000;
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly List<double> FontSizeList = new List<double>() { 10, 12, 14, 16, 18, 20, 22, 24 };
    private static readonly List<string> Times = new List<string>() { "Last Hour", "Last 8 Hours", "Last 24 Hours", "Last 7 Days", "Last 14 Days", "Last 30 Days", "Everything" };
    private static readonly DateUtil DateUtil = new DateUtil();
    private static bool Complete = true;
    private static bool Running = false;
    private readonly DispatcherTimer FilterTimer;
    private List<string> UnFiltered = new List<string>();

    public EQLogViewer()
    {
      InitializeComponent();
      SfSkinManager.SetTheme(colorPicker, new Theme("FluentDark", new string[] { "ColorPicker" }));
      fontSize.ItemsSource = FontSizeList;
      logSearchTime.ItemsSource = Times;

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

    private void LogCopyClick(object sender, RoutedEventArgs e)
    {

    }

    private void UpdateUI()
    {
      if (logFilter.Text == Properties.Resources.LOG_FILTER_TEXT)
      {
        logBox.Text = string.Join(Environment.NewLine, UnFiltered);
        UpdateStatusCount(UnFiltered.Count);
      }
      else if (logFilter.Text != Properties.Resources.LOG_FILTER_TEXT && logFilter.Text.Length > 1)
      {
        var filtered = new List<string>();
        foreach (ref string line in UnFiltered.ToArray().AsSpan())
        {
          if (logFilterModifier.SelectedIndex == 0 && line.IndexOf(logFilter.Text, StringComparison.OrdinalIgnoreCase) > -1 ||
          logFilterModifier.SelectedIndex == 1 && line.IndexOf(logFilter.Text, StringComparison.OrdinalIgnoreCase) < 0)
          {
            filtered.Add(line);
          }
        }

        logBox.Text = string.Join(Environment.NewLine, filtered);
        UpdateStatusCount(filtered.Count);
      }
    }

    private void SearchClick(object sender, MouseButtonEventArgs e)
    {
      if (!Running && Complete)
      {
        Running = true;
        progress.Visibility = Visibility.Visible;
        UpdateStatusCount(0);
        progress.Content = "Searching";
        searchIcon.Icon = EFontAwesomeIcon.Solid_TimesCircle;
        logBox.ClearAllText();
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

              UnFiltered = list.Take(MAX_ROWS).ToList();
              var allData = string.Join(Environment.NewLine, UnFiltered);

              Dispatcher.Invoke(() =>
              {
                if (!string.IsNullOrEmpty(allData))
                {
                  logBox.Text = allData;
                }

                // reset filter
                logFilter.Text = Properties.Resources.LOG_FILTER_TEXT;
                logFilter.FontStyle = FontStyles.Italic;
                UpdateStatusCount(UnFiltered.Count);
              });

              f.Close();
            }
          }

          Dispatcher.Invoke(() =>
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

      if (logBox.Lines.Count > 0)
      {
        logBox.GoToLine(logBox.Lines.Count);
        logBox.Lines[logBox.Lines.Count - 1].BringIntoView();
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
      ConfigUtil.SetSetting("EQLogViewerFontFgColor", TextFormatUtils.GetHexString(colorPicker.Color));
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
