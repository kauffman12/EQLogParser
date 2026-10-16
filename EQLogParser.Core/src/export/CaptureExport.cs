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
 *   Player DPS / NPC DPS   Seq, Time, Log Time, Attacker, Owner, Opponent, Type, Skill, Damage, Defense, Modifiers
 *                          "Owner" is the pet's player (the ask), derived per row from the injected lens rather than
 *                          stored on the fact - a fact carries what its line said, never what the registry believed.
 *   Spells cast            Seq, Time, Log Time, Caster, Spell
 *   Heals by you / to you  Seq, Time, Log Time, Caster, Target, Heal, Over Heal, Spell, Modifiers
 * Plus "Players" (the lookup tab) and "Event Info" (the key/value tab).
 *
 * The clock: Time is SECONDS FROM THE FIGHT, 1 = the anchor's second, zero and negative = the lead-in. GamParse's
 * sheet does exactly this and its users read -5 as "five seconds before the pull". Log Time is the wall clock the
 * log line carried, ISO to the second, which is the other half of the same ask.
 *
 * Scope: an ordinal window over the damage stream (the Fight List selection's own answer, which is what "what I
 * filtered out in Fight List" means), with casts and heals taken from the TIME span that window covers, widened by
 * LeadInS. The heal stream has its own ordinal space, so a time window is the honest join - no fake alignment.
 */
internal static class CaptureExport
{
  /// <summary>Column titles, in sheet order. Kept in one place so the tests and the writer cannot drift.</summary>
  internal static readonly string[] HitHeaders =
    ["Seq", "Time", "Log Time", "Attacker", "Owner", "Opponent", "Type", "Skill", "Damage", "Defense", "Modifiers"];

  internal static readonly string[] CastHeaders = ["Seq", "Time", "Log Time", "Caster", "Spell"];

  internal static readonly string[] HealHeaders =
    ["Seq", "Time", "Log Time", "Caster", "Target", "Heal", "Over Heal", "Spell", "Modifiers"];

  internal static readonly string[] PlayerHeaders = ["Name", "Class", "Kind", "Pet Of"];

  internal static readonly string[] InfoHeaders = ["Key", "Value"];

  /*
   * The identity questions the export asks, injected. This file deliberately knows nothing about rules: a fact row's
   * owner, class and kind are what the timeline answers at export time, so the app passes the same functions its
   * boards read and an export of a board can disagree with the board in exactly zero places.
   */
  internal sealed record Lens(Func<string, bool> IsPlayerSide, Func<string, string> OwnerOf,
    Func<string, string> ClassOf, Func<string, string> KindOf)
  {
    /// <summary>No identity lane: every name reads NPC-side, unclassed. Used by the headless tests.</summary>
    internal static readonly Lens Blind = new(_ => false, _ => null, _ => null, _ => Labels.Unk);
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
        healsBy = WriteHeals(book.AddSheet(new SpreadsheetSpec("Heals by you", HealHeaders, 14)), heals, options, window, byLocal: true);
        healsTo = WriteHeals(book.AddSheet(new SpreadsheetSpec("Heals to you", HealHeaders, 14)), heals, options, window, byLocal: false);
        window.Healing = ScanHealing(heals, options, window);
      }

      playerNames = WritePlayers(book.AddSheet(new SpreadsheetSpec("Players", PlayerHeaders, 18)), facts, options);
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
    var from = window.BeginS - Math.Max(0, options.LeadInS);
    long total = 0;

    foreach (var slice in heals.HealsIn(0, int.MaxValue))
    {
      foreach (var h in slice)
      {
        if (h.TimeS >= from && h.TimeS <= window.EndS) total += h.Total;
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
        row.Text(facts.SubtypeOf(f.SubIdx) ?? string.Empty);
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
    var from = window.BeginS - Math.Max(0, options.LeadInS);
    var rows = 0L;

    foreach (var e in facts.Evidence)
    {
      if (e.Kind != EvidenceFact.EvCast) continue;
      if (e.TimeS < from || e.TimeS > window.EndS) continue;

      using var row = sheet.Row();
      row.Number(e.Seq);
      row.Number(RelativeTime(e.TimeS, window.AnchorS));
      row.Text(IsoTime(e.TimeS));
      row.Text(facts.NameOf(e.NameIdx));
      row.Text(e.AuxIdx >= 0 ? facts.AuxOf(e.AuxIdx) : string.Empty);
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
    var from = window.BeginS - Math.Max(0, options.LeadInS);
    var lens = options.Lens;
    var local = options.LocalPlayer ?? string.Empty;
    var rows = 0L;

    foreach (var slice in heals.HealsIn(0, int.MaxValue))
    {
      foreach (var h in slice)
      {
        if (h.TimeS < from || h.TimeS > window.EndS) continue;

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
        row.Number(h.OverTotal);
        row.Text(heals.SpellOf(h.SubIdx) ?? string.Empty);
        row.Text(LineModifiersParser.Words(h.ModMask));
        rows++;
      }
    }

    return rows;
  }

  // The lookup tab the reporter asked for: every name this capture spoke, with what the identity rules decided.
  private static long WritePlayers(SpreadsheetSheet sheet, DamageFactTable facts, Options options)
  {
    var lens = options.Lens;
    var names = facts.InternedNames;
    long rows = 0;

    for (var i = 0; i < names.Count; i++)
    {
      var name = names[i];
      if (ParserUtil.IsUnattributedName(name)) continue;

      using var row = sheet.Row();
      row.Text(name);
      row.Text(lens.ClassOf(name) ?? string.Empty);
      row.Text(lens.KindOf(name) ?? Labels.Unk);
      row.Text(lens.OwnerOf(name) ?? string.Empty);
      rows++;
    }

    return rows;
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
    Line("Time Column", "seconds from fight start; 1 = the anchor's second, 0 and negative = the lead-in");
    Line("Heal Sheets Filtered By", healRule);
  }

  private static IEnumerable<string> SheetNames(SpreadsheetWorkbook book)
  {
    for (var i = 0; i < book.SheetCount; i++) yield return book.SheetName(i);
  }

  // The time window, when the caller has one. Ordinals arrive already scoped; this narrows further.
  private static bool InTime(long timeS, Options options) =>
    (options.FromTimeS == 0 || timeS >= options.FromTimeS) && (options.ToTimeS == 0 || timeS <= options.ToTimeS);

  // 1 = the anchor's own second, so a cast five seconds early reads -5 the way GamParse's readers expect.
  private static long RelativeTime(long timeS, long anchorS) => anchorS == 0 ? 0 : timeS - anchorS + 1;

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
    public long AnchorS;
    public long BeginS;
    public long EndS;
    public long Damage;
    public long PlayerDamage;
    public long Healing;
    public long Seen;
  }
}
