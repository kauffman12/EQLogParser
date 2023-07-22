using FontAwesome5;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace EQLogParser
{
  public partial class GanttChart : UserControl
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private static readonly List<string> BlockBrushes = new List<string>() { "EQMenuIconBrush", "EQWarnForegroundBrush" };

    private const int ROW_HEIGHT = 24;
    private const int LABELS_WIDTH = 190;
    private const ushort CASTER_ADPS = 1;
    private const ushort MELEE_ADPS = 2;
    private const ushort TANK_ADPS = 4;
    private const ushort HEALING_ADPS = 8;
    private const ushort ANY_ADPS = CASTER_ADPS + MELEE_ADPS + TANK_ADPS + HEALING_ADPS;
    private readonly string[] TYPES = new string[] { "Defensive Skills", "ADPS", "Healing Skills" };

    private readonly Dictionary<string, SpellRange> SpellRanges = new Dictionary<string, SpellRange>();
    private readonly List<Rectangle> Dividers = new List<Rectangle>();
    private readonly List<TextBlock> Headers = new List<TextBlock>();
    private readonly Dictionary<string, byte> SelfOnly = new Dictionary<string, byte>();
    private readonly Dictionary<string, byte> Ignore = new Dictionary<string, byte>();
    private double StartTime;
    private double EndTime;
    private double Length;
    private List<PlayerStats> Selected;
    private int TimelineType;

    private bool CurrentShowSelfOnly = false;
    private bool CurrentShowCasterAdps = true;
    private bool CurrentShowMeleeAdps = true;

    public GanttChart()
    {
      InitializeComponent();
    }

    // timelineType 0 = tanking, 1 = dps, 2 = healing
    internal void Init(CombinedStats currentStats, List<PlayerStats> selected, List<List<ActionBlock>> groups, int timelineType)
    {
      if (selected != null && selected.Count > 0 && timelineType >= 0 && timelineType <= 2)
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
                if (deathMap.TryGetValue(death.Killed, out HashSet<double> values))
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

        for (int i = 0; i < Selected.Count; i++)
        {
          var player = Selected[i].OrigName;
          var allSpells = new List<ActionBlock>();
          allSpells.AddRange(DataManager.Instance.GetCastsDuring(StartTime, EndTime));
          allSpells.AddRange(DataManager.Instance.GetReceivedSpellsDuring(StartTime, EndTime));

          if (deathMap.TryGetValue(Selected[i].OrigName, out HashSet<double> deathTimes))
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
              if (action is SpellCast cast && !cast.Interrupted && cast.Caster == player && cast.SpellData != null && cast.SpellData.Target == (int)SpellTarget.SELF &&
                cast.SpellData.Adps > 0 && (cast.SpellData.MaxHits > 0 || cast.SpellData.Duration <= 1800) && ClassFilter(cast.SpellData))
              {
                UpdateSpellRange(cast.SpellData, block.BeginTime, BlockBrushes[i], deathTimes);
              }
              else if (action is ReceivedSpell received && received.Receiver == player)
              {
                var spellData = received.SpellData;
                if (spellData == null && received.Ambiguity.Count > 0 && DataManager.ResolveSpellAmbiguity(received, out SpellData replaced))
                {
                  spellData = replaced;
                }

                if (spellData != null && spellData.Adps > 0 && (spellData.MaxHits > 0 || spellData.Duration <= 1800) && ClassFilter(spellData))
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
      showSelfOnly.IsEnabled = SpellRanges.Count > 0 && Selected?.Find(stats => stats.OrigName == ConfigUtil.PlayerName) != null;

      AddHeaderLabel(0, string.Format(CultureInfo.CurrentCulture, "Buffs (T-{0})", DataManager.BUFFS_OFFSET), 20);
      AddHeaderLabel(DataManager.BUFFS_OFFSET, DateUtil.FormatSimpleHMS(StartTime + DataManager.BUFFS_OFFSET), 10);

      int minutes = 1;
      for (int more = (int)(DataManager.BUFFS_OFFSET + 60); more < Length; more += 60)
      {
        AddHeaderLabel(more, minutes + "m", 0);
        minutes++;
      }

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
      if (!SpellRanges.TryGetValue(spellData.NameAbbrv, out SpellRange spellRange))
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

        var helper = new DictionaryUniqueListHelper<string, Rectangle>();
        var player1 = new Dictionary<string, List<Rectangle>>();
        var player2 = new Dictionary<string, List<Rectangle>>();

        foreach (var visual in content.Children)
        {
          if (visual is Rectangle rectangle)
          {
            if (rectangle.Tag is string adps && !string.IsNullOrEmpty(adps))
            {
              if (titleLabel1.Foreground.ToString() == rectangle?.Fill.ToString())
              {
                helper.AddToList(player1, adps, rectangle);
              }
              else
              {
                helper.AddToList(player2, adps, rectangle);
              }
            }
          }
        }

        var playerData = new List<List<object>>();
        labels.ForEach(label =>
        {
          if (player1.TryGetValue(label, out List<Rectangle> l1))
          {
            l1.ForEach(rectangle =>
            {
              playerData.Add(new List<object> { label, Selected[0].OrigName, StartTime + rectangle.Margin.Left, StartTime + rectangle.Margin.Left + rectangle.ActualWidth });
            });
          }

          if (player2.TryGetValue(label, out List<Rectangle> l2))
          {
            l2.ForEach(rectangle =>
            {
              playerData.Add(new List<object> { label, Selected[1].OrigName, StartTime + rectangle.Margin.Left, StartTime + rectangle.Margin.Left + rectangle.ActualWidth });
            });
          }
        });

        string title;
        if (string.IsNullOrEmpty(titleLabel2.Content as string))
        {
          title = titleLabel1.Content as string;
        }
        else
        {
          title = string.Format(CultureInfo.CurrentCulture, "{0} {1} {2}", titleLabel1.Content as string, titleLabel2.Content as string, titleLabel3.Content as string);
        }

        List<string> header = new List<string> { "Adps", "Player", "Start", "End" };
        Clipboard.SetDataObject(TextFormatUtils.BuildCsv(header, playerData, title));
      }
      catch (ExternalException ex)
      {
        LOG.Error(ex);
      }
    }

    private void CreateImage(object sender, RoutedEventArgs e) => CreateImage(false);
    private void CreateLargeImage(object sender, RoutedEventArgs e) => CreateImage(true);

    private void CreateImage(bool everything = false)
    {
      Task.Delay(250).ContinueWith(t =>
      {
        Dispatcher.InvokeAsync(() =>
        {
          titlePane.Measure(titlePane.RenderSize);
          contentHeader.Measure(contentHeader.RenderSize);
          contentLabels.Measure(contentLabels.RenderSize);
          content.Measure(content.RenderSize);

          var dpiScale = UIElementUtil.GetDpi();
          var titleHeight = titlePane.ActualHeight;
          var titleWidth = titlePane.DesiredSize.Width;

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
            if (contentScroller.ComputedHorizontalScrollBarVisibility == Visibility.Visible)
            {
              height -= 12; // might be a Syncfusion property for this
            }
          }

          rtb = new RenderTargetBitmap(width, height, dpiScale, dpiScale, PixelFormats.Default);

          var dv = new DrawingVisual();
          using (DrawingContext ctx = dv.RenderOpen())
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
        }, System.Windows.Threading.DispatcherPriority.Background);
      });
    }

    private void Display()
    {
      content.Children.Clear();
      contentLabels.Children.Clear();

      int row = 0;
      foreach (var key in SpellRanges.Keys.OrderBy(key => key))
      {
        var spellRange = SpellRanges[key];
        if ((CurrentShowSelfOnly || !SelfOnly.ContainsKey(key))
          && (CurrentShowCasterAdps && ((spellRange.Adps & CASTER_ADPS) == CASTER_ADPS)
          || CurrentShowMeleeAdps && ((spellRange.Adps & MELEE_ADPS) == MELEE_ADPS)
          || TimelineType == 0 && ((spellRange.Adps & TANK_ADPS) == TANK_ADPS)
          || TimelineType == 2 && ((spellRange.Adps & HEALING_ADPS) == HEALING_ADPS)) && !Ignore.ContainsKey(key))
        {
          int hPos = ROW_HEIGHT * row;
          AddGridRow(hPos, key);
          spellRange.Ranges.ForEach(range => content.Children.Add(CreateAdpsBlock(key, hPos, range.BeginSeconds, range.Duration, range.BlockBrush, Selected.Count)));
          row++;
        }
      }

      int finalHeight = ROW_HEIGHT * row;
      AddDivider(contentLabels, finalHeight, 20);
      AddDivider(content, finalHeight, DataManager.BUFFS_OFFSET);
      AddDivider(content, finalHeight, Length);

      for (int more = (int)(DataManager.BUFFS_OFFSET + 60); more < Length; more += 60)
      {
        AddDivider(content, finalHeight, more);
      }

      if (SpellRanges.TryGetValue("Player Death", out SpellRange range))
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
        Height = 12,
        Width = 12,
        Icon = EFontAwesomeIcon.Solid_Times,
        Margin = new Thickness(4, hPos + 6, 4, 0)
      };

      image.SetResourceReference(ImageAwesome.ForegroundProperty, "EQMenuIconBrush");
      image.PreviewMouseLeftButtonDown += (object sender, MouseButtonEventArgs e) =>
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
        FontSize = 12,
        Margin = new Thickness(24, hPos + 4, 0, 0)
      };

      textBlock.SetResourceReference(TextBlock.ForegroundProperty, "ContentForeground");
      textBlock.SetValue(Panel.ZIndexProperty, 10);
      image.SetValue(Panel.ZIndexProperty, 10);
      contentLabels.Children.Add(image);
      contentLabels.Children.Add(textBlock);
    }

    private void AddHeaderLabel(double left, string text, int offset)
    {
      var textBlock = new TextBlock()
      {
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Text = text,
        Width = 80,
        FontSize = 12,
        Margin = new Thickness(LABELS_WIDTH + left - offset, 0, 0, 0)
      };

      Headers.Add(textBlock);
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

      Dividers.Add(rectangle);

      var brushName = (blockBrush == null) ? "ContentForeground" : blockBrush;
      rectangle.SetResourceReference(Rectangle.StrokeProperty, brushName);
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
      CurrentShowSelfOnly = showSelfOnly?.IsChecked.Value == true;
      CurrentShowCasterAdps = showCasterAdps?.IsChecked.Value == true;
      CurrentShowMeleeAdps = showMeleeAdps?.IsChecked.Value == true;

      // when the UI is ready basically
      if (Selected != null)
      {
        Display();
      }
    }

    private static Rectangle CreateAdpsBlock(string label, int hPos, int start, int length, string blockBrush, int count)
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
        Effect = new DropShadowEffect() { ShadowDepth = 2, Direction = 240, BlurRadius = 0.5, Opacity = 0.5 },
        RadiusX = 2,
        RadiusY = 2,
        Tag = label
      };

      block.SetResourceReference(Rectangle.FillProperty, blockBrush);
      block.SetResourceReference(Rectangle.StrokeProperty, "ContentForeground");
      block.SetValue(Panel.ZIndexProperty, zIndex);
      return block;
    }

    private static Rectangle CreateRowBlock(int hPos)
    {
      var block = new Rectangle
      {
        Height = ROW_HEIGHT,
        StrokeThickness = 0.2,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, hPos, 0, 0)
      };

      block.SetResourceReference(Rectangle.StrokeProperty, "ContentForeground");
      return block;
    }

    private int GetDuration(SpellData spell, double endTime, double currentTime, HashSet<double> deathTimes = null)
    {
      int duration = spell.Duration > 0 ? spell.Duration : 6;

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
          var guess = (spell.MaxHits / 5) * 18 / mod;
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

    private class SpellRange
    {
      public List<TimeRange> Ranges { get; } = new List<TimeRange>();
      public ushort Adps { get; set; }
    }

    private class TimeRange
    {
      public int BeginSeconds { get; set; }
      public int Duration { get; set; }
      public string BlockBrush { get; set; }
    }
  }
}
