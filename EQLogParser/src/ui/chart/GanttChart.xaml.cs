using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace EQLogParser
{
  public partial class GanttChart : UserControl
  {
    private static readonly SolidColorBrush GridBrush = new SolidColorBrush(Colors.White);

    private static readonly List<Brush> BlockBrushes = new List<Brush>()
    {
      new SolidColorBrush(Color.FromRgb(73, 151, 217)),
      new SolidColorBrush(Color.FromRgb(205, 205, 205))
    };

    private const double BUFFS_OFFSET = 90;
    private const int ROW_HEIGHT = 24;
    private const int LABELS_WIDTH = 180;
    private const ushort CASTER_ADPS = 1;
    private const ushort MELEE_ADPS = 2;

    private Dictionary<string, SpellRange> SpellRanges = new Dictionary<string, SpellRange>();
    private List<Rectangle> Dividers = new List<Rectangle>();
    private List<TextBlock> Headers = new List<TextBlock>();
    private Dictionary<string, byte> SelfOnly = new Dictionary<string, byte>();
    private List<PlayerStats> Selected;
    private bool CurrentShowSelfOnly = false;
    private bool CurrentShowCasterAdps = true;
    private bool CurrentShowMeleeAdps = true;
    private double StartTime;
    private double EndTime;
    private double Length;

    public GanttChart(CombinedStats currentStats, List<PlayerStats> selected, List<List<ActionBlock>> groups)
    {
      InitializeComponent();

      Selected = selected;
      StartTime = groups.Min(group => group.First().BeginTime) - BUFFS_OFFSET;
      EndTime = groups.Max(group => group.Last().BeginTime) + 1;
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
        var spellClass = PlayerManager.Instance.GetPlayerClassEnum(player);

        DataManager.Instance.GetReceivedSpellsDuring(StartTime, EndTime).ForEach(group =>
        {
          foreach (var action in group.Actions.Where(action => action is ReceivedSpell spell && spell.Receiver == player &&
            spell.SpellData.Adps > 0 && (spell.SpellData.MaxHits > 0 || spell.SpellData.Duration <= 1800)))
          {
            var received = action as ReceivedSpell;
            var spellData = received.SpellData;

            if (DataManager.Instance.CheckForSpellAmbiguity(spellData, spellClass, out SpellData replaced))
            {
              spellData = replaced;
            }

            var spellName = spellData.NameAbbrv;

            if (string.IsNullOrEmpty(spellData.LandsOnOther))
            {
              SelfOnly[spellName] = 1;
            }

            if (!SpellRanges.TryGetValue(spellName, out SpellRange spellRange))
            {
              spellRange = new SpellRange() { Adps = spellData.Adps };
              var duration = GetDuration(spellData, EndTime, group.BeginTime);
              spellRange.Ranges.Add(new TimeRange() { BlockBrush = BlockBrushes[i], BeginSeconds = (int)(group.BeginTime - StartTime), Duration = duration });
              SpellRanges[spellName] = spellRange;
            }
            else
            {
              var last = spellRange.Ranges.LastOrDefault(range => range.BlockBrush == BlockBrushes[i]);
              var offsetSeconds = (int)(group.BeginTime - StartTime);
              if (last != null && offsetSeconds >= last.BeginSeconds && offsetSeconds <= (last.BeginSeconds + last.Duration))
              {
                last.Duration = GetDuration(spellData, EndTime, group.BeginTime) + (offsetSeconds - last.BeginSeconds);
              }
              else
              {
                var duration = GetDuration(spellData, EndTime, group.BeginTime);
                spellRange.Ranges.Add(new TimeRange() { BlockBrush = BlockBrushes[i], BeginSeconds = (int)(group.BeginTime - StartTime), Duration = duration });
              }
            }
          }
        });
      }

      showSelfOnly.IsEnabled = showCasterAdps.IsEnabled = showMeleeAdps.IsEnabled = SpellRanges.Count > 0;

      addHeaderLabel(0, "Buffs (T-90)", 20);
      addHeaderLabel(BUFFS_OFFSET, DateUtil.FormatSimpleTime(StartTime), 10);

      int minutes = 1;
      for (int more = (int)(BUFFS_OFFSET + 60); more < Length; more += 60)
      {
        addHeaderLabel(more, minutes + "m", 0);
        minutes++;
      }

      Display();
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
          spellRange.Ranges.ForEach(range => content.Children.Add(CreateAdpsBlock(hPos, range.BeginSeconds, range.Duration, range.BlockBrush, Selected.Count)));
          row++;
        }
      }

      int finalHeight = ROW_HEIGHT * row;
      AddDivider(finalHeight, BUFFS_OFFSET);
      AddDivider(finalHeight, Length);

      for (int more = (int)(BUFFS_OFFSET + 60); more < Length; more += 60)
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

    private void addHeaderLabel(double left, string text, int offset)
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

    private Rectangle CreateAdpsBlock(int hPos, int start, int length, Brush blockBrush, int count)
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
        RadiusY = 2
      };

      block.SetValue(Panel.ZIndexProperty, zIndex);
      return block;
    }

    private Rectangle CreateRowBlock(int hPos)
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

    private int GetDuration(SpellData spell, double endTime, double currentTime)
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
