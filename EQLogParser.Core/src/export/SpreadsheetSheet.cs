using System.Globalization;
using System.IO.Compression;
using System.Xml;

namespace EQLogParser;

/*
 * One worksheet, written forward. Rows arrive through Row() and are serialized straight into the sheet's ZIP entry,
 * so the only retained state is counters plus the header (which a split has to re-stamp).
 *
 * The row ceiling is handled INSIDE this object: at the cap the current part is closed with its own filter range and
 * a continuation part opens under a numbered name, invisibly to the caller. A `foreach` over a capture therefore
 * cannot produce a file Excel refuses to open, and the call site reads as one logical sheet (RowCount counts data
 * rows across every part). MaxRows on the spec caps DATA rows per part, so the header is never part of the budget.
 *
 * Row protocol: `using var row = sheet.Row();` — Dispose closes the <row> element. An undisposed builder is a
 * malformed part, which XmlWriter reports when the sheet finishes; every call site here uses `using`.
 */
internal sealed class SpreadsheetSheet
{
  private readonly SpreadsheetWorkbook _owner;
  private readonly string _baseName;
  private SpreadsheetSpec _spec;
  private Stream _stream;
  private XmlWriter _writer;
  private int _partRowIndex;
  private int _partDataRows;
  private int _maxColumn;
  private long _rowCount;
  private bool _finished;

  internal SpreadsheetSheet(SpreadsheetWorkbook owner, SpreadsheetSpec spec, ZipArchiveEntry entry)
  {
    _owner = owner;
    _spec = spec;

    // The ORIGINAL name, not the current one: a third part continues "Player DPS" as "Player DPS 3", and reading
    // _spec.Name here would number off the last continuation ("Player DPS 2 2").
    _baseName = spec.Name;

    OpenPart(entry);
  }

  public string Name => _spec.Name;

  /// <summary>Data rows written across this sheet and any continuation part. Header rows are not counted.</summary>
  public long RowCount => _rowCount;

  private int Ceiling => _spec.MaxRows > 0 ? _spec.MaxRows : SpreadsheetWriter.MaxSheetRows;

  /// <summary>Starts a row. Dispose (usually the `using`) closes it.</summary>
  public RowBuilder Row()
  {
    if (_finished) throw new ObjectDisposedException(nameof(SpreadsheetSheet), $"{Name} is already written");
    if (PartIsFull(_partRowIndex, _partDataRows, Ceiling)) StartContinuation();

    var index = ++_partRowIndex;
    _partDataRows++;
    _rowCount++;
    _writer.WriteStartElement("row", SpreadsheetWorkbook.Main);
    _writer.WriteAttributeString("r", index.ToString(CultureInfo.InvariantCulture));
    return new RowBuilder(this, index);
  }

  /*
   * When a part stops accepting rows. TWO bounds, and the second one is the bug this guards:
   *
   *  - `ceiling` is the spec's budget of DATA rows per part (Excel's number by default, small in a split test), and
   *  - `MaxSheetRows` is the grid itself, counted in PART rows — the header sits at row 1, so a part that writes
   *    `MaxSheetRows` data rows ends at `r="1048577"`, one row outside the sheet. Measured on the first cut of this
   *    writer: 1,048,576 data rows in one part, last reference R1048577, and Excel calls that unreadable.
   *
   * Both stay asserted in SpreadsheetExportTest: MaxRows keeps its data-row meaning for the split test, and the grid
   * bound is checked where it bites — at the header's one-row offset.
   */
  internal static bool PartIsFull(int partRowIndex, int partDataRows, int ceiling) =>
    partDataRows >= ceiling || partRowIndex >= SpreadsheetWriter.MaxSheetRows;

  private void OpenPart(ZipArchiveEntry entry)
  {
    _stream = entry.Open();
    _writer = XmlWriter.Create(_stream, SpreadsheetWorkbook.XmlSettings);
    _partRowIndex = 0;
    _partDataRows = 0;
    _maxColumn = 0;

    _writer.WriteStartDocument(true);
    _writer.WriteStartElement("worksheet", SpreadsheetWorkbook.Main);

    // A frozen header row: on a 200k-row sheet the column names scroll out of sight immediately, and Excel has no
    // per-file setting that remembers this.
    _writer.WriteStartElement("sheetViews", SpreadsheetWorkbook.Main);
    _writer.WriteStartElement("sheetView", SpreadsheetWorkbook.Main);
    _writer.WriteAttributeString("workbookViewId", "0");
    _writer.WriteStartElement("pane", SpreadsheetWorkbook.Main);
    _writer.WriteAttributeString("ySplit", "1");
    _writer.WriteAttributeString("topLeftCell", "A2");
    _writer.WriteAttributeString("activePane", "bottomLeft");
    _writer.WriteAttributeString("state", "frozen");
    _writer.WriteEndElement();
    _writer.WriteEndElement();
    _writer.WriteEndElement();

    if (_spec.Width > 0)
    {
      _writer.WriteStartElement("cols", SpreadsheetWorkbook.Main);
      for (var i = 1; i <= _spec.Headers.Length; i++)
      {
        _writer.WriteStartElement("col", SpreadsheetWorkbook.Main);
        _writer.WriteAttributeString("min", i.ToString(CultureInfo.InvariantCulture));
        _writer.WriteAttributeString("max", i.ToString(CultureInfo.InvariantCulture));
        _writer.WriteAttributeString("width", _spec.Width.ToString(CultureInfo.InvariantCulture));
        _writer.WriteAttributeString("customWidth", "1");
        _writer.WriteEndElement();
      }

      _writer.WriteEndElement();
    }

    _writer.WriteStartElement("sheetData", SpreadsheetWorkbook.Main);
    WriteHeaderRow();
  }

