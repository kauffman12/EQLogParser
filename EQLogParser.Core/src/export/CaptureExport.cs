using System.Globalization;

namespace EQLogParser;

/*
 * A row-level export of one parsed capture in the shape GamParse's users asked for (issue #191: "Request: Detailed
 * export" - every damage row, cast list with rank, a Player/Class/Pet lookup tab, an event key/value tab, so the
 * local lookup tables and the second overlay tool can be retired).
 *
 * What this reads is what the capture already keeps, and that is the whole point: DamageFactTable and HealFactTable
 * are row stores, not aggregates, so a fight's every line survives the parse with its sequence, second, amount,
 * verb/spell text and full modifier mask. Nothing here re-parses a log and nothing is reconstructed from a board.
 *
 * Column mapping from GamParse's five sheets, and where we differ on purpose:
 *   Player DPS / NPC DPS   Seq, Time, Log Time, Attacker, Owner, Opponent, Type, Skill, Rank, Damage, Defense,
 *                          Modifiers. "Owner" is the pet's player (the ask), derived per row from the injected lens
 *                          rather than stored on the fact - a fact carries what its line said, never what the registry
 *                          believed.
 *   Spells cast            Seq, Time, Log Time, Caster, Spell, Rank
 *   Heals by you / to you  Seq, Time, Log Time, Caster, Target, Heal, Over Heal, Spell, Rank, Modifiers
 * Plus "Players" (the lookup tab: Name, Class, Kind, Pets, Pet Of) and "Event Info" (the key/value tab).
 *
 * Spell and Rank are TWO columns because the ask named them separately ("having the clean spell name in Spell, and
 * rank in a separate column"). The clean name arrives through the lens - EQDataStore.AbbreviateSpellName in the app -
 * so this file prints the tail the spell data strips and never grows a rank grammar of its own to drift from it.
 *
 * The clock: Time is SECONDS FROM THE FIGHT on GamParse's scale, which has no zero: 1 = the anchor's second, -1 = one
 * second before it. Matching that exactly is not nostalgia - the reporter unions every export into years of saved
 * GamParse files, and a one-second shift on the lead-in rows would be invisible and permanent. Log Time is the wall
 * clock the log line carried, ISO to the second, which is the other half of the same ask.
 *
 * Scope: an ordinal window over the damage stream (the Fight List selection's own answer, which is what "what I
 * filtered out in Fight List" means), with casts and heals taken from the TIME span that window covers, widened by
 * LeadInS and closed at the caller's requested end - healing after the last damage row of a fight is still this
 * fight's. The heal stream has its own ordinal space, so a time window is the honest join - no fake alignment.
 */
internal static class CaptureExport
{
  /// <summary>Column titles, in sheet order. Kept in one place so the tests and the writer cannot drift.</summary>
  internal static readonly string[] HitHeaders =
    ["Seq", "Time", "Log Time", "Attacker", "Owner", "Opponent", "Type", "Skill", "Rank", "Damage", "Defense",
      "Modifiers"];

  internal static readonly string[] CastHeaders = ["Seq", "Time", "Log Time", "Caster", "Spell", "Rank"];

  internal static readonly string[] HealHeaders =
    ["Seq", "Time", "Log Time", "Caster", "Target", "Heal", "Over Heal", "Spell", "Rank", "Modifiers"];

  internal static readonly string[] PlayerHeaders = ["Name", "Class", "Kind", "Pets", "Pet Of"];

  internal static readonly string[] InfoHeaders = ["Key", "Value"];

  /*
   * The identity questions the export asks, injected. This file deliberately knows nothing about rules: a fact row's
   * owner, class and kind are what the timeline answers at export time, so the app passes the same functions its
   * boards read and an export of a board can disagree with the board in exactly zero places.
   */
  internal sealed record Lens(Func<string, bool> IsPlayerSide, Func<string, string> OwnerOf,
    Func<string, string> ClassOf, Func<string, string> KindOf, Func<string, string> CleanSpell)
  {
    /// <summary>No identity lane: every name reads NPC-side, unclassed, and no spell is shortenable.</summary>
    internal static readonly Lens Blind = new(_ => false, _ => null, _ => null, _ => Labels.Unk, s => s);
  }

