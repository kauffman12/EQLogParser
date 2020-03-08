using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace EQLogParser
{
  public partial class GanttChart : UserControl
  {
    private static readonly SolidColorBrush GridBrush = new SolidColorBrush(Colors.White);
    private static readonly SolidColorBrush BlockColorBrush = new SolidColorBrush(Color.FromRgb(94, 137, 202));

    private const double BUFFS_OFFSET = 90;
    private const int ROW_HEIGHT = 20;
    private const int LABELS_WIDTH = 180;

    private List<Rectangle> Dividers = new List<Rectangle>();
    private List<TextBlock> Headers = new List<TextBlock>();

    public GanttChart(CombinedStats currentStats, PlayerStats selected, List<List<ActionBlock>> groups)
    {
      InitializeComponent();

      var spellRanges = new Dictionary<string, List<TimeRange>>();
      var startTime = groups.Min(group => group.First().BeginTime) - BUFFS_OFFSET;
      var endTime = groups.Max(group => group.Last().BeginTime) + 1;
      var length = endTime - startTime;

      if (selected != null && !string.IsNullOrEmpty(selected.OrigName))
      {
        titleLabel.Content = selected.OrigName + "'s ADPS | " + currentStats?.ShortTitle;
      }

      DataManager.Instance.GetReceivedSpellsDuring(startTime, endTime).ForEach(group =>
      {
        foreach (var action in group.Actions.Where(action => action is ReceivedSpell spell && spell.Receiver == selected.OrigName && 
          spell.SpellData.IsAdps && (spell.SpellData.MaxHits > 0 || spell.SpellData.Duration <= 1800)))
        {
          if (action is ReceivedSpell spell)
          {
            var spellName = spell.SpellData.NameAbbrv;

            if (!spellRanges.TryGetValue(spellName, out List<TimeRange> ranges))
            {
              ranges = new List<TimeRange>();
              var duration = getDuration(spell.SpellData, endTime, group.BeginTime);
              ranges.Add(new TimeRange() { BeginSeconds = (int)(group.BeginTime - startTime), Duration = duration });
              spellRanges[spellName] = ranges;
            }
            else
            {
              var last = ranges.Last();
              var offsetSeconds = (int) (group.BeginTime - startTime);
              if (offsetSeconds >= last.BeginSeconds && offsetSeconds <= (last.BeginSeconds + last.Duration))
              {
                last.Duration = getDuration(spell.SpellData, endTime, group.BeginTime) + (offsetSeconds - last.BeginSeconds);
              }
              else
              {
                var duration = getDuration(spell.SpellData, endTime, group.BeginTime);
                ranges.Add(new TimeRange() { BeginSeconds = (int)(group.BeginTime - startTime), Duration = duration });
              }
            }
          }
        }
      });

      int row = 0;
      foreach (var key in spellRanges.Keys.OrderBy(key => key))
      {
        int hPos = ROW_HEIGHT * row;
        AddGridRow(hPos, key);
        spellRanges[key].ForEach(range => AddAdpsBlock(hPos, range.BeginSeconds, range.Duration, key));
        row++;
      }

      int finalHeight = ROW_HEIGHT * row;
      createHeaderLabel(0, "Buffs (T-90)", 20);
      createHeaderLabel(BUFFS_OFFSET, DateUtil.FormatSimpleTime(startTime), 10);
      createDivider(finalHeight, BUFFS_OFFSET);
      createDivider(finalHeight, length);

      int minutes = 1;
      for (int more = (int) (BUFFS_OFFSET + 60); more < length; more += 60)
      {
        createHeaderLabel(more, minutes + "m", 0);
        createDivider(finalHeight, more);
        minutes++;
      }
    }

    private void AddAdpsBlock(int hPos, int start, int length, string name)
    {
      if (length > 0)
      {
        var block = new Rectangle()
        {
          Stroke = GridBrush,
          StrokeThickness = 0.2,
          Height = ROW_HEIGHT / 2.5,
          HorizontalAlignment = HorizontalAlignment.Left,
          VerticalAlignment = VerticalAlignment.Top,
          Fill = BlockColorBrush,
          Width = length,
          Margin = new Thickness(start, hPos + (ROW_HEIGHT / 3), 0, 0),
          RadiusX = 2,
          RadiusY = 2
        };

        block.SetValue(Panel.ZIndexProperty, 10);
        content.Children.Add(block);
      }
    }

    private void AddGridRow(int hPos, string name)
    {
      var labelsRow = createRowBlock(hPos);
      var contentRow = createRowBlock(hPos);

      contentLabels.Children.Add(labelsRow);
      content.Children.Add(contentRow);

      var textBlock = new TextBlock()
      {
        Foreground = GridBrush,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Text = name, // get short name
        Width = LABELS_WIDTH,
        FontSize = 12,
        Margin = new Thickness(5, hPos + 2, 0, 0)
      };

      textBlock.SetValue(Panel.ZIndexProperty, 10);
      contentLabels.Children.Add(textBlock);
    }

    private void createHeaderLabel(double left, string text, int offset)
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

    private void createDivider(int hPos, double left)
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

    private Rectangle createRowBlock(int hPos)
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

    private int getDuration(SpellData spell, double endTime, double currentTime)
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
        duration = (int) (duration - (currentTime + duration - endTime));
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

    private class TimeRange
    {
      public int BeginSeconds { get; set; }
      public int Duration { get; set; }
    }
  }
}
