using System.IO.Compression;
using System.Xml.Linq;
using EQLogParser;

namespace EQLogParser;

/*
 * The row-level export of issue #191, asserted as SHAPE rather than as pixels: a small capture goes in, the workbook
 * comes out, and every column the reporter asked for is checked where it lands.
 *
 * What this pins:
 *  - the seven sheets, in the order a reader looks for them;
 *  - Player DPS vs NPC DPS split on the identity answer, with the pet's PLAYER in its own column (the ask: "add a
 *    Spell and Pet Owner column") - derived per row through the lens, because a fact carries what its line said and
 *    never what the registry believed;
 *  - the cast list from captured evidence, inside the fight window and in its lead-in, with negative relative time
 *    (GamParse's readers count seconds before the pull that way);
 *  - the two heal sheets filtered on the local player;
 *  - the lookup tab, which is the "stop maintaining my own Player/Class/Pet list" ask.
 */
[TestClass]
public sealed class CaptureExportShapeTest : IDisposable
{
  private static readonly XNamespace Main = SpreadsheetWorkbook.Main;
  private static readonly XNamespace RelPkg = "http://schemas.openxmlformats.org/package/2006/relationships";

  private static readonly long T0 = (long)DateUtil.ToDotNetSeconds(new DateTime(2024, 7, 18, 20, 0, 0));

  private string? _dir;
  private DamageFactTable _facts = new(16);
  private HealFactTable _heals = null!;

  [TestInitialize]
  public void Setup()
  {
    PlayerRegistry.Instance.Clear();
    _dir = Path.Combine(Path.GetTempPath(), "eqlog-export-" + Guid.NewGuid().ToString("N"));
    BuildCapture();
  }

  [TestCleanup]
  public void Cleanup() => Dispose();

  public void Dispose()
  {
    if (_dir is { } dir && Directory.Exists(dir)) Directory.Delete(dir, true);
    _dir = null;
  }

  private string Target(string name) => Path.Combine(_dir ?? throw new InvalidOperationException("no temp dir"), name);

  private static readonly string[] RaidNames = ["Kizant", "Bulgar", "Bulgar`s pet"];

  private void BuildCapture()
  {
    _facts = new DamageFactTable(16);
    _heals = new HealFactTable(_facts);

    var kizant = _facts.InternName("Kizant");
    var bulgar = _facts.InternName("Bulgar");
    var pet = _facts.InternName("Bulgar`s pet");
    var npc = _facts.InternName("A crypt broker");
    var frostBite = _facts.InternSubtype("Frost Bite XI");

    _facts.AddFact(new DamageFact(1, T0, kizant, npc, 500, LabelTypes.Melee, 0, LineModifiersParser.Crit, DamageFactTable.NoSubtype));
    _facts.AddFact(new DamageFact(2, T0 + 3, pet, npc, 250, LabelTypes.Melee, (byte)DamageFact.FlagOwnerInLine, LineModifiersParser.None, DamageFactTable.NoSubtype));
    _facts.AddFact(new DamageFact(3, T0 + 4, npc, kizant, 0, LabelTypes.Miss, 0, LineModifiersParser.None, DamageFactTable.NoSubtype));
    _facts.AddFact(new DamageFact(4, T0 + 6, kizant, npc, 1200, LabelTypes.Dd, 0, LineModifiersParser.None, frostBite));

    // One cast five seconds before the pull (the lead-in), one an hour before it (not this fight's), one inside.
    _facts.AddEvidence(new EvidenceFact(0, T0 - 5, kizant, EvidenceFact.EvCast, _facts.InternAux("Heal VIII")));
    _facts.AddEvidence(new EvidenceFact(0, T0 - 3600, kizant, EvidenceFact.EvCast, _facts.InternAux("Portal: Nexus")));
    _facts.AddEvidence(new EvidenceFact(0, T0 + 5, bulgar, EvidenceFact.EvCast, _facts.InternAux("Cure")));

    var heal = _heals.InternSpell("Heal Rk. II");
    var cure = _heals.InternSpell("Cure");
    _heals.AddHeal(new HealFact(5, T0 + 2, kizant, bulgar, 800, 200, LabelTypes.Heal, 0, LineModifiersParser.None, heal));
    _heals.AddHeal(new HealFact(6, T0 + 5, bulgar, kizant, 300, 0, LabelTypes.Hot, 0, LineModifiersParser.None, cure));
  }

  private static CaptureExport.Lens Lens => new(
    name => RaidNames.Contains(name, StringComparer.OrdinalIgnoreCase),
    name => string.Equals(name, "Bulgar`s pet", StringComparison.Ordinal) ? "Bulgar" : null,
    name => name switch
    {
      "Kizant" => "Magician",
      "Bulgar" => "Cleric",
      _ => null
    },
    name => string.Equals(name, "Bulgar`s pet", StringComparison.Ordinal) ? "Pet" : RaidNames.Contains(name, StringComparer.OrdinalIgnoreCase) ? "Player" : "NPC");