  internal sealed record Options
  {
    public string EventName = string.Empty;
    public string SourceFile = string.Empty;

    // Origin of the Time column. long.MinValue means "the first damage row in the window", which is what a caller who
    // hands over ordinals (a Fight List selection) knows; a caller holding a DerivedFight passes its begin time.
    public long AnchorS = long.MinValue;

    // How far before the anchor a cast or heal still counts as this fight's - GamParse leads its cast sheet by five.
    public long LeadInS = 5;

    public int FromOrdinal;
    public int ToOrdinal = int.MaxValue;

    /*
     * A TIME window on top of the ordinal one, for callers who hold a fight's seconds rather than its ordinals (a
     * time-range board, an .spool re-open). Ordinals are the door a Fight List selection answers through; these two
     * exist because a projection gives begin/end and making a caller translate them into ordinals invents an ordinal
     * it does not own. 0 = unset.
     */
    public long FromTimeS;
    public long ToTimeS;

    /// <summary>The local player for the two heal sheets. Empty means "not detected": the sheet widens to the raid side.</summary>
    public string LocalPlayer = string.Empty;

    /// <summary>Which capture wrote this file, printed on Event Info: a row dump outlives the session that made it.</summary>
    public int SessionId;

    public Lens Lens = Lens.Blind;
  }

  internal sealed record Summary(long PlayerRows, long NpcRows, long CastRows, long HealsByRows, long HealsToRows,
    long PlayerNames, long Damage, long Healing, long BeginS, long EndS, long Bytes)
  {
    public override string ToString() =>
      $"player={PlayerRows} npc={NpcRows} casts={CastRows} healsBy={HealsByRows} healsTo={HealsToRows} " +
      $"players={PlayerNames} damage={Damage} healing={Healing} bytes={Bytes}";
  }

  /// <summary>Writes the workbook. Rows stream straight from the fact tables; nothing here is materialized.</summary>
  internal static Summary Write(string path, DamageFactTable facts, HealFactTable heals, Options options)
  {
    var window = ScanWindow(facts, options);

    long playerRows, npcRows, castRows, healsBy = 0, healsTo = 0, playerNames = 0;

    using (var book = SpreadsheetWriter.Open(path, "Player DPS", HitHeaders, 14))
    {
      playerRows = WriteHits(book.CurrentSheet!, facts, options, window, playerSide: true);
      npcRows = WriteHits(book.AddSheet(new SpreadsheetSpec("NPC DPS", HitHeaders, 14)), facts, options, window, playerSide: false);
      castRows = WriteCasts(book.AddSheet(new SpreadsheetSpec("Spells cast", CastHeaders, 16)), facts, options, window);

      if (heals != null)
      {
        // The healing total runs FIRST: it is also the pass that files healers and healed names into the window's
        // census, which is exactly the roster the lookup tab lists.
        window.Healing = ScanHealing(heals, options, window);
        healsBy = WriteHeals(book.AddSheet(new SpreadsheetSpec("Heals by you", HealHeaders, 14)), heals, options, window, byLocal: true);
        healsTo = WriteHeals(book.AddSheet(new SpreadsheetSpec("Heals to you", HealHeaders, 14)), heals, options, window, byLocal: false);
      }

      playerNames = WritePlayers(book.AddSheet(new SpreadsheetSpec("Players", PlayerHeaders, 18)), facts, options, window);
      WriteInfo(book.AddSheet(new SpreadsheetSpec("Event Info", InfoHeaders, 26)), options, window, book,
        playerRows, npcRows, castRows, healsBy, healsTo, playerNames);
    }

    return new Summary(playerRows, npcRows, castRows, healsBy, healsTo, playerNames, window.Damage, window.Healing,
      window.BeginS, window.EndS, new FileInfo(path).Length);
  }

