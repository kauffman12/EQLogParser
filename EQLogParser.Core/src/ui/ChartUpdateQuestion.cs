/*
 * Annotations only (the project builds with Nullable=disable): a chart event's selection is legitimately empty or absent, and the decision
 * this file makes has to say so without a warning per line.
 */
#nullable enable annotations

using System.Collections.Generic;

namespace EQLogParser;

/*
 * Whether a chart UPDATE is a QUESTION IT ALREADY ANSWERED.
 *
 * The door this closes was measured on a field run over a whole night's select-all: two `DamageChart UPDATE` passes over 4,660,915 records
 * (the walk being 97 % of each) drawing the identical answer — 5 lines, 36,163 points — 1,589 ms apart in wall time and with no stats build
 * between them. The summary panes fire `"UPDATE"` for their own interactions because that is the event that fills a chart; what they mean by
 * the second one is "redraw", not "aggregate four million records again".
 *
 * The comparison is deliberately a **whole question**, and it lives here rather than in `LineChart` for the reason every other rule in this
 * folder lives here: the decision has to be testable without a window, and a wrong answer is a chart that quietly shows yesterday's drawing.
 * So every input the pass reads is a term:
 *
 *   - **the data generation** (`DataPointEvent.DataGeneration`) — WHICH records the iterator walks, stamped by the build that wrote them;
 *   - **the selection and group selection** — which rows the plot is about;
 *   - **the view option and top count** — how to draw them, including which records the view option's own `ShouldSkipRecord` filters out.
 *
 * A missing term is a stale chart, so the list is not a hint list: `DataGeneration` alone would let a pane's row selection be ignored (wrong
 * drawing), and selection alone would let new records be ignored (stale drawing). Adding a term can only ever cost a skipped skip.
 *
 * Names are length-prefixed rather than joined with a separator: player and group names come out of a log file, and `A|B` followed by `C` must
 * not read as the same question as `A` followed by `B|C`.
 */
internal static class ChartUpdateQuestion
{
  /*
   * The key for one UPDATE, or null when it cannot be keyed at all — an unstamped event (generation below the first build's number) means
   * unknown content and must
   * walk. Callers compare this against the key of the pass they last applied via IsRepeat.
   */
  internal static string? KeyOf(DataPointEvent e, string? viewOption, int topCount)
  {
    // Anything below the first real build's number is "not a build": builders stamp a trace sequence that starts at 1, so 0 and every negative
    // mean nothing produced these records. Tighter than the -1 sentinel alone, and it costs nothing -- a key that cannot exist just walks.
    if (e is null || e.DataGeneration < 1)
      return null;

    // Length-prefixing each term keeps a name containing a separator from fusing two terms into one.
    var sb = new System.Text.StringBuilder(64);
    // Invariant: the key is compared Ordinal, and a culture that prints different digits would make two identical questions look different.
    Append(sb, e.DataGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Append(sb, viewOption);
    Append(sb, topCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Append(sb, "p" + (e.Selected?.Count ?? 0));

    if (e.Selected is { Count: > 0 })
    {
      foreach (var p in e.Selected)
      {
        Append(sb, p?.Name);
      }
    }

    Append(sb, "g" + (e.SelectedGroups?.Count ?? 0));

    if (e.SelectedGroups is { Count: > 0 })
    {
      foreach (var g in e.SelectedGroups)
      {
        Append(sb, g?.Name);
      }
    }

    return sb.ToString();
  }

  /*
   * True when this is the question the chart already drew. Both halves matter: a key match against NOTHING drawn (`hasAppliedData` false) is
   * not a repeat, and a null key is never a repeat — unknown content always walks, which is why forgetting to stamp costs milliseconds rather
   * than showing a stale chart.
   */
  internal static bool IsRepeat(string? key, string? lastAppliedKey, bool hasAppliedData)
    => key is not null && hasAppliedData && string.Equals(key, lastAppliedKey, System.StringComparison.Ordinal);

  private static void Append(System.Text.StringBuilder sb, string? term)
  {
    term ??= "";
    sb.Append(term.Length).Append('\u0001').Append(term);
  }
}