  // Written directly rather than through Row(): the header is not a data row, and borrowing the public door would
  // make it one (and let a cap of 1 mean "no rows").
  private void WriteHeaderRow()
  {
    _partRowIndex = 1;
    _writer.WriteStartElement("row", SpreadsheetWorkbook.Main);
    _writer.WriteAttributeString("r", "1");

    for (var i = 0; i < _spec.Headers.Length; i++) WriteCell(i + 1, 1, _spec.Headers[i], CellKind.Text, bold: true);

    _writer.WriteEndElement();
  }

  private void StartContinuation()
  {
    ClosePart();
    var name = _owner.AddContinuation(_baseName, out var entry);
    _spec = _spec with { Name = name };
    OpenPart(entry);
  }

  /*
   * Closes sheetData and stamps the filter over what this part actually holds. The range is written here because the
   * extent is only known now, and autoFilter belongs after sheetData in the schema.
   */
  private void ClosePart()
  {
    _writer.WriteEndElement();   // sheetData

    if (_partRowIndex > 0 && _maxColumn > 0)
    {
      _writer.WriteStartElement("autoFilter", SpreadsheetWorkbook.Main);
      _writer.WriteAttributeString("ref", $"A1:{SpreadsheetWriter.ColumnLetter(_maxColumn)}{_partRowIndex}");
      _writer.WriteEndElement();
    }

    _writer.WriteEndElement();   // worksheet
    _writer.WriteEndDocument();
    _writer.Flush();
    _writer.Dispose();
    _stream.Dispose();
  }

  internal void Finish()
  {
    if (_finished) return;
    _finished = true;
    ClosePart();
  }

  private void WriteCell(int column, int rowIndex, string value, CellKind kind, bool bold)
  {
    _maxColumn = Math.Max(_maxColumn, column);

    _writer.WriteStartElement("c", SpreadsheetWorkbook.Main);
    _writer.WriteAttributeString("r", $"{SpreadsheetWriter.ColumnLetter(column)}{rowIndex}");
    if (bold) _writer.WriteAttributeString("s", "1");

    switch (kind)
    {
      case CellKind.Number:
        _writer.WriteElementString("v", SpreadsheetWorkbook.Main, value);
        break;

      case CellKind.Boolean:
        _writer.WriteAttributeString("t", "b");
        _writer.WriteElementString("v", SpreadsheetWorkbook.Main, value);
        break;

      default:
        // Inline strings: no sharedStrings.xml to grow in memory, no second pass over the file, and writing stays
        // forward-only.
        _writer.WriteAttributeString("t", "inlineStr");
        _writer.WriteStartElement("is", SpreadsheetWorkbook.Main);
        var text = SpreadsheetWriter.NeedsCleaning(value) ? SpreadsheetWriter.Clean(value) : value;
        if (text.Length == 0)
        {
          // An empty <t/> survives as an empty cell; without xml:space a whitespace-only name would be shaved.
          _writer.WriteStartElement("t", SpreadsheetWorkbook.Main);
          _writer.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
          _writer.WriteEndElement();
        }
        else
        {
          _writer.WriteElementString("t", SpreadsheetWorkbook.Main, text);
        }

        _writer.WriteEndElement();
        break;
    }

    _writer.WriteEndElement();
  }

  internal enum CellKind { Text, Number, Boolean }

  /*
   * The per-cell door. A ref struct so a builder cannot outlive the write window (a captured one would write into a
   * closed ZIP entry), and no boxing for numbers - a raid export is millions of cells.
   */
  internal ref struct RowBuilder(SpreadsheetSheet sheet, int rowIndex)
  {
    private readonly SpreadsheetSheet _sheet = sheet;
    private readonly int _rowIndex = rowIndex;
    private int _column;
    private bool _open = true;

    public void Number(long value) => Cell(value.ToString(CultureInfo.InvariantCulture), CellKind.Number);

    public void Number(double value) => Cell(value.ToString("0.###", CultureInfo.InvariantCulture), CellKind.Number);

    /// <summary>A real TRUE/FALSE cell, so Excel filters it as a boolean (a "Yes"/"No" string sorts as text).</summary>
    public void Flag(bool value) => Cell(value ? "1" : "0", CellKind.Boolean);

    public void Text(string value, bool bold = false)
    {
      var column = ++_column;
      _sheet.WriteCell(column, _rowIndex, value ?? string.Empty, CellKind.Text, bold);
    }

    /// <summary>Leaves the cell empty and moves on, so a sparse row keeps its columns lined up.</summary>
    public void Blank() => _column++;

    private void Cell(string value, CellKind kind)
    {
      var column = ++_column;
      _sheet.WriteCell(column, _rowIndex, value, kind, false);
    }

    public void Dispose()
    {
      if (!_open) return;
      _open = false;
      _sheet._writer.WriteEndElement();   // row
    }
  }
}