  /*
   * Pass one: the window's shape. Everything downstream - the Time column's origin, the cast/heal time filter, the
   * event tab - reads these numbers, and a slice walk over even a cold night is milliseconds per chunk, so an
   * explicit pass costs less than carrying state through the sheet writers.
   */
  private static Window ScanWindow(DamageFactTable facts, Options options)
  {
    var result = new Window();
    var anchorSet = options.AnchorS != long.MinValue;
    if (anchorSet)
    {
      result.AnchorS = options.AnchorS;
      result.BeginS = options.AnchorS;
      result.EndS = options.AnchorS;
    }

    foreach (var slice in facts.FactsIn(options.FromOrdinal, options.ToOrdinal))
    {
      foreach (var f in slice)
      {
        if (!InTime(f.TimeS, options)) continue;

        // The window's roster of names, collected on the pass that already walks it: "all players present in the
        // current Fight list selection" is what the lookup tab is for, and a name that only spoke in tonight's other
        // eleven pulls does not belong in it.
        result.NameIdx.Add(f.AtkIdx);
        result.NameIdx.Add(f.DefIdx);

        if (f.Total > 0)
        {
          result.Damage += f.Total;
          if (options.Lens.IsPlayerSide(facts.NameOf(f.AtkIdx))) result.PlayerDamage += f.Total;
        }

        if (!anchorSet && result.Seen == 0)
        {
          result.AnchorS = f.TimeS;
          result.BeginS = f.TimeS;
          result.EndS = f.TimeS;
        }
        else
        {
          result.BeginS = Math.Min(result.BeginS, f.TimeS);
          result.EndS = Math.Max(result.EndS, f.TimeS);
        }

        result.Seen++;
      }
    }

    // A window with no rows still writes a workbook: an empty sheet that opens and filters is an answer, and a
    // missing file is not. The clock lands on 0 so relative time cannot be read as the year 1.
    if (result.Seen == 0)
    {
      result.AnchorS = options.AnchorS != long.MinValue ? options.AnchorS : 0;
      result.BeginS = result.AnchorS;
      result.EndS = result.AnchorS;
    }

    return result;
  }

  // Landed healing inside the window, counted once (the two heal sheets overlap by design, so their row counts are
  // not a total and this pass is what Event Info prints).
  private static long ScanHealing(HealFactTable heals, Options options, Window window)
  {
    var (from, to) = SpanOf(window, options);
    long total = 0;

    foreach (var slice in heals.HealsIn(0, int.MaxValue))
    {
      foreach (var h in slice)
      {
        if (h.TimeS < from || h.TimeS > to) continue;

        total += h.Total;
        window.NameIdx.Add(h.HealerIdx);
        window.NameIdx.Add(h.HealedIdx);
      }
    }

    return total;
  }

  private static long WriteHits(SpreadsheetSheet sheet, DamageFactTable facts, Options options, Window window, bool playerSide)
  {
    var lens = options.Lens;
    long rows = 0;

    foreach (var slice in facts.FactsIn(options.FromOrdinal, options.ToOrdinal))
    {
      foreach (var f in slice)
      {
        if (!InTime(f.TimeS, options)) continue;

        var attacker = facts.NameOf(f.AtkIdx);
        if (lens.IsPlayerSide(attacker) != playerSide) continue;

        using var row = sheet.Row();
        row.Number(f.Seq);
        row.Number(RelativeTime(f.TimeS, window.AnchorS));
        row.Text(IsoTime(f.TimeS));
        row.Text(attacker);
        row.Text(lens.OwnerOf(attacker) ?? string.Empty);
        row.Text(facts.NameOf(f.DefIdx));
        row.Text(LabelTypes.LabelOf(f.TypeId) ?? Labels.Unk);
        var skill = facts.SubtypeOf(f.SubIdx) ?? string.Empty;
        row.Text(skill);
        row.Text(RankOf(skill, lens));
        row.Number(f.Total);
        row.Text(DefenseWord(f.TypeId));
        row.Text(LineModifiersParser.Words(f.ModMask));
        rows++;
      }
    }

    return rows;
  }

