using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace EQLogParser
{
  public partial class GanttChart : UserControl
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private static readonly SolidColorBrush GridBrush = new SolidColorBrush(Colors.White);
    private static readonly List<Brush> BlockBrushes = new List<Brush>()
    {
      new SolidColorBrush(Color.FromRgb(73, 151, 217)),
      new SolidColorBrush(Color.FromRgb(205, 205, 205))
    };

    private const int ROW_HEIGHT = 24;
    private const int LABELS_WIDTH = 180;
    private const ushort CASTER_ADPS = 1;
    private const ushort MELEE_ADPS = 2;

    private readonly Dictionary<string, SpellRange> SpellRanges = new Dictionary<string, SpellRange>();
    private readonly List<Rectangle> Dividers = new List<Rectangle>();
    private readonly List<TextBlock> Headers = new List<TextBlock>();
    private readonly Dictionary<string, byte> SelfOnly = new Dictionary<string, byte>();
    private readonly Dictionary<string, byte> SelfOnlyOverride = new Dictionary<string, byte>();
    private readonly double StartTime;
    private readonly double EndTime;
    private readonly double Length;
    private readonly List<PlayerStats> Selected;

    private bool CurrentShowSelfOnly = false;
    private bool CurrentShowCasterAdps = true;
    private bool CurrentShowMeleeAdps = true;

    internal GanttChart(CombinedStats currentStats, List<PlayerStats> selected, List<List<ActionBlock>> groups)
    {
      InitializeComponent();

      if (selected != null && selected.Count > 0)
      {
        Selected = selected;
        StartTime = groups.Min(block => block.First().BeginTime) - DataManager.BUFFS_OFFSET;
        EndTime = groups.Max(block => block.Last().BeginTime) + 1;
        Length = EndTime - StartTime;

        switch (Selected.Count)
        {
          case 1:
            titleLabel1.Content = Selected[0].OrigName + "'s ADPS | " + currentStats?.ShortTitle;
            break;
          case 2:
            titleLabel1.Content = Selected[0].OrigName + " vs ";
            titleLabel2.Content = Selected[1].OrigName + "'s ";
            titleLabel3.Content = "ADPS | " + currentStats?.ShortTitle;
            break;
        }

        for (int i = 0; i < Selected.Count; i++)
        {
          var player = Selected[i].OrigName;
          var allSpells = new List<ActionBlock>();
          allSpells.AddRange(DataManager.Instance.GetCastsDuring(StartTime, EndTime));
          allSpells.AddRange(DataManager.Instance.GetReceivedSpellsDuring(StartTime, EndTime));

          foreach (var block in allSpells.OrderBy(block => block.BeginTime).ThenBy(block => (block.Actions.Count > 0 && block.Actions[0] is ReceivedSpell) ? 1 : -1))
          {
            foreach (var action in block.Actions)
            {
              if (action is SpellCast cast && cast.Caster == player && cast.SpellData != null && cast.SpellData.Target == (int)SpellTarget.SELF &&
                cast.SpellData.Adps > 0 && (cast.SpellData.MaxHits > 0 || cast.SpellData.Duration <= 1800))
              {
                if (string.IsNullOrEmpty(cast.SpellData.LandsOnOther))
                {
                  SelfOnlyOverride[cast.SpellData.NameAbbrv] = 1;

                  if (SelfOnly.ContainsKey(cast.SpellData.NameAbbrv))
                  {
                    SelfOnly.Remove(cast.SpellData.NameAbbrv);
                  }
                }

                UpdateSpellRange(cast.SpellData, block.BeginTime, BlockBrushes[i]);
              }
              else if (action is ReceivedSpell received && received.Receiver == player)
              {
                var spellData = received.SpellData;

                if (spellData == null && received.Ambiguity.Count > 0 && DataManager.ResolveSpellAmbiguity(received, out SpellData replaced))
                {
                  spellData = replaced;
                }

                if (spellData != null && spellData.Adps > 0 && (spellData.MaxHits > 0 || spellData.Duration <= 1800))
                {
                  if (string.IsNullOrEmpty(spellData.LandsOnOther) && !SelfOnlyOverride.ContainsKey(spellData.NameAbbrv))
                  {
                    SelfOnly[spellData.NameAbbrv] = 1;
                  }

                  UpdateSpellRange(spellData, block.BeginTime, BlockBrushes[i]);
                }
              }
            }
          }
        }
      }

      showCasterAdps.IsEnabled = showMeleeAdps.IsEnabled = SpellRanges.Count > 0;
      showSelfOnly.IsEnabled = SpellRanges.Count > 0 && Selected?.Find(stats => stats.OrigName == ConfigUtil.PlayerName) != null;

      AddHeaderLabel(0, string.Format(CultureInfo.CurrentCulture, "Buffs (T-{0})", DataManager.BUFFS_OFFSET), 20);
      AddHeaderLabel(DataManager.BUFFS_OFFSET, DateUtil.FormatSimpleTime(StartTime + DataManager.BUFFS_OFFSET), 10);

      int minutes = 1;
      for (int more = (int)(DataManager.BUFFS_OFFSET + 60); more < Length; more += 60)
      {
        AddHeaderLabel(more, minutes + "m", 0);
        minutes++;
      }

      Display();
    }

    private void UpdateSpellRange(SpellData spellData, double beginTime, Brush brush)
    {
      if (!SpellRanges.TryGetValue(spellData.NameAbbrv, out SpellRange spellRange))
      {
        spellRange = new SpellRange() { Adps = spellData.Adps };
        var duration = GetDuration(spellData, EndTime, beginTime);
        spellRange.Ranges.Add(new TimeRange() { BlockBrush = brush, BeginSeconds = (int)(beginTime - StartTime), Duration = duration });
        SpellRanges[spellData.NameAbbrv] = spellRange;
      }
      else
      {
        var last = spellRange.Ranges.LastOrDefault(range => range.BlockBrush == brush);
        var offsetSeconds = (int)(beginTime - StartTime);
        if (last != null && offsetSeconds >= last.BeginSeconds && offsetSeconds <= (last.BeginSeconds + last.Duration))
        {
          last.Duration = GetDuration(spellData, EndTime, beginTime) + (offsetSeconds - last.BeginSeconds);
        }
        else
        {
          var duration = GetDuration(spellData, EndTime, beginTime);
          spellRange.Ranges.Add(new TimeRange() { BlockBrush = brush, BeginSeconds = (int)(beginTime - StartTime), Duration = duration });
        }
      }
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e)
    {
      try
      {
        List<string> labels = new List<string>();
        foreach (var visual in contentLabels.Children)
        {
          if (visual is TextBlock block)
          {
            labels.Add(block.Text);
          }
        }

        DictionaryListHelper<string, Rectangle> helper = new DictionaryListHelper<string, Rectangle>();
        Dictionary<string, List<Rectangle>> player1 = new Dictionary<string, List<Rectangle>>();
        Dictionary<string, List<Rectangle>> player2 = new Dictionary<string, List<Rectangle>>();

        foreach (var visual in content.Children)
        {
          if (visual is Rectangle rectangle)
          {
            if (rectangle.Tag is string adps && !string.IsNullOrEmpty(adps))
            {
              if (BlockBrushes[0] == rectangle.Fill)
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

        List<List<object>> playerData = new List<List<object>>();
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

    private void CreateImageClick(object sender, RoutedEventArgs e)
    {
      Task.Delay(100).ContinueWith((task) => Dispatcher.InvokeAsync(() =>
      {
        int paddingTop = 4;
        int padding = 8;
        
        var titleHeight = titleLabel1.ActualHeight - titleLabel1.Padding.Top * 2;
        var height = (int)content.ActualHeight + (int)contentHeader.ActualHeight + (int)titleHeight;
        var width = (int)contentLabels.ActualWidth + (int)content.ActualWidth;

        var dpiScale = VisualTreeHelper.GetDpi(content);
        RenderTargetBitmap rtb = new RenderTargetBitmap(width, height + (int)titleHeight + padding, dpiScale.PixelsPerInchX, dpiScale.PixelsPerInchY, PixelFormats.Pbgra32);

        DrawingVisual dv = new DrawingVisual();
        using (DrawingContext ctx = dv.RenderOpen())
        {
          var grayBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2d2d30"));
          var titleBrush = new VisualBrush(titlePane);
          var headerBrush = new VisualBrush(contentHeader);
          var labelsBrush = new VisualBrush(contentLabels);
          var contentBrush = new VisualBrush(content);
          ctx.DrawRectangle(grayBrush, null, new Rect(new Point(0, 0), new Size(width, height)));
          ctx.DrawRectangle(titleBrush, null, new Rect(new Point(6, paddingTop), new Size(titlePane.ActualWidth, titleHeight))); // add padding that's normally on the label
          ctx.DrawRectangle(headerBrush, null, new Rect(new Point(0, titleHeight + padding), new Size(width, contentHeader.ActualHeight)));
          ctx.DrawRectangle(labelsBrush, null, new Rect(new Point(0, contentHeader.ActualHeight + titleHeight + padding), new Size(contentLabels.ActualWidth, height - contentHeader.ActualHeight)));
          ctx.DrawRectangle(contentBrush, null, new Rect(new Point(contentLabels.ActualWidth, contentHeader.ActualHeight + titleHeight + padding), new Size(content.ActualWidth, height - contentHeader.ActualHeight)));
        }

        rtb.Render(dv);
        Clipboard.SetImage(rtb);
      }
      ), TaskScheduler.Default);
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
          || CurrentShowMeleeAdps && ((spellRange.Adps & MELEE_ADPS) == MELEE_ADPS)))
        {
          int hPos = ROW_HEIGHT * row;
          AddGridRow(hPos, key);
          spellRange.Ranges.ForEach(range => content.Children.Add(CreateAdpsBlock(key, hPos, range.BeginSeconds, range.Duration, range.BlockBrush, Selected.Count)));
          row++;
        }
      }

      int finalHeight = ROW_HEIGHT * row;
      AddDivider(finalHeight, DataManager.BUFFS_OFFSET);
      AddDivider(finalHeight, Length);

      for (int more = (int)(DataManager.BUFFS_OFFSET + 60); more < Length; more += 60)
      {
        AddDivider(finalHeight, more);
      }
    }

    private void AddGridRow(int hPos, string name)
    {
      var labelsRow = CreateRowBlock(hPos);
      var contentRow = CreateRowBlock(hPos);

      contentLabels.Children.Add(labelsRow);
      content.Children.Add(contentRow);

      var textBlock = new TextBlock()
      {
        Foreground = GridBrush,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Text = name,
        Width = LABELS_WIDTH,
        FontSize = 12,
        Margin = new Thickness(5, hPos + 4, 0, 0)
      };

      textBlock.SetValue(Panel.ZIndexProperty, 10);
      contentLabels.Children.Add(textBlock);
    }

    private void AddHeaderLabel(double left, string text, int offset)
    {
      var textBlock = new TextBlock()
      {
        Foreground = GridBrush,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Text = text,
        Width = 80,
        FontSize = 12,
        Margin = new Thickness(LABELS_WIDTH + left - offset, 0, 0, 0)
      };

      Headers.Add(textBlock);
      textBlock.SetValue(Panel.ZIndexProperty, 10);
      contentHeader.Children.Add(textBlock);
    }

    private void AddDivider(int hPos, double left)
    {
      var rectangle = new Rectangle()
      {
        Stroke = GridBrush,
        StrokeThickness = 0.3,
        Height = hPos,
        Width = 0.3,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(left, 0, 0, 0)
      };

      Dividers.Add(rectangle);
      content.Children.Add(rectangle);
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

    private static Rectangle CreateAdpsBlock(string label, int hPos, int start, int length, Brush blockBrush, int count)
    {
      var offset = count == 1 ? 0 : blockBrush == BlockBrushes[0] ? -3 : 3;
      var zIndex = blockBrush == BlockBrushes[0] ? 11 : 10;

      var block = new Rectangle()
      {
        Stroke = GridBrush,
        StrokeThickness = 0.2,
        Height = ROW_HEIGHT / 3,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Fill = blockBrush,
        Opacity = 1.0,
        Width = length,
        Margin = new Thickness(start, hPos + (ROW_HEIGHT / 3) + offset, 0, 0),
        Effect = new DropShadowEffect() { ShadowDepth = 2, Direction = 240, BlurRadius = 0.5, Opacity = 0.5 },
        RadiusX = 2,
        RadiusY = 2,
        Tag = label
      };

      block.SetValue(Panel.ZIndexProperty, zIndex);
      return block;
    }

    private static Rectangle CreateRowBlock(int hPos)
    {
      return new Rectangle()
      {
        Height = ROW_HEIGHT,
        Stroke = GridBrush,
        StrokeThickness = 0.2,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, hPos, 0, 0)
      };
    }

    private static int GetDuration(SpellData spell, double endTime, double currentTime)
    {
      int duration = spell.Duration > 0 ? spell.Duration : 6;

      if (spell.MaxHits > 0)
      {
        if (spell.MaxHits == 1)
        {
          duration = duration > 6 ? 6 : duration;
        }
        else if (spell.MaxHits <= 3)
        {
          duration = duration > 12 ? 12 : duration;
        }
        else if (spell.MaxHits == 4)
        {
          duration = duration > 18 ? 18 : duration;
        }
        else
        {
          var guess = (spell.MaxHits / 5) * 18;
          duration = duration > guess ? guess : duration;
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
      public List<TimeRange> Ranges { get; set; } = new List<TimeRange>();
      public ushort Adps { get; set; }
    }

    private class TimeRange
    {
      public int BeginSeconds { get; set; }
      public int Duration { get; set; }
      public Brush BlockBrush { get; set; }
    }
  }
}
