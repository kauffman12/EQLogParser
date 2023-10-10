using FontAwesome5;
using Syncfusion.Windows.Edit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
    private const int CONTEXT = 30000;
    private const int MAX_ROWS = 250000;
    private const string DAMAGEAVOIDED = "Damage Avoided";
    private const string NOCAT = "Uncategorized";
    private const string OTHERCHAT = "Other Chat";
    private static readonly List<double> FontSizeList = new List<double>() { 10, 12, 14, 16, 18, 20, 22, 24 };
    private static readonly List<string> Times = new List<string>() { "Last Hour", "Last 8 Hours", "Last 24 Hours", "Last 7 Days", "Last 14 Days", "Last 30 Days", "Selected Fights", "Everything" };
    private static bool Complete = true;
    private static bool Running = false;
    private readonly DispatcherTimer FilterTimer;
    private List<string> UnFiltered = new List<string>();
    private readonly Dictionary<long, long> FilteredLinePositionMap = new Dictionary<long, long>();
    private readonly Dictionary<long, long> LinePositions = new Dictionary<long, long>();
    private readonly int LineTypeCount = 0;
    private readonly bool Ready = false;

    public EQLogViewer()
    {
      InitializeComponent();
      FilterTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1500) };
      (Application.Current.MainWindow as MainWindow).EventsThemeChanged += EventsThemeChanged;

      logSearchTime.ItemsSource = Times;

      var allFonts = UIElementUtil.GetSystemFontFamilies();
      fontFamily.ItemsSource = allFonts;
      var family = ConfigUtil.GetSetting("EQLogViewerFontFamily") ?? logBox.FontFamily?.Source;
      if (allFonts.FirstOrDefault(item => item.Source == family) is FontFamily found)
      {
        fontFamily.SelectedItem = found;
      }

      fontSize.ItemsSource = FontSizeList;
      var size = ConfigUtil.GetSetting("EQLogViewerFontSize");
      if (size != null && double.TryParse(size, out var dsize))
      {
        fontSize.SelectedItem = dsize;
      }
      else
      {
        fontSize.SelectedValue = logBox.FontSize;
      }

      UpdateCurrentTextColor();

      logSearch.Text = Resource.LOG_SEARCH_TEXT;
      logSearch2.Text = Resource.LOG_SEARCH_TEXT;
      logFilter.Text = Resource.LOG_FILTER_TEXT;

      var list = new List<ComboBoxItemDetails>();
      list.Add(new ComboBoxItemDetails { IsChecked = true, Text = DAMAGEAVOIDED, Value = DAMAGEAVOIDED });
      list.Add(new ComboBoxItemDetails { IsChecked = true, Text = Labels.DS, Value = Labels.DS });
      list.Add(new ComboBoxItemDetails { IsChecked = true, Text = Labels.DD, Value = Labels.DD });
      list.Add(new ComboBoxItemDetails { IsChecked = true, Text = Labels.DOT, Value = Labels.DOT });
      list.Add(new ComboBoxItemDetails { IsChecked = true, Text = "Fellowship", Value = ChatChannels.Fellowship });
      list.Add(new ComboBoxItemDetails { IsChecked = true, Text = "Group", Value = ChatChannels.Group });
      list.Add(new ComboBoxItemDetails { IsChecked = true, Text = "Guild", Value = ChatChannels.Guild });
      list.Add(new ComboBoxItemDetails { IsChecked = true, Text = Labels.MELEE, Value = Labels.MELEE });
      list.Add(new ComboBoxItemDetails { IsChecked = true, Text = OTHERCHAT, Value = OTHERCHAT });
      list.Add(new ComboBoxItemDetails { IsChecked = true, Text = Labels.OTHERDMG, Value = Labels.OTHERDMG });
      list.Add(new ComboBoxItemDetails { IsChecked = true, Text = Labels.PROC, Value = Labels.PROC });
      list.Add(new ComboBoxItemDetails { IsChecked = true, Text = "Raid", Value = ChatChannels.Raid });
      list.Add(new ComboBoxItemDetails { IsChecked = true, Text = "Say", Value = ChatChannels.Say });
      list.Add(new ComboBoxItemDetails { IsChecked = true, Text = NOCAT, Value = NOCAT });
      LineTypeCount = list.Count;

      lineTypes.ItemsSource = list;
      UIElementUtil.SetComboBoxTitle(lineTypes, list.Count, Resource.LINE_TYPES_SELECTED);

      FilterTimer.Tick += (sender, e) =>
      {
        FilterTimer.Stop();
        UpdateUI();
      };

      Ready = true;
    }

    private void EventsThemeChanged(string _) => UpdateCurrentTextColor();
    private void SelectLineTypes(object sender, EventArgs e) => UpdateUI();

    private void UpdateCurrentTextColor()
    {
      var defaultColor = (Color)Application.Current.Resources["ContentForeground.Color"];

      try
      {
        var colorSetting = "EQLogViewerFontFgColor" + MainWindow.CurrentTheme;
        var fgColor = ConfigUtil.GetSetting(colorSetting, defaultColor.ToString());
        colorPicker.Color = (Color)ColorConverter.ConvertFromString(fgColor);
      }
      catch (FormatException)
      {
        colorPicker.Color = defaultColor;
      }
    }

    private void UpdateUI()
    {
      if (Ready && logBox?.Lines != null)
      {
        logFilter.IsEnabled = false;
        lineTypes.IsEnabled = false;
        FilteredLinePositionMap.Clear();

        var types = (lineTypes.ItemsSource as List<ComboBoxItemDetails>).Where(item => item.IsChecked).ToDictionary(item => item.Value, item => true);
        UIElementUtil.SetComboBoxTitle(lineTypes, types.Count, Resource.LINE_TYPES_SELECTED);

        if (logFilter.FontStyle == FontStyles.Italic && types.Count == LineTypeCount)
        {
          logBox.Text = string.Join(Environment.NewLine, UnFiltered);
          UpdateStatusCount(UnFiltered.Count);
          if (logBox.Lines.Count > 0)
          {
            GoToLine(logBox, logBox.Lines.Count);
          }
        }
        else if ((logFilter.FontStyle != FontStyles.Italic && logFilter.Text.Length > 1) || types.Count < LineTypeCount)
        {
          var filtered = new List<string>();
          var lineCount = -1;
          foreach (ref var line in UnFiltered.ToArray().AsSpan())
          {
            lineCount++;

            if (types.Count < LineTypeCount)
            {
              ChatType chatType = null;
              var action = line.Substring(MainWindow.ACTION_INDEX);
              var damageRecord = DamageLineParser.ParseLine(action);

              if (damageRecord != null)
              {
                var ignore = false;
                switch (damageRecord.Type)
                {
                  case Labels.DS:
                  case Labels.DD:
                  case Labels.DOT:
                  case Labels.OTHERDMG:
                  case Labels.MELEE:
                  case Labels.PROC:
                    ignore = !types.ContainsKey(damageRecord.Type);
                    break;
                  case Labels.ABSORB:
                  case Labels.BLOCK:
                  case Labels.DODGE:
                  case Labels.MISS:
                  case Labels.PARRY:
                  case Labels.INVULNERABLE:
                    ignore = !types.ContainsKey(DAMAGEAVOIDED);
                    break;
                }

                if (ignore)
                {
                  continue;
                }
              }
              else
              {
                chatType = ChatLineParser.ParseChatType(action);
                if (chatType != null)
                {
                  var ignore = false;
                  switch (chatType.Channel)
                  {
                    case ChatChannels.Fellowship:
                    case ChatChannels.Group:
                    case ChatChannels.Guild:
                    case ChatChannels.Raid:
                    case ChatChannels.Say:
                      ignore = !types.ContainsKey(chatType.Channel);
                      break;
                    default:
                      ignore = !types.ContainsKey(OTHERCHAT);
                      break;
                  }

                  if (ignore)
                  {
                    continue;
                  }
                }
              }

              if (damageRecord == null && chatType == null && !types.ContainsKey(NOCAT))
              {
                continue;
              }
            }

            if (logFilter.FontStyle == FontStyles.Italic ||
              (logFilterModifier.SelectedIndex == 0 && line.IndexOf(logFilter.Text, StringComparison.OrdinalIgnoreCase) > -1) ||
               (logFilterModifier.SelectedIndex == 1 && line.IndexOf(logFilter.Text, StringComparison.OrdinalIgnoreCase) < 0))
            {
              FilteredLinePositionMap[filtered.Count] = LinePositions[lineCount];
              filtered.Add(line);
            }
          }

          logBox.Text = string.Join(Environment.NewLine, filtered);
          UpdateStatusCount(filtered.Count);
          if (logBox.Lines != null && logBox.Lines.Count > 0)
          {
            GoToLine(logBox, logBox.Lines.Count);
          }
        }

        logFilter.IsEnabled = true;
        lineTypes.IsEnabled = true;
      }
    }

    private void LoadContext(long pos, string text)
    {
      Task.Delay(50).ContinueWith(task =>
      {
        if (MainWindow.CurrentLogFile != null)
        {
          using var f = File.OpenRead(MainWindow.CurrentLogFile);
          f.Seek(Math.Max(0, pos - CONTEXT), SeekOrigin.Begin);
          var s = Helpers.GetStreamReader(f);
          var list = new List<string>();

          if (!s.EndOfStream)
          {
            // since position is not the start of a line just read one as junk
            s.ReadLine();
          }

          var FoundLines = new List<int>();
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
            var highlight = Application.Current.Resources["EQSearchBackgroundBrush"] as SolidColorBrush;
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
        selectedContext.IsEnabled = false;
        progress.Visibility = Visibility.Visible;
        progress.Content = "Searching";
        searchIcon.Icon = EFontAwesomeIcon.Solid_TimesCircle;
        logBox.ClearAllText();
        var logSearchText = logSearch.Text;
        var logSearchText2 = logSearch2.Text;
        var modifierIndex = logSearchModifier.SelectedIndex;
        var logTimeIndex = logSearchTime.SelectedIndex;
        var regexEnabled = useRegex.IsChecked.Value;
        LinePositions.Clear();
        var fights = (Application.Current.MainWindow as MainWindow).GetFightTable()?.GetSelectedFights().OrderBy(sel => sel.Id).ToList();

        Task.Delay(75).ContinueWith(task =>
        {
          if (MainWindow.CurrentLogFile != null)
          {
            using var f = File.OpenRead(MainWindow.CurrentLogFile);
            double start = -1;
            TimeRange ranges = null;
            switch (logTimeIndex)
            {
              case 0:
                start = DateUtil.ToDouble(DateTime.Now) - (60 * 60);
                break;
              case 1:
                start = DateUtil.ToDouble(DateTime.Now) - (60 * 60 * 8);
                break;
              case 2:
                start = DateUtil.ToDouble(DateTime.Now) - (60 * 60 * 24);
                break;
              case 3:
                start = DateUtil.ToDouble(DateTime.Now) - (60 * 60 * 24 * 7);
                break;
              case 4:
                start = DateUtil.ToDouble(DateTime.Now) - (60 * 60 * 24 * 14);
                break;
              case 5:
                start = DateUtil.ToDouble(DateTime.Now) - (60 * 60 * 24 * 30);
                break;
              case 6:
                if (fights.Count > 0)
                {
                  start = fights[0].BeginTime - 15;
                  ranges = new TimeRange();
                  fights.ForEach(fight => ranges.Add(new TimeSegment(fight.BeginTime - 15, fight.LastTime)));
                }
                break;
              case 7:
                start = 0;
                break;
            }

            var s = Helpers.GetStreamReader(f, start);

            if (!s.EndOfStream)
            {
              // since position is not the start of a line just read one as junk
              s.ReadLine();
            }

            var list = new List<string>();
            var lastPercent = -1;

            Regex searchRegex = null;
            if (logSearchText != Resource.LOG_SEARCH_TEXT && logSearchText.Length > 1)
            {
              searchRegex = new Regex(logSearchText, RegexOptions.IgnoreCase);
            }

            Regex searchRegex2 = null;
            if (logSearchText2 != Resource.LOG_SEARCH_TEXT && logSearchText2.Length > 1)
            {
              searchRegex2 = new Regex(logSearchText2, RegexOptions.IgnoreCase);
            }

            while (!s.EndOfStream && Running)
            {
              var line = s.ReadLine();
              if (TimeRange.TimeCheck(line, start, ranges, out var exceeds))
              {
                var match = true;
                var firstIndex = -2;
                var secondIndex = -2;

                if (searchRegex != null)
                {
                  firstIndex = DoSearch(line, logSearchText, searchRegex, regexEnabled);
                }

                if (searchRegex2 != null)
                {
                  secondIndex = DoSearch(line, logSearchText2, searchRegex2, regexEnabled);
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

              if (exceeds)
              {
                break;
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
                selectedContext.IsEnabled = true;
              }

              // reset filter
              tabControl.SelectedItem = resultsTab;
              logFilter.Text = Resource.LOG_FILTER_TEXT;
              logFilter.FontStyle = FontStyles.Italic;
              UpdateStatusCount(UnFiltered.Count);
              if (logBox.Lines != null && logBox.Lines.Count > 0)
              {
                GoToLine(logBox, logBox.Lines.Count);
              }
            });

            f.Close();
          }

          Dispatcher.InvokeAsync(() =>
          {
            searchIcon.IsEnabled = true;
            searchIcon.Icon = EFontAwesomeIcon.Solid_Search;
            progress.Visibility = Visibility.Hidden;
            Running = false;
            Complete = true;
            UpdateUI();
          });
        }, TaskScheduler.Default);
      }
      else
      {
        searchIcon.IsEnabled = false;
        Running = false;
      }
    }

    private int DoSearch(string line, string text, Regex searchRegex, bool regexEnabled)
    {
      if (regexEnabled)
      {
        return searchRegex.IsMatch(line) ? 1 : -1;
      }
      else
      {
        return line.IndexOf(text, StringComparison.OrdinalIgnoreCase);
      }
    }

    private void UpdateStatusCount(int count)
    {
      if (statusCount != null)
      {
        statusCount.Text = count + " Lines";
        if (count == MAX_ROWS)
        {
          statusCount.Text += " (Maximum Reached)";
        }
      }
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
        if (e.Delta < 0 && fontSize?.SelectedIndex > 0)
        {
          fontSize.SelectedIndex--;
          e.Handled = true;
        }
        else if (e.Delta > 0 && fontSize?.SelectedIndex < (fontSize?.Items.Count - 1))
        {
          fontSize.SelectedIndex++;
          e.Handled = true;
        }
      }
    }

    private void SearchTextChange(object sender, RoutedEventArgs e)
    {
      if (searchIcon != null)
      {
        searchIcon.IsEnabled = (logSearch.Text != Resource.LOG_SEARCH_TEXT && (logSearch.Text.Length > 1)) ||
          (logSearch2.Text != Resource.LOG_SEARCH_TEXT && logSearch2.Text.Length > 1);
      }
    }

    private void SearchKeyDown(object sender, KeyEventArgs e)
    {
      if (sender is TextBox textBox)
      {
        if (e.Key == Key.Escape)
        {
          if (logSearchModifier?.Focus() == true)
          {
            textBox.Text = Resource.LOG_SEARCH_TEXT;
            textBox.FontStyle = FontStyles.Italic;
          }
        }
        else if (e.Key == Key.Enter)
        {
          tabControl?.Focus();
          SearchClick(sender, null);
        }
      }
    }

    private void SearchGotFocus(object sender, RoutedEventArgs e)
    {
      if (sender is TextBox textBox && textBox.FontStyle == FontStyles.Italic)
      {
        textBox.Text = "";
        textBox.FontStyle = FontStyles.Normal;
      }
    }

    private void SearchLostFocus(object sender, RoutedEventArgs e)
    {
      if (sender is TextBox textBox && string.IsNullOrEmpty(textBox.Text))
      {
        textBox.Text = Resource.LOG_SEARCH_TEXT;
        textBox.FontStyle = FontStyles.Italic;
      }
    }
    private void FontFgColor_Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      if (logBox != null && contextBox != null)
      {
        logBox.Foreground = new SolidColorBrush(colorPicker.Color);
        contextBox.Foreground = new SolidColorBrush(colorPicker.Color);
        var colorSetting = "EQLogViewerFontFgColor" + MainWindow.CurrentTheme;
        ConfigUtil.SetSetting(colorSetting, colorPicker.Color.ToString());
      }
    }

    private void FontSize_Changed(object sender, SelectionChangedEventArgs e)
    {
      if (fontSize?.SelectedItem != null && logBox != null)
      {
        Application.Current.Resources["EQLogFontSize"] = (double)fontSize.SelectedItem;
        ConfigUtil.SetSetting("EQLogViewerFontSize", fontSize.SelectedItem.ToString());
      }
    }

    private void FontFamily_Changed(object sender, SelectionChangedEventArgs e)
    {
      if (fontFamily?.SelectedItem != null)
      {
        var family = fontFamily.SelectedItem as FontFamily;
        Application.Current.Resources["EQLogFontFamily"] = family;
        ConfigUtil.SetSetting("EQLogViewerFontFamily", family.ToString());
      }
    }

    private void FilterKeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.Escape && logFilterModifier?.Focus() == true)
      {
        logFilter.Text = Resource.LOG_FILTER_TEXT;
        logFilter.FontStyle = FontStyles.Italic;
      }
    }

    private void FilterGotFocus(object sender, RoutedEventArgs e)
    {
      if (logFilter != null && logFilter.FontStyle == FontStyles.Italic)
      {
        logFilter.Text = "";
        logFilter.FontStyle = FontStyles.Normal;
      }
    }

    private void FilterLostFocus(object sender, RoutedEventArgs e)
    {
      if (logFilter != null && string.IsNullOrEmpty(logFilter.Text))
      {
        logFilter.Text = Resource.LOG_FILTER_TEXT;
        logFilter.FontStyle = FontStyles.Italic;
      }
    }

    private void FilterTextChanged(object sender, TextChangedEventArgs e)
    {
      if (Ready)
      {
        FilterTimer?.Stop();
        FilterTimer?.Start();
      }
    }

    private void SelectedContext(object sender, RoutedEventArgs e)
    {
      if (logBox?.LineNumber > 0)
      {
        long start;
        var line = logBox.LineNumber - 1;
        if (FilteredLinePositionMap.TryGetValue(line, out var value))
        {
          start = value;
        }
        else
        {
          start = LinePositions[line];
        }

        LoadContext(start, logBox.Lines[line].Text);
      }
    }

    private void SearchIconIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
      if (searchIcon != null)
      {
        var brush = searchIcon.IsEnabled ? "EQMenuIconBrush" : "ContentBackgroundAlt5";
        searchIcon.Foreground = Application.Current.Resources[brush] as SolidColorBrush;
      }
    }

    private void WindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
      if (e.OriginalSource is ScrollViewer)
      {
        e.Handled = true;
      }
    }

    private void PreviewSelectedItemChangedEvent(object sender, Syncfusion.Windows.Tools.Controls.PreviewSelectedItemChangedEventArgs e)
    {
      if (e.NewSelectedItem == resultsTab)
      {
        if (logBox?.Lines != null)
        {
          UpdateStatusCount(logBox.Lines.Count - 1);
        }
      }
      else if (e.NewSelectedItem == contextTab)
      {
        if (contextBox?.Lines != null)
        {
          UpdateStatusCount(contextBox.Lines.Count - 1);
        }
      }
    }

    private void TabClosed(object sender, Syncfusion.Windows.Tools.Controls.CloseTabEventArgs e)
    {
      // can only close the context and display the results
      if (logBox?.Lines != null)
      {
        UpdateStatusCount(logBox.Lines.Count - 1);
      }
    }

    private void OptionsChange(object sender, EventArgs e) => UpdateUI();

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        (Application.Current.MainWindow as MainWindow).EventsThemeChanged -= EventsThemeChanged;
        logBox.Dispose();
        contextBox.Dispose();
        tabControl.Dispose();
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
