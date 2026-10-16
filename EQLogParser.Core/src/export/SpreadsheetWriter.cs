using System.Text;

namespace EQLogParser;

/*
 * A forward-only writer for ECMA-376 SpreadsheetML - what Excel calls .xlsx. This file holds the vocabulary
 * (limits, names, the A1 reference arithmetic); SpreadsheetWorkbook is the package and SpreadsheetSheet is one
 * sheet's rows.
 *
 * Why by hand rather than a package: an xlsx is an OPC package, i.e. a ZIP of small XML parts, and .NET ships both
 * halves already (System.IO.Compression.ZipArchive - in production here since ChatDB - plus XmlWriter). A row-level
 * export of a raid night runs to hundreds of thousands of rows, which is exactly where the whole-workbook object
 * models cost most: they build a live object per cell before saving anything. Here a row becomes bytes as it is
 * produced and nothing above the current row is retained. It also keeps the format testable headless: unzip, parse
 * with XDocument, assert (SpreadsheetWriterTest) - no Excel on the box.
 *
 * Parts written, the minimum a strict reader accepts:
 *   [Content_Types].xml, _rels/.rels, xl/workbook.xml, xl/_rels/workbook.xml.rels, xl/styles.xml,
 *   xl/worksheets/sheetN.xml
 * Deliberately absent: sharedStrings.xml (strings go out inline, so there is no shared table to grow in memory and
 * no second pass over the file), calcChain (there are no formulas), themes, docProps.
 *
 * Two choices that matter to whoever opens the file:
 *  - numbers are <c><v>..</v></c>, i.e. real numeric cells with no style. "Numbers come in as text" is the standing
 *    complaint against XML exports (issue #191), and a zero-padded number ("0000550447") is a string to every tool
 *    downstream of Excel.
 *  - every sheet gets a frozen header row and an autoFilter over the range actually written, so it is filterable on
 *    open with nothing to click. Excel Tables (xl/tables/tableN.xml, which is what makes a pivot one keystroke) sit
 *    one part away; the id/displayName bookkeeping is not worth taking before the column list settles.
 */
internal static class SpreadsheetWriter
{
  // Excel's own ceiling (ECMA-376 Part 1): a sheet with more rows than this cannot be opened at all, so a long
  // capture splits into continuation sheets, each carrying its own header row and filter.
  internal const int MaxSheetRows = 1_048_576;

  /// <summary>Opens a workbook with a single sheet, ready for rows.</summary>
  internal static SpreadsheetWorkbook Open(string path, string sheetName, string[] headers, int width = 12)
    => SpreadsheetWorkbook.OpenWith(path, new SpreadsheetSpec(sheetName, headers, width));

  internal static SpreadsheetWorkbook OpenWith(string path, params SpreadsheetSpec[] specs)
    => SpreadsheetWorkbook.OpenWith(path, specs);

  /*
   * Column number (1-based) to its A1-style letters, by hand: the alternative is interpolated string arithmetic on a
   * per-cell reference for every cell of a seven-figure export.
   */
  internal static string ColumnLetter(int column)
  {
    Span<char> buffer = stackalloc char[8];
    var index = buffer.Length;
    var value = column;
    while (value > 0)
    {
      var remainder = (value - 1) % 26;
      buffer[--index] = (char)('A' + remainder);
      value = (value - remainder) / 26;
    }

    return new string(buffer[index..]);
  }

  /*
   * A sheet name Excel accepts: at most 31 characters, none of []:*?/\, not empty, unique in the workbook. Names
   * arrive from log text (a sheet named after a boss), so all three refusals are live cases, not paranoia.
   */
  internal static string SanitizeSheetName(string name, IReadOnlyCollection<string> taken)
  {
    var cleaned = new StringBuilder(name?.Length ?? 6);
    foreach (var c in name ?? string.Empty)
    {
      if (c is '[' or ']' or ':' or '*' or '?' or '/' or '\\') continue;
      if (c < 0x20) continue;
      cleaned.Append(c);
    }

    var result = cleaned.ToString().Trim();
    if (result.Length == 0) result = "Sheet";
    if (result.Length > 31) result = result[..31];

    // The continuation tail has to fit inside the 31, which is why the base gets trimmed rather than the whole name.
    var baseName = result;
    for (var n = 2; taken.Contains(result, StringComparer.OrdinalIgnoreCase); n++)
    {
      var tail = $" {n}";
      result = baseName.Length + tail.Length > 31 ? baseName[..(31 - tail.Length)] + tail : baseName + tail;
    }

    return result;
  }

  // Control characters make a whole part unparseable and log text carries them (a name copied out of a bazaar tell
  // is the usual source). Scanned only when something looks wrong, since the clean case is every line.
  internal static bool NeedsCleaning(string value)
  {
    foreach (var c in value)
    {
      if (c < 0x20 && c is not ('\t' or '\n' or '\r')) return true;
    }

    return false;
  }

  internal static string Clean(string value)
  {
    var builder = new StringBuilder(value.Length);
    foreach (var c in value)
    {
      if (c < 0x20 && c is not ('\t' or '\n' or '\r')) continue;
      builder.Append(c);
    }

    return builder.ToString();
  }
}

/*
 * One sheet's shape.
 *
 * MaxRows is a cap on THIS sheet: 0 means Excel's own ceiling, and a positive value splits into continuation parts
 * sooner. It exists for two reasons - a caller who knows a board export should stay under N rows per tab, and a test
 * that has to prove the split behaviour without writing a million rows.
 */
internal sealed record SpreadsheetSpec(string Name, string[] Headers, int Width = 12, int MaxRows = 0);
