using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace EQLogParser;

/*
 * The OPC package half of the xlsx writer: the ZIP, the workbook part that lists the sheets, the relationship
 * parts, and the one styles part.
 *
 * Ordering is forced by the format, not chosen: ZipArchive in Create mode allows exactly ONE open entry stream at a
 * time, so sheet parts are written as their rows arrive and the parts that DESCRIBE the workbook (workbook.xml, its
 * rels, [Content_Types].xml) are written last, in Dispose, when the sheet list is final. A caller who forgets to
 * dispose gets a ZIP with sheets and no workbook, so callers must wrap it in `using`.
 */
internal sealed class SpreadsheetWorkbook : IDisposable
{
  internal const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
  private const string RelPackage = "http://schemas.openxmlformats.org/package/2006/relationships";
  private const string RelDoc = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
  private const string RelTypes = "http://schemas.openxmlformats.org/package/2006/content-types";

  internal static readonly XmlWriterSettings XmlSettings = new()
  {
    CloseOutput = false,
    Encoding = new UTF8Encoding(false),
    Indent = false,
    NewLineHandling = NewLineHandling.Entitize,
    NamespaceHandling = NamespaceHandling.OmitDuplicates
  };

  private readonly FileStream _stream;
  private readonly ZipArchive _archive;
  private readonly List<SheetRef> _sheets = [];
  private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
  private SpreadsheetSheet _current;
  private bool _disposed;

  /// <summary>Opens a workbook and adds every sheet up front (the shape the export callers use).</summary>
  internal static SpreadsheetWorkbook OpenWith(string path, params SpreadsheetSpec[] specs)
  {
    var workbook = new SpreadsheetWorkbook(path);
    try
    {
      foreach (var spec in specs) workbook.AddSheet(spec);
      return workbook;
    }
    catch
    {
      workbook.CloseArchive();
      throw;
    }
  }

  internal SpreadsheetWorkbook(string path)
  {
    var directory = Path.GetDirectoryName(Path.GetFullPath(path));
    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

    _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
    _archive = new ZipArchive(_stream, ZipArchiveMode.Create, leaveOpen: false);
  }

  public int SheetCount => _sheets.Count;

  /// <summary>The sheet being written, i.e. the last one added. Null before the first AddSheet.</summary>
  public SpreadsheetSheet CurrentSheet => _current;

  /// <summary>A sheet's Excel name, including any continuation number it was given.</summary>
  public string SheetName(int index) => _sheets[index].Name;

  /// <summary>Starts a new sheet (finishing the one before it) and returns it ready for rows.</summary>
  public SpreadsheetSheet AddSheet(SpreadsheetSpec spec)
  {
    _current?.Finish();
    var name = SpreadsheetWriter.SanitizeSheetName(spec.Name, _names);
    _names.Add(name);

    var part = $"worksheets/sheet{_sheets.Count + 1}.xml";
    _sheets.Add(new SheetRef(name, part));
    _current = new SpreadsheetSheet(this, spec with { Name = name }, NewEntry(part));
    return _current;
  }

  /*
   * A sheet that hit Excel's row ceiling continues in a NEW part under a numbered name. The caller keeps the same
   * SpreadsheetSheet handle - a row loop must not have to notice the boundary - so the workbook only mints the name
   * and the part, and the sheet re-points its own writer.
   */
  internal string AddContinuation(string baseName, out ZipArchiveEntry entry)
  {
    var name = SpreadsheetWriter.SanitizeSheetName(baseName, _names);
    _names.Add(name);

    var part = $"worksheets/sheet{_sheets.Count + 1}.xml";
    _sheets.Add(new SheetRef(name, part));
    entry = NewEntry(part);
    return name;
  }

  private ZipArchiveEntry NewEntry(string partPath) => _archive.CreateEntry($"xl/{partPath}", CompressionLevel.Optimal);

  public void Dispose()
  {
    if (_disposed) return;
    _disposed = true;
    try
    {
      _current?.Finish();
      _current = null;
      WritePart("xl/workbook.xml", WriteWorkbook);
      WritePart("xl/_rels/workbook.xml.rels", WriteWorkbookRels);
      WritePart("xl/styles.xml", WriteStyles);
      WritePart("_rels/.rels", WriteRootRels);
      WritePart("[Content_Types].xml", WriteContentTypes);
    }
    finally
    {
      CloseArchive();
    }
  }