  /*
   * The cast list comes from captured evidence (EvCast), not from damage rows: "X begins casting Y" is a line with no
   * number on it, and the capture kept it as a fact precisely so questions like "did the interrupts land" can be
   * answered. Spell text carries its rank verbatim ("Heal VIII"), which is what the lookup-table ask needs.
   */
  private static long WriteCasts(SpreadsheetSheet sheet, DamageFactTable facts, Options options, Window window)
  {
    var (from, to) = SpanOf(window, options);
    var lens = options.Lens;
    var rows = 0L;

    foreach (var e in facts.Evidence)
    {
      if (e.Kind != EvidenceFact.EvCast) continue;
      if (e.TimeS < from || e.TimeS > to) continue;

      window.NameIdx.Add(e.NameIdx);

      var spell = e.AuxIdx >= 0 ? facts.AuxOf(e.AuxIdx) : string.Empty;

      using var row = sheet.Row();
      row.Number(e.Seq);
      row.Number(RelativeTime(e.TimeS, window.AnchorS));
      row.Text(IsoTime(e.TimeS));
      row.Text(facts.NameOf(e.NameIdx));
      row.Text(spell);
      row.Text(RankOf(spell, lens));
      rows++;
    }

    return rows;
  }

  /*
   * "By you" and "to you" are GamParse's local-player view. When the app knows the local player it filters on that
   * name; when it does not (a log parsed for someone else, which is most of what gets posted), the honest widening is
   * the raid side - the sheet stays useful and Event Info says which rule was used, so a reader is never guessing.
   */
  private static long WriteHeals(SpreadsheetSheet sheet, HealFactTable heals, Options options, Window window, bool byLocal)
  {
    var (from, to) = SpanOf(window, options);
    var lens = options.Lens;
    var local = options.LocalPlayer ?? string.Empty;
    var rows = 0L;

    foreach (var slice in heals.HealsIn(0, int.MaxValue))
    {
      foreach (var h in slice)
      {
        if (h.TimeS < from || h.TimeS > to) continue;

        var healer = heals.NameOf(h.HealerIdx);
        var healed = heals.NameOf(h.HealedIdx);

        var keep = local.Length > 0
          ? string.Equals(byLocal ? healer : healed, local, StringComparison.OrdinalIgnoreCase)
          : lens.IsPlayerSide(byLocal ? healer : healed);
        if (!keep) continue;

        using var row = sheet.Row();
        row.Number(h.Seq);
        row.Number(RelativeTime(h.TimeS, window.AnchorS));
        row.Text(IsoTime(h.TimeS));
        row.Text(healer);
        row.Text(healed);
        row.Number(h.Total);

        // The healing the target did not need - NOT HealFact.OverTotal, which is the GROSS the line asked for
        // ("for 3461 (60581)" stores Total 3461 and OverTotal 60581). Printing OverTotal under a header that says
        // "Over Heal" reads as an overheal inflated by exactly the landed amount. The verbatim-OverTotal law belongs
        // to record materialization (HealSummarySource feeding StatsUtil), not to a printed column.
        row.Number(h.Overheal);

        var spell = heals.SpellOf(h.SubIdx) ?? string.Empty;
        row.Text(spell);
        row.Text(RankOf(spell, lens));
        row.Text(LineModifiersParser.Words(h.ModMask));
        rows++;
      }
    }

    return rows;
  }

