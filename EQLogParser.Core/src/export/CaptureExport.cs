using System.Globalization;

namespace EQLogParser;

/// <summary>Streams captured records into analysis-ready sheets; selection and identity are supplied by the caller.</summary>
internal static class CaptureExport
{
  internal static readonly string[] HitHeaders =
    ["Seq", "Time", "Log Time", "Attacker", "Owner", "Opponent", "Type", "Skill", "Rank", "Damage", "Defense",
      "Modifiers", "Player", "Class", "Kind", "Skill Full", "Category"];
  internal static readonly string[] CastHeaders =
    ["Seq", "Time", "Log Time", "Caster", "Spell", "Rank", "Direction", "Class", "Spell Full"];
  internal static readonly string[] HealHeaders =
    ["Seq", "Time", "Log Time", "Caster", "Target", "Heal", "Over Heal", "Spell", "Rank", "Modifiers",
      "Type", "Requested Raw", "Spell Full", "Owner", "Class"];
  internal static readonly string[] PlayerHeaders =
    ["Name", "Class", "Kind", "Pets", "Pet Of", "First Seen", "Last Seen"];
  internal static readonly string[] InfoHeaders = ["Key", "Value"];

  internal readonly record struct Identity(bool Side, string Owner, string Class, string Kind);

  internal sealed record Lens(Func<string, long, Identity> At, Func<string, string> CleanSpell)
  {
    internal Lens(Func<string, bool> side, Func<string, string> owner, Func<string, string> playerClass,
      Func<string, string> kind, Func<string, string> cleanSpell)
      : this((name, _) => new Identity(side(name), owner(name) ?? string.Empty,
        playerClass(name) ?? string.Empty, kind(name) ?? Labels.Unk), cleanSpell) { }

    internal static readonly Lens Blind = new((_, _) => new Identity(false, "", "", Labels.Unk), s => s);
  }

  internal readonly record struct Span(long From, long To);

  internal sealed record Options
  {
    public string EventName = string.Empty;
    public string SourceFile = string.Empty;
    public long AnchorS = long.MinValue;
    public long LeadInS = 5;
    public int FromOrdinal;
    public int ToOrdinal = int.MaxValue;
    public int HealLimit = int.MaxValue;
    public int EvidenceLimit = int.MaxValue;
    public long FromTimeS;
    public long ToTimeS;
    public string LocalPlayer = string.Empty;
    public int SessionId;
    public Lens Lens = Lens.Blind;

    // Null means the whole ordinal range. An empty selection must export no damage, not the whole capture.
    public int[] DamageOrdinals;
    public Span[] Windows;
    public EvidenceFact[] EvidenceRows;
  }

  internal sealed record Summary(long PlayerRows, long NpcRows, long CastRows, long HealsByRows, long HealsToRows,
    long PlayerNames, long Damage, long Healing, long BeginS, long EndS, long Bytes)
  {
    public long HealRows { get; init; }
    public override string ToString() =>
      $"player={PlayerRows} npc={NpcRows} casts={CastRows} heals={HealRows} healsBy={HealsByRows} " +
      $"healsTo={HealsToRows} players={PlayerNames} damage={Damage} healing={Healing} bytes={Bytes}";
  }