  private CaptureExport.Summary Write(string file = "fight.xlsx") =>
    CaptureExport.Write(Target(file), _facts, _heals, new CaptureExport.Options
    {
      EventName = "Kizant - xegony",
      SourceFile = "eqlog_Kizant_xegony.txt",
      LocalPlayer = "Kizant",
      Lens = Lens
    });

  private static List<XElement> Rows(ZipArchive zip, string sheetName)
  {
    var workbook = Part(zip, "xl/workbook.xml");
    var rels = Part(zip, "xl/_rels/workbook.xml.rels");
    var sheet = workbook.Root?.Element(Main + "sheets")?.Elements(Main + "sheet")
      .FirstOrDefault(s => s.Attribute("name")?.Value == sheetName);
    Assert.IsNotNull(sheet, $"sheet {sheetName} is not in the workbook");

    var relId = sheet.Attribute("{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id")?.Value;
    var target = rels.Root?.Elements(RelPkg + "Relationship").FirstOrDefault(r => r.Attribute("Id")?.Value == relId)?.Attribute("Target")?.Value;
    Assert.IsNotNull(target, $"sheet {sheetName} has no relationship");

    return Part(zip, "xl/" + target).Descendants(Main + "row").ToList();
  }

  private static XDocument Part(ZipArchive archive, string entry)
  {
    var found = archive.GetEntry(entry);
    Assert.IsNotNull(found, $"part {entry} is missing from the package");
    using var stream = found.Open();
    return XDocument.Load(stream);
  }

  private static List<string> CellTexts(XElement row) => row.Elements(Main + "c").Select(c =>
    c.Element(Main + "v")?.Value ?? c.Element(Main + "is")?.Element(Main + "t")?.Value ?? string.Empty).ToList();

  private static string[] SheetNames(ZipArchive zip) =>
    Part(zip, "xl/workbook.xml").Root?.Element(Main + "sheets")?.Elements(Main + "sheet")
      .Select(s => s.Attribute("name")?.Value).OfType<string>().ToArray() ?? [];

  [TestMethod]
  public void TheWorkbookCarriesEverySheetTheIssueAskedFor()
  {
    Write();

    using var zip = ZipFile.OpenRead(Target("fight.xlsx"));
    CollectionAssert.AreEqual(
      new[] { "Player DPS", "NPC DPS", "Spells cast", "Heals by you", "Heals to you", "Players", "Event Info" },
      SheetNames(zip));
  }

  [TestMethod]
  public void DamageRowsSplitOnSideAndThePetsPlayerGetsItsOwnColumn()
  {
    var summary = Write();

    Assert.AreEqual(3, summary.PlayerRows);
    Assert.AreEqual(1, summary.NpcRows);
    Assert.AreEqual(1950, summary.Damage);

    using var zip = ZipFile.OpenRead(Target("fight.xlsx"));

    var playerRows = Rows(zip, "Player DPS");
    Assert.AreEqual(4, playerRows.Count, "header plus three raid-side rows");

    // Columns: Seq, Time, Log Time, Attacker, Owner, Opponent, Type, Skill, Damage, Defense, Modifiers
    var first = CellTexts(playerRows[1]);
    Assert.AreEqual("Kizant", first[3]);
    Assert.AreEqual(string.Empty, first[4], "a player has no owner");
    Assert.AreEqual("A crypt broker", first[5]);
    Assert.AreEqual(LabelTypes.LabelOf(LabelTypes.Melee), first[6]);
    Assert.AreEqual("500", first[8], "damage is the raw number");
    Assert.AreEqual("Hit", first[9]);
    Assert.AreEqual("Critical", first[10], "the modifier mask is spelled out");

    var petRow = CellTexts(playerRows[2]);
    Assert.AreEqual("Bulgar`s pet", petRow[3]);
    Assert.AreEqual("Bulgar", petRow[4], "the Pet Owner column the ask named, derived per row");

    var spellRow = CellTexts(playerRows[3]);
    Assert.AreEqual("Frost Bite XI", spellRow[7], "the spell with its rank, verbatim");
    Assert.AreEqual("1200", spellRow[8]);

    var npcRow = CellTexts(Rows(zip, "NPC DPS")[1]);
    Assert.AreEqual("A crypt broker", npcRow[3]);
    Assert.AreEqual("Miss", npcRow[9]);
    Assert.AreEqual("0", npcRow[8], "a miss is a row with zero on it, not a missing row");
  }

