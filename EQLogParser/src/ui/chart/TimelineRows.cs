using System.Collections.Generic;

namespace EQLogParser
{
  /// <summary>
  /// What the rows dropdown does to a timeline's row order, kept apart from the control so the rules are
  /// testable without a window. The rule that matters: a row ticked back on lands at the end of the list and
  /// gets dragged home from there — it never claims a remembered slot, because nothing remembers one. A
  /// layout's SpellOrder is only ever what was switched on, so this is also why "missing from the layout"
  /// means "off but available", not "gone".
  /// </summary>
  internal static class TimelineRows
  {
    /// <summary>
    /// Applies each row's tick to <paramref name="order"/> in place and reports whether anything moved.
    /// Rows that stay on keep the position they were put in, including by dragging.
    /// </summary>
    internal static bool Apply(List<string> order, IEnumerable<ComboBoxItemDetails> rows)
    {
      var changed = false;

      foreach (var row in rows)
      {
        var isOn = order.Contains(row.Text);

        if (row.IsChecked && !isOn)
        {
          order.Add(row.Text);
          changed = true;
        }
        else if (!row.IsChecked && isOn)
        {
          order.Remove(row.Text);
          changed = true;
        }
      }

      return changed;
    }
  }
}