  internal static Summary Write(string path, DamageFactTable facts, HealFactTable heals, Options options)
  {
    // Every pass reads the same prefixes, including when a live capture appends inside the selected last second.
    options = options with
    {
      ToOrdinal = Math.Min(options.ToOrdinal, facts.FactCount),
      HealLimit = Math.Min(options.HealLimit, heals?.HealCount ?? 0),
      EvidenceRows = options.EvidenceRows
        ?? facts.Evidence[..Math.Min(options.EvidenceLimit, facts.EvidenceCount)].ToArray(),
      Windows = options.Windows is null ? null : Merge(options.Windows),
      DamageOrdinals = CopyOrdinals(options.DamageOrdinals)
    };
    var window = ScanWindow(facts, options);
    var spans = AuxiliarySpans(window, options);
    long playerRows, npcRows, castRows, healRows = 0, healsBy = 0, healsTo = 0, playerNames;
    using (var book = SpreadsheetWriter.Open(path, "Player DPS", HitHeaders, 14))
    {
      playerRows = WriteHits(book.CurrentSheet, facts, options, window, true);
      npcRows = WriteHits(book.AddSheet(new SpreadsheetSpec("NPC DPS", HitHeaders, 14)), facts, options, window, false);
      castRows = WriteCasts(book.AddSheet(new SpreadsheetSpec("Spells cast", CastHeaders, 16)),
        facts, options, window, spans);
      if (heals != null)
      {
        window.Healing = ScanHealing(heals, options, window, spans);
        healRows = WriteHeals(book.AddSheet(new SpreadsheetSpec("All heals", HealHeaders, 14)),
          heals, options, window, spans, 0);
        healsBy = WriteHeals(book.AddSheet(new SpreadsheetSpec("Heals by you", HealHeaders, 14)),
          heals, options, window, spans, 1);
        healsTo = WriteHeals(book.AddSheet(new SpreadsheetSpec("Heals to you", HealHeaders, 14)),
          heals, options, window, spans, 2);
      }
      playerNames = WritePlayers(book.AddSheet(new SpreadsheetSpec("Players", PlayerHeaders, 18)), window);
      WriteInfo(book.AddSheet(new SpreadsheetSpec("Event Info", InfoHeaders, 26)), options, window, book,
        playerRows + npcRows, castRows, healRows, healsBy, healsTo, playerNames);
    }
    return new Summary(playerRows, npcRows, castRows, healsBy, healsTo, playerNames, window.Damage, window.Healing,
      window.BeginS, window.EndS, new FileInfo(path).Length) { HealRows = healRows };
  }

  private static IEnumerable<int> Ordinals(Options options)
  {
    if (options.DamageOrdinals is { } selected)
    {
      foreach (var ordinal in selected)
        if (ordinal >= options.FromOrdinal && ordinal < options.ToOrdinal) yield return ordinal;
    }
    else
    {
      for (var i = Math.Max(0, options.FromOrdinal); i < options.ToOrdinal; i++) yield return i;
    }
  }

  private static Window ScanWindow(DamageFactTable facts, Options options)
  {
    var result = new Window();
    foreach (var ordinal in Ordinals(options))
    {
      var f = facts.At(ordinal);
      if (!InTime(f.TimeS, options)) continue;
      AddName(result, facts.NameOf(f.AtkIdx), f.TimeS, options);
      AddName(result, facts.NameOf(f.DefIdx), f.TimeS, options);
      result.Damage += f.Total;
      if (options.Lens.At(facts.NameOf(f.AtkIdx), f.TimeS).Side) result.PlayerDamage += f.Total;
      if (result.Seen++ == 0) result.BeginS = result.EndS = f.TimeS;
      else
      {
        result.BeginS = Math.Min(result.BeginS, f.TimeS);
        result.EndS = Math.Max(result.EndS, f.TimeS);
      }
    }
    if (options.Windows is { Length: > 0 } windows)
    {
      result.BeginS = windows.Min(s => s.From);
      result.EndS = windows.Max(s => s.To);
    }
    else
    {
      if (options.FromTimeS > 0) result.BeginS = options.FromTimeS;
      if (options.ToTimeS > 0) result.EndS = options.ToTimeS;
    }
    result.AnchorS = options.AnchorS != long.MinValue ? options.AnchorS : result.BeginS;
    return result;
  }

  private static long ScanHealing(HealFactTable heals, Options options, Window window, Span[] spans)
  {
    long total = 0;
    foreach (var slice in heals.HealsIn(0, options.HealLimit))
      foreach (var h in slice)
      {
        if (!Contains(spans, h.TimeS)) continue;
        total += h.Total;
        AddName(window, heals.NameOf(h.HealerIdx), h.TimeS, options);
        AddName(window, heals.NameOf(h.HealedIdx), h.TimeS, options);
      }
    return total;
  }

