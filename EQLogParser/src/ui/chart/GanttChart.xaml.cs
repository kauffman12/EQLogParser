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
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace EQLogParser
{
  public partial class GanttChart : IDisposable
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly List<string> BlockBrushes = ["EQMenuIconBrush", "EQWarnForegroundBrush"];

    private double _rowHeight;
    private const int LabelsWidth = 190;
    private const ushort CasterAdps = 1;
    private const ushort MeleeAdps = 2;
    private const ushort TankAdps = 4;
    private const ushort HealingAdps = 8;
    private const ushort AnyAdps = CasterAdps + MeleeAdps + TankAdps + HealingAdps;
    private readonly string[] _types = { "Defensive Skills", "ADPS", "Healing Skills" };
    private readonly Dictionary<string, SpellRange> _spellRanges = [];
    private readonly List<TextBlock> _headers = [];
    private readonly Dictionary<string, byte> _selfOnly = [];
    private readonly Dictionary<string, byte> _ignore = [];
    private double _startTime;
    private double _endTime;
    private double _length;
    private List<PlayerStats> _selected;
    private int _timelineType;
    private bool _currentHideSelfOnly = true;
    private bool _currentShowCasterAdps = true;
    private bool _currentShowMeleeAdps = true;

    public GanttChart()
    {
      InitializeComponent();
      _rowHeight = (int)MainWindow.CurrentFontSize + 12;
      MainActions.EventsThemeChanged += EventsThemeChanged;
    }

    // timelineType 0 = tanking, 1 = dps, 2 = healing
    internal void Init(CombinedStats currentStats, List<PlayerStats> selected, List<List<ActionGroup>> groups, int timelineType)
    {
      if (selected is { Count: > 0 } && timelineType is >= 0 and <= 2)
      {
        _timelineType = timelineType;
        _selected = selected;
        _startTime = groups.Min(block => block.First().BeginTime) - DataManager.BuffsOffset;
        _endTime = groups.Max(block => block.Last().BeginTime) + 1;
        _length = _endTime - _startTime;

        switch (_selected.Count)
        {
          case 1:
            titleLabel1.Content = _selected[0].OrigName + "'s " + _types[_timelineType] + " | " + currentStats?.ShortTitle;
            break;
          case 2:
            titleLabel1.Content = _selected[0].OrigName + " vs ";
            titleLabel2.Content = _selected[1].OrigName + "'s ";
            titleLabel3.Content = _types[_timelineType] + " | " + currentStats?.ShortTitle;
            break;
        }

        if (_timelineType is 0 or 2)
        {
          showMeleeAdps.Visibility = Visibility.Hidden;
          showCasterAdps.Visibility = Visibility.Hidden;
        }

        var deathMap = new Dictionary<string, HashSet<double>>();
        foreach (var (beginTime, record) in RecordManager.Instance.GetDeathsDuring(_startTime, _endTime))
        {
          if (_selected.FindIndex(stats => stats.OrigName == record.Killed) > -1)
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

        for (var i = 0; i < _selected.Count; i++)
        {
          var player = _selected[i].OrigName;
          if (deathMap.TryGetValue(_selected[i].OrigName, out var deathTimes))
          {
            var death = new SpellData { Adps = (byte)AnyAdps, Duration = 3, NameAbbrv = "Player Death", Name = "Player Death" };
            foreach (var time in deathTimes)
            {
              UpdateSpellRange(death, time, BlockBrushes[i]);
            }
          }

          var castSpells = new List<SpellData>();
          foreach (var (beginTime, action) in RecordManager.Instance.GetSpellsDuring(_startTime, _endTime))
          {
            if (action is SpellCast { Interrupted: false } cast && cast.Caster == player && cast.SpellData is { Target: (int)SpellTarget.Self, Adps: > 0 }
              && (cast.SpellData.MaxHits > 0 || cast.SpellData.Duration <= 1800) && ClassFilter(cast.SpellData))
            {
              castSpells.Add(cast.SpellData);
              UpdateSpellRange(cast.SpellData, beginTime, BlockBrushes[i], deathTimes);
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
                if (string.IsNullOrEmpty(spellData.LandsOnOther))
                {
                  _selfOnly[spellData.NameAbbrv] = 1;
                }

                UpdateSpellRange(spellData, beginTime, BlockBrushes[i], deathTimes, received.IsWearOff);
              }
            }
          }
        }
      }

      showCasterAdps.IsEnabled = showMeleeAdps.IsEnabled = _spellRanges.Count > 0;
      hideSelfOnly.IsEnabled = _spellRanges.Count > 0 && _selected?.Find(stats => stats.OrigName == ConfigUtil.PlayerName) != null;

      AddHeaderLabel(0, string.Format(CultureInfo.CurrentCulture, "Buffs (T-{0})", DataManager.BuffsOffset), 20);
      AddHeaderLabel(DataManager.BuffsOffset, DateUtil.FormatSimpleHms(_startTime + DataManager.BuffsOffset), 10);

      var minutes = 1;
      for (var more = (int)(DataManager.BuffsOffset + 60); more < _length; more += 60)
      {
        AddHeaderLabel(more, minutes + "m", 0);
        minutes++;
      }

      Display();
    }

    private void EventsThemeChanged(string _)
    {
      _rowHeight = (int)MainWindow.CurrentFontSize + 12;
      Display();
    }

    private void RefreshClick(object sender, RoutedEventArgs e)
    {
      _ignore.Clear();
      Display();
    }

    private bool ClassFilter(SpellData data)
    {
      return (_timelineType == 0 && (data.Adps & TankAdps) != 0) || (_timelineType == 1 && ((data.Adps & CasterAdps) != 0 || (data.Adps & MeleeAdps) != 0)) ||
        (_timelineType == 2 && (data.Adps & HealingAdps) != 0);
    }

    private void UpdateSpellRange(SpellData spellData, double beginTime, string brush, HashSet<double> deathTimes = null, bool isWearOff = false)
    {
      if (!_spellRanges.TryGetValue(spellData.NameAbbrv, out var spellRange))
      {
        if (!isWearOff)
        {
          spellRange = new SpellRange { Adps = spellData.Adps };
          var duration = GetDuration(spellData, _endTime, beginTime, deathTimes);
          var range = new TimeRange { BlockBrush = brush, BeginSeconds = (int)(beginTime - _startTime), Duration = duration };
          spellRange.Ranges.Add(range);
          _spellRanges[spellData.NameAbbrv] = spellRange;
        }
      }
      else
      {
        var last = spellRange.Ranges.LastOrDefault(range => range.BlockBrush == brush);
        var offsetSeconds = (int)(beginTime - _startTime);
        if (last != null && offsetSeconds >= last.BeginSeconds && offsetSeconds <= (last.BeginSeconds + last.Duration))
        {
          if (isWearOff)
          {
            var newOffset = offsetSeconds - last.BeginSeconds;
            last.Duration = newOffset - last.Duration <= 6 ? newOffset : last.Duration;
          }
          else
          {
            last.Duration = GetDuration(spellData, _endTime, beginTime, deathTimes) + (offsetSeconds - last.BeginSeconds);
          }
        }
        else if (!isWearOff)
        {
          var duration = GetDuration(spellData, _endTime, beginTime, deathTimes);
          var range = new TimeRange { BlockBrush = brush, BeginSeconds = (int)(beginTime - _startTime), Duration = duration };
          spellRange.Ranges.Add(range);
        }
      }
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e)
    {
      try
      {
        var labels = new List<string>();
        foreach (var visual in contentLabels.Children)
        {
          if (visual is TextBlock block)
          {
            labels.Add(block.Text);
          }
        }

        var player1 = new Dictionary<string, List<Rectangle>>();
        var player2 = new Dictionary<string, List<Rectangle>>();

        foreach (var visual in content.Children)
        {
          if (visual is Rectangle { Tag: string adps } rectangle && !string.IsNullOrEmpty(adps))
          {
            var selected = titleLabel1.Foreground.ToString() == rectangle.Fill.ToString() ? player1 : player2;
            AddValue(selected, adps, rectangle);
          }
        }

        var playerData = new List<List<object>>();
        foreach (var label in CollectionsMarshal.AsSpan(labels))
        {
          if (player1.TryGetValue(label, out var rectangles1))
          {
            foreach (var rectangle in CollectionsMarshal.AsSpan(rectangles1))
            {
              playerData.Add(
                [
                  label,
                  _selected[0].OrigName,
                  _startTime + rectangle.Margin.Left,
                  _startTime + rectangle.Margin.Left + rectangle.ActualWidth
                ]);
            }
          }

          if (player2.TryGetValue(label, out var rectangles2))
          {
            foreach (var rectangle in CollectionsMarshal.AsSpan(rectangles2))
            {
              playerData.Add(
                [
                  label,
                  _selected[1].OrigName, _startTime + rectangle.Margin.Left,
                  _startTime + rectangle.Margin.Left + rectangle.ActualWidth
                ]);
            }
          }
        }

        string title;
        if (string.IsNullOrEmpty(titleLabel2.Content as string))
        {
          title = titleLabel1.Content as string;
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

    private void CreateImage(object sender, RoutedEventArgs e) => CreateImage();
    private void CreateLargeImage(object sender, RoutedEventArgs e) => CreateImage(true);

    private static void AddValue(Dictionary<string, List<Rectangle>> dict, string name, Rectangle value)
    {
      if (dict.TryGetValue(name, out var list))
      {
        list.Add(value);
      }
      else
      {
        dict[name] = [value];
      }
    }

    private void CreateImage(bool everything = false)
    {
      double previousOffset = -1;
      var hidden = new List<TextBlock>();

      if (everything)
      {
        foreach (var header in CollectionsMarshal.AsSpan(_headers))
        {
          if (header.Visibility == Visibility.Hidden)
          {
            hidden.Add(header);
            header.Visibility = Visibility.Visible;
          }
        }

        previousOffset = contentScroller.HorizontalOffset;
        contentScroller.ScrollToHorizontalOffset(0);
        headerScroller.ScrollToHorizontalOffset(0);
      }

      Task.Delay(150).ContinueWith(_ =>
      {
        Dispatcher.InvokeAsync(() =>
        {
          titlePane.Measure(titlePane.RenderSize);
          contentHeader.Measure(contentHeader.RenderSize);
          contentLabels.Measure(contentLabels.RenderSize);
          content.Measure(content.RenderSize);

          var dpiScale = UiElementUtil.GetDpi();
          var titleHeight = titlePane.ActualHeight;

          // create title image
          var rtb = new RenderTargetBitmap((int)contentLabels.ActualWidth + (int)content.ActualWidth,
            (int)titleHeight, dpiScale, dpiScale, PixelFormats.Default);
          rtb.Render(titlePane);
          var titleImage = BitmapFrame.Create(rtb);

          // create content header image
          rtb = new RenderTargetBitmap((int)contentHeader.ActualWidth, (int)contentHeader.ActualHeight,
            dpiScale, dpiScale, PixelFormats.Default);
          rtb.Render(contentHeader);
          var headerImage = BitmapFrame.Create(rtb);

          // create labels pane image
          rtb = new RenderTargetBitmap((int)contentLabels.ActualWidth, (int)contentLabels.ActualHeight,
            dpiScale, dpiScale, PixelFormats.Default);
          rtb.Render(contentLabels);
          var labelsImage = BitmapFrame.Create(rtb);

          // create content pane image
          rtb = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight,
            dpiScale, dpiScale, PixelFormats.Default);
          rtb.Render(content);
          var contentImage = BitmapFrame.Create(rtb);

          var width = (int)labelsImage.Width + (int)content.Children.Cast<FrameworkElement>().ToList().Max(x => x.Margin.Left) + 5;
          if (!everything)
          {
            width = Math.Min(width, (int)labelsScroller.ActualWidth + (int)contentScroller.ActualWidth + 5);
            if (contentScroller.ComputedVerticalScrollBarVisibility == Visibility.Visible)
            {
              width -= 12; // might be a Syncfusion property for this
            }
          }

          var last = contentLabels.Children.Cast<FrameworkElement>().LastOrDefault();
          var contentHeight = (last == null) ? 0 : (int)last.ActualHeight;
          var height = (int)titleImage.Height + (int)headerImage.Height;
          if (everything)
          {
            height += contentHeight;
          }
          else
          {
            height += Math.Min(contentHeight, (int)labelsScroller.ActualHeight) + 1;
            if (contentScroller.ComputedVerticalScrollBarVisibility == Visibility.Visible)
            {
              height += 2; // might be a Syncfusion property for this
            }
            if (contentScroller.ComputedHorizontalScrollBarVisibility == Visibility.Visible &&
              contentScroller.ComputedVerticalScrollBarVisibility == Visibility.Visible)
            {
              height -= 12; // might be a Syncfusion property for this
            }
          }

          rtb = new RenderTargetBitmap(width, height, dpiScale, dpiScale, PixelFormats.Default);

          var dv = new DrawingVisual();
          using (var ctx = dv.RenderOpen())
          {
            // add images together and fix missing background
            var background = Application.Current.Resources["ContentBackground"] as SolidColorBrush;
            ctx.DrawRectangle(background, null, new Rect(new Point(0, 0), new Size(width, height)));
            ctx.DrawImage(titleImage, new Rect(new Point(0, 0), new Size(titleImage.Width, titleImage.Height)));
            ctx.DrawImage(headerImage, new Rect(new Point(0, titleImage.Height), new Size(headerImage.Width, headerImage.Height)));
            ctx.DrawImage(labelsImage, new Rect(new Point(0, titleImage.Height + headerImage.Height), new Size(labelsImage.Width, labelsImage.Height)));
            ctx.DrawImage(contentImage, new Rect(new Point(labelsImage.Width, titleImage.Height + headerImage.Height), new Size(contentImage.Width, contentImage.Height)));
          }

          rtb.Render(dv);
          Clipboard.SetImage(BitmapFrame.Create(rtb));

          if (everything)
          {
            hidden.ForEach(header => header.Visibility = Visibility.Hidden);
            contentScroller.ScrollToHorizontalOffset(previousOffset);
          }
        }, DispatcherPriority.Background);
      });
    }

    private void Display()
    {
      content.Children.Clear();
      contentLabels.Children.Clear();

      var row = 0;
      foreach (var key in _spellRanges.Keys.OrderBy(key => key))
      {
        var spellRange = _spellRanges[key];
        if ((!_currentHideSelfOnly || !_selfOnly.ContainsKey(key))
          && ((_currentShowCasterAdps && ((spellRange.Adps & CasterAdps) == CasterAdps))
          || (_currentShowMeleeAdps && ((spellRange.Adps & MeleeAdps) == MeleeAdps))
          || (_timelineType == 0 && ((spellRange.Adps & TankAdps) == TankAdps))
          || (_timelineType == 2 && ((spellRange.Adps & HealingAdps) == HealingAdps))) && !_ignore.ContainsKey(key))
        {
          var hPos = _rowHeight * row;
          AddGridRow(hPos, key);
          spellRange.Ranges.ForEach(timeRange => content.Children.Add(CreateAdpsBlock(key, hPos, timeRange.BeginSeconds,
            timeRange.Duration, timeRange.BlockBrush, _selected.Count)));
          row++;
        }
      }

      var finalHeight = _rowHeight * row;
      AddDivider(contentLabels, finalHeight, 20);
      AddDivider(content, finalHeight, DataManager.BuffsOffset);
      AddDivider(content, finalHeight, _length);

      for (var more = (int)(DataManager.BuffsOffset + 60); more < _length; more += 60)
      {
        AddDivider(content, finalHeight, more);
      }

      if (_spellRanges.TryGetValue("Player Death", out var range))
      {
        range.Ranges.ForEach(instance =>
        {
          // add 1 to center it a bit
          AddDivider(content, finalHeight, instance.BeginSeconds + 1, instance.BlockBrush);
        });
      }
    }

    private void AddGridRow(double hPos, string name)
    {
      var labelsRow = CreateRowBlock(hPos);
      var contentRow = CreateRowBlock(hPos);

      contentLabels.Children.Add(labelsRow);
      content.Children.Add(contentRow);

      var image = new ImageAwesome
      {
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Icon = EFontAwesomeIcon.Solid_Times,
        Margin = new Thickness(4, hPos + 6, 4, 0)
      };

      image.SetResourceReference(HeightProperty, "EQContentSize");
      image.SetResourceReference(WidthProperty, "EQContentSize");
      image.SetResourceReference(ImageAwesome.ForegroundProperty, "EQMenuIconBrush");
      image.PreviewMouseLeftButtonDown += (_, _) =>
      {
        _ignore[name] = 1;
        Display();
      };

      var textBlock = new TextBlock
      {
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Text = name,
        Width = LabelsWidth - 20,
        Margin = new Thickness(24, hPos + 4, 0, 0)
      };

      textBlock.SetResourceReference(TextBlock.FontSizeProperty, "EQContentSize");
      textBlock.SetResourceReference(TextBlock.ForegroundProperty, "ContentForeground");
      textBlock.SetValue(Panel.ZIndexProperty, 10);
      image.SetValue(Panel.ZIndexProperty, 10);
      contentLabels.Children.Add(image);
      contentLabels.Children.Add(textBlock);
    }

    private void AddHeaderLabel(double left, string text, int offset)
    {
      var textBlock = new TextBlock
      {
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Text = text,
        Width = 80,
        Margin = new Thickness(LabelsWidth + left - offset, 0, 0, 0)
      };

      _headers.Add(textBlock);
      textBlock.SetResourceReference(TextBlock.FontSizeProperty, "EQContentSize");
      textBlock.SetResourceReference(TextBlock.ForegroundProperty, "ContentForeground");
      textBlock.SetValue(Panel.ZIndexProperty, 10);
      contentHeader.Children.Add(textBlock);
    }

    private static void AddDivider(Grid target, double hPos, double left, string blockBrush = null)
    {
      var rectangle = new Rectangle
      {
        StrokeThickness = (blockBrush == null) ? 0.3 : 0.9,
        Height = hPos,
        Width = (blockBrush == null) ? 0.3 : 0.9,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(left, 0, 0, 0)
      };

      var brushName = blockBrush ?? "ContentForeground";
      rectangle.SetResourceReference(Shape.StrokeProperty, brushName);
      target.Children.Add(rectangle);
    }

    private void ContentScrollViewChanged(object sender, ScrollChangedEventArgs e)
    {
      if (sender is ScrollViewer scrollable)
      {
        if (scrollable.ComputedHorizontalScrollBarVisibility != labelsScroller.ComputedHorizontalScrollBarVisibility)
        {
          labelsScroller.HorizontalScrollBarVisibility =
            scrollable.ComputedHorizontalScrollBarVisibility == Visibility.Visible ? ScrollBarVisibility.Visible : ScrollBarVisibility.Hidden;
        }

        if (!labelsScroller.VerticalOffset.Equals(e.VerticalOffset))
        {
          labelsScroller.ScrollToVerticalOffset(e.VerticalOffset);
        }

        if (!headerScroller.HorizontalOffset.Equals(e.HorizontalOffset))
        {
          headerScroller.ScrollToHorizontalOffset(e.HorizontalOffset);
        }

        _headers.ForEach(header =>
        {
          if (header.Margin.Left + (header.ActualWidth / 2) < (e.HorizontalOffset + 10 + LabelsWidth))
          {
            if (header.Visibility != Visibility.Hidden)
            {
              header.Visibility = Visibility.Hidden;
            }
          }
          else
          {
            if (header.Visibility != Visibility.Visible)
            {
              header.Visibility = Visibility.Visible;
            }
          }
        });
      }
    }

    private void LabelsScrollViewChanged(object sender, ScrollChangedEventArgs e)
    {
      if (!contentScroller.VerticalOffset.Equals(e.VerticalOffset))
      {
        contentScroller.ScrollToVerticalOffset(e.VerticalOffset);
      }
    }

    private void OptionsChange(object sender, RoutedEventArgs e)
    {
      _currentHideSelfOnly = hideSelfOnly?.IsChecked == true;
      _currentShowCasterAdps = showCasterAdps?.IsChecked == true;
      _currentShowMeleeAdps = showMeleeAdps?.IsChecked == true;

      // when the UI is ready basically
      if (_selected != null)
      {
        Display();
      }
    }

    private Rectangle CreateAdpsBlock(string label, double hPos, int start, int length, string blockBrush, int count)
    {
      var offset = count == 1 ? 0 : blockBrush == BlockBrushes[0] ? -3 : 3;
      var zIndex = blockBrush == BlockBrushes[0] ? 11 : 10;

      var block = new Rectangle
      {
        StrokeThickness = 0.2,
        Height = _rowHeight / 3,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Opacity = 1.0,
        Width = length,
        Margin = new Thickness(start, hPos + (_rowHeight / 3) + offset, 0, 0),
        Effect = new DropShadowEffect { ShadowDepth = 2, Direction = 240, BlurRadius = 0.5, Opacity = 0.5 },
        RadiusX = 2,
        RadiusY = 2,
        Tag = label
      };

      block.SetResourceReference(Shape.FillProperty, blockBrush);
      block.SetResourceReference(Shape.StrokeProperty, "ContentForeground");
      block.SetValue(Panel.ZIndexProperty, zIndex);
      return block;
    }

    private Rectangle CreateRowBlock(double hPos)
    {
      var block = new Rectangle
      {
        Height = _rowHeight,
        StrokeThickness = 0.2,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, hPos, 0, 0)
      };

      block.SetResourceReference(Shape.StrokeProperty, "ContentForeground");
      return block;
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

    #region IDisposable Support
    private bool _disposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!_disposedValue)
      {
        MainActions.EventsThemeChanged -= EventsThemeChanged;
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

    private class SpellRange
    {
      public List<TimeRange> Ranges { get; } = [];
      public ushort Adps { get; init; }
    }

    private class TimeRange
    {
      public int BeginSeconds { get; init; }
      public int Duration { get; set; }
      public string BlockBrush { get; init; }
    }
  }
}
