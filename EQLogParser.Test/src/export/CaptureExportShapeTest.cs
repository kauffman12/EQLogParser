using System.IO.Compression;
using System.Text.RegularExpressions;
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

    // One cast five seconds before the pull (the lead-in), one an hour before it (not this fight's), one inside, and
    // a pet skill whose arabic tail is a level, not a rank — the shape that separates a real rank grammar from a regex.
    _facts.AddEvidence(new EvidenceFact(0, T0 - 5, kizant, EvidenceFact.EvCast, _facts.InternAux("Heal VIII")));
    _facts.AddEvidence(new EvidenceFact(0, T0 - 3600, kizant, EvidenceFact.EvCast, _facts.InternAux("Portal: Nexus")));
    _facts.AddEvidence(new EvidenceFact(0, T0 + 5, bulgar, EvidenceFact.EvCast, _facts.InternAux("Cure")));
    _facts.AddEvidence(new EvidenceFact(0, T0 + 2, pet, EvidenceFact.EvCast, _facts.InternAux("Reciprocal Roar Strike 6")));

    var heal = _heals.InternSpell("Heal Rk. II");
    var cure = _heals.InternSpell("Cure");

    // "for 800 (1000)": Total is what landed, OverTotal is the GROSS the line asked for, so the overheal a healing tab
    // prints is 200 — not 1000. The first cut of the export printed the gross under a header that said "Over Heal".
    _heals.AddHeal(new HealFact(5, T0 + 2, kizant, bulgar, 800, 1000, LabelTypes.Heal, 0, LineModifiersParser.None, heal));
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
    name => string.Equals(name, "Bulgar`s pet", StringComparison.Ordinal) ? "Pet" : RaidNames.Contains(name, StringComparer.OrdinalIgnoreCase) ? "Player" : "NPC",
    // The app wires EQDataStore.AbbreviateSpellName; this mirrors its rule ("Rk. <roman>", then trailing roman/number)
    // so the fixture tests the COLUMN plumbing and not a second rank grammar invented here.
    CleanSpell);

  private static string CleanSpell(string spell)
  {
    var parts = spell.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var count = parts.Length;

    if (count >= 3 && parts[count - 2].Equals("Rk.", StringComparison.OrdinalIgnoreCase) && IsRoman(parts[count - 1]))
      count -= 2;

    while (count > 0 && (IsRoman(parts[count - 1]) || int.TryParse(parts[count - 1], out _))) count--;

    return count == parts.Length ? spell : string.Join(" ", parts, 0, count);
  }

  private static bool IsRoman(string token) =>
    Regex.IsMatch(token, @"^M{0,3}(CM|CD|D?C{0,3})(XC|XL|L?X{0,3})(IX|IV|V?I{0,3})$",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
    Assert.AreEqual(string.Empty, first[8], "a melee row carries no skill name, so it has no rank either");
    Assert.AreEqual("500", first[9], "damage is the raw number");
    Assert.AreEqual("Hit", first[10]);
    Assert.AreEqual("Critical", first[11], "the modifier mask is spelled out");

    var petRow = CellTexts(playerRows[2]);
    Assert.AreEqual("Bulgar`s pet", petRow[3]);
    Assert.AreEqual("Bulgar", petRow[4], "the Pet Owner column the ask named, derived per row");

    var spellRow = CellTexts(playerRows[3]);
    Assert.AreEqual("Frost Bite XI", spellRow[7], "the spell with its rank, verbatim");
    Assert.AreEqual("XI", spellRow[8], "and the same row's rank on its own — the ask named both");
    Assert.AreEqual("1200", spellRow[9]);

    var npcRow = CellTexts(Rows(zip, "NPC DPS")[1]);
    Assert.AreEqual("A crypt broker", npcRow[3]);
    Assert.AreEqual("Miss", npcRow[10]);
    Assert.AreEqual("0", npcRow[9], "a miss is a row with zero on it, not a missing row");
  }

  [TestMethod]
  public void TheCastSheetTakesTheWindowAndItsLeadInWithNegativeTime()
  {
    var summary = Write();
    Assert.AreEqual(3, summary.CastRows, "the lead-in cast and the two in-fight casts; an hour earlier is not this fight");

    using var zip = ZipFile.OpenRead(Target("fight.xlsx"));
    var rows = Rows(zip, "Spells cast");
    Assert.AreEqual(4, rows.Count);

    var leadIn = CellTexts(rows[1]);
    Assert.AreEqual("Kizant", leadIn[3]);
    Assert.AreEqual("Heal VIII", leadIn[4]);
    // GamParse's clock has NO zero: the pull's own second is 1 and five seconds early is -5. Readers union our files
    // with years of saved GamParse ones, so one scale means one scale on both sides of the pull.
    Assert.AreEqual("-5", leadIn[1]);
    Assert.AreEqual("VIII", leadIn[5], "rank split out of the name");

    var inFight = CellTexts(rows[2]);
    Assert.AreEqual("Bulgar", inFight[3]);
    Assert.AreEqual("6", inFight[1], "sixth second of the fight, on a clock whose first second is 1");

    var petSkill = CellTexts(rows[3]);
    Assert.AreEqual("Bulgar`s pet", petSkill[3]);
    Assert.AreEqual("Reciprocal Roar Strike 6", petSkill[4], "the name verbatim, rank tail included");
    Assert.AreEqual("6", petSkill[5], "the spell data reads an arabic tail as a level; the export prints what it strips");
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
    Assert.AreEqual("800", by[5], "what landed");
    Assert.AreEqual("200", by[6], "the healing the target did NOT need — OverTotal - Total, never the gross (1000)");
    Assert.AreEqual("Heal Rk. II", by[7]);
    Assert.AreEqual("Rk. II", by[8], "the rank tail, verbatim");

    var to = CellTexts(Rows(zip, "Heals to you")[1]);
    Assert.AreEqual("Bulgar", to[3]);
    Assert.AreEqual("Kizant", to[4]);
    Assert.AreEqual("Cure", to[7]);
    Assert.AreEqual(string.Empty, to[8], "a spell with no rank leaves the column empty, not a guess");
  }

  [TestMethod]
  public void TheLookupTabIsThePlayerClassPetListTheReporterStoppedMaintaining()
  {
    var summary = Write();
    Assert.AreEqual(4, summary.PlayerNames, "every name this capture spoke, including the npc");

    using var zip = ZipFile.OpenRead(Target("fight.xlsx"));
    var rows = Rows(zip, "Players").Skip(1).Select(CellTexts).ToList();

    // Columns: Name, Class, Kind, Pets, Pet Of
    var kizant = rows.Single(r => r[0] == "Kizant");
    Assert.AreEqual("Magician", kizant[1]);
    Assert.AreEqual("Player", kizant[2]);
    Assert.AreEqual(string.Empty, kizant[3], "no pet of its own in this capture");

    var bulgar = rows.Single(r => r[0] == "Bulgar");
    Assert.AreEqual("Cleric", bulgar[1]);
    Assert.AreEqual("Bulgar`s pet", bulgar[3], "the FORWARD map the ask named: 'Pet -- Players PetName'");

    var pet = rows.Single(r => r[0] == "Bulgar`s pet");
    Assert.AreEqual("Pet", pet[2]);
    Assert.AreEqual("Bulgar", pet[4], "and the reverse column, for a reader joining on the pet");

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
    // The clock's contract, stated in the file: 1 is the pull's own second and the lead-in counts down from -1.
    StringAssert.Contains(Value("Time Column"), "1 = the anchor's second", Value("Time Column"));
    StringAssert.Contains(Value("Time Column"), "-1", Value("Time Column"));
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

  /*
   * "List all players present in the current Fight list selection": the lookup tab is the roster of the EXPORTED
   * window. A nine-second pull that ships the night's 102 names is a tab the reader has to filter before using.
   */
  [TestMethod]
  public void TheLookupTabListsThisWindowAndNotTheWholeFile()
  {
    var facts = new DamageFactTable(16);
    var heals = new HealFactTable(facts);

    var kizant = facts.InternName("Kizant");
    var npc = facts.InternName("A crypt broker");
    var lateRaid = facts.InternName("Farzi");

    facts.AddFact(new DamageFact(1, T0, kizant, npc, 500, LabelTypes.Melee, 0, LineModifiersParser.None, DamageFactTable.NoSubtype));
    facts.AddFact(new DamageFact(2, T0 + 3600, lateRaid, npc, 500, LabelTypes.Melee, 0, LineModifiersParser.None, DamageFactTable.NoSubtype));

    CaptureExport.Write(Target("scoped.xlsx"), facts, heals, new CaptureExport.Options
    {
      FromTimeS = T0,
      ToTimeS = T0 + 60,
      Lens = Lens
    });

    using var zip = ZipFile.OpenRead(Target("scoped.xlsx"));
    var names = Rows(zip, "Players").Skip(1).Select(CellTexts).Select(r => r[0]).ToList();

    CollectionAssert.Contains(names, "Kizant");
    CollectionAssert.Contains(names, "A crypt broker");
    CollectionAssert.DoesNotContain(names, "Farzi", "a name that only spoke in a later pull is not this window's roster");
  }

  // The fight's last damage lands at T0+6; the heal two seconds after it is the tail of the pull. Closing the cast/heal
  // span at the last DAMAGE row (the first cut) dropped exactly the rows a "Heals by you" reader came for.
  [TestMethod]
  public void HealingAfterTheLastDamageRowIsStillThisFights()
  {
    var spell = _heals.InternSpell("Heal Rk. II");
    var kizant = _heals.InternName("Kizant");
    var bulgar = _heals.InternName("Bulgar");
    _heals.AddHeal(new HealFact(9, T0 + 8, kizant, bulgar, 400, 0, LabelTypes.Heal, 0, LineModifiersParser.None, spell));

    CaptureExport.Write(Target("lateheal.xlsx"), _facts, _heals, new CaptureExport.Options
    {
      LocalPlayer = "Kizant",
      ToTimeS = T0 + 10,
      Lens = Lens
    });

    using var zip = ZipFile.OpenRead(Target("lateheal.xlsx"));
    var rows = Rows(zip, "Heals by you").Skip(1).Select(CellTexts).ToList();

    Assert.AreEqual(2, rows.Count, "the in-fight heal and the one that lands after the killing blow");
    CollectionAssert.Contains(rows.Select(r => r[1]).ToList(), "9", "on the same clock: ninth second of the fight");
  }

  // Every column added since the first cut gets a line on Event Info, because the file has to explain itself.
  [TestMethod]
  public void TheEventTabExplainsTheRankColumnTheLookupScopeAndTheSession()
  {
    CaptureExport.Write(Target("info.xlsx"), _facts, _heals, new CaptureExport.Options
    { LocalPlayer = "Kizant", SessionId = 42, Lens = Lens });

    using var zip = ZipFile.OpenRead(Target("info.xlsx"));
    var info = Rows(zip, "Event Info").Skip(1).Select(CellTexts).ToList();

    string Value(string key) => info.Single(r => r[0] == key)[1];

    Assert.AreEqual("42", Value("Capture Session"), "which capture wrote this file — a row dump outlives its session");
    StringAssert.Contains(Value("Lookup Scope"), "window", Value("Lookup Scope"));
    StringAssert.Contains(Value("Rank Column"), "rank", Value("Rank Column"));
    StringAssert.Contains(Value("Time Column"), "no zero", Value("Time Column"));
  }
}
