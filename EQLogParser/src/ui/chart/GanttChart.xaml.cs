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
  public partial class GanttChart : UserControl, IDisposable
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private static readonly List<string> BlockBrushes = new() { "EQMenuIconBrush", "EQWarnForegroundBrush" };

    private int ROW_HEIGHT;
    private const int LABELS_WIDTH = 190;
    private const ushort CASTER_ADPS = 1;
    private const ushort MELEE_ADPS = 2;
    private const ushort TANK_ADPS = 4;
    private const ushort HEALING_ADPS = 8;
    private const ushort ANY_ADPS = CASTER_ADPS + MELEE_ADPS + TANK_ADPS + HEALING_ADPS;
    private readonly string[] TYPES = { "Defensive Skills", "ADPS", "Healing Skills" };

    private readonly Dictionary<string, SpellRange> SpellRanges = new();
    private readonly List<TextBlock> Headers = new();
    private readonly Dictionary<string, byte> SelfOnly = new();
    private readonly Dictionary<string, byte> Ignore = new();
    private double StartTime;
    private double EndTime;
    private double Length;
    private List<PlayerStats> Selected;
    private int TimelineType;

    private bool CurrentHideSelfOnly = true;
    private bool CurrentShowCasterAdps = true;
    private bool CurrentShowMeleeAdps = true;

    public GanttChart()
    {
      InitializeComponent();
      ROW_HEIGHT = (int)MainWindow.CurrentFontSize + 12;
      MainActions.EventsThemeChanged += EventsThemeChanged;
    }

    // timelineType 0 = tanking, 1 = dps, 2 = healing
    internal void Init(CombinedStats currentStats, List<PlayerStats> selected, List<List<ActionGroup>> groups, int timelineType)
    {
      if (selected is { Count: > 0 } && timelineType is >= 0 and <= 2)
      {
        TimelineType = timelineType;
        Selected = selected;
        StartTime = groups.Min(block => block.First().BeginTime) - DataManager.BUFFS_OFFSET;
        EndTime = groups.Max(block => block.Last().BeginTime) + 1;
        Length = EndTime - StartTime;

        switch (Selected.Count)
        {
          case 1:
            titleLabel1.Content = Selected[0].OrigName + "'s " + TYPES[TimelineType] + " | " + currentStats?.ShortTitle;
            break;
          case 2:
            titleLabel1.Content = Selected[0].OrigName + " vs ";
            titleLabel2.Content = Selected[1].OrigName + "'s ";
            titleLabel3.Content = TYPES[TimelineType] + " | " + currentStats?.ShortTitle;
            break;
        }

        if (TimelineType == 0 || TimelineType == 2)
        {
          showMeleeAdps.Visibility = Visibility.Hidden;
          showCasterAdps.Visibility = Visibility.Hidden;
        }

        var deathMap = new Dictionary<string, HashSet<double>>();
        foreach (var block in DataManager.Instance.GetDeathsDuring(StartTime, EndTime))
        {
          foreach (var action in block.Actions)
          {
            if (action is DeathRecord death)
            {
              if (Selected.FindIndex(stats => stats.OrigName == death.Killed) > -1)
              {
                if (deathMap.TryGetValue(death.Killed, out var values))
                {
                  values.Add(block.BeginTime);
                }
                else
                {
                  deathMap[death.Killed] = new HashSet<double> { block.BeginTime };
                }
              }
            }
          }
        }

        for (var i = 0; i < Selected.Count; i++)
        {
          var player = Selected[i].OrigName;
          var allSpells = new List<ActionGroup>();
          allSpells.AddRange(DataManager.Instance.GetCastsDuring(StartTime, EndTime));
          allSpells.AddRange(DataManager.Instance.GetReceivedSpellsDuring(StartTime, EndTime));

          if (deathMap.TryGetValue(Selected[i].OrigName, out var deathTimes))
          {
            var death = new SpellData { Adps = (byte)ANY_ADPS, Duration = 3, NameAbbrv = "Player Death", Name = "Player Death" };
            foreach (var time in deathTimes)
            {
              UpdateSpellRange(death, time, BlockBrushes[i]);
            }
          }

          foreach (var block in allSpells.OrderBy(block => block.BeginTime).ThenBy(block => (block.Actions.Count > 0 && block.Actions[0] is ReceivedSpell) ? 1 : -1))
          {
            foreach (var action in block.Actions)
            {
              if (action is SpellCast { Interrupted: false } cast && cast.Caster == player && cast.SpellData is
                {
                  Target: (int)SpellTarget.SELF, Adps: > 0
                } && (cast.SpellData.MaxHits > 0 || cast.SpellData.Duration <= 1800) && ClassFilter(cast.SpellData))
              {
                UpdateSpellRange(cast.SpellData, block.BeginTime, BlockBrushes[i], deathTimes);
              }
              else if (action is ReceivedSpell received && received.Receiver == player)
              {
                var spellData = received.SpellData;
                if (spellData == null && received.Ambiguity.Count > 0 && DataManager.ResolveSpellAmbiguity(received, out var replaced))
                {
                  spellData = replaced;
                }

                if (spellData is { Adps: > 0 } && (spellData.MaxHits > 0 || spellData.Duration <= 1800) && ClassFilter(spellData))
                {
                  if (string.IsNullOrEmpty(spellData.LandsOnOther))
                  {
                    SelfOnly[spellData.NameAbbrv] = 1;
                  }

                  UpdateSpellRange(spellData, block.BeginTime, BlockBrushes[i], deathTimes);
                }
              }
            }
          }
        }
      }

      showCasterAdps.IsEnabled = showMeleeAdps.IsEnabled = SpellRanges.Count > 0;
      hideSelfOnly.IsEnabled = SpellRanges.Count > 0 && Selected?.Find(stats => stats.OrigName == ConfigUtil.PlayerName) != null;

      AddHeaderLabel(0, string.Format(CultureInfo.CurrentCulture, "Buffs (T-{0})", DataManager.BUFFS_OFFSET), 20);
      AddHeaderLabel(DataManager.BUFFS_OFFSET, DateUtil.FormatSimpleHMS(StartTime + DataManager.BUFFS_OFFSET), 10);

      var minutes = 1;
      for (var more = (int)(DataManager.BUFFS_OFFSET + 60); more < Length; more += 60)
      {
        AddHeaderLabel(more, minutes + "m", 0);
        minutes++;
      }

      Display();
    }

    private void EventsThemeChanged(string _)
    {
      ROW_HEIGHT = (int)MainWindow.CurrentFontSize + 12;
      Display();
    }

    private void RefreshClick(object sender, RoutedEventArgs e)
    {
      Ignore.Clear();
      Display();
    }

    private bool ClassFilter(SpellData data)
    {
      return (TimelineType == 0 && (data.Adps & TANK_ADPS) != 0) || (TimelineType == 1 && ((data.Adps & CASTER_ADPS) != 0 || (data.Adps & MELEE_ADPS) != 0)) ||
        (TimelineType == 2 && (data.Adps & HEALING_ADPS) != 0);
    }

    private void UpdateSpellRange(SpellData spellData, double beginTime, string brush, HashSet<double> deathTimes = null)
    {
      if (!SpellRanges.TryGetValue(spellData.NameAbbrv, out var spellRange))
      {
        spellRange = new SpellRange { Adps = spellData.Adps };
        var duration = GetDuration(spellData, EndTime, beginTime, deathTimes);
        var range = new TimeRange { BlockBrush = brush, BeginSeconds = (int)(beginTime - StartTime), Duration = duration };
        spellRange.Ranges.Add(range);
        SpellRanges[spellData.NameAbbrv] = spellRange;
      }
      else
      {
        var last = spellRange.Ranges.LastOrDefault(range => range.BlockBrush == brush);
        var offsetSeconds = (int)(beginTime - StartTime);
        if (last != null && offsetSeconds >= last.BeginSeconds && offsetSeconds <= (last.BeginSeconds + last.Duration))
        {
          last.Duration = GetDuration(spellData, EndTime, beginTime, deathTimes) + (offsetSeconds - last.BeginSeconds);
        }
        else
        {
          var duration = GetDuration(spellData, EndTime, beginTime, deathTimes);
          var range = new TimeRange { BlockBrush = brush, BeginSeconds = (int)(beginTime - StartTime), Duration = duration };
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
        foreach (ref var label in labels.ToArray().AsSpan())
        {
          if (player1.TryGetValue(label, out var rectangles1))
          {
            foreach (ref var rectangle in rectangles1.ToArray().AsSpan())
            {
              playerData.Add(
                new List<object>
                {
                  label,
                  Selected[0].OrigName,
                  StartTime + rectangle.Margin.Left,
                  StartTime + rectangle.Margin.Left + rectangle.ActualWidth
                });
            }
          }

          if (player2.TryGetValue(label, out var rectangles2))
          {
            foreach (ref var rectangle in rectangles2.ToArray().AsSpan())
            {
              playerData.Add(
                new List<object>
                {
                  label,
                  Selected[1].OrigName, StartTime + rectangle.Margin.Left,
                  StartTime + rectangle.Margin.Left + rectangle.ActualWidth
                });
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

    private static void AddValue(IDictionary<string, List<Rectangle>> dict, string name, Rectangle value)
    {
      if (dict.TryGetValue(name, out var list))
      {
        list.Add(value);
      }
      else
      {
        dict[name] = new List<Rectangle> { value };
      }
    }

    private void CreateImage(bool everything = false)
    {
      double previousOffset = -1;
      var hidden = new List<TextBlock>();

      if (everything)
      {
        foreach (var header in Headers)
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

          var dpiScale = UIElementUtil.GetDpi();
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
      foreach (var key in SpellRanges.Keys.OrderBy(key => key))
      {
        var spellRange = SpellRanges[key];
        if ((!CurrentHideSelfOnly || !SelfOnly.ContainsKey(key))
          && ((CurrentShowCasterAdps && ((spellRange.Adps & CASTER_ADPS) == CASTER_ADPS))
          || (CurrentShowMeleeAdps && ((spellRange.Adps & MELEE_ADPS) == MELEE_ADPS))
          || (TimelineType == 0 && ((spellRange.Adps & TANK_ADPS) == TANK_ADPS))
          || (TimelineType == 2 && ((spellRange.Adps & HEALING_ADPS) == HEALING_ADPS))) && !Ignore.ContainsKey(key))
        {
          var hPos = ROW_HEIGHT * row;
          AddGridRow(hPos, key);
          spellRange.Ranges.ForEach(range => content.Children.Add(CreateAdpsBlock(key, hPos, range.BeginSeconds, range.Duration, range.BlockBrush, Selected.Count)));
          row++;
        }
      }

      var finalHeight = ROW_HEIGHT * row;
      AddDivider(contentLabels, finalHeight, 20);
      AddDivider(content, finalHeight, DataManager.BUFFS_OFFSET);
      AddDivider(content, finalHeight, Length);

      for (var more = (int)(DataManager.BUFFS_OFFSET + 60); more < Length; more += 60)
      {
        AddDivider(content, finalHeight, more);
      }

      if (SpellRanges.TryGetValue("Player Death", out var range))
      {
        range.Ranges.ForEach(instance =>
        {
          // add 1 to center it a bit
          AddDivider(content, finalHeight, instance.BeginSeconds + 1, instance.BlockBrush);
        });
      }
    }

    private void AddGridRow(int hPos, string name)
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
        Ignore[name] = 1;
        Display();
      };

      var textBlock = new TextBlock
      {
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Text = name,
        Width = LABELS_WIDTH - 20,
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
        Margin = new Thickness(LABELS_WIDTH + left - offset, 0, 0, 0)
      };

      Headers.Add(textBlock);
      textBlock.SetResourceReference(TextBlock.FontSizeProperty, "EQContentSize");
      textBlock.SetResourceReference(TextBlock.ForegroundProperty, "ContentForeground");
      textBlock.SetValue(Panel.ZIndexProperty, 10);
      contentHeader.Children.Add(textBlock);
    }

    private void AddDivider(Grid target, int hPos, double left, string blockBrush = null)
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
      if (sender is ScrollViewer scroller)
      {
        if (scroller.ComputedHorizontalScrollBarVisibility != labelsScroller.ComputedHorizontalScrollBarVisibility)
        {
          labelsScroller.HorizontalScrollBarVisibility = scroller.ComputedHorizontalScrollBarVisibility == Visibility.Visible ? ScrollBarVisibility.Visible : ScrollBarVisibility.Hidden;
        }

        if (labelsScroller.VerticalOffset != e.VerticalOffset)
        {
          labelsScroller.ScrollToVerticalOffset(e.VerticalOffset);
        }

        if (headerScroller.HorizontalOffset != e.HorizontalOffset)
        {
          headerScroller.ScrollToHorizontalOffset(e.HorizontalOffset);
        }

        Headers.ForEach(header =>
        {
          if (header.Margin.Left + (header.ActualWidth / 2) < (e.HorizontalOffset + 10 + LABELS_WIDTH))
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
      if (contentScroller.VerticalOffset != e.VerticalOffset)
      {
        contentScroller.ScrollToVerticalOffset(e.VerticalOffset);
      }
    }

    private void OptionsChange(object sender, RoutedEventArgs e)
    {
      CurrentHideSelfOnly = hideSelfOnly?.IsChecked.Value == true;
      CurrentShowCasterAdps = showCasterAdps?.IsChecked.Value == true;
      CurrentShowMeleeAdps = showMeleeAdps?.IsChecked.Value == true;

      // when the UI is ready basically
      if (Selected != null)
      {
        Display();
      }
    }

    private Rectangle CreateAdpsBlock(string label, int hPos, int start, int length, string blockBrush, int count)
    {
      var offset = count == 1 ? 0 : blockBrush == BlockBrushes[0] ? -3 : 3;
      var zIndex = blockBrush == BlockBrushes[0] ? 11 : 10;

      var block = new Rectangle
      {
        StrokeThickness = 0.2,
        Height = ROW_HEIGHT / 3,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Opacity = 1.0,
        Width = length,
        Margin = new Thickness(start, hPos + (ROW_HEIGHT / 3) + offset, 0, 0),
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

    private Rectangle CreateRowBlock(int hPos)
    {
      var block = new Rectangle
      {
        Height = ROW_HEIGHT,
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
      var mod = TimelineType == 0 ? 3 : 1;

      if (spell.MaxHits > 0)
      {
        if (spell.MaxHits == 1)
        {
          duration = duration > 6 ? 6 / mod : duration;
        }
        else if (spell.MaxHits <= 3)
        {
          duration = duration > 12 ? 12 / mod : duration;
        }
        else if (spell.MaxHits == 4)
        {
          duration = duration > 18 ? 18 / mod : duration;
        }
        else
        {
          var guess = spell.MaxHits / 5 * 18 / mod;
          duration = duration > guess ? guess : duration;
        }
      }

      if (deathTimes != null && !spell.Name.StartsWith("Glyph of"))
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
    private bool disposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        MainActions.EventsThemeChanged -= EventsThemeChanged;
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

    private class SpellRange
    {
      public List<TimeRange> Ranges { get; } = new();
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