  /*
   * The lookup tab the reporter asked for - "Player / Class / Pet" - scoped to the names THIS window spoke, so a
   * nine-second pull exports its own raid instead of the file's whole census. Class arrives as the OPERATOR's saved
   * word first (the app wires PlayerRegistry.GetAssignedOrLastKnownPlayerClass): a hand-typed class is precisely the
   * value that must survive into the export - their persona argument, "a static list on my local pc will assign wrong
   * class to anyone raiding on a persona". Boards keep reading GetPlayerClass(name, t), which stays time-correct.
   */
  private static long WritePlayers(SpreadsheetSheet sheet, DamageFactTable facts, Options options, Window window)
  {
    var lens = options.Lens;
    var names = facts.InternedNames;
    var petsOf = PetsByName(names, window, lens);
    long rows = 0;

    for (var i = 0; i < names.Count; i++)
    {
      if (!window.NameIdx.Contains(i)) continue;

      var name = names[i];
      if (ParserUtil.IsUnattributedName(name)) continue;

      using var row = sheet.Row();
      row.Text(name);
      row.Text(lens.ClassOf(name) ?? string.Empty);
      row.Text(lens.KindOf(name) ?? Labels.Unk);
      row.Text(petsOf.GetValueOrDefault(name, string.Empty));
      row.Text(lens.OwnerOf(name) ?? string.Empty);
      rows++;
    }

    return rows;
  }

  /*
   * "Pet -- Players PetName": the FORWARD map. OwnerOf answers pet -> player and the ask runs the other way, so invert
   * it once over the window's names. A summoner with three pets gets them comma-joined on its own row, walking the
   * sorted index set because identical input must give an identical file.
   */
  private static Dictionary<string, string> PetsByName(IReadOnlyList<string> names, Window window, Lens lens)
  {
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    foreach (var idx in window.NameIdx)
    {
      if (idx < 0 || idx >= names.Count) continue;

      var pet = names[idx];
      var owner = lens.OwnerOf(pet);
      if (string.IsNullOrEmpty(owner) || string.Equals(owner, pet, StringComparison.OrdinalIgnoreCase)) continue;

      map[owner] = map.TryGetValue(owner, out var seen) && seen.Length > 0 ? $"{seen}, {pet}" : pet;
    }

    return map;
  }

