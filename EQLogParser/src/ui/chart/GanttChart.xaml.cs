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
    private const double ADPS_OFFSET = 90;
    private const int ROW_HEIGHT = 20;
    private const int NAME_OFFSET = 200;

    public GanttChart(CombinedStats currentStats, PlayerStats selected, List<List<ActionBlock>> groups)
    {
      InitializeComponent();

      var spellRanges = new Dictionary<string, List<TimeRange>>();
      var startTime = groups.Min(group => group.First().BeginTime) - ADPS_OFFSET;
      var endTime = groups.Max(group => group.Last().BeginTime) + 1;
      var length = endTime - startTime;

      if (selected != null && !string.IsNullOrEmpty(selected.OrigName))
      {
        titleLabel.Content = selected.OrigName + "'s ADPS | " + currentStats?.ShortTitle;
      }

      DataManager.Instance.GetReceivedSpellsDuring(startTime, endTime).ForEach(group =>
      {
        foreach (var action in group.Actions.Where(action => action is ReceivedSpell spell && spell.Receiver == selected.OrigName && spell.SpellData.IsAdps))
        {
          if (action is ReceivedSpell spell)
          {
            if (!spellRanges.TryGetValue(spell.SpellData.Spell, out List<TimeRange> ranges))
            {
              ranges = new List<TimeRange>();
              var duration = getDuration(spell.SpellData, endTime, group.BeginTime);
              ranges.Add(new TimeRange() { BeginSeconds = (int)(group.BeginTime - startTime), Duration = duration });
              spellRanges[spell.SpellData.Spell] = ranges;
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

      var divider90 = new Rectangle()
      {
        Stroke = GridBrush,
        StrokeThickness = 0.3,
        Width = 0.3,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(NAME_OFFSET, ROW_HEIGHT, 0, 0)
      };

      var dividerStart = new Rectangle()
      {
        Stroke = GridBrush,
        StrokeThickness = 0.3,
        Width = 0.3,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(NAME_OFFSET + ADPS_OFFSET, ROW_HEIGHT, 0, 0)
      };

      var dividerEnd = new Rectangle()
      {
        Stroke = GridBrush,
        StrokeThickness = 0.3,
        Width = 0.3,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(NAME_OFFSET + length, ROW_HEIGHT, 0, 0)
      };

      content.Children.Add(divider90);
      content.Children.Add(dividerStart);
      content.Children.Add(dividerEnd);

      var preText = new TextBlock()
      {
        Foreground = GridBrush,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Text = "Buffs (-90s)",
        Width = 80,
        FontSize = 12,
        Margin = new Thickness(NAME_OFFSET - 22, 0, 0, 0)
      };

      var startText = new TextBlock()
      {
        Foreground = GridBrush,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Text = "Start (0s)",
        Width = 80,
        FontSize = 12,
        Margin = new Thickness(NAME_OFFSET + ADPS_OFFSET - 20, 0, 0, 0)
      };

      var endText = new TextBlock()
      {
        Foreground = GridBrush,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Text = "Finish (" + (length - ADPS_OFFSET) + "s)",
        Width = 80,
        FontSize = 12,
        Margin = new Thickness(NAME_OFFSET + length - 25, 0, 0, 0)
      };

      preText.SetValue(Panel.ZIndexProperty, 10);
      startText.SetValue(Panel.ZIndexProperty, 10);
      endText.SetValue(Panel.ZIndexProperty, 10);
      content.Children.Add(preText);
      content.Children.Add(startText);
      content.Children.Add(endText);

      int row = 1;
      foreach (var key in spellRanges.Keys.OrderBy(key => key))
      {
        int hPos = ROW_HEIGHT * row;
        divider90.Height = hPos;
        dividerStart.Height = hPos;
        dividerEnd.Height = hPos;
        AddGridRow(hPos, key);

        spellRanges[key].ForEach(range => AddAdpsBlock(hPos, range.BeginSeconds, range.Duration, key));
        row++;
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
          Fill = new SolidColorBrush(Color.FromRgb(38, 96, 183)),
          Width = length,
          Margin = new Thickness(start + NAME_OFFSET, hPos + (ROW_HEIGHT / 3), 0, 0),
        };

        block.SetValue(Panel.ZIndexProperty, 10);
        content.Children.Add(block);
      }
    }

    private void AddGridRow(int hPos, string name)
    {
      var row = new Rectangle()
      {
        Height = ROW_HEIGHT,
        Stroke = GridBrush,
        StrokeThickness = 0.2,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, hPos, 0, 0)
      };

      content.Children.Add(row);

      var textBlock = new TextBlock()
      {
        Foreground = GridBrush,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Text = name, // get short name
        Width = NAME_OFFSET,
        FontSize = 12,
        Margin = new Thickness(5, hPos + 2, 0, 0)
      };

      textBlock.SetValue(Panel.ZIndexProperty, 10);
      content.Children.Add(textBlock);
    }

    private int getDuration(SpellData spell, double endTime, double currentTime)
    {
      var duration = spell.MaxHits == 1 ? 6 : spell.Duration;
      duration = duration > 0 ? duration : 6;

      if (currentTime + duration > endTime)
      {
        duration = (int) (duration - (currentTime + duration - endTime));
      }

      return duration;
    }

    private class TimeRange
    {
      public int BeginSeconds { get; set; }
      public int Duration { get; set; }
    }
  }
}