  private static long WriteHits(SpreadsheetSheet sheet, DamageFactTable facts, Options options,
    Window window, bool side)
  {
    long rows = 0;
    foreach (var ordinal in Ordinals(options))
    {
      var f = facts.At(ordinal);
      if (!InTime(f.TimeS, options)) continue;
      var attacker = facts.NameOf(f.AtkIdx);
      var identity = options.Lens.At(attacker, f.TimeS);
      if (identity.Side != side) continue;
      var skill = facts.SubtypeOf(f.SubIdx) ?? string.Empty;
      using var row = sheet.Row();
      row.Number(f.Seq);
      row.Number(RelativeTime(f.TimeS, window.AnchorS));
      row.Text(IsoTime(f.TimeS));
      row.Text(attacker);
      row.Text(identity.Owner);
      row.Text(facts.NameOf(f.DefIdx));
      var category = LabelTypes.LabelOf(f.TypeId) ?? Labels.Unk;
      row.Text(f.TypeId == LabelTypes.Melee && skill.Length > 0 ? skill : category);
      row.Text(CleanSpell(skill, options.Lens));
      row.Text(RankOf(skill, options.Lens));
      row.Number(f.Total);
      row.Text(DefenseWord(f.TypeId));
      row.Text(LineModifiersParser.Words(f.ModMask));
      row.Text(!string.IsNullOrEmpty(identity.Owner) ? identity.Owner : identity.Side ? attacker : string.Empty);
      row.Text(!string.IsNullOrEmpty(identity.Owner) ? options.Lens.At(identity.Owner, f.TimeS).Class : identity.Class);
      row.Text(identity.Kind);
      row.Text(skill);
      row.Text(category);
      rows++;
    }
    return rows;
  }

  private static long WriteCasts(SpreadsheetSheet sheet, DamageFactTable facts, Options options,
    Window window, Span[] spans)
  {
    long rows = 0;
    foreach (var e in options.EvidenceRows)
    {
      if (e.Kind != EvidenceFact.EvCast || !Contains(spans, e.TimeS)) continue;
      var name = facts.NameOf(e.NameIdx);
      AddName(window, name, e.TimeS, options);
      var spell = facts.AuxOf(e.AuxIdx) ?? string.Empty;
      using var row = sheet.Row();
      row.Number(e.Seq);
      row.Number(RelativeTime(e.TimeS, window.AnchorS));
      row.Text(IsoTime(e.TimeS));
      row.Text(name);
      row.Text(CleanSpell(spell, options.Lens));
      row.Text(RankOf(spell, options.Lens));
      row.Text("Cast");
      row.Text(options.Lens.At(name, e.TimeS).Class);
      row.Text(spell);
      rows++;
    }
    return rows;
  }

  private static long WriteHeals(SpreadsheetSheet sheet, HealFactTable heals, Options options, Window window,
    Span[] spans, int filter)
  {
    long rows = 0;
    foreach (var slice in heals.HealsIn(0, options.HealLimit))
      foreach (var h in slice)
      {
        if (!Contains(spans, h.TimeS)) continue;
        var healer = heals.NameOf(h.HealerIdx);
        var healed = heals.NameOf(h.HealedIdx);
        var local = options.LocalPlayer ?? string.Empty;
        if (filter != 0)
        {
          var subject = filter == 1 ? healer : healed;
          var keep = local.Length > 0 ? subject.Equals(local, StringComparison.OrdinalIgnoreCase)
            : options.Lens.At(subject, h.TimeS).Side;
          if (!keep) continue;
        }
        var spell = heals.SpellOf(h.SubIdx) ?? string.Empty;
        var identity = options.Lens.At(healer, h.TimeS);
        using var row = sheet.Row();
        row.Number(h.Seq);
        row.Number(RelativeTime(h.TimeS, window.AnchorS));
        row.Text(IsoTime(h.TimeS));
        row.Text(healer);
        row.Text(healed);
        row.Number(h.Total);
        row.Number(h.Overheal);
        row.Text(CleanSpell(spell, options.Lens));
        row.Text(RankOf(spell, options.Lens));
        row.Text(LineModifiersParser.Words(h.ModMask));
        row.Text(LabelTypes.LabelOf(h.TypeId));
        row.Number(h.OverTotal);
        row.Text(spell);
        row.Text(identity.Owner);
        row.Text(identity.Class);
        rows++;
      }
    return rows;
  }

