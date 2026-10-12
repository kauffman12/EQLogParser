/*
 * Annotations only, no null-flow analysis (the project builds with Nullable=disable): a row's class and owner are
 * genuinely optional words, and stating that must not turn on a warning wave in code written before nullable existed.
 */
#nullable enable annotations

namespace EQLogParser;

/*
 * The five words a row of the Identity Table answers with, which is also the whole vocabulary its columns can sort by.
 * Declared here so the ORDER — the part that can be wrong — is a Core rule with tests, rather than something only a grid
 * on a Windows desktop can demonstrate.
 */
internal interface IIdentityRowKeys
{
  string Name { get; }
  string Type { get; }
  string? PlayerClass { get; }
  string Owner { get; }
  string Why { get; }
}

/*
 * The Identity Table's sort: which column decides, and what equal rows do.
 *
 * The complete filtered index carries the header's order before the pager slices it. The vendor's page-local sort uses
 * the same comparison, including tie breaks, so its second sort cannot disagree. See docs/DesignNotes.md → "Identity Table paging".
 *
 * Three laws:
 *
 *   - **Names are compared without case.** The parser capitalizes only the first letter of every name it hands out and a
 *     dozen other seams look entities up OrdinalIgnoreCase; an ordinal sort would split "aBone" from "Abone" for a reason
 *     no reader can see.
 *   - **A tie on the clicked column breaks on NAME**, so the order does not depend on which row the census happened to
 *     emit first. Roughly 4,000 rows share a handful of Type words ("NPC"), so without this the list is census order
 *     wearing a sort arrow again — through the back door.
 *   - **Descending reverses the whole line**, tiebreak included: a header click flips the display, not one field inside it.
 */
internal static class IdentityRowOrder
{
  internal const string NameColumn = "Name";
  internal const string TypeColumn = "Type";
  internal const string ClassColumn = "Class";
  internal const string OwnerColumn = "Owner";
  internal const string WhyColumn = "Why";

  /// <summary>The column names the pane can sort by, in header order. A sixth word needs a case below.</summary>
  internal static readonly IReadOnlyList<string> Columns = [NameColumn, TypeColumn, ClassColumn, OwnerColumn, WhyColumn];

  /// <summary>The name column: what the table shows when nobody clicked a header.</summary>
  internal const string DefaultColumn = NameColumn;

  /*
   * The comparison for one header state. `column` arrives from the grid's own descriptor (or null / unknown, which means
   * "nobody has decided yet" and takes the default) — never from a guess about which column the reader meant.
   */
  internal static Comparison<T> For<T>(string? column, bool descending = false) where T : IIdentityRowKeys
  {
    var key = column is not null && Columns.Contains(column) ? column : DefaultColumn;
    Comparison<T> order = (a, b) =>
    {
      var byColumn = string.Compare(WordOf(a, key), WordOf(b, key), StringComparison.OrdinalIgnoreCase);
      if (byColumn != 0) return byColumn;
      byColumn = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
      return byColumn != 0 ? byColumn : string.CompareOrdinal(a.Name, b.Name);
    };

    return descending
      ? (x, y) => -order(x, y)
      : order;
  }

  /*
   * The index at which a new row belongs in an ordered list: the FIRST slot past every row that sorts before it, so rows
   * already there keep their position and equal keys group with each other rather than jumping ahead. This is how a name
   * arriving on a live pass joins the list where its NAME says instead of piling at the bottom out of order — while the
   * rows the reader is looking at stay exactly where they were.
   */
  internal static int InsertIndex<T>(IReadOnlyList<T> ordered, T item, Comparison<T> order) where T : IIdentityRowKeys
  {
    var low = 0;
    for (var high = ordered.Count; low < high;)
    {
      var mid = (low + high) >>> 1;
      if (order(ordered[mid], item) <= 0) low = mid + 1; else high = mid;
    }

    return low;
  }

  /// <summary>Is this list in this order? Cheap (one pass, exits at the first violation), which is what lets a merge ask it
  /// every time rather than only when something is suspected.</summary>
  internal static bool IsOrdered<T>(IReadOnlyList<T> rows, Comparison<T> order) where T : IIdentityRowKeys
  {
    for (var i = 1; i < rows.Count; i++)
      if (order(rows[i - 1], rows[i]) > 0) return false;

    return true;
  }

  /* A missing word sorts as empty — a row with no class sits at the top of a Class sort rather than throwing or
   * borrowing the name. It is the only shape here that can be absent (the census fills the rest with real words,
   * including "No Owner" where nobody claimed the summon). */
  private static string WordOf(IIdentityRowKeys row, string column) => column switch
  {
    TypeColumn => row.Type,
    ClassColumn => row.PlayerClass ?? string.Empty,
    OwnerColumn => row.Owner,
    WhyColumn => row.Why,
    _ => row.Name,
  };
}