  private void CloseArchive()
  {
    _disposed = true;
    try
    {
      _archive.Dispose();
    }
    finally
    {
      _stream.Dispose();
    }
  }

  private void WritePart(string entryPath, Action<XmlWriter> body)
  {
    var entry = _archive.CreateEntry(entryPath, CompressionLevel.Optimal);
    using var stream = entry.Open();
    using var writer = XmlWriter.Create(stream, XmlSettings);
    writer.WriteStartDocument(true);
    body(writer);
    writer.WriteEndDocument();
  }

  private void WriteWorkbook(XmlWriter w)
  {
    w.WriteStartElement("workbook", Main);
    w.WriteAttributeString("xmlns", "r", null, RelDoc);

    w.WriteStartElement("sheets", Main);
    for (var i = 0; i < _sheets.Count; i++)
    {
      w.WriteStartElement("sheet", Main);
      w.WriteAttributeString("name", _sheets[i].Name);
      w.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
      w.WriteAttributeString("r", "id", RelDoc, $"rId{i + 1}");
      w.WriteEndElement();
    }

    w.WriteEndElement();
    w.WriteEndElement();
  }

  private void WriteWorkbookRels(XmlWriter w)
  {
    w.WriteStartElement("Relationships", RelPackage);
    for (var i = 0; i < _sheets.Count; i++)
    {
      w.WriteStartElement("Relationship", RelPackage);
      w.WriteAttributeString("Id", $"rId{i + 1}");
      w.WriteAttributeString("Type", $"{RelDoc}/worksheet");
      w.WriteAttributeString("Target", _sheets[i].PartPath);
      w.WriteEndElement();
    }

    w.WriteStartElement("Relationship", RelPackage);
    w.WriteAttributeString("Id", $"rId{_sheets.Count + 1}");
    w.WriteAttributeString("Type", $"{RelDoc}/styles");
    w.WriteAttributeString("Target", "styles.xml");
    w.WriteEndElement();
    w.WriteEndElement();
  }

  /*
   * Two fonts (plain, bold header) and two cell formats: index 0 plain, index 1 bold. Both fills are required -
   * Excel rejects a styles part without the none/gray125 pair - and no numFmt is declared on purpose so every
   * number lands as a general numeric cell rather than a formatted string.
   */
  private static void WriteStyles(XmlWriter w)
  {
    w.WriteStartElement("styleSheet", Main);
    w.WriteRaw("<fonts count=\"2\"><font><sz val=\"11\"/><color theme=\"1\"/><name val=\"Calibri\"/></font>" +
               "<font><b/><sz val=\"11\"/><color theme=\"1\"/><name val=\"Calibri\"/></font></fonts>" +
               "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill>" +
               "<fill><patternFill patternType=\"gray125\"/></fill></fills>" +
               "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
               "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
               "<cellXfs count=\"2\">" +
               "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
               "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
               "</cellXfs>" +
               "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>");
    w.WriteEndElement();
  }

  private static void WriteRootRels(XmlWriter w)
  {
    w.WriteStartElement("Relationships", RelPackage);
    w.WriteStartElement("Relationship", RelPackage);
    w.WriteAttributeString("Id", "rId1");
    w.WriteAttributeString("Type", $"{RelDoc}/officeDocument");
    w.WriteAttributeString("Target", "xl/workbook.xml");
    w.WriteEndElement();
    w.WriteEndElement();
  }

  private void WriteContentTypes(XmlWriter w)
  {
    w.WriteStartElement("Types", RelTypes);
    Default(w, "rels", "application/vnd.openxmlformats-package.relationships+xml");
    Default(w, "xml", "application/xml");
    Override(w, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
    Override(w, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
    foreach (var sheet in _sheets)
      Override(w, $"/xl/{sheet.PartPath}", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
    w.WriteEndElement();
  }

  private static void Default(XmlWriter w, string extension, string contentType)
  {
    w.WriteStartElement("Default", RelTypes);
    w.WriteAttributeString("Extension", extension);
    w.WriteAttributeString("ContentType", contentType);
    w.WriteEndElement();
  }

  private static void Override(XmlWriter w, string partName, string contentType)
  {
    w.WriteStartElement("Override", RelTypes);
    w.WriteAttributeString("PartName", partName);
    w.WriteAttributeString("ContentType", contentType);
    w.WriteEndElement();
  }

  internal sealed record SheetRef(string Name, string PartPath);
}