  private static void AddName(Window window, string name, long time, Options options)
  {
    if (string.IsNullOrEmpty(name) || ParserUtil.IsUnattributedName(name)) return;
    var identity = options.Lens.At(name, time);
    Add(name, identity);
    if (!string.IsNullOrEmpty(identity.Owner) && !identity.Owner.Equals(name, StringComparison.OrdinalIgnoreCase))
      Add(identity.Owner, options.Lens.At(identity.Owner, time));

    void Add(string actor, Identity answer)
    {
      if (!window.Names.TryGetValue(actor, out var entries)) window.Names[actor] = entries = [];
      foreach (var entry in entries)
      {
        if (entry.Identity != answer) continue;
        entry.First = Math.Min(entry.First, time);
        entry.Last = Math.Max(entry.Last, time);
        return;
      }
      entries.Add(new CensusEntry(answer, time));
    }
  }

  private static long WritePlayers(SpreadsheetSheet sheet, Window window)
  {
    var pets = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
    foreach (var pair in window.Names)
      foreach (var entry in pair.Value)
      {
        var owner = entry.Identity.Owner;
        if (string.IsNullOrEmpty(owner) || owner.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)) continue;
        if (!pets.TryGetValue(owner, out var names)) pets[owner] = names = new(StringComparer.OrdinalIgnoreCase);
        names.Add(pair.Key);
      }
    long rows = 0;
    foreach (var pair in window.Names.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
      foreach (var entry in pair.Value.OrderBy(entry => entry.First))
      {
        using var row = sheet.Row();
        row.Text(pair.Key);
        row.Text(entry.Identity.Class);
        row.Text(entry.Identity.Kind);
        row.Text(pets.TryGetValue(pair.Key, out var names) ? string.Join(", ", names) : string.Empty);
        row.Text(entry.Identity.Owner);
        row.Text(IsoTime(entry.First));
        row.Text(IsoTime(entry.Last));
        rows++;
      }
    return rows;
  }

  private static void WriteInfo(SpreadsheetSheet sheet, Options options, Window window, SpreadsheetWorkbook book,
    long damageRows, long casts, long heals, long by, long to, long names)
  {
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
    var spans = Merge(options.Windows ?? [new Span(window.BeginS, window.EndS)]);
    var seconds = window.Seen == 0 ? 0 : spans.Sum(span => span.To - span.From + 1);
    Line("Seconds", seconds.ToString(CultureInfo.InvariantCulture));
    Line("Lead In Seconds", Math.Max(0, options.LeadInS).ToString(CultureInfo.InvariantCulture));
    Line("Damage Rows", damageRows.ToString(CultureInfo.InvariantCulture));
    Line("Player Damage", window.PlayerDamage.ToString(CultureInfo.InvariantCulture));
    Line("Total Damage", window.Damage.ToString(CultureInfo.InvariantCulture));
    Line("Healing", window.Healing.ToString(CultureInfo.InvariantCulture));
    Line("All Heal Rows", heals.ToString(CultureInfo.InvariantCulture));
    Line("Cast Rows", casts.ToString(CultureInfo.InvariantCulture));
    Line("Heal Rows By Side", by.ToString(CultureInfo.InvariantCulture));
    Line("Heal Rows To Side", to.ToString(CultureInfo.InvariantCulture));
    Line("Names Listed", names.ToString(CultureInfo.InvariantCulture));
    Line("Sheet Names", string.Join(", ", Enumerable.Range(0, book.SheetCount).Select(book.SheetName)));
    Line("Scope", options.DamageOrdinals is null ? "damage inside the requested window" :
      "selected fight fact membership; casts and heals use the union of selected lifetimes plus lead-in");
    Line("Lookup Scope", "names seen in this window, widened for casts and heals; "
      + "separate rows for observed class/owner changes");
    Line("Time Column", "seconds from fight start: 1 = the anchor's second, -1 = one second before it (no zero)");
    Line("Rank Column", "rank tail removed from Spell/Skill; the original text is preserved in Spell Full/Skill Full");
    Line("Player Column", "owner for an owned attacker; attacker for a player-side name without a resolved owner");
    Line("Class Columns", "class observed at the record's log time, saved class as fallback; "
      + "damage Class follows Player");
    Line("Heal Sheets Filtered By", string.IsNullOrEmpty(options.LocalPlayer)
      ? "no local player detected - raid-side rows on both sheets" : options.LocalPlayer);
    Line("All heals", "authoritative captured healing, once per record; "
      + "the local-player sheets overlap and are views only");
    Line("Requested Raw", "verbatim parenthesized amount; 0 is also the parser default when that amount is absent");
    Line("Seq", "capture event order, NOT an original log line number or file position");
    Line("Cast Coverage", "captured cast events only; receive events are not captured by this export");
    Line("Capture Coverage", "parsed records, not a lossless copy of the log; "
      + "unnamed/passive heals and ignored entities may be absent");
    Line("NPC DPS", "non-player-side damage, including Unknown and Spell identities; Kind disambiguates them");
  }

