
using FontAwesome5;
using log4net;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace EQLogParser
{
  public partial class Timeline
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private const ushort CasterAdps = 1;
    private const ushort MeleeAdps = 2;
    private const ushort TankAdps = 4;
    private const ushort HealingAdps = 8;
    private const ushort AnyAdps = CasterAdps + MeleeAdps + TankAdps + HealingAdps;
    private const double StartTimeOffset = 90;
    private readonly string[] _types = ["Defensive Skills", "ADPS", "Healing Skills"];
    private readonly Dictionary<string, SpellRange> _spellRanges = [];
    private readonly Dictionary<string, byte> _selfOnly = [];
    private readonly Dictionary<string, byte> _spellCasts = [];
    private readonly List<UIElement> _draggedElements = [];
    private readonly List<string> _keyOrder = [];
    private Rectangle _selectedRectangle;
    private double _pixelsPerSecond = 1;
    private int _fightLength;
    private List<PlayerStats> _selectedStats;
    private int _timelineType;
    private bool _currentHideSelfOnly = true;
    private bool _currentShowCasterAdps = true;
    private bool _currentShowMeleeAdps = true;

    public Timeline()
    {
      InitializeComponent();
      MainActions.EventsThemeChanged += EventsThemeChanged;
    }

    // timelineType 0 = tanking, 1 = dps, 2 = healing
    internal void Init(CombinedStats currentStats, List<PlayerStats> selected, List<List<ActionGroup>> groups, int timelineType)
    {
      if (selected is { Count: > 0 } && timelineType is >= 0 and <= 2)
      {
        _timelineType = timelineType;
        _selectedStats = selected;
        var startTime = groups.Min(block => block.First().BeginTime);
        var endTime = groups.Max(block => block.Last().BeginTime) + 1;
        _fightLength = (int)(endTime - startTime);

        switch (_selectedStats.Count)
        {
          case 1:
            titleLabel1.Content = _selectedStats[0].OrigName + "'s " + _types[_timelineType] + " | " + currentStats.ShortTitle;
            break;
          case 2:
            titleLabel1.Content = _selectedStats[0].OrigName + " vs ";
            titleLabel2.Content = _selectedStats[1].OrigName + "'s ";
            titleLabel3.Content = _types[_timelineType] + " | " + currentStats.ShortTitle;
            break;
        }

        if (_timelineType is 0 or 2)
        {
          showMeleeAdps.Visibility = Visibility.Hidden;
          showCasterAdps.Visibility = Visibility.Hidden;
        }

        var deathMap = new Dictionary<string, HashSet<double>>();
        foreach (var (beginTime, record) in RecordManager.Instance.GetDeathsDuring(startTime, endTime))
        {
          if (_selectedStats.FindIndex(stats => stats.OrigName == record.Killed) > -1)
          {
            if (deathMap.TryGetValue(record.Killed, out var values))
            {
              values.Add(beginTime);
            }
            else
            {
              deathMap[record.Killed] = [beginTime];
            }
          }
        }

        for (var i = 0; i < _selectedStats.Count; i++)
        {
          var player = _selectedStats[i].OrigName;
          if (deathMap.TryGetValue(_selectedStats[i].OrigName, out var deathTimes))
          {
            var death = new SpellData { Adps = (byte)AnyAdps, Duration = 3, NameAbbrv = "Player Death", Name = "Player Death" };
            foreach (var time in deathTimes)
            {
              UpdateSpellRange(death, time, startTime, endTime, i == 0);
            }
          }

          var castSpells = new List<SpellData>();
          foreach (var (beginTime, action) in RecordManager.Instance.GetSpellsDuring(startTime - StartTimeOffset, endTime))
          {
            if (action is SpellCast { Interrupted: false } cast && cast.Caster == player && cast.SpellData is { Target: (int)SpellTarget.Self, Adps: > 0 }
              && (cast.SpellData.MaxHits > 0 || cast.SpellData.Duration <= 1800) && ClassFilter(cast.SpellData))
            {
              castSpells.Add(cast.SpellData);
              UpdateSpellRange(cast.SpellData, beginTime, startTime, endTime, i == 0, deathTimes);
              _spellCasts[cast.SpellData.NameAbbrv] = 1;
              _selfOnly.Remove(cast.SpellData.NameAbbrv);
            }
            else if (action is ReceivedSpell received && received.Receiver == player)
            {
              var spellData = received.SpellData;
              if (spellData == null && received.Ambiguity.Count > 0)
              {
                if (!received.IsWearOff)
                {
                  if (DataManager.ResolveSpellAmbiguity(received, out var replaced))
                  {
                    castSpells.Add(replaced);
                    spellData = replaced;
                  }
                }
                else
                {
                  foreach (var possible in received.Ambiguity)
                  {
                    if (castSpells.Find(spell => spell.WearOff == possible.WearOff || spell.LandsOnYou == possible.LandsOnYou
                          || spell.LandsOnOther == possible.LandsOnOther) is { } found)
                    {
                      spellData = found;
                    }
                  }
                }
              }

              if (spellData is { Adps: > 0 } && (spellData.MaxHits > 0 || spellData.Duration <= 1800) && ClassFilter(spellData))
              {
                if (string.IsNullOrEmpty(spellData.LandsOnOther) && !_spellCasts.ContainsKey(spellData.NameAbbrv))
                {
                  _selfOnly[spellData.NameAbbrv] = 1;
                }

                UpdateSpellRange(spellData, beginTime, startTime, endTime, i == 0, deathTimes, received.IsWearOff);
              }
            }
          }
        }
      }

      showCasterAdps.IsEnabled = showMeleeAdps.IsEnabled = _spellRanges.Count > 0;
      hideSelfOnly.IsEnabled = _spellRanges.Count > 0 && _selectedStats.Find(stats => stats.OrigName == ConfigUtil.PlayerName) != null;
      Display();
    }

    private void EventsThemeChanged(string _)
    {
      UpdateResources();
      Display();
    }

    private void Display()
    {
      headerCanvas.Children.Clear();
      labelStackPanel.Children.Clear();
      mainStackPanel.Children.Clear();
      DrawTimeLinesAndLabels();
      labelStackPanel.Children.Add(CreateHorizontalLine("EQTimelineLeftPaneWidth"));
      mainStackPanel.Children.Add(CreateHorizontalLine("EQTimelineContentWidth"));

      var maxLength = 150.0;
      SpellRange deathRanges = null;

      if (_keyOrder.Count == 0)
      {
        _keyOrder.AddRange(_spellRanges.Keys.OrderBy(key => key));
      }

      foreach (var key in _keyOrder)
      {
        var spellRange = _spellRanges[key];

        if (key == "Player Death")
        {
          deathRanges = spellRange;
        }

        if ((!_currentHideSelfOnly || !_selfOnly.ContainsKey(key))
          && ((_currentShowCasterAdps && ((spellRange.Adps & CasterAdps) == CasterAdps))
          || (_currentShowMeleeAdps && ((spellRange.Adps & MeleeAdps) == MeleeAdps))
          || (_timelineType == 0 && ((spellRange.Adps & TankAdps) == TankAdps))
          || (_timelineType == 2 && ((spellRange.Adps & HealingAdps) == HealingAdps))))
        {
          var calc = DataGridUtil.CalculateMinGridHeaderWidth(key);
          if (calc > maxLength)
          {
            maxLength = calc;
          }

          AddRowToContent(key, spellRange.TopRanges, spellRange.BottomRanges);
        }
      }

      // handle deaths last so can draw on all rows
      if (deathRanges != null)
      {
        foreach (var visual in mainStackPanel.Children)
        {
          if (visual is StackPanel { Children.Count: > 0 } rightPanel)
          {
            foreach (var child in rightPanel.Children)
            {
              if (child is Canvas { } canvas)
              {
                foreach (var range in deathRanges.TopRanges)
                {
                  var position = (StartTimeOffset * _pixelsPerSecond) + ((range.BeginSeconds + (range.Duration / 2.0)) * _pixelsPerSecond);
                  canvas.Children.Add(CreateVerticalLine(position, "EQStopForegroundBrush", 1.0));
                }
                foreach (var range in deathRanges.BottomRanges)
                {
                  var position = (StartTimeOffset * _pixelsPerSecond) + ((range.BeginSeconds + (range.Duration / 2.0)) * _pixelsPerSecond);
                  canvas.Children.Add(CreateVerticalLine(position, "EQStopForegroundBrush", 1.0));
                }
              }
            }
          }
        }
      }

      // figure out label length
      Application.Current.Resources["EQTimelineLabelWidth"] = maxLength;
      UpdateResources();
    }

    private void UpdateResources()
    {
      var margin = new Thickness(4, 2, 4, 2);
      Application.Current.Resources["EQTimelineSecondColor"] = Application.Current.Resources["ContentForeground"];
      Application.Current.Resources["EQTimelineLabelMargin"] = margin;
      // bar height 1/3 of row
      Application.Current.Resources["EQTimelineBarHeight"] =
        (double)Application.Current.Resources["EQTableRowHeight"]! / 3;
      // calculate label area width
      var leftPaneWidth = new GridLength(
        // label
        (double)Application.Current.Resources["EQTimelineLabelWidth"]! +
        // icon
        (double)Application.Current.Resources["EQContentSize"]! +
        // margins for each
        ((margin.Left + margin.Right) * 2) +
        // extra divider
        3);
      Application.Current.Resources["EQTimelineLeftPaneGridWidth"] = leftPaneWidth;
      Application.Current.Resources["EQTimelineLeftPaneWidth"] = leftPaneWidth.Value;
      // entire content area plus a bit at the end
      var contentWidth = (StartTimeOffset * _pixelsPerSecond * 1.5) + (_fightLength * _pixelsPerSecond);
      Application.Current.Resources["EQTimelineContentWidth"] = contentWidth;
    }

    private void OptionsChange(object sender, RoutedEventArgs e)
    {
      // ignore during init
      if (hideSelfOnly == null || showCasterAdps == null || showMeleeAdps == null)
      {
        return;
      }

      // check for changes
      if (_currentHideSelfOnly != hideSelfOnly.IsChecked ||
          _currentShowCasterAdps != showCasterAdps.IsChecked ||
          _currentShowMeleeAdps != showMeleeAdps.IsChecked)
      {
        _currentHideSelfOnly = hideSelfOnly?.IsChecked == true;
        _currentShowCasterAdps = showCasterAdps?.IsChecked == true;
        _currentShowMeleeAdps = showMeleeAdps?.IsChecked == true;
        Display();
      }
    }

    private void RefreshClick(object sender, RoutedEventArgs e)
    {
      _keyOrder.Clear();
      Display();
    }

    private void UpdateSpellRange(SpellData spellData, double time, double startTime, double endTime,
      bool isTop, HashSet<double> deathTimes = null, bool isWearOff = false)
    {
      if (!_spellRanges.TryGetValue(spellData.NameAbbrv, out var spellRange))
      {
        if (!isWearOff)
        {
          spellRange = new SpellRange { Adps = spellData.Adps };
          var theRange = isTop ? spellRange.TopRanges : spellRange.BottomRanges;
          var duration = GetDuration(spellData, endTime, time, deathTimes);
          var range = new TimeRange((int)(time - startTime), duration);
          theRange.Add(range);
          _spellRanges[spellData.NameAbbrv] = spellRange;
        }
      }
      else
      {
        var theRange = isTop ? spellRange.TopRanges : spellRange.BottomRanges;
        var last = theRange.LastOrDefault();
        var offsetSeconds = (int)(time - startTime);
        if (last != null && offsetSeconds >= last.BeginSeconds && offsetSeconds <= (last.BeginSeconds + last.Duration))
        {
          if (isWearOff)
          {
            var newOffset = offsetSeconds - last.BeginSeconds;
            last.Duration = newOffset - last.Duration <= 6 ? newOffset : last.Duration;
          }
          else
          {
            last.Duration = GetDuration(spellData, endTime, time, deathTimes) + (offsetSeconds - last.BeginSeconds);
          }
        }
        else if (!isWearOff)
        {
          var duration = GetDuration(spellData, endTime, time, deathTimes);
          var range = new TimeRange((int)(time - startTime), duration);
          theRange.Add(range);
        }
      }
    }

    private int GetDuration(SpellData spell, double endTime, double currentTime, HashSet<double> deathTimes = null)
    {
      var duration = spell.Duration > 0 ? spell.Duration : 6;

      // tanking hits happen a lot faster than spell casting so have our guesses be 1/3 as long
      var mod = _timelineType == 0 ? 3 : 1;

      var maxHitDuration = 0;
      if (spell.MaxHits > 0)
      {
        if (spell.MaxHits == 1)
        {
          maxHitDuration = duration > 6 ? 6 / mod : duration;
        }
        else if (spell.MaxHits <= 3)
        {
          maxHitDuration = duration > 12 ? 12 / mod : duration;
        }
        else if (spell.MaxHits == 4)
        {
          maxHitDuration = duration > 18 ? 18 / mod : duration;
        }
        else
        {
          var guess = spell.MaxHits / 5 * 18 / mod;
          maxHitDuration = duration > guess ? guess : duration;
        }
      }

      duration = spell.MaxHits > 0 ? Math.Min(maxHitDuration, duration) : duration;

      if (deathTimes != null && !spell.Name.StartsWith("Glyph of", StringComparison.OrdinalIgnoreCase))
      {
        foreach (var time in deathTimes)
        {
          if (time >= currentTime && time <= endTime)
          {
            endTime = time;
          }
        }
      }

      if (currentTime + duration > endTime)
      {
        duration = (int)(duration - (currentTime + duration - endTime));
      }

      return duration;
    }

    private bool ClassFilter(SpellData data)
    {
      return (_timelineType == 0 && (data.Adps & TankAdps) != 0) || (_timelineType == 1 && ((data.Adps & CasterAdps) != 0 || (data.Adps & MeleeAdps) != 0)) ||
             (_timelineType == 2 && (data.Adps & HealingAdps) != 0);
    }

    private void DrawTimeLinesAndLabels()
    {
      var position = 0d;
      var fontSize = (double)Application.Current.Resources["EQContentSize"]!;
      var iconWidth = (double)Application.Current.Resources["EQContentSize"]!;
      var minus90Label = CreateTextBlock("Buffs (T-90)");
      minus90Label.Margin = Margin = new Thickness(0, 2, 0, 2);
      Canvas.SetLeft(minus90Label, position - fontSize - iconWidth);
      Canvas.SetBottom(minus90Label, 2);
      headerCanvas.Children.Add(minus90Label);

      // Now handle the labels from 0m onwards
      for (double time = 0; time <= _fightLength; time += 60)
      {
        position = (StartTimeOffset * _pixelsPerSecond) + (time * _pixelsPerSecond);
        var labelText = time == 0 ? "0m" : $"{time / 60}m";
        var timeLabel = CreateTextBlock(labelText);
        timeLabel.Margin = Margin = new Thickness(0, 2, 0, 2);
        Canvas.SetLeft(timeLabel, position - 6);
        Canvas.SetBottom(timeLabel, 2);
        headerCanvas.Children.Add(timeLabel);
      }
    }

    private void AddRowToContent(string labelText, List<TimeRange> topRanges, List<TimeRange> bottomRanges)
    {
      var leftPanel = new StackPanel
      {
        Orientation = Orientation.Horizontal,
        VerticalAlignment = VerticalAlignment.Center,
        Tag = labelText
      };

      leftPanel.SetResourceReference(BackgroundProperty, "ContentBackground");
      leftPanel.SetResourceReference(HeightProperty, "EQTableRowHeight");

      // icon
      var image = new ImageAwesome
      {
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Icon = EFontAwesomeIcon.Solid_Times,
      };

      image.SetResourceReference(HeightProperty, "EQContentSize");
      image.SetResourceReference(WidthProperty, "EQContentSize");
      image.SetResourceReference(ImageAwesome.ForegroundProperty, "EQMenuIconBrush");
      image.SetResourceReference(MarginProperty, "EQTimelineLabelMargin");
      image.PreviewMouseLeftButtonDown += (_, _) =>
      {
        _keyOrder.Remove(labelText);
        Display();
      };

      leftPanel.Children.Add(image);
      leftPanel.Children.Add(CreateVerticalLine(2));

      // Label
      var label = CreateTextBlock(labelText);
      label.Cursor = Cursors.Hand;
      label.SetResourceReference(MarginProperty, "EQTimelineLabelMargin");
      label.SetResourceReference(WidthProperty, "EQTimelineLabelWidth");

      // highlight death row
      if (labelText == "Player Death")
      {
        label.SetResourceReference(ForegroundProperty, "EQStopForegroundBrush");
      }

      leftPanel.Children.Add(label);
      labelStackPanel.Children.Add(leftPanel);
      labelStackPanel.Children.Add(CreateHorizontalLine("EQTimelineLeftPaneWidth"));

      label.PreviewMouseLeftButtonDown += (_, e) =>
      {
        for (var i = 0; i < labelStackPanel.Children.Count; i++)
        {
          if (ReferenceEquals(labelStackPanel.Children[i], leftPanel) && labelStackPanel.Children[i] is StackPanel stack)
          {
            _draggedElements.Clear();
            _draggedElements.Add(stack);
            _draggedElements.Add(labelStackPanel.Children[i + 1]);
            _draggedElements.Add(mainStackPanel.Children[i]);
            _draggedElements.Add(mainStackPanel.Children[i + 1]);
            Panel.SetZIndex(_draggedElements[0], 999);
            stack.Opacity = 0.8;
            stack.SetResourceReference(Panel.BackgroundProperty, "EQWarnBackgroundBrush");
            labelStackPanel.Children.RemoveAt(i);
            labelStackPanel.Children.RemoveAt(i);
            mainStackPanel.Children.RemoveAt(i);
            mainStackPanel.Children.RemoveAt(i);

            dragCanvas.IsHitTestVisible = true;
            var pos = e.GetPosition(contentGrid);
            dragCanvas.Children.Add(_draggedElements[0]);
            Canvas.SetLeft(_draggedElements[0], pos.X);
            Canvas.SetTop(_draggedElements[0], pos.Y + labelsScroller.VerticalOffset);
            break;
          }
        }
      };

      var rightPanel = new StackPanel
      {
        Orientation = Orientation.Horizontal,
        VerticalAlignment = VerticalAlignment.Center
      };

      rightPanel.SetResourceReference(BackgroundProperty, "ContentBackground");
      rightPanel.SetResourceReference(HeightProperty, "EQTableRowHeight");

      // Canvas for tasks
      var taskCanvas = new Canvas
      {
        Background = Brushes.Transparent,
        Width = double.NaN, // Auto size
        VerticalAlignment = VerticalAlignment.Center
      };

      taskCanvas.SetResourceReference(HeightProperty, "EQTableRowHeight");

      taskCanvas.Loaded += (_, _) =>
      {
        DrawTaskRectangles(taskCanvas, labelText, topRanges, bottomRanges);
        DrawVerticalLinesInContent(taskCanvas);
      };

      rightPanel.Children.Add(taskCanvas);
      mainStackPanel.Children.Add(rightPanel);
      mainStackPanel.Children.Add(CreateHorizontalLine("EQTimelineContentWidth"));
    }

    private void DrawVerticalLinesInContent(Canvas canvas)
    {
      // buff time
      canvas.Children.Add(CreateVerticalLine(0));

      // Draw vertical lines in content canvas similar to the header
      for (var time = 0; time <= _fightLength; time += 60)
      {
        var position = (StartTimeOffset * _pixelsPerSecond) + (time * _pixelsPerSecond);
        canvas.Children.Add(CreateVerticalLine(position));
      }

      // end
      var end = (StartTimeOffset * _pixelsPerSecond) + (_fightLength * _pixelsPerSecond);
      canvas.Children.Add(CreateVerticalLine(end));
    }

    private void DrawTaskRectangles(Canvas canvas, string text, List<TimeRange> topRanges, List<TimeRange> bottomRanges)
    {
      var rowHeight = (double)Application.Current.Resources["EQTableRowHeight"]!;
      var barHeight = (double)Application.Current.Resources["EQTimelineBarHeight"]!;
      var middle = (rowHeight - barHeight) / 2;

      foreach (var range in topRanges)
      {
        Draw(range, text, 11, "EQMenuIconBrush", bottomRanges.Count == 0 ? middle : rowHeight / 5);
      }

      if (bottomRanges.Count > 0)
      {
        foreach (var range in bottomRanges)
        {
          Draw(range, text, 12, "EQTimelineSecondColor", rowHeight / 2);
        }
      }

      return;

      void Draw(TimeRange range, string adps, int zIndex, string fillKey, double pos)
      {
        var startXPosition = (StartTimeOffset * _pixelsPerSecond) + (range.BeginSeconds * _pixelsPerSecond); // 90 for the -1:30 offset
        var rectangleWidth = range.Duration * _pixelsPerSecond;

        FrameworkElement shape;
        if (adps == "Player Death")
        {
          shape = new ImageAwesome
          {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Icon = EFontAwesomeIcon.Solid_SkullCrossbones,
            ToolTip = ":("
          };

          shape.SetResourceReference(ImageAwesome.ForegroundProperty, fillKey);
          shape.SetResourceReference(HeightProperty, "EQContentSize");
          shape.SetResourceReference(WidthProperty, "EQContentSize");
          startXPosition -= 4 - (_pixelsPerSecond * 1.1);
        }
        else
        {
          shape = new Rectangle
          {
            Width = rectangleWidth,
            StrokeThickness = 0.2,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 1.0,
            Effect = new DropShadowEffect { ShadowDepth = 2, Direction = 240, BlurRadius = 0.5, Opacity = 0.5 },
            RadiusX = 2,
            RadiusY = 2,
            ToolTip = adps
          };

          shape.SetResourceReference(Shape.FillProperty, fillKey);
          shape.SetResourceReference(HeightProperty, "EQTimelineBarHeight");
        }

        shape.SetValue(Panel.ZIndexProperty, zIndex);
        Canvas.SetLeft(shape, startXPosition);
        Canvas.SetTop(shape, pos);
        canvas.Children.Add(shape);
      }
    }

    private static TextBlock CreateTextBlock(string text)
    {
      var textBlock = new TextBlock
      {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center
      };

      textBlock.SetResourceReference(ForegroundProperty, "ContentForeground");
      textBlock.SetResourceReference(FontSizeProperty, "EQContentSize");
      return textBlock;
    }

    private static Rectangle CreateHorizontalLine(string widthProperty)
    {
      var rect = new Rectangle
      {
        Height = 1.0,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Opacity = 0.4
      };

      rect.SetResourceReference(Shape.StrokeProperty, "ContentForeground");
      rect.SetResourceReference(WidthProperty, widthProperty);
      return rect;
    }

    private static Rectangle CreateVerticalLine(double position, string fill = "ContentForeground", double opacity = 0.4)
    {
      var rect = new Rectangle
      {
        Width = 1.0,
        Margin = new Thickness(position, 0, 0, 0),
        Opacity = opacity
      };

      rect.SetResourceReference(Shape.StrokeProperty, fill);
      rect.SetResourceReference(HeightProperty, "EQTableRowHeight");
      return rect;
    }

    private void CreateImage(object sender, RoutedEventArgs e) => CreateImage();
    private void CreateLargeImage(object sender, RoutedEventArgs e) => CreateImage(true);

    private void CreateImage(bool everything = false)
    {
      var pVertical = mainScroller.VerticalOffset;
      var pHorizontal = mainScroller.HorizontalOffset;

      Task.Delay(150).ContinueWith(_ =>
      {
        if (everything)
        {
          Dispatcher.Invoke(() =>
          {
            mainScroller.ScrollToHome();
          }, DispatcherPriority.Send);
        }

        Dispatcher.InvokeAsync(() =>
        {
          titlePane.Measure(titlePane.RenderSize);
          headerPanel.Measure(headerPanel.RenderSize);
          labelStackPanel.Measure(labelStackPanel.RenderSize);
          mainStackPanel.Measure(mainStackPanel.RenderSize);

          var dpiScale = UiElementUtil.GetDpi();
          var titleHeight = titlePane.ActualHeight;

          var calcLabelHeight = 0d;
          foreach (var child in labelStackPanel.Children)
          {
            if (child is FrameworkElement { } elem)
            {
              calcLabelHeight += elem.ActualHeight;
            }
          }

          var labelHeight = (int)calcLabelHeight;
          var height = (int)titlePane.ActualHeight + (int)headerPanel.ActualHeight + labelHeight;
          var width = (int)labelStackPanel.ActualWidth + (int)mainStackPanel.ActualWidth;

          // create title image
          var rtb = new RenderTargetBitmap(width, (int)titleHeight, dpiScale, dpiScale, PixelFormats.Default);
          rtb.Render(titlePane);
          var titleImage = BitmapFrame.Create(rtb);

          // create content header image
          rtb = new RenderTargetBitmap(width, (int)headerPanel.ActualHeight, dpiScale, dpiScale, PixelFormats.Default);
          rtb.Render(headerPanel);
          var headerImage = BitmapFrame.Create(rtb);

          // create labels pane image
          rtb = new RenderTargetBitmap((int)labelStackPanel.ActualWidth, labelHeight, dpiScale, dpiScale, PixelFormats.Default);
          rtb.Render(labelStackPanel);
          var labelsImage = BitmapFrame.Create(rtb);

          // create content pane image
          rtb = new RenderTargetBitmap((int)mainStackPanel.ActualWidth, labelHeight, dpiScale, dpiScale, PixelFormats.Default);
          rtb.Render(mainStackPanel);
          var contentImage = BitmapFrame.Create(rtb);

          if (!everything)
          {
            height = (int)titlePane.ActualHeight + (int)headerPanel.ActualHeight + (int)Math.Min(mainScroller.ActualHeight, labelHeight);
            width = (int)labelStackPanel.ActualWidth + (int)mainScroller.ActualWidth;
          }

          rtb = new RenderTargetBitmap(width, height, dpiScale, dpiScale, PixelFormats.Default);

          var dv = new DrawingVisual();
          using (var ctx = dv.RenderOpen())
          {
            // add images together and fix missing background
            var background = Application.Current.Resources["ContentBackground"] as SolidColorBrush;
            var backgroundAlt2 = Application.Current.Resources["ContentBackgroundAlt2"] as SolidColorBrush;
            ctx.DrawRectangle(background, null, new Rect(new Point(0, 0), new Size(width, height)));
            ctx.DrawImage(titleImage, new Rect(new Point(0, 0), new Size(titleImage.Width, titleImage.Height)));
            ctx.DrawRectangle(backgroundAlt2, null, new Rect(new Point(0, titleImage.Height), new Size(headerImage.Width, headerImage.Height)));
            ctx.DrawImage(headerImage, new Rect(new Point(0, titleImage.Height), new Size(headerImage.Width, headerImage.Height)));
            ctx.DrawImage(labelsImage, new Rect(new Point(0, titleImage.Height + headerImage.Height), new Size(labelsImage.Width, labelsImage.Height)));
            ctx.DrawImage(contentImage, new Rect(new Point(labelsImage.Width, titleImage.Height + headerImage.Height), new Size(contentImage.Width, contentImage.Height)));
          }

          rtb.Render(dv);
          Clipboard.SetImage(BitmapFrame.Create(rtb));
        }, DispatcherPriority.Send);

        Dispatcher.InvokeAsync(() =>
        {
          if (everything)
          {
            mainScroller.ScrollToVerticalOffset(pVertical);
            mainScroller.ScrollToHorizontalOffset(pHorizontal);
          }
        }, DispatcherPriority.DataBind);
      });
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e)
    {
      try
      {
        var labels = new List<string>();
        foreach (var visual in labelStackPanel.Children)
        {
          if (visual is StackPanel { Children.Count: > 0 } leftPanel)
          {
            foreach (var child in leftPanel.Children)
            {
              if (child is TextBlock block)
              {
                labels.Add(block.Text);
              }
            }
          }
        }

        var playerData = new List<List<object>>();
        foreach (var label in labels)
        {
          if (!string.IsNullOrEmpty(label) && _spellRanges.TryGetValue(label, out var value))
          {
            foreach (var top in value.TopRanges)
            {
              playerData.Add(
              [
                label,
                _selectedStats[0].OrigName,
                top.BeginSeconds,
                label == "Player Death" ? 1 : top.Duration
              ]);
            }

            foreach (var bottom in value.BottomRanges)
            {
              playerData.Add(
              [
                label,
                _selectedStats[1].OrigName,
                bottom.BeginSeconds,
                label == "Player Death" ? 1 : bottom.Duration
              ]);
            }
          }
        }

        string title;
        if (string.IsNullOrEmpty(titleLabel2.Content as string))
        {
          title = titleLabel1.Content?.ToString();
        }
        else
        {
          title = string.Format(CultureInfo.CurrentCulture, "{0} {1} {2}", titleLabel1.Content as string,
            titleLabel2.Content as string, titleLabel3.Content as string);
        }

        var header = new List<string> { "Adps", "Player", "Start", "End" };
        Clipboard.SetDataObject(TextUtils.BuildCsv(header, playerData, title));
      }
      catch (ExternalException ex)
      {
        Log.Error(ex);
      }
    }

    private class SpellRange
    {
      public List<TimeRange> TopRanges { get; } = [];
      public List<TimeRange> BottomRanges { get; } = [];
      public ushort Adps { get; init; }
    }

    private class TimeRange(int begin, int end)
    {
      public int BeginSeconds { get; } = begin;
      public int Duration { get; set; } = end;
    }

    private void ScrollViewerOnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
      headerCanvas.Margin = new Thickness(-e.HorizontalOffset, 0, 0, 0);

      if (!labelsScroller.VerticalOffset.Equals(e.VerticalOffset))
      {
        labelsScroller.ScrollToVerticalOffset(e.VerticalOffset);
      }

      if (mainScroller.ComputedHorizontalScrollBarVisibility == Visibility.Visible)
      {
        labelsScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Visible;
      }
      else
      {
        labelsScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
      }
    }

    private void LabelsScrollerOnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
      if (!mainScroller.VerticalOffset.Equals(e.VerticalOffset))
      {
        mainScroller.ScrollToVerticalOffset(e.VerticalOffset);
      }
    }

    private void ContentGridOnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
      if ((Keyboard.Modifiers & ModifierKeys.Control) > 0)
      {
        if (e.Delta < 0 && _pixelsPerSecond > 1.0)
        {
          _pixelsPerSecond -= 0.5;
          e.Handled = true;
          Dispatcher.InvokeAsync(Display);
        }
        else if (e.Delta > 0 && _pixelsPerSecond < 5.0)
        {
          _pixelsPerSecond += 0.5;
          e.Handled = true;
          Dispatcher.InvokeAsync(Display);
        }
      }
    }

    private void DragOnPreviewMouseMove(object sender, MouseEventArgs e)
    {
      if (e.LeftButton == MouseButtonState.Pressed && _draggedElements.Count == 4)
      {
        var pos = e.GetPosition(contentGrid);
        Canvas.SetLeft(_draggedElements[0], pos.X);
        Canvas.SetTop(_draggedElements[0], pos.Y);

        // Find the closest rectangle to the mouse position
        if (FindClosestRectangle(pos) is { } closest)
        {
          if (_selectedRectangle != null && _selectedRectangle != closest)
          {
            _selectedRectangle.SetResourceReference(Shape.StrokeProperty, "ContentForeground");
          }

          // highlight
          closest.SetResourceReference(Shape.StrokeProperty, "EQWarnForegroundBrush");
          _selectedRectangle = closest;
        }
      }
    }

    private void DragOnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
      if (_draggedElements.Count == 4)
      {
        for (var i = 0; i < labelStackPanel.Children.Count; i++)
        {
          if (_selectedRectangle == labelStackPanel.Children[i])
          {
            if (i >= 0)
            {
              dragCanvas.Children.Remove(_draggedElements[0]);
              dragCanvas.IsHitTestVisible = false;
              Panel.SetZIndex(_draggedElements[0], 1);

              if (_draggedElements[0] is StackPanel stack)
              {
                stack.SetResourceReference(Panel.BackgroundProperty, "ContentBackground");
                stack.Opacity = 1.0;
                _keyOrder.Remove(stack.Tag.ToString());

                if (i >= labelStackPanel.Children.Count - 1)
                {
                  _keyOrder.Add(stack.Tag.ToString());
                }
                else if (labelStackPanel.Children[i + 1] is StackPanel before && _keyOrder.IndexOf(before.Tag.ToString()) is var found and > -1)
                {
                  _keyOrder.Insert(found, stack.Tag.ToString());
                }
              }

              labelStackPanel.Children.Insert(i + 1, _draggedElements[0]);
              labelStackPanel.Children.Insert(i + 2, _draggedElements[1]);
              mainStackPanel.Children.Insert(i + 1, _draggedElements[2]);
              mainStackPanel.Children.Insert(i + 2, _draggedElements[3]);
            }
            break;
          }
        }

        _draggedElements.Clear();
      }

      if (_selectedRectangle != null)
      {
        _selectedRectangle.SetResourceReference(Shape.StrokeProperty, "ContentForeground");
        _selectedRectangle = null;
      }
    }

    private Rectangle FindClosestRectangle(Point mousePosition)
    {
      Rectangle closestRectangle = null;
      var closestDistance = double.MaxValue;

      foreach (var visual in labelStackPanel.Children)
      {
        if (visual is Rectangle rectangle)
        {
          // Get the top-left corner position of the rectangle relative to the stackPanel
          var rectTopLeft = rectangle.TranslatePoint(new Point(0, 0), labelStackPanel);
          rectTopLeft.Y -= labelsScroller.VerticalOffset;

          // Calculate the center of the rectangle
          var rectCenter = new Point(
            rectTopLeft.X + (rectangle.ActualWidth / 2),
            rectTopLeft.Y + (rectangle.ActualHeight / 2));

          // Calculate the distance from the mouse to the center of the rectangle
          var distance = GetDistance(mousePosition, rectCenter);

          // Check if this is the closest rectangle so far
          if (distance < closestDistance)
          {
            closestDistance = distance;
            closestRectangle = rectangle;
          }
        }
      }

      return closestRectangle;
    }

    // Helper method to calculate the distance between two points
    private static double GetDistance(Point p1, Point p2)
    {
      return Math.Sqrt(Math.Pow(p1.X - p2.X, 2) + Math.Pow(p1.Y - p2.Y, 2));
    }
  }
}
