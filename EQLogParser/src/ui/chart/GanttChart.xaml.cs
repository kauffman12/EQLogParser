using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace EQLogParser
{
  public partial class GanttChart : UserControl
  {

    private readonly Dictionary<string, byte> ValidAdps = new Dictionary<string, byte>()
    {
      { "Auspice of the Hunter", 1 },
      { "Fierce Eye", 1 },
      { "Illusions of Grandeur", 1 },
      { "Spirit of Vesagran", 1 },
      { "Third Spire of Enchantment", 1 }
    };

    public GanttChart(List<List<ActionBlock>> groups)
    {
      InitializeComponent();
      var now = DateTime.Now;

      var received = DataManager.Instance.GetReceivedSpellsDuring(groups.First().First().BeginTime, groups.Last().Last().BeginTime);

      var blocks = new List<ActionBlock>();
      received.ForEach(group =>
      {
        var block = new ActionBlock() { BeginTime = group.BeginTime };
        foreach (var action in group.Actions.Where(action => action is ReceivedSpell spell && spell.Receiver == "Kazint" && ValidAdps.ContainsKey(spell.SpellData.SpellAbbrv)))
        {
          block.Actions.Add(action);
        }

        if (block.Actions.Count > 0)
        {
          blocks.Add(block);
        }
      });

    }

    private void AddGridRow()
    {
      var row = new Rectangle()
      {
        Height = 40,
        Stroke = new SolidColorBrush(Colors.White),
        StrokeThickness = 0.5
      };

      content.Children.Add(row);
    }
  }
}