  private static bool InTime(long time, Options options) =>
    (options.FromTimeS == 0 || time >= options.FromTimeS) && (options.ToTimeS == 0 || time <= options.ToTimeS)
    && (options.Windows is null || Contains(options.Windows, time));

  private static Span[] AuxiliarySpans(Window window, Options options) => Merge(
    (options.Windows ?? [new Span(window.BeginS, window.EndS)])
      .Select(span => new Span(span.From - Math.Max(0, options.LeadInS), span.To)));

  private static Span[] Merge(IEnumerable<Span> source)
  {
    var result = new List<Span>();
    foreach (var span in source.Where(span => span.To >= span.From).OrderBy(span => span.From))
    {
      if (result.Count == 0 || span.From > result[^1].To + 1) result.Add(span);
      else result[^1] = new Span(result[^1].From, Math.Max(result[^1].To, span.To));
    }
    return result.ToArray();
  }

  private static bool Contains(Span[] spans, long time)
  {
    var lo = 0;
    var hi = spans.Length;
    while (lo < hi)
    {
      var mid = lo + ((hi - lo) / 2);
      if (spans[mid].From <= time) lo = mid + 1;
      else hi = mid;
    }
    return lo > 0 && time <= spans[lo - 1].To;
  }

  private static int[] CopyOrdinals(int[] source)
  {
    if (source is null) return null;
    var sorted = source.ToArray();
    Array.Sort(sorted);
    var count = 0;
    foreach (var ordinal in sorted)
      if (count == 0 || ordinal != sorted[count - 1]) sorted[count++] = ordinal;
    if (count < sorted.Length) Array.Resize(ref sorted, count);
    return sorted;
  }

  private static long RelativeTime(long time, long anchor) =>
    anchor == 0 ? 0 : time >= anchor ? time - anchor + 1 : time - anchor;
  private static string IsoTime(long time) => time <= 0 ? string.Empty :
    DateUtil.FromDotNetSeconds(time).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
  private static string CleanSpell(string full, Lens lens) =>
    string.IsNullOrEmpty(full) ? string.Empty : lens.CleanSpell(full) ?? full;
  private static string RankOf(string full, Lens lens)
  {
    var clean = CleanSpell(full, lens);
    return clean.Length > 0 && clean.Length < full.Length && full.StartsWith(clean, StringComparison.Ordinal)
      ? full[clean.Length..].Trim() : string.Empty;
  }
  private static string DefenseWord(byte type) => type switch
  {
    LabelTypes.Miss => Labels.Miss, LabelTypes.Dodge => Labels.Dodge, LabelTypes.Parry => Labels.Parry,
    LabelTypes.Block => Labels.Block, LabelTypes.Riposte => Labels.Riposte,
    LabelTypes.Invulnerable => Labels.Invulnerable,
    LabelTypes.Absorb => Labels.Absorb, _ => "Hit"
  };

  private sealed class CensusEntry(Identity identity, long time)
  {
    internal readonly Identity Identity = identity;
    internal long First = time;
    internal long Last = time;
  }

  private sealed class Window
  {
    internal readonly Dictionary<string, List<CensusEntry>> Names = new(StringComparer.OrdinalIgnoreCase);
    internal long AnchorS, BeginS, EndS, Damage, PlayerDamage, Healing, Seen;
  }
}
