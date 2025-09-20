using FontAwesome5;
using log4net;
using Syncfusion.Windows.Edit;
using Syncfusion.Windows.Tools.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EQLogParser
{
  public partial class EqLogViewer : IDisposable
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private const int Context = 30000;
    private const int MaxRows = 500000;
    private const string Damageavoided = "Damage Avoided";
    private const string Nocat = "Uncategorized";
    private const string Otherchat = "Other Chat";
    private static readonly List<double> FontSizeList = [10, 12, 14, 16, 18, 20, 22, 24];
    private static readonly List<string> Places = [
      "Monitored Log", "Selected Fights"
    ];
    private static readonly List<string> Times =
    [
      "Last Hour", "Last 8 Hours", "Last 24 Hours", "Last 2 Days", "Last 7 Days", "Last 14 Days", "Last 30 Days", "Last 90 Days", "Any Time"
    ];
    private static bool _complete = true;
    private static bool _running;
    private readonly DispatcherTimer _filterTimer;
    private readonly List<string> _unFiltered = [];
    private readonly List<FileSearcher<string>.LinePosition> _linePositions = [];
    private readonly List<FileSearcher<string>.LinePosition> _filteredLinePositions = [];
    private readonly int _lineTypeCount;
    private readonly bool _ready;
    private CancellationTokenSource _cts;
    private TriggerConfig _config;

    public EqLogViewer()
    {
      InitializeComponent();
      _filterTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 1500) };

      logSearchTime.ItemsSource = Times;

      TriggerStateManager.Instance.GetConfig().ContinueWith(task => LoadPlaces(task.Result));

      var allFonts = UiElementUtil.GetSystemFontFamilies();
      fontFamily.ItemsSource = allFonts;
      var family = ConfigUtil.GetSetting("EQLogViewerFontFamily") ?? logBox.FontFamily?.Source;
      if (allFonts.FirstOrDefault(item => item.Source == family) is { } found)
      {
        fontFamily.SelectedItem = found;
      }

      fontSize.ItemsSource = FontSizeList;
      var size = ConfigUtil.GetSettingAsDouble("EQLogViewerFontSize");
      if (size > 0)
      {
        fontSize.SelectedItem = size;
      }
      else
      {
        fontSize.SelectedValue = logBox.FontSize;
      }

      UpdateCurrentTextColor();
      logSearch.Text = Resource.LOG_SEARCH_TEXT;
      logSearch2.Text = Resource.LOG_SEARCH_TEXT;
      logFilter.Text = Resource.LOG_FILTER_TEXT;

      var list = new List<ComboBoxItemDetails>
      {
        new() { IsChecked = true, Text = Damageavoided, Value = Damageavoided },
        new() { IsChecked = true, Text = Labels.Ds, Value = Labels.Ds },
        new() { IsChecked = true, Text = Labels.Dd, Value = Labels.Dd },
        new() { IsChecked = true, Text = Labels.Dot, Value = Labels.Dot },
        new() { IsChecked = true, Text = "Fellowship", Value = ChatChannels.Fellowship },
        new() { IsChecked = true, Text = "Group", Value = ChatChannels.Group },
        new() { IsChecked = true, Text = "Guild", Value = ChatChannels.Guild },
        new() { IsChecked = true, Text = Labels.Melee, Value = Labels.Melee },
        new() { IsChecked = true, Text = Otherchat, Value = Otherchat },
        new() { IsChecked = true, Text = Labels.OtherDmg, Value = Labels.OtherDmg },
        new() { IsChecked = true, Text = Labels.Proc, Value = Labels.Proc },
        new() { IsChecked = true, Text = "Raid", Value = ChatChannels.Raid },
        new() { IsChecked = true, Text = "Say", Value = ChatChannels.Say },
        new() { IsChecked = true, Text = Nocat, Value = Nocat }
      };

      _lineTypeCount = list.Count;

      lineTypes.ItemsSource = list;
      UiElementUtil.SetComboBoxTitle(lineTypes, Resource.LINE_TYPES_SELECTED);

      _filterTimer.Tick += async (_, _) =>
      {
        _filterTimer.Stop();
        await UpdateUiAsync();
      };

      MainActions.EventsThemeChanged += EventsThemeChanged;
      TriggerStateManager.Instance.TriggerConfigUpdateEvent += TriggerConfigUpdateEvent;

      _ready = true;
    }

    private void EventsThemeChanged(string _) => UpdateCurrentTextColor();
    private void TriggerConfigUpdateEvent(TriggerConfig config) => LoadPlaces(config);
    private async void SelectLineTypes(object sender, EventArgs e) => await UpdateUiAsync();

    private void LoadPlaces(TriggerConfig config)
    {
      _config = config;

      Dispatcher.InvokeAsync(() =>
      {
        List<string> placeList;
        var lastIndex = logSearchPlace.SelectedIndex;
        if (config.IsAdvanced && config.Characters?.Count > 0)
        {
          placeList = [.. Places, .. config.Characters.Select(character => character.Name).OrderBy(name => name)];
        }
        else
        {
          placeList = Places;
        }

        logSearchPlace.ItemsSource = placeList;

        if (lastIndex >= placeList.Count)
        {
          logSearchPlace.SelectedIndex = 0;
        }
      });
    }

    private void UpdateCurrentTextColor()
    {
      var defaultColor = (Color)Application.Current.Resources["ContentForeground.Color"]!;

      try
      {
        var colorSetting = "EQLogViewerFontFgColor" + MainActions.CurrentTheme;
        var fgColor = ConfigUtil.GetSetting(colorSetting, defaultColor.ToString(null));
        colorPicker.Color = (Color)ColorConverter.ConvertFromString(fgColor)!;
      }
      catch (FormatException)
      {
        colorPicker.Color = defaultColor;
      }
    }

    private async Task UpdateUiAsync()
    {
      if (_ready && logBox?.Lines != null)
      {
        // only prevent changes if there is a lot of data
        if (logBox.Lines.Count > 100)
        {
          logFilter.IsEnabled = false;
          lineTypes.IsEnabled = false;
          logFilterModifier.IsEnabled = false;
        }

        _filteredLinePositions.Clear();

        var types = (lineTypes.ItemsSource as List<ComboBoxItemDetails>)!.Where(item => item.IsChecked).ToDictionary(item => item.Value, _ => true);
        UiElementUtil.SetComboBoxTitle(lineTypes, Resource.LINE_TYPES_SELECTED);

        if (logFilter.FontStyle == FontStyles.Italic && types.Count == _lineTypeCount)
        {
          logBox.Text = string.Join(Environment.NewLine, _unFiltered);
          UpdateStatusCount(_unFiltered.Count);
          if (logBox.Lines.Count > 0)
          {
            await GoToLine(logBox, logBox.Lines.Count);
          }
        }
        else if ((logFilter.FontStyle != FontStyles.Italic && logFilter.Text.Length > 1) || types.Count < _lineTypeCount)
        {
          var filtered = new List<string>();
          var lineCount = -1;
          foreach (var line in _unFiltered)
          {
            lineCount++;

            if (types.Count < _lineTypeCount)
            {
              ChatType chatType = null;
              var action = line[MainWindow.ActionIndex..];
              var damageRecord = DamageLineParser.ParseLine(action);

              if (damageRecord != null)
              {
                var ignore = false;
                switch (damageRecord.Type)
                {
                  case Labels.Ds:
                  case Labels.Dd:
                  case Labels.Dot:
                  case Labels.OtherDmg:
                  case Labels.Melee:
                  case Labels.Proc:
                    ignore = !types.ContainsKey(damageRecord.Type);
                    break;
                  case Labels.Absorb:
                  case Labels.Block:
                  case Labels.Dodge:
                  case Labels.Miss:
                  case Labels.Parry:
                  case Labels.Invulnerable:
                    ignore = !types.ContainsKey(Damageavoided);
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
                      ignore = !types.ContainsKey(Otherchat);
                      break;
                  }

                  if (ignore)
                  {
                    continue;
                  }
                }
              }

              if (damageRecord == null && chatType == null && !types.ContainsKey(Nocat))
              {
                continue;
              }
            }

            if (logFilter.FontStyle == FontStyles.Italic ||
              (logFilterModifier.SelectedIndex == 0 && line.IndexOf(logFilter.Text, StringComparison.OrdinalIgnoreCase) > -1) ||
               (logFilterModifier.SelectedIndex == 1 && line.IndexOf(logFilter.Text, StringComparison.OrdinalIgnoreCase) < 0))
            {
              filtered.Add(line);
              _filteredLinePositions.Add(_linePositions[lineCount]);
            }
          }

          logBox.Text = string.Join(Environment.NewLine, filtered);
          UpdateStatusCount(filtered.Count);
          if (logBox.Lines is { Count: > 0 })
          {
            await GoToLine(logBox, logBox.Lines.Count);
          }
        }

        logFilter.IsEnabled = true;
        lineTypes.IsEnabled = true;
        logFilterModifier.IsEnabled = true;
      }
    }

    private async Task LoadContextAsync(string theFile, long pos, string text)
    {
      await Task.Delay(100);

      await Task.Run(() =>
      {
        try
        {
          if (!string.IsNullOrEmpty(theFile) && File.Exists(theFile))
          {
            var list = new List<string>();
            var foundLines = new List<int>();
            using var f = File.OpenRead(theFile);
            var isGzip = Path.GetExtension(theFile).Equals(".gz", StringComparison.OrdinalIgnoreCase);

            if (!isGzip)
            {
              f.Seek(Math.Max(0, pos - Context), SeekOrigin.Begin);
              using var s = FileUtil.GetStreamReader(f);

              if (!s.EndOfStream)
              {
                // since position is not the start of a line just read one as junk
                s.ReadLine();
              }

              while (!s.EndOfStream)
              {
                if (s.ReadLine() is { } line)
                {
                  list.Add(line);
                  if (line.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                  {
                    foundLines.Add(list.Count);
                  }

                  if (f.Position >= (pos + Context))
                  {
                    break;
                  }
                }
              }
            }
            else
            {
              var firstFound = false;
              var queue = new Queue<string>();
              using var s = FileUtil.GetStreamReader(f);

              while (!s.EndOfStream)
              {
                if (s.ReadLine() is { } line)
                {
                  queue.Enqueue(line);
                  if (line.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                  {
                    firstFound = true;
                    foundLines.Add(queue.Count);
                  }

                  // grab += 1000 lines
                  // not really sure how many the non-gz is doing tbh
                  if (!firstFound && queue.Count > 1000)
                  {
                    queue.Dequeue();
                  }

                  if (firstFound && queue.Count == 2000)
                  {
                    break;
                  }
                }
              }

              list.AddRange([.. queue]);
            }

            var allText = string.Join(Environment.NewLine, list);
            Dispatcher.InvokeAsync(async () =>
            {
              var highlight = Application.Current.Resources["EQSearchBackgroundBrush"] as SolidColorBrush;
              contextBox.Text = allText;
              contextTab.Visibility = Visibility.Visible;
              foundLines.ForEach(line => contextBox.SetLineBackground(line, true, highlight));
              tabControl.SelectedItem = contextTab;
              UpdateStatusCount(contextBox.Lines.Count);

              if (foundLines.Count > 0)
              {
                await GoToLine(contextBox, foundLines[0] + 3);
              }
            }, DispatcherPriority.Background);
          }
        }
        catch (Exception ex)
        {
          Log.Error("Error Loading Context", ex);
        }
      });
    }

    private static async Task GoToLine(EditControl control, int line)
    {
      // GoToLine just doesn't work until UI is fully rendered
      await Task.Delay(300);

      try
      {
        control.GoToLine(Math.Min(line, control.Lines.Count));
      }
      catch (NullReferenceException)
      {
        // ignore
      }
    }

    private async void SearchClick(object sender, MouseButtonEventArgs e)
    {
      if (!_running && _complete)
      {
        _running = true;
        selectedContext.IsEnabled = false;
        progress.Visibility = Visibility.Visible;
        progress.Content = "Searching";
        searchIcon.Icon = EFontAwesomeIcon.Solid_TimesCircle;
        searchIcon.Foreground = Application.Current.Resources["EQStopForegroundBrush"] as SolidColorBrush;
        logBox.Text = "";
        contextTab.Visibility = Visibility.Collapsed;
        contextBox.Text = "";
        UpdateStatusCount(0);
        var logSearchText = logSearch.Text;
        var logSearchText2 = logSearch2.Text;
        var doSearchText = logSearch.FontStyle != FontStyles.Italic && logSearchText.Length > 1;
        var doSearchText2 = logSearch2.FontStyle != FontStyles.Italic && logSearchText2.Length > 1;
        var modifierIndex = logSearchModifier.SelectedIndex;
        var logPlaceIndex = logSearchPlace.SelectedIndex;
        var logTimeIndex = logSearchTime.SelectedIndex;
        var regexEnabled = doSearchText && IsUseRegex(logSearchText);
        var regex2Enabled = doSearchText2 && IsUseRegex(logSearchText2);
        _unFiltered.Clear();
        _linePositions.Clear();
        _filteredLinePositions.Clear();

        double start = -1;
        TimeRange range = null;

        // use selected fights
        if (logPlaceIndex == 1)
        {
          // get fights
          List<Fight> fights = [.. MainActions.GetFights(true).OrderBy(sel => sel.Id)];

          if (fights?.Count > 0)
          {
            start = fights[0].BeginTime - 15;
            range = new TimeRange();
            fights.ForEach(fight => range.Add(new TimeSegment(fight.BeginTime - 15, fight.LastTime)));
          }
        }
        else
        {
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
              start = DateUtil.ToDouble(DateTime.Now) - (60 * 60 * 24 * 2);
              break;
            case 4:
              start = DateUtil.ToDouble(DateTime.Now) - (60 * 60 * 24 * 7);
              break;
            case 5:
              start = DateUtil.ToDouble(DateTime.Now) - (60 * 60 * 24 * 14);
              break;
            case 6:
              start = DateUtil.ToDouble(DateTime.Now) - (60 * 60 * 24 * 30);
              break;
            case 7:
              start = DateUtil.ToDouble(DateTime.Now) - (60 * 60 * 24 * 90);
              break;
            case 8:
              start = 0;
              break;
          }
        }

        var fileList = new List<string>();
        if (logPlaceIndex < 2)
        {
          if (MainWindow.CurrentLogFile is { } current)
          {
            fileList.Add(current);

            if (includeArchive.IsChecked == true && FileUtil.FindArchivedLogFiles(ConfigUtil.PlayerName, ConfigUtil.ServerName, start) is { } archiveFiles)
            {
              fileList.AddRange(archiveFiles);
            }
          }
        }
        else
        {
          if (_config?.Characters?.Count > 0)
          {
            var character = _config.Characters.Where(character => character.Name == (logSearchPlace.SelectedItem as string)).FirstOrDefault();
            if (character?.FilePath != null)
            {
              fileList.Add(character.FilePath);

              if (includeArchive.IsChecked == true && FileUtil.ParseFileName(character.FilePath, out var player, out var server) &&
                FileUtil.FindArchivedLogFiles(player, server, start) is { } archiveFiles)
              {
                fileList.AddRange(archiveFiles);
              }
            }
          }
        }

        if (fileList.Count > 0)
        {
          Regex searchRegex = null;
          if (regexEnabled)
          {
            searchRegex = new Regex(logSearchText, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
          }

          Regex searchRegex2 = null;
          if (regex2Enabled)
          {
            searchRegex2 = new Regex(logSearchText2, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
          }

          var searcher = new FileSearcher<string>(fileList);
          searcher.ProgressUpdated += percent =>
          {
            Dispatcher.Invoke(() =>
            {
              progress.Content = $"Searching ({percent}% Complete)";
            }, DispatcherPriority.Background);
          };

          searcher.ResultsReady += (lines, positions) =>
          {
            _unFiltered.InsertRange(0, lines);
            _linePositions.InsertRange(0, positions);

            if (_unFiltered.Count >= MaxRows)
            {
              _unFiltered.Clear();
              _unFiltered.AddRange(_unFiltered.Take(MaxRows));
              _linePositions.Clear();
              _linePositions.AddRange(_linePositions.Take(MaxRows));
              _cts?.Cancel();
            }

            Dispatcher.InvokeAsync(async () =>
            {
              if (_unFiltered.Count > 0)
              {
                logBox.Text = string.Join(Environment.NewLine, _unFiltered);

                UpdateStatusCount(_unFiltered.Count);
                if (logBox.Lines is { Count: > 0 })
                {
                  await GoToLine(logBox, logBox.Lines.Count);
                }
              }
            }, DispatcherPriority.Background);
          };

          _cts = new CancellationTokenSource();

          try
          {
            await searcher.SearchLogsAsync(start, range, line =>
            {
              var match = true;
              var firstIndex = -2;
              var secondIndex = -2;

              if (doSearchText)
              {
                firstIndex = CheckLine(line, logSearchText, searchRegex);
              }

              if (doSearchText2)
              {
                secondIndex = CheckLine(line, logSearchText2, searchRegex2);
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
                return line;
              }

              return null;
            }, 2, _cts.Token);
          }
          catch (Exception)
          {
            // user cancel or error
          }
          finally
          {
            _cts?.Dispose();
            _cts = null;
          }

          // reset filter
          tabControl.SelectedItem = resultsTab;
          logFilter.Text = Resource.LOG_FILTER_TEXT;
          logFilter.FontStyle = FontStyles.Italic;
          selectedContext.IsEnabled = true;
        }
        else
        {
          // show progress briefly
          await Task.Delay(500);
        }

        searchIcon.IsEnabled = true;
        searchIcon.Icon = EFontAwesomeIcon.Solid_Search;
        searchIcon.Foreground = Application.Current.Resources["EQGoodForegroundBrush"] as SolidColorBrush;
        progress.Visibility = Visibility.Hidden;
        _running = false;
        _complete = true;
        await UpdateUiAsync();
      }
      else
      {
        searchIcon.IsEnabled = false;
        _running = false;
        _complete = true;
        _cts?.Cancel();
      }
    }

    private static bool IsUseRegex(string pattern)
    {
      // Heuristic: looks like it has regex features
      var looksLikeRegex = Regex.IsMatch(pattern, @"[\[\]\(\)\.\*\+\?\|\^\$\{\}\\]");

      if (looksLikeRegex)
      {
        try
        {
          _ = new Regex(pattern);
          return true;
        }
        catch
        {
          // invalid regex, fallback to Contains
          return false;
        }
      }

      return false;
    }

    private static int CheckLine(string line, string text, Regex searchRegex)
    {
      if (searchRegex != null)
      {
        return searchRegex.IsMatch(line) ? 1 : -1;
      }

      return line.IndexOf(text, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateStatusCount(int count)
    {
      if (statusCount != null)
      {
        statusCount.Text = count + " Lines";
        if (count == MaxRows)
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
        searchIcon.IsEnabled = (logSearch.FontStyle != FontStyles.Italic && (logSearch.Text.Length > 1)) ||
          (logSearch2.FontStyle != FontStyles.Italic && logSearch2.Text.Length > 1);
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
    private void FontFgColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      if (logBox != null && contextBox != null)
      {
        logBox.Foreground = UiUtil.GetBrush(colorPicker.Color);
        contextBox.Foreground = UiUtil.GetBrush(colorPicker.Color);
        var colorSetting = "EQLogViewerFontFgColor" + MainActions.CurrentTheme;
        ConfigUtil.SetSetting(colorSetting, colorPicker.Color.ToString(null));
      }
    }

    private void FontSizeChanged(object sender, SelectionChangedEventArgs e)
    {
      if (fontSize?.SelectedItem != null && logBox != null)
      {
        Application.Current.Resources["EQLogFontSize"] = (double)fontSize.SelectedItem;
        ConfigUtil.SetSetting("EQLogViewerFontSize", fontSize.SelectedItem.ToString());
      }
    }

    private void FontFamilyChanged(object sender, SelectionChangedEventArgs e)
    {
      if (fontFamily?.SelectedItem is FontFamily family)
      {
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
      if (logFilter?.FontStyle == FontStyles.Italic)
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
      if (_ready)
      {
        _filterTimer?.Stop();
        _filterTimer?.Start();
      }
    }

    private void LogPlaceChanged(object sender, SelectionChangedEventArgs e)
    {
      if (_ready)
      {
        logSearchTime.IsEnabled = logSearchPlace.SelectedIndex != 1;
        includeArchive.IsEnabled = logSearchPlace.SelectedIndex != 1;
      }
    }

    private async void SelectedContext(object sender, RoutedEventArgs e)
    {
      if (logBox?.LineNumber > 0)
      {
        var index = logBox.LineNumber - 1;
        var linePos = _filteredLinePositions.Count > index ? _filteredLinePositions[index] : _linePositions[index];
        var text = logBox.Lines[index].Text;
        await LoadContextAsync(linePos.File, linePos.Position, text);
      }
    }

    private void SearchIconIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
      if (searchIcon != null)
      {
        var brush = searchIcon.IsEnabled ? "EQGoodForegroundBrush" : "ContentBackgroundAlt5";
        searchIcon.Foreground = Application.Current.Resources[brush] as SolidColorBrush;
      }
    }

    // fix for edit control crashing if empty
    private void WindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
      if (e.OriginalSource is ScrollViewer && logBox?.Lines?.Count == 0)
      {
        e.Handled = true;
      }
    }

    private void PreviewSelectedItemChangedEvent(object sender, PreviewSelectedItemChangedEventArgs e)
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

    private void TabClosed(object sender, CloseTabEventArgs e)
    {
      // can only close the context and display the results
      if (logBox?.Lines != null)
      {
        UpdateStatusCount(logBox.Lines.Count - 1);
      }
    }

    private async void OptionsChange(object sender, EventArgs e) => await UpdateUiAsync();

    #region IDisposable Support
    private bool _disposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!_disposedValue)
      {
        _config = null;
        MainActions.EventsThemeChanged -= EventsThemeChanged;
        TriggerStateManager.Instance.TriggerConfigUpdateEvent -= TriggerConfigUpdateEvent;
        logBox.Dispose();
        contextBox.Dispose();
        tabControl.Dispose();
        _disposedValue = true;
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