  [TestMethod]
  public void TheCastSheetTakesTheWindowAndItsLeadInWithNegativeTime()
  {
    var summary = Write();
    Assert.AreEqual(2, summary.CastRows, "the lead-in cast and the in-fight cast; an hour earlier is not this fight");

    using var zip = ZipFile.OpenRead(Target("fight.xlsx"));
    var rows = Rows(zip, "Spells cast");
    Assert.AreEqual(3, rows.Count);

    var leadIn = CellTexts(rows[1]);
    Assert.AreEqual("Kizant", leadIn[3]);
    Assert.AreEqual("Heal VIII", leadIn[4]);
    // The clock a GamParse reader already knows: the pull's own second is 1, so five seconds early is -4.
    Assert.AreEqual("-4", leadIn[1]);

    var inFight = CellTexts(rows[2]);
    Assert.AreEqual("Bulgar", inFight[3]);
    Assert.AreEqual("6", inFight[1], "sixth second of the fight, on a clock whose first second is 1");
  }

  [TestMethod]
  public void TheTwoHealSheetsFollowTheLocalPlayer()
  {
    var summary = Write();
    Assert.AreEqual(1, summary.HealsByRows);
    Assert.AreEqual(1, summary.HealsToRows);
    Assert.AreEqual(1100, summary.Healing, "landed healing counted once, not once per sheet");

    using var zip = ZipFile.OpenRead(Target("fight.xlsx"));

    var by = CellTexts(Rows(zip, "Heals by you")[1]);
    Assert.AreEqual("Kizant", by[3]);
    Assert.AreEqual("Bulgar", by[4]);
    Assert.AreEqual("800", by[5]);
    Assert.AreEqual("200", by[6], "overheal travels as its own number, exactly as the line said it");
    Assert.AreEqual("Heal Rk. II", by[7]);

    var to = CellTexts(Rows(zip, "Heals to you")[1]);
    Assert.AreEqual("Bulgar", to[3]);
    Assert.AreEqual("Kizant", to[4]);
    Assert.AreEqual("Cure", to[7]);
  }

  [TestMethod]
  public void TheLookupTabIsThePlayerClassPetListTheReporterStoppedMaintaining()
  {
    var summary = Write();
    Assert.AreEqual(4, summary.PlayerNames, "every name this capture spoke, including the npc");

    using var zip = ZipFile.OpenRead(Target("fight.xlsx"));
    var rows = Rows(zip, "Players").Skip(1).Select(CellTexts).ToList();

    var kizant = rows.Single(r => r[0] == "Kizant");
    Assert.AreEqual("Magician", kizant[1]);
    Assert.AreEqual("Player", kizant[2]);

    var pet = rows.Single(r => r[0] == "Bulgar`s pet");
    Assert.AreEqual("Pet", pet[2]);
    Assert.AreEqual("Bulgar", pet[3]);

    var npc = rows.Single(r => r[0] == "A crypt broker");
    Assert.AreEqual("NPC", npc[2]);
  }

  [TestMethod]
  public void TheEventTabExplainsTheClockAndWhichHealRuleRan()
  {
    Write();

    using var zip = ZipFile.OpenRead(Target("fight.xlsx"));
    var info = Rows(zip, "Event Info").Skip(1).Select(CellTexts).ToList();

    string Value(string key) => info.Single(r => r[0] == key)[1];

    Assert.AreEqual("Kizant - xegony", Value("Event"));
    // ParseExact, invariant: a plain Parse reads this through the machine's culture and CI is not en-US.
    var start = DateTime.ParseExact(Value("Fight Start"), "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    Assert.AreEqual(T0, (long)DateUtil.ToDotNetSeconds(start), "the wall clock the fight started at");
    Assert.AreEqual("7", Value("Seconds"), "inclusive seconds, matching TimeSegment: Last - Begin + 1");
    Assert.AreEqual("Kizant", Value("Heal Sheets Filtered By"));
    Assert.AreEqual("1950", Value("Total Damage"));
    Assert.IsTrue(Value("Time Column").Contains("lead-in", StringComparison.Ordinal));
  }

  [TestMethod]
  public void AnExportWithoutALocalPlayerSaysSoInsteadOfChoosingQuietly()
  {
    CaptureExport.Write(Target("noisy.xlsx"), _facts, _heals, new CaptureExport.Options { Lens = Lens });

    using var zip = ZipFile.OpenRead(Target("noisy.xlsx"));
    var info = Rows(zip, "Event Info").Skip(1).Select(CellTexts).ToList();
    var rule = info.Single(r => r[0] == "Heal Sheets Filtered By")[1];

    Assert.IsTrue(rule.Contains("no local player", StringComparison.Ordinal), rule);
    Assert.AreEqual(2, Rows(zip, "Heals by you").Count - 1, "both heals have a raid-side healer");
  }
}
