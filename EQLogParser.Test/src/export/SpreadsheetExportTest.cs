using System.IO.Compression;
using System.Xml.Linq;
using EQLogParser;

namespace EQLogParser;

/*
 * The .xlsx writer, checked the only way a machine can check a spreadsheet format without Excel: unzip it and read
 * the parts. ECMA-376 is strict about what a reader needs (the workbook part, its relationships, a content type for
 * every part) and strict about what makes a number a number, so both halves are asserted here - net10.0, no Office.
 *
 * The cell-shape assertions are the reason the format was chosen: "numbers come in as text" (issue #191) is exactly
 * what a cell written as shared/inline string does to a pivot table, and a zero-padded line number is the same bug
 * in disguise. The split test pins the row ceiling, because a sheet past Excel's limit cannot be opened at all.
 */
[TestClass]
public sealed class SpreadsheetExportTest : IDisposable
{
  private static readonly XNamespace Main = SpreadsheetWorkbook.Main;

  private string? _dir;

  [TestInitialize]
  public void Setup()
  {
    PlayerRegistry.Instance.Clear();
    _dir = Path.Combine(Path.GetTempPath(), "eqlog-xlsx-" + Guid.NewGuid().ToString("N"));
  }

  [TestCleanup]
  public void Cleanup() => Dispose();

  public void Dispose()
  {
    if (_dir is { } dir && Directory.Exists(dir)) Directory.Delete(dir, true);
    _dir = null;
  }

  private string Target(string name) => Path.Combine(_dir ?? throw new InvalidOperationException("no temp dir"), name);

  private static XDocument Part(ZipArchive archive, string entry)
  {
    var found = archive.GetEntry(entry);
    Assert.IsNotNull(found, $"part {entry} is missing from the package");
    using var stream = found.Open();
    return XDocument.Load(stream);
  }

  private static List<string> SheetNames(ZipArchive archive) =>
    Part(archive, "xl/workbook.xml").Root?.Element(Main + "sheets")?.Elements(Main + "sheet")
      .Select(s => s.Attribute("name")?.Value).OfType<string>().ToList() ?? [];

  [TestMethod]
  public void APackageCarriesEveryPartAReaderNeedsAndEveryRelationshipResolves()
  {
    using (var book = SpreadsheetWriter.Open(Target("basic.xlsx"), "Player DPS", ["Name", "Damage"]))
    {
      using var row = book.CurrentSheet!.Row();
      row.Text("Kizant");
      row.Number(1234);
    }

    using var zip = ZipFile.OpenRead(Target("basic.xlsx"));

    foreach (var entry in new[]
             {
               "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels",
               "xl/styles.xml", "xl/worksheets/sheet1.xml"
             })
    {
      Assert.IsNotNull(zip.GetEntry(entry), $"missing {entry}");
    }

    // Every relationship Target must name a part that exists. Excel refuses the file on either half, so "it unzips"
    // is not the assertion - the graph is.
    var parts = zip.Entries.Select(e => e.FullName).ToList();
    foreach (var entry in zip.Entries.Where(entry => entry.FullName.EndsWith(".rels", StringComparison.Ordinal)))
    {
      var source = entry.FullName == "_rels/.rels" ? "/" :
        "/" + entry.FullName.Replace("/_rels/", "/", StringComparison.Ordinal)[..^5];
      foreach (var rel in Part(zip, entry.FullName).Root?.Elements() ?? [])
      {
        if (rel.Attribute("TargetMode")?.Value == "External") continue;
        var target = rel.Attribute("Target")?.Value;
        Assert.IsNotNull(target);
        var resolved = new Uri(new Uri("https://package.invalid" + source), target).AbsolutePath[1..];
        Assert.IsTrue(parts.Contains(resolved), $"{entry.FullName}: target {target} resolves to missing {resolved}");
      }
    }

    var types = Part(zip, "[Content_Types].xml");
    var names = SheetNames(zip);
    for (var i = 0; i < names.Count; i++)
    {
      var declared = types.Root?.Elements().Any(e => e.Attribute("PartName")?.Value == $"/xl/worksheets/sheet{i + 1}.xml") ?? false;
      Assert.IsTrue(declared, $"sheet {names[i]} has no content type override");
    }

    CollectionAssert.AreEqual(new[] { "Player DPS" }, names);
  }

