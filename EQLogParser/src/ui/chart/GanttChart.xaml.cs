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
    private const double ADPS_OFFSET = 15;
    private const int RowHeight = 30;
    private const int TimeLines = 50;
    private int Row = 0;

    private readonly Dictionary<string, string> ValidAdps = new Dictionary<string, string>()
    {
      { "Arcane Fury", "Arcane Fury" },
      { "Auspice of the Hunter", "Auspice" },
      { "Beguiler's Synergy", "S" },
      { "Chromatic Haze", "C" },
      { "Gift of Chromatic Haze", "G" },
      { "Glyph of Destruction", "Glyph of Destruction" },
      { "Fierce Eye", "Fierce Eye" },
      { "Group Spirit of the Black Wolf", "Black Wolf" },
      { "Second Spire of Arcanum", "Wiz 2nd Spire" },
      { "Spirit of the Black Wolf", "Block Wolf" },
      { "Illusions of Grandeur", "Illusions of Grandeur" },
      { "Spirit of Vesagran", "Spirit of Vesagran" },
      { "Third Spire of Enchantment", "Enc 3rd Spire" },
      { "Focused Paragon of Spirit", "Paragon" },
      { "Paragon of Spirit", "Paragon" },
      { "Quick Time", "Quick Time" }
    };

    private readonly List<Rectangle> TimeLineList = new List<Rectangle>();
    private readonly List<AdpsEntry> AdpsEntries = new List<AdpsEntry>();

    public GanttChart(CombinedStats currentStats, PlayerStats selected, List<List<ActionBlock>> groups)
    {
      InitializeComponent();
      titleLabel.Content = currentStats?.ShortTitle;

      var startTime = groups.First().First().BeginTime - ADPS_OFFSET;
      var received = DataManager.Instance.GetReceivedSpellsDuring(startTime, groups.Last().Last().BeginTime);

      var blocks = new List<ActionBlock>();
      received.ForEach(group =>
      {
        var block = new ActionBlock() { BeginTime = group.BeginTime };
        foreach (var action in group.Actions.Where(action => action is ReceivedSpell spell && spell.Receiver == selected.OrigName && ValidAdps.ContainsKey(spell.SpellData.SpellAbbrv)))
        {
          block.Actions.Add(action);
        }

        if (block.Actions.Count > 0)
        {
          blocks.Add(block);
        }
      });

      AddTimeLines();

      blocks.ForEach(block =>
      {
        block.Actions.ForEach(action =>
        {
          if (action is ReceivedSpell spell)
          {
            int start = (int)(block.BeginTime - startTime);
            int row = FindAvailableRow(start, spell.SpellData.Duration);
            AddAdpsBlock(row, start, spell.SpellData.Duration, spell.SpellData.SpellAbbrv);
          }
        });
      });
    }

    private int FindAvailableRow(int start, int duration)
    {
      var overlap = AdpsEntries.FindAll(entry =>
      {
        int end = start + duration;
        double entryStart = entry.Block.Margin.Left;
        double entryEnd = entryStart + entry.Block.Width;
        return (start >= entryStart && start <= entryEnd) || (end >= entryStart && end <= entryEnd);
      }).OrderBy(entry => entry.Row).ToList();

      int row;
      for (row = 0; row < overlap.Count; row++)
      {
        if (row != overlap[row].Row)
        {
          break;
        }
      }

      return row;
    }

    private void AddGridRow()
    {
      int hPos = RowHeight * Row + Row;

      var row = new Rectangle()
      {
        Height = RowHeight,
        Stroke = GridBrush,
        StrokeThickness = 0.5,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, hPos, 0, 0)
      };

      content.Children.Add(row);
      Row++;

      UpdateTimeLinesHeight();
    }

    private void AddTimeLines()
    {
      for (int i = 0; i < TimeLines; i++)
      {
        var col = i == 0 ? ADPS_OFFSET : ADPS_OFFSET + 60 * i;

        var line = new Rectangle()
        {
          Stroke = GridBrush,
          StrokeThickness = 0.5,
          Width = 0.5,
          HorizontalAlignment = HorizontalAlignment.Left,
          VerticalAlignment = VerticalAlignment.Top,
          Margin = new Thickness(col, 0, 0, 0)
        };

        TimeLineList.Add(line);
        content.Children.Add(line);
      }
    }

    private void AddAdpsBlock(int row, int start, int length, string name)
    {
      if (length > 0)
      {
        int hPos = RowHeight * row + row;

        while (Row <= row)
        {
          AddGridRow();
        }

        var block = new Rectangle()
        {
          Stroke = GridBrush,
          StrokeThickness = 0.5,
          Height = RowHeight,
          HorizontalAlignment = HorizontalAlignment.Left,
          VerticalAlignment = VerticalAlignment.Top,
          Fill = new SolidColorBrush(Color.FromRgb(38, 96, 183)),
          Width = length,
          Margin = new Thickness(start, hPos, 0, 0),
        };

        block.SetValue(Panel.ZIndexProperty, 10);

        var textStart = length > 18 ? start + 5 : start + 2;
        var textWidth = length > 18 ? length - 5 : length;
        var textBlock = new TextBlock()
        {
          Foreground = GridBrush,
          HorizontalAlignment = HorizontalAlignment.Left,
          VerticalAlignment = VerticalAlignment.Top,
          Text = ValidAdps[name], // get short name
          Width = textWidth,
          FontSize = 12,
          Margin = new Thickness(textStart, hPos + 10, 0, 0)
        };

        textBlock.SetValue(Panel.ZIndexProperty, 10);

        content.Children.Add(block);
        content.Children.Add(textBlock);
        AdpsEntries.Add(new AdpsEntry() { Block = block, Row = row });
      }
    }

    private void UpdateTimeLinesHeight()
    {
      TimeLineList.ForEach(line => line.Height = Row * RowHeight + Row);
    }

    private class AdpsEntry
    {
      public Rectangle Block { get; set; }
      public int Row { get; set; }
    }
  }
}