  /*
   * The key/value tab. Everything a reader needs to interpret the other sheets travels in the file itself: which log,
   * which window, what the Time column means, and which rule the two heal sheets used. An export that needs a forum
   * post to be read correctly is not finished.
   */
  private static void WriteInfo(SpreadsheetSheet sheet, Options options, Window window, SpreadsheetWorkbook book,
    long playerRows, long npcRows, long castRows, long healsBy, long healsTo, long playerNames)
  {
    var local = options.LocalPlayer;
    var healRule = string.IsNullOrEmpty(local)
      ? "no local player detected - raid-side rows on both sheets"
      : local;

    void Line(string key, string value)
    {
      using var row = sheet.Row();
      row.Text(key);
      row.Text(value);
    }

    Line("Event", options.EventName);
    Line("Source Log", options.SourceFile);
    Line("Capture Session", options.SessionId.ToString(CultureInfo.InvariantCulture));
    Line("Exported", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
    Line("Fight Start", IsoTime(window.AnchorS));
    Line("Window Start", IsoTime(window.BeginS));
    Line("Window End", IsoTime(window.EndS));
    Line("Seconds", window.Seen == 0 ? "0" : (window.EndS - window.BeginS + 1).ToString(CultureInfo.InvariantCulture));
    Line("Lead In Seconds", Math.Max(0, options.LeadInS).ToString(CultureInfo.InvariantCulture));
    Line("Damage Rows", (playerRows + npcRows).ToString(CultureInfo.InvariantCulture));
    Line("Player Damage", window.PlayerDamage.ToString(CultureInfo.InvariantCulture));
    Line("Total Damage", window.Damage.ToString(CultureInfo.InvariantCulture));
    Line("Healing", window.Healing.ToString(CultureInfo.InvariantCulture));
    Line("Cast Rows", castRows.ToString(CultureInfo.InvariantCulture));
    Line("Heal Rows By Side", healsBy.ToString(CultureInfo.InvariantCulture));
    Line("Heal Rows To Side", healsTo.ToString(CultureInfo.InvariantCulture));
    Line("Names Listed", playerNames.ToString(CultureInfo.InvariantCulture));
    Line("Sheet Names", string.Join(", ", SheetNames(book)));
    Line("Lookup Scope", $"names seen in this window, widened {Math.Max(0, options.LeadInS)}s for casts and heals");
    Line("Time Column", "seconds from fight start on GamParse's scale: 1 = the anchor's second, -1 = one second " +
                        "before it (no zero)");
    Line("Rank Column", "the tail the spell data strips off the name ('Rk. III', a level number); empty when the " +
                        "name carries no rank");
    Line("Heal Sheets Filtered By", healRule);
  }

  private static IEnumerable<string> SheetNames(SpreadsheetWorkbook book)
  {
    for (var i = 0; i < book.SheetCount; i++) yield return book.SheetName(i);
  }

  // The time window, when the caller has one. Ordinals arrive already scoped; this narrows further.
  private static bool InTime(long timeS, Options options) =>
    (options.FromTimeS == 0 || timeS >= options.FromTimeS) && (options.ToTimeS == 0 || timeS <= options.ToTimeS);

  /*
   * GamParse's clock has NO zero: the pull's own second is 1 and one second early is -1. Matching it exactly is not
   * nostalgia - the reporter unions every export into years of saved GamParse files, and a plain `t - anchor + 1`
   * shifts our whole lead-in one second closer to the pull than theirs (-4 where their file says -5).
   */
  private static long RelativeTime(long timeS, long anchorS)
  {
    if (anchorS == 0) return 0;

    var delta = timeS - anchorS;
    return delta >= 0 ? delta + 1 : delta;
  }

  /*
   * The span a cast or heal must fall inside: the damage window widened by the lead-in at the front, and closed at the
   * CALLER's requested end when it has one. Closing it at the last damage row instead dropped the healing after a kill
   * - exactly the tail a "Heals by you" reader wants.
   */
  private static (long From, long To) SpanOf(Window window, Options options) =>
    (window.BeginS - Math.Max(0, options.LeadInS), Math.Max(window.EndS, options.ToTimeS));

  /*
   * Rank is the tail the clean name does not have. The spell data owns what counts as a rank (AbbreviateSpellName,
   * through the lens); this only reports what it removed, so the export cannot drift from the rule R4 already uses.
   */
  private static string RankOf(string full, Lens lens)
  {
    if (string.IsNullOrEmpty(full)) return string.Empty;

    var clean = lens.CleanSpell(full) ?? string.Empty;
    return clean.Length > 0 && clean.Length < full.Length && full.StartsWith(clean, StringComparison.Ordinal)
      ? full[clean.Length..].Trim()
      : string.Empty;
  }

  private static string IsoTime(long epochSeconds) =>
    epochSeconds <= 0 ? string.Empty
      : DateUtil.FromDotNetSeconds(epochSeconds).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

  /*
   * GamParse's Defense column: what happened to the swing. A landed hit says "Hit" because the outcome IS that it
   * landed; every non-damage label names itself, and the words are the app's own (Labels), not a second vocabulary.
   */
  private static string DefenseWord(byte typeId) => typeId switch
  {
    LabelTypes.Miss => Labels.Miss,
    LabelTypes.Dodge => Labels.Dodge,
    LabelTypes.Parry => Labels.Parry,
    LabelTypes.Block => Labels.Block,
    LabelTypes.Riposte => Labels.Riposte,
    LabelTypes.Invulnerable => Labels.Invulnerable,
    LabelTypes.Absorb => Labels.Absorb,
    _ => "Hit"
  };

  internal sealed class Window
  {
    // Sorted, not hashed: the lookup tab walks it, and an export must be reproducible row for row.
    public readonly SortedSet<int> NameIdx = [];

    public long AnchorS;
    public long BeginS;
    public long EndS;
    public long Damage;
    public long PlayerDamage;
    public long Healing;
    public long Seen;
  }
}