  [TestMethod]
  public void NumbersLandAsNumericCellsAndTextLandsInline()
  {
    using (var book = SpreadsheetWriter.Open(Target("types.xlsx"), "Sheet", ["Name", "Damage", "Line"]))
    {
      using var row = book.CurrentSheet!.Row();
      row.Text("Kizant");
      row.Number(1234567);
      row.Number(558492);   // GamParse writes this one as "0000550447", i.e. a string to everything downstream
    }

    using var zip = ZipFile.OpenRead(Target("types.xlsx"));
    var cells = Part(zip, "xl/worksheets/sheet1.xml").Descendants(Main + "row").Last().Elements(Main + "c").ToList();

    Assert.AreEqual(3, cells.Count);

    Assert.AreEqual("inlineStr", cells[0].Attribute("t")?.Value);
    Assert.AreEqual("Kizant", cells[0].Element(Main + "is")?.Element(Main + "t")?.Value);

    Assert.IsNull(cells[1].Attribute("t"), "a number carries no t attribute - that is how Excel reads it as text");
    Assert.AreEqual("1234567", cells[1].Element(Main + "v")?.Value);
    Assert.AreEqual("558492", cells[2].Element(Main + "v")?.Value, "no padding: a padded number is a string in disguise");
  }

  [TestMethod]
  public void TheHeaderRowIsBoldFrozenAndFilteredOverTheWrittenRange()
  {
    using (var book = SpreadsheetWriter.Open(Target("header.xlsx"), "Spells cast", ["Seq", "Caster", "Spell"]))
    {
      for (var i = 0; i < 4; i++)
      {
        using var row = book.CurrentSheet!.Row();
        row.Number(i + 1);
        row.Text("Kizant");
        row.Text("Ice Salve XI");
      }
    }

    using var zip = ZipFile.OpenRead(Target("header.xlsx"));
    var sheet = Part(zip, "xl/worksheets/sheet1.xml");

    var pane = sheet.Descendants(Main + "pane").Single();
    Assert.AreEqual("frozen", pane.Attribute("state")?.Value);
    Assert.AreEqual("1", pane.Attribute("ySplit")?.Value, "the header scrolls away on a 200k-row sheet without this");

    var headerCells = sheet.Descendants(Main + "row").First().Elements(Main + "c").ToList();
    Assert.AreEqual(3, headerCells.Count);
    foreach (var cell in headerCells) Assert.AreEqual("1", cell.Attribute("s")?.Value, "the header uses the bold format");

    // Row 1 is the header and four rows were written, so the filter range ends on row 5.
    Assert.AreEqual("A1:C5", sheet.Descendants(Main + "autoFilter").Single().Attribute("ref")?.Value);
  }

  [TestMethod]
  public void ControlCharactersInTheTextDoNotCorruptThePart()
  {
    using (var book = SpreadsheetWriter.Open(Target("nasty.xlsx"), "Players", ["Name"]))
    {
      using var row = book.CurrentSheet!.Row();
      row.Text("Kiz\u0001ant");
    }

    using var zip = ZipFile.OpenRead(Target("nasty.xlsx"));
    var text = Part(zip, "xl/worksheets/sheet1.xml").Descendants(Main + "t").Last().Value;
    Assert.AreEqual("Kizant", text, "the illegal character is dropped, not escaped into an unparseable part");
  }

  /*
   * Excel cannot open a sheet past its row ceiling, so the split IS the feature: continuation parts, numbered names,
   * each with its own header and filter, every row accounted for. MaxRows on the spec is what makes this provable
   * here without writing a million rows.
   */
  [TestMethod]
  public void ASheetPastTheRowCeilingSplitsAndKeepsEveryRow()
  {
    const int total = 7;

    using (var book = new SpreadsheetWorkbook(Target("split.xlsx")))
    {
      var sheet = book.AddSheet(new SpreadsheetSpec("Player DPS", ["Seq"], 0, MaxRows: 3));
      for (var i = 1; i <= total; i++)
      {
        using var row = sheet.Row();
        row.Number(i);
      }

      Assert.AreEqual(total, sheet.RowCount, "the caller's handle sees the whole logical sheet, split and all");
      Assert.AreEqual(3, book.SheetCount);
      CollectionAssert.AreEqual(new[] { "Player DPS", "Player DPS 2", "Player DPS 3" },
        Enumerable.Range(0, book.SheetCount).Select(book.SheetName).ToArray());
    }

    using var zip = ZipFile.OpenRead(Target("split.xlsx"));
    var numbers = new List<int>();
    for (var i = 1; i <= 3; i++)
    {
      var rows = Part(zip, $"xl/worksheets/sheet{i}.xml").Descendants(Main + "row").ToList();
      Assert.AreEqual("Seq", rows.First().Descendants(Main + "t").Single().Value, "each part restates the header");
      numbers.AddRange(rows.Skip(1).Select(r => (int)r.Descendants(Main + "v").Single()));
    }

    CollectionAssert.AreEqual(Enumerable.Range(1, total).ToArray(), numbers);
  }

  [TestMethod]
  public void AnEmptyCaptureStillWritesAWorkbookThatOpens()
  {
    var facts = new DamageFactTable(4);
    var heals = new HealFactTable(facts);

    var summary = CaptureExport.Write(Target("empty.xlsx"), facts, heals, new CaptureExport.Options { EventName = "Nothing" });

    Assert.AreEqual(0, summary.PlayerRows);
    Assert.IsTrue(File.Exists(Target("empty.xlsx")));

    using var zip = ZipFile.OpenRead(Target("empty.xlsx"));
    foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.Ordinal)))
    {
      using var stream = entry.Open();
      _ = XDocument.Load(stream);   // a part that will not parse is a corrupt workbook, and it must fail loudly here
    }

    CollectionAssert.Contains(SheetNames(zip), "Event Info");
  }

  /*
   * The ceiling has TWO bounds and the first cut of this writer honoured one of them: MaxRows counts DATA rows, while
   * Excel's grid counts PART rows — the header sits at row 1, so a part written to the data budget ends at
   * r="1048577", one row outside the sheet (measured: 1,048,576 rows in one part, last reference R1048577).
   */
  [TestMethod]
  public void APartStopsAtTheGridEdgeCountingTheHeaderRow()
  {
    const int grid = SpreadsheetWriter.MaxSheetRows;

    // The spec's data budget keeps its meaning (this is what the split test above drives).
    Assert.IsFalse(SpreadsheetSheet.PartIsFull(1, 2, ceiling: 3), "two of three data rows");
    Assert.IsTrue(SpreadsheetSheet.PartIsFull(2, 3, ceiling: 3), "the data budget is spent");

    // And the grid itself is honoured with the header inside it, which is the row the first cut wrote past.
    Assert.IsFalse(SpreadsheetSheet.PartIsFull(grid - 1, 4, ceiling: grid), "one part-row of grid left: write it");
    Assert.IsTrue(SpreadsheetSheet.PartIsFull(grid, 4, ceiling: grid), "row 1048576 is taken; a new row belongs to the next part");
  }

  [TestMethod]
  public void SheetNamesLoseWhatExcelRefusesAndStayUnique()
  {
    var taken = new List<string> { "Pull 1", "Epic Loot[BCA]" };

    Assert.AreEqual("Pull 1 2", SpreadsheetWriter.SanitizeSheetName("Pull 1", taken), "a repeat gets a numbered tail, not a rewrite");
    Assert.AreEqual("Epic LootBCA", SpreadsheetWriter.SanitizeSheetName("Epic Loot[BCA]", []), "brackets are illegal");
    Assert.AreEqual(31, SpreadsheetWriter.SanitizeSheetName(new string('n', 80), []).Length, "31 characters is the ceiling");
    Assert.AreEqual("Sheet", SpreadsheetWriter.SanitizeSheetName(":::", []), "a name of nothing is still a sheet");
  }
}
