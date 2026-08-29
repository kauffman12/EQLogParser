using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQLogParser;

/* Import outcome of one NAG trigger. Fixed set of related values, so an enum rather than the
 * "Imported"/"Partial"/"Skipped" magic strings it replaced (CodingStandards: Enums). */
internal enum NagImportStatus
{
  Imported,
  Partial,
  Skipped
}

internal class NagImportResult
{
  public string TriggerName { get; set; }
  public string TriggerId { get; set; }

  /* The real status. Status is its text form for the HTML report and existing consumers. */
  public NagImportStatus ImportStatus { get; set; } = NagImportStatus.Imported;
  public string Status => ImportStatus.ToString();
  public string Reason { get; set; } // For skipped/partial: which features were unsupported
  public string FolderPath { get; set; }
  public string ActionsSummary { get; set; }
  public double Score { get; set; } = 0.5;
  public List<string> DroppedFeatures { get; set; }
  public List<string> MissingAudioFiles { get; set; } = [];
}

internal static class NagUtil
{
  private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

  // NAG template syntax: ${var} or {groupName} → EQLP: {$var} or {$groupName}
  private static readonly Regex VarPattern = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

  private static readonly Regex GroupPattern = new(@"(?<!\$)\{([a-zA-Z][^}]*)\}", RegexOptions.Compiled);

  // NAG "unlimited" countdown repeat has no finite EQLP equivalent — approximate
  // with a very large loop count.
  private const long UnlimitedRepeatLoops = 999999;

  // NAG interruptSpeech note. Listed in droppedFeatures for report transparency, but it is an
  // implemented approximation (priority 1) rather than a missing feature, so it does not
  // downgrade the trigger's status to Partial on its own.
  internal const string InterruptSpeechNote = "speech interruption (approximated as priority 1)";

  // OverlayData.Source prefix for NAG-imported overlays: "nag:{overlayId}". EQLP node ids are
  // always store-generated UUIDs, so the NAG identity travels in Source instead — the store
  // matches it on re-import to update the existing overlay (see TriggerStateDB.Import).
  internal const string NagSourcePrefix = "nag:";

  // Dropped-feature notes that are approximations of implemented behavior — they stay in the
  // report/Comments but do not affect import status. True gaps still mark a trigger Partial.
  private static readonly HashSet<string> NonStatusDroppedFeatures = new() { InterruptSpeechNote };

  // Action types whose phrase scoping the import routes per-phrase node (text/sound/TTS/clipboard
  // values via RouteActionValue, variable effects via SetVariables/PhraseClearVariables). Every
  // other scoped action type (timers, counters) still applies to all phrase nodes.
  private static readonly HashSet<int> RouteablePhraseScopedTypes = new() { 0, 1, 2, 5, 7, 9 };

  // NAG ${varName} in regex phrases — not supported by EQLP, replace with (?<varName>.+?)
  // Exception: ${Character} maps to EQLP's native {c} (replaced with player name at runtime).
  private static readonly Regex DollarVarRegex = new(@"\$\{(\w+)\}", RegexOptions.Compiled);

  // NAG {VAR} in regex phrases that EQLP does NOT handle at runtime.
  // Excluded (passed through for EQLP runtime handling):
  //   {S}/{s}/{N}/{n} + digit suffixes — CheckOptions() string/number captures
  //   {TS}/{ts} — CheckOptions() timer duration
  //   {C}/{c} — TriggerProcessor replaces with player character name at runtime
  // Everything else becomes a named capture group:
  //   {LN} → (?<LN>\w+) (player/NPC name, single word)
  //   {target} → (?<target>.+?) (NPC names can have spaces, commas, quotes)
  //   ${SpellBeingCast} → {SpellBeingCast} in display text (handled by VarPattern above)
  private static readonly Regex UnhandledVarRegex = new(
    @"(?<!\$)\{(?!S\d?|s\d?|N\d?|n\d?|TS|ts|[Cc])[a-zA-Z][^}]*\}",
    RegexOptions.Compiled);

  private static Dictionary<string, string> _audioFileMap;

  // Parse NAG conditions into EQLP MatchVariableCondition syntax.
  //
  // NAG OperatorTypes (verified against the NAG v0.2.26 engine source —
  // src/electron/data/models/trigger.js and checkCondition in log-watcher.js):
  //   0=IsNull, 1=Equals, 2=DoesNotEqual, 4=LessThan, 8=GreaterThan, 16=Contains.
  // Equals matches a stored value exactly against NAG's pipe-separated condition values
  // (case-sensitive in NAG); Contains is a case-insensitive substring check. EQLP's
  // condition evaluator treats both "=" and "contains" case-insensitively, so the mapping is:
  //   0 (IsNull)       → !{var}                        (variable has no stored value)
  //   1 (Equals)       → {var} = "A" || {var} = "B"    (per-value equality, OR-combined)
  //   2 (DoesNotEqual) → with values: !({var} = "A" || ...); without a value: {var} (must be set)
  //   16 (Contains)    → {var} contains "A" || ...     (per-value substring, OR-combined)
  // Operators and condition types EQLP cannot express are reported through droppedFeatures.
  internal static string ParseConditions(JsonElement element, ICollection<string> droppedFeatures)
  {
    if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
    {
      return null;
    }

    var parts = new List<string>();
    foreach (var cond in element.EnumerateArray())
    {
      var conditionType = cond.TryGetProperty("conditionType", out var ct) ? ReadInt(ct, -1) : -1;
      if (conditionType != 1) // Only variable-based conditions are expressible in EQLP
      {
        droppedFeatures.Add(conditionType == 3 ? "counter condition" : $"condition type {conditionType}");
        continue;
      }

      var varName = cond.TryGetProperty("variableName", out var vn) ? vn.GetString() : null;
      if (string.IsNullOrEmpty(varName))
      {
        continue;
      }

      var operatorType = cond.TryGetProperty("operatorType", out var ot) && ot.ValueKind != JsonValueKind.Null
        ? ReadInt(ot, -1)
        : -1;
      var value = cond.TryGetProperty("variableValue", out var vv) && vv.ValueKind == JsonValueKind.String
        ? vv.GetString()
        : null;

      // NAG separates multiple condition values with |. Split them and OR-combine the
      // clauses; callers parenthesize multi-clause results so they cannot leak across a
      // larger "&&" join (EQLP's condition grammar binds AND tighter than OR).
      (string Joined, bool Multiple) OrClauses(Func<string, string> clause)
      {
        var values = value.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (values.Length == 0)
        {
          return (null, false);
        }

        return (string.Join(" || ", values.Select(clause)), values.Length > 1);
      }

      static string Escaped(string v) => v.Replace("\\", "\\\\").Replace("\"", "\\\"");

      (string Joined, bool Multiple) orClauses = (null, false);
      string part = null;
      switch (operatorType)
      {
        // IsNull: passes while the variable has no stored value.
        case 0:
          part = $"!{{{varName}}}";
          break;

        // Equals: exact match against any of NAG's pipe-separated values.
        case 1:
          {
            orClauses = OrClauses(v => $"{{{varName}}} = \"{Escaped(v)}\"");
            part = orClauses.Joined is null ? null : orClauses.Multiple ? $"({orClauses.Joined})" : orClauses.Joined;
            break;
          }

        // DoesNotEqual: with values, no stored value may equal a condition value (an unset
        // variable passes); without a value, NAG passes only when the variable has at
        // least one stored value.
        case 2:
          {
            if (string.IsNullOrEmpty(value))
            {
              part = $"{{{varName}}}";
            }
            else
            {
              // The negation must bind to the whole OR group, so always parenthesize.
              orClauses = OrClauses(v => $"{{{varName}}} = \"{Escaped(v)}\"");
              part = orClauses.Joined is null ? null : $"!({orClauses.Joined})";
            }
            break;
          }

        // Contains: case-insensitive substring of any pipe-separated value.
        case 16:
          {
            orClauses = OrClauses(v => $"{{{varName}}} contains \"{Escaped(v)}\"");
            part = orClauses.Joined is null ? null : orClauses.Multiple ? $"({orClauses.Joined})" : orClauses.Joined;
            break;
          }
      }

      if (part is null)
      {
        droppedFeatures.Add($"condition operator {operatorType} on {varName}");
      }
      else
      {
        parts.Add(part);
      }
    }

    return parts.Count > 0 ? string.Join(" && ", parts) : null;
  }

  internal static (List<ExportTriggerNode> nodes, List<NagImportResult> results) ConvertTriggers(string json, string databaseDirectory = null)
  {
    var nodes = new List<ExportTriggerNode>();
    var results = new List<NagImportResult>();

    try
    {
      // Always reset so a call without a database directory can't inherit the previous
      // caller's map (stale resolution would corrupt SoundToPlay and missing-audio tracking).
      _audioFileMap = null;
      if (!string.IsNullOrEmpty(databaseDirectory))
      {
        LoadAudioFileMap(databaseDirectory);
      }

      using var doc = JsonDocument.Parse(json);
      var root = doc.RootElement;

      // Parse folder structure for tree hierarchy
      var folderPaths = new Dictionary<string, string>();
      if (root.TryGetProperty("folders", out var foldersElem))
      {
        foreach (var folder in foldersElem.EnumerateArray())
        {
          ParseFolderStructure(folder, "", folderPaths);
        }
      }

      var triggers = root.GetProperty("triggers");
      // Folder nodes shared across all triggers (keyed by full path), so every trigger in the
      // same NAG folder attaches to one chain instead of creating per-trigger duplicates.
      var folderNodes = new Dictionary<string, ExportTriggerNode>(StringComparer.Ordinal);
      foreach (var trigger in triggers.EnumerateArray())
      {
        // Parse each trigger in its own try/catch so one malformed trigger is reported as
        // Skipped instead of aborting the import of every remaining trigger.
        List<ExportTriggerNode> triggerNodes;
        NagImportResult parsedResult;
        try
        {
          (triggerNodes, parsedResult) = ParseTrigger(trigger);
        }
        catch (Exception ex)
        {
          var triggerName = GetTriggerDisplayName(trigger);
          Log.Warn($"NAG import: failed to parse trigger '{triggerName}': {ex.Message}");
          triggerNodes = [];
          parsedResult = new NagImportResult
          {
            TriggerName = triggerName,
            ImportStatus = NagImportStatus.Skipped,
            Reason = $"Error parsing trigger: {ex.Message}"
          };
        }

        foreach (var n in triggerNodes)
        {
          // Set folder path on the result for reporting. Triggers whose parent folder no
          // longer exists (e.g. after a package uninstall) go to "Orphaned Triggers",
          // mirroring NAG's own startup re-filing (trigger-database.js findOrphanedTriggers).
          // Flattening them to the root instead would merge same-named triggers from
          // different former folders into one import dedup bucket.
          if (trigger.TryGetProperty("folderId", out var fid) &&
              fid.GetString() is { } folderId)
          {
            parsedResult.FolderPath = folderPaths.TryGetValue(folderId, out var fpath) ? fpath : "Orphaned Triggers";
          }
          else
          {
            parsedResult.FolderPath = "(root)";
          }

          // Attach trigger node to the shared folder tree (or the root) if not at root level.
          // All triggers under one NAG folder reuse the same folder nodes, mirroring NAG's own
          // hierarchy — per-trigger chains would produce siblings like "Common", "Common (2)"…
          if (parsedResult.FolderPath != "(root)")
          {
            EnsureFolderPath(folderNodes, nodes, parsedResult.FolderPath).Nodes.Add(n);
          }
          else
          {
            nodes.Add(n);
          }
        }
        results.Add(parsedResult);
      }
    }
    catch (Exception ex)
    {
      Log.Error("Error Parsing NAG Triggers", ex);
    }

    // Wrap all nodes in a root node — consistent with GINA export format
    // so the first Import() overload skips the root and processes folders correctly
    var rootNode = new ExportTriggerNode { Nodes = nodes };

    // NAG allows several children with the same name under one parent. The trigger store keys
    // children by (kind, name), so duplicate names would merge into or shadow each other on
    // re-import — normalize to unique sibling names here, the same normalization the app's own
    // .tgf exports already contain.
    UniquifySiblingNames(rootNode);

    return (new List<ExportTriggerNode> { rootNode }, results);
  }

  internal static void WriteImportReportHtml(List<NagImportResult> results, string outputPath, int skippedFctOverlays = 0,
      List<string> overlayNotes = null)
  {
    try
    {
      var sb = new StringBuilder();
      sb.AppendLine("<!DOCTYPE html>");
      sb.AppendLine("<html lang=\"en\">\n<head>");
      sb.AppendLine("<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>NAG Import Report</title>");
      sb.AppendLine("<style>");
      sb.AppendLine("body { font-family: 'Segoe UI', Arial, sans-serif; margin: 20px; background: #f5f5f5; color: #333; }");
      sb.AppendLine("h1 { color: #1976d2; margin-bottom: 5px; }");
      sb.AppendLine(".summary { display: flex; gap: 12px; margin: 16px 0; flex-wrap: wrap; }");
      sb.AppendLine(".stat { background: #fff; border-radius: 8px; padding: 12px 20px; box-shadow: 0 1px 3px rgba(0,0,0,0.1); text-align: center; min-width: 100px; }");
      sb.AppendLine(".stat .num { font-size: 28px; font-weight: bold; display: block; }");
      sb.AppendLine(".stat .label { font-size: 12px; color: #666; text-transform: uppercase; }");
      sb.AppendLine(".stat.imported .num { color: #388e3c; }");
      sb.AppendLine(".stat.partial .num { color: #f57c00; }");
      sb.AppendLine(".stat.skipped .num { color: #d32f2f; }");
      sb.AppendLine(".note { margin: 0 0 16px; font-size: 13px; color: #555; }");
      sb.AppendLine("table { border-collapse: collapse; width: 100%; background: #fff; box-shadow: 0 1px 3px rgba(0,0,0,0.1); border-radius: 8px; overflow: hidden; }");
      sb.AppendLine("th { background: #e3f2fd; color: #1976d2; padding: 10px 12px; text-align: left; font-size: 13px; position: sticky; top: 0; border-bottom: 1px solid #ddd; }");
      sb.AppendLine("td { padding: 8px 12px; border-bottom: 1px solid #eee; font-size: 13px; }");
      sb.AppendLine("tr:hover td { background: #f5faff; }");
      sb.AppendLine(".badge { display: inline-block; padding: 2px 8px; border-radius: 10px; font-size: 11px; font-weight: bold; }");
      sb.AppendLine(".badge-imported { background: #c8e6c9; color: #1b5e20; }");
      sb.AppendLine(".badge-partial { background: #fff9c4; color: #f57f17; }");
      sb.AppendLine(".badge-skipped { background: #ffcdd2; color: #b71c1c; }");
      sb.AppendLine(".folder { font-family: monospace; font-size: 12px; color: #666; }");
      sb.AppendLine(".missing-audio { font-size: 11px; color: #b71c1c; font-weight: bold; }");
      sb.AppendLine(".actions { max-width: 300px; word-break: break-word; }");
      sb.AppendLine(".reason { max-width: 250px; word-break: break-word; font-size: 12px; }");
      sb.AppendLine("th .folder-col { width: 220px; }");
      sb.AppendLine("th .actions-col { width: 280px; }");
      sb.AppendLine("th .reason-col { width: 200px; }");
      sb.AppendLine("</style>\n</head>\n<body>");

      var total = results.Count;
      var imported = results.Count(r => r.ImportStatus == NagImportStatus.Imported);
      var partial = results.Count(r => r.ImportStatus == NagImportStatus.Partial);
      var skipped = results.Count(r => r.ImportStatus == NagImportStatus.Skipped);

      sb.AppendLine($"<h1>NAG Import Report</h1>");
      sb.AppendLine($"<div class=\"summary\">\n<div class=\"stat imported\"><span class=\"num\">{imported}</span><span class=\"label\">Success</span></div>\n<div class=\"stat partial\"><span class=\"num\">{partial}</span><span class=\"label\">Partial</span></div>\n<div class=\"stat skipped\"><span class=\"num\">{skipped}</span><span class=\"label\">Skipped</span></div>\n<div class=\"stat\"><span class=\"num\">{total}</span><span class=\"label\">Total</span></div>\n</div>");
      var fctNote = skippedFctOverlays > 0 ? $" {skippedFctOverlays} FCT overlay(s) were not imported (no EQLP equivalent)." : "";
      sb.AppendLine($"<div class=\"note\">All imported triggers start <b>disabled</b>. NAG per-character enable states are not imported — enable what you need in the Triggers view.{fctNote}</div>");

      // Reduced-fidelity overlay notes (e.g. timer sort order reversed vs NAG)
      if (overlayNotes is { Count: > 0 })
      {
        sb.AppendLine($"<div class=\"note\">{string.Join("<br>", overlayNotes.Select(HtmlEncode))}</div>");
      }

      sb.AppendLine("<table>\n<thead>\n<tr><th>Trigger</th><th>Status</th><th class=\"folder-col\">Folder Path</th><th class=\"actions-col\">Actions</th><th class=\"reason-col\">Details / Reason</th><th>Missing Audio</th></tr>\n</thead>\n<tbody>");

      // Sort: Skipped first, then Partial, then Success (Imported)
      var sorted = results.OrderBy(r => r.ImportStatus switch
      {
        NagImportStatus.Skipped => 0,
        NagImportStatus.Partial => 1,
        NagImportStatus.Imported => 2,
        _ => 3
      }).ToList();

      foreach (var r in sorted)
      {
        var badgeClass = r.ImportStatus switch
        {
          NagImportStatus.Imported => "badge-imported",
          NagImportStatus.Partial => "badge-partial",
          NagImportStatus.Skipped => "badge-skipped",
          _ => ""
        };
        // Display "Success" instead of "Imported" in the report
        var displayStatus = r.ImportStatus == NagImportStatus.Imported ? "Success" : r.Status;
        var badge = $"<span class=\"badge {badgeClass}\">{displayStatus}</span>";
        var folder = string.IsNullOrEmpty(r.FolderPath) || r.FolderPath == "(root)"
          ? "<em>(root)</em>" : $"<span class=\"folder\">{HtmlEncode(r.FolderPath)}</span>";
        var actions = HtmlEncode(r.ActionsSummary ?? "");
        var reason = string.IsNullOrEmpty(r.Reason) ? "—" : FormatDroppedFeaturesForReport(r.Reason);
        var missingAudio = r.MissingAudioFiles?.Count > 0
          ? $"<div class=\"missing-audio\">{string.Join("<br>", r.MissingAudioFiles.Select(HtmlEncode))}</div>"
          : "—";
        sb.AppendLine($"<tr><td>{HtmlEncode(r.TriggerName)}</td><td>{badge}</td><td>{folder}</td><td class=\"actions\">{actions}</td><td class=\"reason\">{reason}</td><td>{missingAudio}</td></tr>");
      }

      sb.AppendLine("</tbody>\n</table>\n</body>\n</html>");
      File.WriteAllText(outputPath, sb.ToString());
    }
    catch (Exception ex)
    {
      Log.Error("Error writing HTML import report", ex);
    }
  }

  /// <summary>
  /// Converts NAG overlay JSON to EQLP export trigger nodes (overlay definitions only, no trigger links).
  /// </summary>
  internal static List<ExportTriggerNode> ConvertOverlays(string json, out int skippedFctOverlays, out List<string> overlayNotes)
  {
    var result = new List<ExportTriggerNode>();
    skippedFctOverlays = 0;
    // Notes for features imported with reduced fidelity (e.g. reversed timer sort order),
    // surfaced in the import report and completion dialog.
    overlayNotes = [];

    try
    {
      using var doc = JsonDocument.Parse(json);
      var overlays = doc.RootElement.GetProperty("overlays");

      foreach (var overlay in overlays.EnumerateArray())
      {
        // FCT (Fight Combat Tracker) overlays have no EQLP equivalent — skip and report them.
        var overlayType = overlay.TryGetProperty("overlayType", out var t) ? t.GetString() : null;
        if (overlayType?.Equals("FCT", StringComparison.OrdinalIgnoreCase) == true)
        {
          skippedFctOverlays++;
          continue;
        }

        // NAG 'Ascending' timer sort (most time remaining first — overlay.js sorts by dir * timeRemaining,
        // dir = -1 for Ascending) has no EQLP equivalent. The overlay still imports with 'Remaining Time'
        // sorting, so its timers display in the opposite order from NAG — note it for the import report.
        if (overlay.TryGetProperty("timerSortType", out var sortProp) && ReadInt(sortProp, 0) == 1)
        {
          var overlayName = overlay.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "(unnamed)" : "(unnamed)";
          var note = $"Overlay \"{overlayName}\": NAG 'Ascending' timer sort (most time remaining first) has no" +
            " EQLP equivalent — imported as 'Remaining Time', so timers display in reversed order.";
          overlayNotes.Add(note);
        }

        var nagOverlay = ParseOverlay(overlay);
        if (nagOverlay is not null)
        {
          result.Add(nagOverlay);
        }
      }
    }
    catch (Exception ex)
    {
      Log.Error("Error Parsing NAG Overlays", ex);
    }

    return result;
  }

  /* Builds the NAG overlayId → stored EQLP overlay node id map from the store's overlay list.
   * Only overlays whose Source starts with NagSourcePrefix are included — user-created and
   * GINA/Quick Share overlays carry no such marker and are ignored. */
  internal static Dictionary<string, string> BuildOverlayIdRemap(IEnumerable<OtData> storedOverlays)
  {
    var remap = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var overlay in storedOverlays ?? [])
    {
      var source = overlay?.OverlayData?.Source;
      if (source is not null && source.Length > NagSourcePrefix.Length &&
          source.StartsWith(NagSourcePrefix, StringComparison.Ordinal))
      {
        remap[source[NagSourcePrefix.Length..]] = overlay.Id;
      }
    }

    return remap;
  }

  /* Rewrites trigger → overlay references from NAG overlayIds to stored EQLP node ids (see
   * BuildOverlayIdRemap). NAG actions reference overlays by their NAG id, but after the overlay
   * import the store only knows its own generated UUIDs — without this rewrite every reference
   * would be stripped by ValidateOverlays during the trigger import. Ids absent from the map
   * (e.g. FCT overlays skipped on conversion) are left as-is and dropped safely there.
   * Returns the number of references rewritten. */
  internal static int RemapOverlayReferences(IEnumerable<ExportTriggerNode> nodes, IReadOnlyDictionary<string, string> nagIdToNodeId)
  {
    var remapped = 0;
    foreach (var node in nodes ?? [])
    {
      if (node?.Nodes is { Count: > 0 } children)
      {
        remapped += RemapOverlayReferences(children, nagIdToNodeId);
      }

      if (node?.TriggerData?.SelectedOverlays is { Count: > 0 } overlays && nagIdToNodeId.Count > 0)
      {
        for (var i = 0; i < overlays.Count; i++)
        {
          if (overlays[i] is { } reference && nagIdToNodeId.TryGetValue(reference, out var storedId))
          {
            overlays[i] = storedId;
            remapped++;
          }
        }
      }
    }

    return remapped;
  }

  /* NAG JSON readers that never throw. Every numeric/boolean property of a NAG dump goes through
   * these: the raw JsonElement getters throw on kinds real dumps contain — String ("12.5"),
   * fractional or out-of-range Numbers ("400.0", "5e1", "1e999"), JsonNull where a bool is
   * expected — and any throw aborts the whole trigger (the import reports it "Skipped").
   * Unrecognised input falls back to def. */
  private static double ReadNumber(JsonElement e, double def)
  {
    switch (e.ValueKind)
    {
      // TryGetDouble instead of GetDouble: a Number token can be fractional/out of range ("1e999"
      // parses as +infinity, which would lock a trigger forever); strings go through the fast,
      // allocation-free invariant-culture parser rather than throwing.
      case JsonValueKind.Number when e.TryGetDouble(out var value) && double.IsFinite(value):
        return value;
      case JsonValueKind.String:
        return TextUtils.ParseDouble(e.GetString().AsSpan(), def);
      default:
        return def;
    }
  }

  /* Truncates toward zero ("12.7" → 12); anything unparseable or outside the range is def. */
  private static int ReadInt(JsonElement e, int def)
  {
    var value = ReadNumber(e, double.NaN);
    return double.IsFinite(value) && value is >= int.MinValue and <= int.MaxValue ? (int)value : def;
  }

  private static long ReadLong(JsonElement e, long def)
  {
    var value = ReadNumber(e, double.NaN);
    // 2^53: the largest integer range a double still represents exactly
    return double.IsFinite(value) && value is >= -9007199254740992d and <= 9007199254740992d ? (long)value : def;
  }

  private static bool ReadBool(JsonElement e, bool def) =>
    e.ValueKind switch
    {
      JsonValueKind.True => true,
      JsonValueKind.False => false,
      // tolerating the string/number forms of hand-edited dumps; GetBoolean() would throw on them
      JsonValueKind.String when bool.TryParse(e.GetString(), out var flag) => flag,
      JsonValueKind.Number when e.TryGetDouble(out var value) && double.IsFinite(value) => value != 0,
      _ => def
    };

  // NAG fontWeight (numeric) → EQLP FontWeight (string)
  private static string ConvertFontWeight(int weight) => weight switch
  {
    >= 700 => "Bold",
    >= 500 => "Medium",
    _ => "Normal"
  };

  // NAG horizontalAlignment (string) → EQLP HorizontalAlignment (int enum)
  private static int ConvertHorizontalAlignment(string value) => value.ToLowerInvariant() switch
  {
    "left" => 0,
    "center" or "centre" => 1,
    "right" => 2,
    _ => 1 // default to center
  };

  // NAG verticalAlignment (string) → EQLP VerticalAlignment (int enum)
  private static int ConvertVerticalAlignment(string value) => value.ToLowerInvariant() switch
  {
    "top" => -1,
    "center" or "centre" => 0,
    "bottom" => 1,
    _ => 0 // default to center
  };

  // NAG 6-char RGB hex ("#RRGGBB") → EQLP 8-char ARGB hex ("#AARRGGBB") with full opacity
  private static string ConvertColor(string color)
  {
    if (string.IsNullOrEmpty(color))
    {
      return "#FFFFFFFF";
    }

    // Already 8-char ARGB (#AARRGGBB)
    if (color.Length == 9 && color.StartsWith('#'))
    {
      return color;
    }

    // 6-char RGB ("#RRGGBB") → prepend FF for full opacity
    if (color.Length == 7)
    {
      return "#FF" + color[1..];
    }

    // rgba(r, g, b, a) → #AARRGGBB
    if (color.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase))
    {
      var inner = color[5..^1];
      var parts = inner.Split(',');
      if (parts.Length == 4 &&
          int.TryParse(parts[0].Trim(), out var r) &&
          int.TryParse(parts[1].Trim(), out var g) &&
          int.TryParse(parts[2].Trim(), out var b) &&
          double.TryParse(parts[3].Trim(), out var a))
      {
        var alpha = (int)Math.Round(a * 255);
        alpha = Math.Clamp(alpha, 0, 255);
        return $"#{alpha:X2}{r:X2}{g:X2}{b:X2}";
      }
    }

    // Fallback: treat as unknown color, use black with full opacity
    return "#FF000000";
  }

  // NAG backgroundTransparency (0 = opaque, 1 = transparent) → alpha channel
  private static string ConvertBackground(string color, double transparency)
  {
    var baseColor = color.TrimStart('#');
    if (baseColor.Length == 6)
    {
      // transparency 0 = fully opaque (FF), 1 = fully transparent (00)
      var alpha = (int)Math.Round((1.0 - transparency) * 255);
      alpha = Math.Clamp(alpha, 0, 255);
      return $"#{alpha:X2}{baseColor}";
    }

    // If already rgba, just convert directly
    return ConvertColor(color);
  }

  // Convert NAG template syntax to EQLP syntax
  private static string ConvertTemplates(string input)
  {
    if (string.IsNullOrEmpty(input))
    {
      return input;
    }

    // ${var} → {var} ($1 = capture group value; EQLP TokenRegex matches {var} or ${var})
    input = VarPattern.Replace(input, "{$1}");

    // {groupName} → {groupName} (but not already converted ${...})
    input = GroupPattern.Replace(input, "{$1}");

    return input;
  }

  /// <summary>
  /// Checks if a regex pattern string contains an un-named capture group.
  /// Looks for '(' not followed by '?' (which would indicate named, non-capturing, etc.).
  /// </summary>
  private static bool HasUnnamedCaptureGroup(string pattern)
  {
    var idx = pattern.IndexOf('(');
    while (idx >= 0 && idx + 1 < pattern.Length && pattern[idx + 1] == '?')
    {
      idx = pattern.IndexOf('(', idx + 1);
    }
    return idx >= 0;
  }

  // Checks if a regex pattern contains any named capture groups (e.g., (?<name>...)).
  private static bool HasNamedCaptureGroup(string pattern)
  {
    var idx = pattern.IndexOf("?<", StringComparison.Ordinal);
    return idx >= 0;
  }

  // NAG score (0-1, higher = more important) → EQLP Priority (lower = more important)
  private static long ConvertScore(double score)
  {
    // Map: score 1.0 → priority 1, score 0.0 → priority 5
    var inverted = 1.0 - score;
    return Math.Clamp((long)(inverted * 4) + 1, 1, 5);
  }

  // Resolve audio file ID to filename using files-database.json if available
  /// <summary>Renames duplicate child names under each parent ("X", "X" → "X", "X (2)").</summary>
  private static void UniquifySiblingNames(ExportTriggerNode parent)
  {
    if (parent?.Nodes is not { Count: > 1 } children)
    {
      return;
    }

    var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    // Names the siblings currently use — generated "Name (n)" suffixes must not collide with a
    // literal sibling that already has such a name.
    var taken = new HashSet<string>(
      children.Where(c => c?.Name is not null).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
    foreach (var child in children)
    {
      if (child is null)
      {
        continue;
      }

      if (child.Name is not null)
      {
        var seen = counts.TryGetValue(child.Name, out var count) ? ++counts[child.Name] : counts[child.Name] = 1;
        if (seen > 1)
        {
          var suffix = seen;
          var candidate = $"{child.Name} ({suffix})";
          while (taken.Contains(candidate))
          {
            candidate = $"{child.Name} ({++suffix})";
          }

          child.Name = candidate;
        }

        taken.Add(child.Name);
      }

      // Recurse even for null-named nodes: their children are still siblings under one parent.
      UniquifySiblingNames(child);
    }
  }

  private static void LoadAudioFileMap(string databaseDirectory)
  {
    // Clear any previous cache so importing from a different directory works correctly
    _audioFileMap = null;
    try
    {
      var filePath = Path.Combine(databaseDirectory, "files-database.json");
      if (File.Exists(filePath))
      {
        using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
        _audioFileMap = new Dictionary<string, string>();
        // files-database.json has structure: { "files": [ { fileId, mediaType, fileName, physicalName }, ... ] }
        // Prefer physicalName (full path on disk) so ResolveSoundPath can use it directly.
        // Fall back to fileName if physicalName is absent.
        var filesElem = doc.RootElement.GetProperty("files");
        foreach (var file in filesElem.EnumerateArray())
        {
          if (file.TryGetProperty("fileId", out var id) && file.TryGetProperty("fileName", out var name))
          {
            var fileId = id.GetString();
            var fileName = name.GetString();
            if (!string.IsNullOrEmpty(fileId) && !string.IsNullOrEmpty(fileName))
            {
              // physicalName holds the real path to the audio file on the user's machine.
              // ResolveSoundPath handles both bare filenames and full paths.
              var resolved = file.TryGetProperty("physicalName", out var phys) && phys.GetString() is { Length: > 0 } p
                ? p
                : fileName;
              _audioFileMap[fileId] = resolved;
            }
          }
        }
      }
    }
    catch (Exception ex)
    {
      Log.Debug("Error loading files-database.json", ex);
    }
  }

  // Best-effort display name for a NAG trigger element, used when reporting per-trigger parse failures.
  private static string GetTriggerDisplayName(JsonElement trigger)
  {
    if (trigger.ValueKind == JsonValueKind.Object &&
        trigger.TryGetProperty("name", out var n) &&
        n.GetString() is { Length: > 0 } name)
    {
      return name;
    }

    return "(unknown)";
  }

  // Recursively parse NAG folder structure to build a folderId → path mapping
  private static void ParseFolderStructure(JsonElement folder, string parentPath, Dictionary<string, string> folderPaths)
  {
    var folderId = folder.TryGetProperty("folderId", out var fid) ? fid.GetString() : null;
    if (string.IsNullOrEmpty(folderId))
      return;

    var name = folder.TryGetProperty("name", out var n) ? n.GetString() : "(unnamed)";
    var currentPath = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";
    folderPaths[folderId] = currentPath;

    if (folder.TryGetProperty("children", out var children))
    {
      foreach (var child in children.EnumerateArray())
      {
        ParseFolderStructure(child, currentPath, folderPaths);
      }
    }
  }

  /// <summary>Returns the folder node for a path, creating it and any missing ancestors on first use.</summary>
  // "Raids/Kunark" and "Raids/CoTF" both attach under one "Raids" node — ancestor nodes are
  // created once and shared, never duplicated per trigger.
  private static ExportTriggerNode EnsureFolderPath(
    Dictionary<string, ExportTriggerNode> cache, List<ExportTriggerNode> roots, string folderPath)
  {
    var parts = folderPath.Split('/');
    var children = roots;
    var pathSoFar = "";

    for (var i = 0; i < parts.Length; i++)
    {
      if (i > 0)
      {
        pathSoFar += '/';
      }

      pathSoFar += parts[i];
      if (!cache.TryGetValue(pathSoFar, out var node))
      {
        node = new ExportTriggerNode { Name = parts[i] };
        cache[pathSoFar] = node;
        children.Add(node);
      }

      children = node.Nodes;
    }

    return cache[folderPath];
  }

  private static (List<ExportTriggerNode> nodes, NagImportResult result) ParseTrigger(JsonElement element)
  {
    var name = element.GetProperty("name").GetString();
    if (string.IsNullOrEmpty(name))
    {
      return ([], new NagImportResult { TriggerName = name ?? "(null)", ImportStatus = NagImportStatus.Skipped, Reason = "Missing name" });
    }

    // Skip dev-only triggers (but still track missing audio files for reporting)
    if (element.TryGetProperty("onlyExecuteInDev", out var devProp) && ReadBool(devProp, false))
    {
      var devTriggerId = element.GetProperty("triggerId").GetString();
      List<string> devMissingAudio = [];
      if (element.TryGetProperty("actions", out var devActions))
      {
        foreach (var action in devActions.EnumerateArray())
        {
          if (action.TryGetProperty("actionType", out var at) && ReadInt(at, -1) == 1 &&
              action.TryGetProperty("audioFileId", out var af) && !string.IsNullOrEmpty(af.GetString()))
          {
            ResolveAudioFile(af.GetString(), devMissingAudio);
          }
        }
      }
      return ([], new NagImportResult { TriggerName = name, TriggerId = devTriggerId, ImportStatus = NagImportStatus.Skipped, Reason = "Dev-only trigger", MissingAudioFiles = devMissingAudio });
    }

    var triggerId = element.GetProperty("triggerId").GetString() ?? "";
    var comments = element.TryGetProperty("comments", out var c) ? c.GetString() : null;
    var score = element.TryGetProperty("score", out var s) ? ReadNumber(s, 0.5) : 0.5;
    var useCooldown = element.TryGetProperty("useCooldown", out var cd) && ReadBool(cd, false);
    var cooldownDuration = element.TryGetProperty("cooldownDuration", out var cdDur) ? ReadNumber(cdDur, 0) : 0;

    // Check for classLevels — no EQLP equivalent, mark as dropped feature
    var hasClassLevels = element.TryGetProperty("classLevels", out var cl) && cl.ValueKind == JsonValueKind.Array && cl.GetArrayLength() > 0;
    if (hasClassLevels)
    {
      // Note: class level filtering is silently lost — trigger will fire for all classes
    }

    // Check capture method
    var captureMethod = element.TryGetProperty("captureMethod", out var cm) ? cm.GetString() : "Any match";
    var isSequential = captureMethod?.Equals("Sequential", StringComparison.OrdinalIgnoreCase) == true;

    // Parse capture phrases - NAG uses capturePhrases array
    var capturePhrases = element.GetProperty("capturePhrases");
    if (capturePhrases.GetArrayLength() == 0)
    {
      return ([], new NagImportResult { TriggerName = name, TriggerId = triggerId, ImportStatus = NagImportStatus.Skipped, Reason = "No capture phrases" });
    }

    // Collect all phrases — each becomes its own EQLP trigger (no alternation combining).
    // This avoids named group collisions when multiple phrases share capture group names.
    var phrases = new List<(string pattern, bool useRegex, string phraseId)>();
    var hasDollarVarCondition = false;
    var hasCaseSensitiveNonRegexPhrase = false;
    foreach (var phrase in capturePhrases.EnumerateArray())
    {
      if (phrase.TryGetProperty("phrase", out var p) && p.GetString() is { Length: > 0 } text)
      {
        var useRegex = phrase.TryGetProperty("useRegEx", out var re) && ReadBool(re, false);

        // NAG uses several variable-reference syntaxes in capture phrases that need
        // special handling before the pattern is stored:
        //
        // 1. ${varName} (e.g., ${Character}, ${SpellBeingCast}) — NAG variable references
        //    resolved at runtime by NAG. EQLP doesn't support {$var} in regex patterns.
        //    Replace with (?<varName>.+?) so the regex compiles and matches any text,
        //    and the captured value can be referenced in display text via {varName}.
        //    Affects 9 triggers using ${Character} and ${SpellBeingCast}.
        //
        // 2. {TS} — NAG duration placeholder. EQLP's CheckOptions() at runtime converts
        //    {TS} → (?<TS>(?:\d+[dhms]?:?){1,4}) for dynamic timer durations.
        //    We must NOT run ConvertTemplates on it (which would make {$TS}).
        //
        // 3. {S}, {N} and variants — EQLP's CheckOptions() also handles these at runtime
        //    ({S}→(?<S>.+), {N}→(?<N>\d+)). Must NOT be converted by ConvertTemplates.
        //
        // 4. {C}, {c} — EQLP replaces with player character name at runtime.
        //    Passed through untouched (no regex capture group needed).
        //
        // 5. {LN} — Player/NPC name, single word. Replaced with (?<LN>\w+).
        //
        // 6. {target} — Target entity name (can have spaces, commas, quotes).
        //    Replaced with (?<target>.+?).
        //
        // 7. Other unhandled vars like {SpellBeingCast} in ${var} syntax —
        //    replaced with (?<VAR>.+?) via DollarVarRegex.
        //
        // For non-regex phrases containing NAG variable references ({VAR}), we must
        // also enable regex mode because these variables require capture groups to work.
        // Excluded from this check: {C}/{c} (EQLP replaces natively), ${var} (handled below).
        // Variables like {S}, {N}, {TS} and their digit-suffixed variants DO force regex
        // mode because EQLP's CheckOptions() needs them as named capture groups.
        var hasNagVars = Regex.IsMatch(text, @"(?<!\$)\{(?![Cc]|S\d?|s\d?|N\d?|n\d?|TS|ts)");
        if (useRegex || hasNagVars)
        {
          useRegex = true;

          // Replace ${varName} with (?<varName>.+?) so it can be referenced in display text.
          // Exception: ${Character} → {c} (EQLP replaces {c} with player name at runtime).
          text = DollarVarRegex.Replace(text, m =>
          {
            if (m.Groups[1].Value.Equals("Character", StringComparison.OrdinalIgnoreCase))
            {
              return "{c}";
            }

            // NAG treats ${var} as a match-time restriction — the phrase only matches when the
            // variable currently holds the captured value. EQLP cannot express that, so the
            // import matches any text; flag it once per trigger after actions are parsed.
            hasDollarVarCondition = true;
            return $"(?<{m.Groups[1].Value}>.+?)";
          });

          // Replace unhandled {VAR} patterns with named capture groups.
          // {LN} → (?<LN>\w+) (player/NPC name, single word like a valid EQ player name)
          // {target}, others → (?<VAR>.+?) (NPC names/spell names can have spaces, commas, quotes)
          text = UnhandledVarRegex.Replace(text, m =>
          {
            var varName = m.Groups[0].Value.Trim('{', '}');
            if (varName.Equals("LN", StringComparison.OrdinalIgnoreCase))
            {
              return "(?<LN>\\w+)";
            }
            return char.IsLetterOrDigit(varName[0]) && varName.All(c => char.IsLetterOrDigit(c) || c == '_')
              ? $"(?<{varName}>.+?)"
              : "(.+?)";
          });

          // Do NOT call ConvertTemplates — keep {S}, {N}, {TS} as literals. The EQLP runtime
          // does not expand these in plain (non-regex) patterns, which matches NAG: its
          // non-regex path never expands them either, so such phrases match literally there too.

          // NAG phrases are case-insensitive by default but can opt out per phrase. EQLP compiles
          // every pattern with RegexOptions.IgnoreCase, so re-enable sensitivity for those.
          if (phrase.TryGetProperty("ignoreCase", out var ignoreCase) && !ReadBool(ignoreCase, true))
          {
            text = "(?-i)" + text;
          }

          var phraseId = phrase.TryGetProperty("phraseId", out var pid) ? pid.GetString() : null;
          phrases.Add((text, useRegex, phraseId));
        }
        else
        {
          var phraseId2 = phrase.TryGetProperty("phraseId", out var pid2) ? pid2.GetString() : null;
          phrases.Add((ConvertTemplates(text), false, phraseId2));
          if (phrase.TryGetProperty("ignoreCase", out var ignoreCase) && !ReadBool(ignoreCase, true))
          {
            hasCaseSensitiveNonRegexPhrase = true;
          }
        }
      }
      else if (phrase.TryGetProperty("useRegEx", out var re2) && ReadBool(re2, false))
      {
        // Phrase text was empty/null but useRegEx is set — skip it
      }
    }

    if (phrases.Count == 0)
    {
      return ([], new NagImportResult { TriggerName = name, TriggerId = triggerId, ImportStatus = NagImportStatus.Skipped, Reason = "No valid phrases" });
    }

    // Parse actions once — non-timer actions are shared across all nodes; timer actions are
    // collected per NAG timer action (each becomes its own node in the fan-out below)
    var parsed = ParseActions(element.GetProperty("actions"), phrases.Any(p => p.useRegex));
    if (parsed.BaseTriggerData is null)
    {
      return ([], new NagImportResult { TriggerName = name, TriggerId = triggerId, ImportStatus = NagImportStatus.Skipped, Reason = parsed.SkipReason ?? "No supported actions" });
    }

    var baseTriggerData = parsed.BaseTriggerData;
    var droppedFeatures = parsed.DroppedFeatures;

    // Apply set-variable actions: convert numbered capture groups in the
    // referenced phrase to simple named groups, then add VariableActions so
    // EQLP's global variable system stores the captured value under the NAG
    // variable name. This enables cross-trigger variable sharing (e.g., one
    // trigger captures the spell name, others reference it via {SpellBeingCast}).
    // NAG's actionType 5 stores a captured group into a named variable.
    var phraseVarMap = new Dictionary<string, List<(string groupName, string varName)>>();
    // Track the last set-variable action that matched a phrase, so subsequent
    // capture phrases without an explicit match can fall back to it.
    // This handles triggers like "Capture spell casting" where only the first
    // capture phrase has an explicit actionId routing; later capture phrases
    // (e.g., "You activate X", "You begin singing X") should also set the variable.
    string lastSetVarName = null;
    foreach (var (phraseId, varName) in parsed.SetVariables)
    {
      // If no specific phraseId, apply to all regex phrases with un-named groups.
      // NAG uses this when the variable is set from whatever phrase triggered.
      for (var i = 0; i < phrases.Count; i++)
      {
        var matchesPhrase = string.IsNullOrEmpty(phraseId) || phrases[i].phraseId == phraseId;
        if (!matchesPhrase || !phrases[i].useRegex) continue;

        lastSetVarName = varName;
        var pattern = phrases[i].pattern;
        // Convert the next un-named capture group to a simple named group
        var openParenIdx = pattern.IndexOf('(');
        while (openParenIdx >= 0 && openParenIdx + 1 < pattern.Length && pattern[openParenIdx + 1] == '?')
        {
          openParenIdx = pattern.IndexOf('(', openParenIdx + 1);
        }
        if (openParenIdx >= 0)
        {
          var groupName = "s" + (i + 1);
          pattern = pattern.Substring(0, openParenIdx) + "(?<" + groupName + ">" + pattern.Substring(openParenIdx + 1);
          if (!phraseVarMap.TryGetValue(phrases[i].phraseId ?? "", out var list))
          {
            phraseVarMap[phrases[i].phraseId ?? ""] = list = new List<(string, string)>();
          }
          list.Add((groupName, varName));
        }
        phrases[i] = (pattern, true, phrases[i].phraseId);
      }
    }

    // Fallback: for regex capture phrases that didn't match any set-variable action,
    // but have un-named capture groups and no ${var} references, inherit the last
    // matched set-variable action. This ensures all capture phrases in triggers like
    // "Capture spell casting" get VariableActions to store captured values.
    if (lastSetVarName != null)
    {
      for (var i = 0; i < phrases.Count; i++)
      {
        var currentPhraseId = phrases[i].phraseId ?? "";
        if (phraseVarMap.ContainsKey(currentPhraseId)) continue;
        if (!phrases[i].useRegex) continue;

        var pattern = phrases[i].pattern;
        // Only apply to capture phrases with un-named groups and no ${var} references.
        // Note: by this point, ${var} has already been converted to (?<var>.+?) by DollarVarRegex,
        // so we check for existing named groups instead. If the pattern already has named
        // capture groups (from ${var} conversion), it's a dependent phrase that references
        // an existing variable — don't add another set-variable action.
        if (!HasUnnamedCaptureGroup(pattern) || HasNamedCaptureGroup(pattern)) continue;

        // Convert the next un-named capture group to a simple named group
        var openParenIdx = pattern.IndexOf('(');
        while (openParenIdx >= 0 && openParenIdx + 1 < pattern.Length && pattern[openParenIdx + 1] == '?')
        {
          openParenIdx = pattern.IndexOf('(', openParenIdx + 1);
        }
        if (openParenIdx >= 0)
        {
          var groupName = "s" + (i + 1);
          pattern = pattern.Substring(0, openParenIdx) + "(?<" + groupName + ">" + pattern.Substring(openParenIdx + 1);
          if (!phraseVarMap.TryGetValue(currentPhraseId, out var list))
          {
            phraseVarMap[currentPhraseId] = list = new List<(string, string)>();
          }
          list.Add((groupName, lastSetVarName));
        }
        phrases[i] = (pattern, true, phrases[i].phraseId);
      }
    }

    // Add class level filtering as a dropped feature (no EQLP equivalent)
    if (hasClassLevels)
    {
      droppedFeatures.Add("class level filtering");
    }

    // Report phrase-level limitations found while converting capture phrases.
    if (hasDollarVarCondition)
    {
      droppedFeatures.Add("phrase ${var} restriction (NAG only matches stored variable values; import matches any text)");
    }

    if (hasCaseSensitiveNonRegexPhrase)
    {
      droppedFeatures.Add("case-sensitive non-regex phrase(s) imported as case-insensitive");
    }

    // Report NAG action features EQLP cannot represent exactly (one pass over all actions):
    // - interruptSpeech: NAG preempts any currently-speaking text. Approximated by importing
    //   the trigger at priority 1 (top urgency) — the EQLP audio engine stops playing audio of
    //   lower priority and drops queued lower-priority events. Same convention as GINA import.
    // - secondaryPhrases: extra phrase IDs the same action also matches; no EQLP equivalent.
    // - per-phrase action scoping: NAG actions can target specific phrases ("phrases" array).
    //   Text/sound/TTS/clipboard values and variable effects are routed to exactly those phrase
    //   nodes (see RouteActionValue / SetVariables). Timers and counters have no such routing —
    //   they fan out to every phrase node — so a subset-scoped timer/counter is still flagged,
    //   as is any routable action scoped to phrase ids that no longer exist in the trigger.
    var phraseIdSet = phrases.Where(p => p.phraseId != null).Select(p => p.phraseId!).ToHashSet();
    var hasInterruptSpeech = false;
    foreach (var action in element.GetProperty("actions").EnumerateArray())
    {
      if (action.TryGetProperty("interruptSpeech", out var interruptSpeech) && ReadBool(interruptSpeech, false))
      {
        hasInterruptSpeech = true;
        droppedFeatures.Add(InterruptSpeechNote);
      }

      if (action.TryGetProperty("secondaryPhrases", out var secondary) &&
          secondary.ValueKind == JsonValueKind.Array && secondary.GetArrayLength() > 0)
        droppedFeatures.Add("secondary phrases");

      if (phrases.Count > 1 && phraseIdSet.Count == phrases.Count &&
          action.TryGetProperty("phrases", out var targetPhrases) &&
          targetPhrases.ValueKind == JsonValueKind.Array && targetPhrases.GetArrayLength() > 0)
      {
        var scopedIds = targetPhrases.EnumerateArray().Select(e => e.GetString()).Where(s => s is not null).ToHashSet();
        var actionType = action.TryGetProperty("actionType", out var at2) ? ReadInt(at2, -1) : -1;
        if (RouteablePhraseScopedTypes.Contains(actionType))
        {
          // The values were routed during ParseActions — the only divergence left to report is
          // scoping onto phrase ids that no longer exist in this trigger (those were dropped).
          if (scopedIds.Any(id => !phraseIdSet.Contains(id)))
            droppedFeatures.Add("action targets phrase(s) missing from trigger");
        }
        else if (scopedIds.Count > 0 && scopedIds.Count < phraseIdSet.Count && scopedIds.IsSubsetOf(phraseIdSet))
        {
          droppedFeatures.Add("per-phrase action scoping");
        }
      }
    }

    // Parse conditions into MatchVariableCondition (shared across all phrase-triggers)
    string conditionStr = null;
    if (element.TryGetProperty("conditions", out var conds))
    {
      conditionStr = ParseConditions(conds, droppedFeatures);
    }

    // Build Comments field content. The NAG author's own comment describes the trigger as a
    // whole, so every split node carries it — each one is a full EQLP trigger in its own right
    // and must stand on its own. Comments are inert metadata (unlike the shared non-timer
    // actions, they cannot double-fire), so duplicating them costs nothing. The import notes
    // are per-node diagnostics and stay on every node too.
    var importNotes = droppedFeatures.Count > 0
      ? $"EQLP Import Notes: {string.Join(", ", droppedFeatures)}"
      : null;

    // Trigger-level end-early phrases, parsed once and shared by every node's merged list.
    var triggerEndEarly = new List<(string phrase, bool useRegex)>();
    if (element.TryGetProperty("endEarlyPhrases", out var eep) && eep.ValueKind == JsonValueKind.Array)
    {
      foreach (var ee in eep.EnumerateArray())
      {
        if (ee.TryGetProperty("phrase", out var ep) && ep.GetString() is { Length: > 0 } phrase)
        {
          var useRegex = ee.TryGetProperty("useRegEx", out var useRe) && ReadBool(useRe, false);
          triggerEndEarly.Add((ApplyEndEarlyCaseSensitivity(ee, ConvertTemplates(phrase), useRegex, droppedFeatures), useRegex));
        }
      }
    }

    // One merged end-early list per node variant (index-aligned with the timer variant loop):
    // each NAG timer action's own endEarlyPhrases stop that timer only, so they merge into its
    // nodes' lists and not into the sibling timers'. A single entry covers triggers without any
    // timer action. EQLP has max 3 slots per node; overflow is reported as dropped.
    var timerActions = parsed.TimerActions;
    // When a trigger fans out into multiple nodes, every node matches the same line. NAG fires
    // its non-timer actions once per execution, so those actions (and the NAG author's comment)
    // attach only to each phrase's first timer variant — otherwise they would double-fire (e.g.
    // "Bard Epic 2.0" TTS spoken twice, counter incremented twice). Later variants carry just
    // their own timer plus the per-node import notes.
    var multiTimer = timerActions.Count > 1;
    var endEarlyByVariant = new List<List<(string phrase, bool useRegex)>>();
    if (timerActions.Count > 0)
    {
      foreach (var timer in timerActions)
      {
        endEarlyByVariant.Add(MergeEndEarlyPhrases(triggerEndEarly, timer.EndEarlyPhrases, droppedFeatures));
      }
    }
    else
    {
      endEarlyByVariant.Add(MergeEndEarlyPhrases(triggerEndEarly, [], droppedFeatures));
    }

    // Build one EQLP trigger per capture phrase AND per NAG timer action (no regex alternation
    // combining). An EQLP trigger holds exactly one timer, so a NAG trigger with M timer actions
    // produces M nodes per phrase. Without this fan-out the last timer action would silently
    // overwrite the earlier ones' duration, label, and restart behavior (e.g. "Bard Epic 2.0":
    // its 180s and 60s timer actions collapsed into a single 60s timer). Shared non-timer
    // actions attach to each phrase's first variant only — see the strip below.
    var nodes = new List<ExportTriggerNode>();
    for (var i = 0; i < phrases.Count; i++)
    {
      var (pattern, useRegEx, _) = phrases[i];

      // Determine which phrase this node corresponds to, for per-phrase action routing.
      var currentPhraseId = phrases[i].phraseId ?? "";
      var phraseName = phrases.Count > 1 ? $"{name} #{i + 1}" : name;

      // One variant per NAG timer action (a single placeholder iteration when there are none).
      var variants = timerActions.Count > 0 ? timerActions.Count : 1;
      for (var t = 0; t < variants; t++)
      {
        var triggerData = baseTriggerData.Clone();

        // Strip the shared non-timer actions from sibling timer variants — they already run on
        // this phrase's first node, which matches the same line. The timer block below still
        // applies this variant's own timer and its overlays.
        if (multiTimer && t > 0)
        {
          triggerData.TextToDisplay = null;
          triggerData.TextToSpeak = null;
          triggerData.SoundToPlay = null;
          triggerData.TextToShare = null;
          triggerData.SelectedOverlays = [];
          triggerData.VariableActions = [];
          triggerData.EndTimerClearVariables = null;
          triggerData.RepeatedResetTime = 0.75; // model default — no counter on this node
        }

        // Assign the pattern for this phrase
        triggerData.Pattern = pattern;
        triggerData.UseRegex = useRegEx;

        // Apply conditions
        if (!string.IsNullOrEmpty(conditionStr))
        {
          triggerData.MatchVariableCondition = conditionStr;
        }

        // The NAG author's comment describes the trigger as a whole; every node carries it (see
        // the rationale at its construction above). Import notes stay on every node.
        triggerData.Comments = string.IsNullOrEmpty(comments) ? importNotes
          : string.IsNullOrEmpty(importNotes) ? comments : $"{comments}\n{importNotes}";

        // interruptSpeech triggers get top urgency so the audio engine preempts lower-priority
        // playback, approximating NAG's speech interruption (see note above). Priority 1 is also
        // what GINA import uses for interrupt triggers.
        triggerData.Priority = hasInterruptSpeech ? 1 : ConvertScore(score);
        triggerData.LockoutTime = useCooldown ? cooldownDuration : 0;

        // Apply the NAG timer action this node represents (no-op for non-timer triggers).
        var timer = t < timerActions.Count ? timerActions[t] : null;
        if (timer is not null)
        {
          triggerData.EnableTimer = timer.Enabled && timer.DurationSeconds > 0;
          triggerData.DurationSeconds = timer.DurationSeconds;
          triggerData.TimerType = timer.TimerType;
          triggerData.TimesToLoop = timer.TimesToLoop;
          triggerData.TriggerAgainOption = timer.TriggerAgainOption >= 0 ? timer.TriggerAgainOption : 0;
          triggerData.WarningSeconds = timer.WarningSeconds;
          // NAG's displayText is the timer bar label — EQLP renders it from AltTimerName.
          triggerData.AltTimerName = timer.AltTimerName;
          triggerData.ActiveColor = timer.ActiveColor;
          triggerData.IdleColor = timer.IdleColor;
          triggerData.WarningSoundToPlay = timer.WarningSoundToPlay;
          triggerData.EndSoundToPlay = timer.EndSoundToPlay;
          triggerData.WarningTextToDisplay = timer.WarningTextToDisplay;
          triggerData.WarningTextToSpeak = timer.WarningTextToSpeak;
          triggerData.EndTextToDisplay = timer.EndTextToDisplay;
          triggerData.EndTextToSpeak = timer.EndTextToSpeak;

          // Overlay routing: non-timer action overlays (base) plus this timer's own overlay.
          if (timer.Overlays.Count > 0)
          {
            var overlays = new List<string>(triggerData.SelectedOverlays);
            foreach (var ov in timer.Overlays)
            {
              if (!overlays.Contains(ov))
                overlays.Add(ov);
            }
            triggerData.SelectedOverlays = overlays;
          }
        }
        else
        {
          triggerData.DurationSeconds = 0; // model default is 0.2 — a timerless import must not export one
        }

        // End-early phrases for this node: trigger-level merged with this timer variant's own.
        ApplyEndEarlyPatterns(triggerData, endEarlyByVariant[t]);

        // Apply this phrase's own action values. NAG actions can target specific capture phrases
        // via "phrases"/"phraseId"; their display/speak/sound/share values apply only to this
        // phrase's nodes, not the siblings' — and only on the first timer variant (shared
        // non-timer actions must not double-fire). For example a clear-variable action with
        // displayText "Spell {var} was interrupted." shows only on the interrupt phrases.
        if (t == 0 && parsed.PhraseScoped.TryGetValue(currentPhraseId, out var phraseScoped))
        {
          if (phraseScoped.TextToDisplay.Length > 0) triggerData.TextToDisplay = phraseScoped.TextToDisplay;
          if (phraseScoped.TextToSpeak.Length > 0) triggerData.TextToSpeak = phraseScoped.TextToSpeak;
          if (phraseScoped.SoundToPlay.Length > 0) triggerData.SoundToPlay = phraseScoped.SoundToPlay;
          if (phraseScoped.TextToShare.Length > 0) triggerData.TextToShare = phraseScoped.TextToShare;
        }

        // Add VariableActions for set-variable mappings on this phrase (first variant only —
        // the sibling timer nodes match the same line and would set the variable again).
        // EQLP stores the captured value as a global variable accessible by other triggers.
        if (t == 0 && phraseVarMap.TryGetValue(currentPhraseId, out var varList))
        {
          foreach (var (groupName, varName) in varList)
          {
            triggerData.VariableActions.Add(new VariableAction
            {
              ActionType = 0, // Set
              DataType = 0,   // Value
              VariableName = varName,
              Value = "{" + groupName + "}"
            });
          }
        }

        // Add phrase-specific clear-variable VariableActions.
        // NAG actionType 7 with a phraseId means "when this specific phrase matches, clear the variable."
        // This is different from EndTimerClearVariables (which fires when a timer ends) — these
        // are per-phrase triggers that fire on match, so we use VariableAction { ActionType=Clear }.
        foreach (var (clearPhraseId, clearVarName) in parsed.PhraseClearVariables)
        {
          if (t == 0 && currentPhraseId == clearPhraseId)
          {
            triggerData.VariableActions.Add(new VariableAction
            {
              ActionType = 1, // Clear
              DataType = 0,   // Value
              VariableName = clearVarName
            });
          }
        }

        // Number the timer variant so same-phrase nodes stay distinct for name-based dedup.
        var triggerName = timerActions.Count > 1 ? $"{phraseName} (Timer {t + 1})" : phraseName;

        nodes.Add(new ExportTriggerNode
        {
          Id = Guid.NewGuid().ToString(),
          Name = triggerName,
          OriginalId = triggerId,
          TriggerData = triggerData
        });
      }
    }

    // Create additional triggers for NAG counter reset phrases. Each reset phrase
    // becomes a trigger that clears the counter variable, simulating NAG's behavior
    // where matching the reset phrase resets the auto-incrementing counter to 0.
    foreach (var (resetPhrase, useRegex, varName) in parsed.CounterResetPhrases)
    {
      var resetNode = new ExportTriggerNode
      {
        Id = Guid.NewGuid().ToString(),
        Name = nodes.Count > 0 ? $"{name} #{nodes.Count + 1} (Counter Reset)" : name,
        OriginalId = triggerId,
        TriggerData = new Trigger
        {
          Pattern = resetPhrase,
          UseRegex = useRegex,
          Comments = "Auto-generated from NAG counter reset phrase. Clears the counter variable when this phrase matches.",
          Priority = ConvertScore(score),
          LockoutTime = useCooldown ? cooldownDuration : 0,
          VariableActions = new List<VariableAction> { new() { ActionType = 1, DataType = 0, VariableName = varName } },
        }
      };
      nodes.Add(resetNode);
    }

    // De-duplicate notes — the same feature can be flagged by multiple actions.
    droppedFeatures = droppedFeatures.Distinct().ToList();

    // Determine import status and reason
    // Triggers with missing audio files are Partial (imported but incomplete).
    // Approximation notes (NonStatusDroppedFeatures) don't count — the behavior is implemented
    // as closely as EQLP allows, so they stay visible without burying real gaps in noise.
    var meaningfulDrops = droppedFeatures.Where(f => !NonStatusDroppedFeatures.Contains(f)).ToList();
    var hasMissingAudio = parsed.MissingAudioFiles?.Count > 0;
    var status = hasMissingAudio || meaningfulDrops.Count > 0 ? NagImportStatus.Partial : NagImportStatus.Imported;
    var reason = isSequential ? "Sequential capture method (not supported)" :
                 hasClassLevels ? "Class level filtering (not supported)" :
                 meaningfulDrops.Count > 0 ? string.Join(", ", meaningfulDrops) :
                 hasMissingAudio ? $"{parsed.MissingAudioFiles.Count} missing audio file(s)" :
                 null;

    // Sequential capture triggers cannot be reliably converted — skip them entirely
    if (isSequential)
    {
      return ([], new NagImportResult
      {
        TriggerName = name,
        TriggerId = triggerId,
        ImportStatus = NagImportStatus.Skipped,
        Reason = "Sequential capture method (not supported)",
        ActionsSummary = parsed.ActionSummary,
        MissingAudioFiles = parsed.MissingAudioFiles
      });
    }

    // Build actions summary
    var actionSummary = parsed.ActionSummary;

    return (nodes, new NagImportResult
    {
      TriggerName = name,
      TriggerId = triggerId,
      ImportStatus = status,
      Reason = reason,
      ActionsSummary = actionSummary,
      Score = score,
      DroppedFeatures = droppedFeatures,
      MissingAudioFiles = parsed.MissingAudioFiles
    });
  }

  /* One NAG timer action (type 3/4/6/10) parsed into EQLP fields. An EQLP trigger holds exactly
   * one timer, so each NAG timer action is imported as its own trigger node — the fields here must
   * never be merged across actions (last-wins overwriting silently dropped earlier timers). */
  private sealed class TimerActionData
  {
    // False for NAG timers with a null (indefinite) duration: the timer imports disabled with
    // the model's default duration, but every other field (end-early phrases, warning text/
    // sound, end texts, label) is populated so enabling it later only takes setting a duration.
    public bool Enabled = true;
    public int TimerType;
    public double DurationSeconds;
    // NAG "ending soon" threshold — seconds before the end at which the timer enters its ending
    // state (warning text/sound). EQLP's "Warn With Time Remaining" (WarningSeconds) is the match.
    public long WarningSeconds;
    public long TimesToLoop;
    // -1 = not set on the NAG action (default to EQLP option 0)
    public int TriggerAgainOption = -1;
    // NAG labels its timer bar with this text (displayText || triggerName in the NAG overlay).
    // EQLP renders the timer-bar label from AltTimerName; TextToDisplay is a separate text
    // notification, so the label must go here — not into TextToDisplay.
    public string AltTimerName = "";
    public string ActiveColor = "";
    public string IdleColor = "";
    public List<string> Overlays = [];
    // NAG can play an audio clip when the timer enters its ending state and/or when it ends.
    // EQLP has the matching warning/end sound slots.
    public string WarningSoundToPlay = "";
    public string EndSoundToPlay = "";
    public string WarningTextToDisplay = "";
    public string WarningTextToSpeak = "";
    public string EndTextToDisplay = "";
    public string EndTextToSpeak = "";
    // Action-level endEarlyPhrases — in NAG they stop this specific timer only.
    public List<(string phrase, bool useRegex)> EndEarlyPhrases = [];
  }

  /* Result of ParseActions: the shared (non-timer) trigger data plus per-timer-action data. */
  private sealed class ParsedActions
  {
    // null when nothing importable was found (see SkipReason)
    public Trigger BaseTriggerData;
    public string SkipReason;
    public List<string> DroppedFeatures = [];
    public string ActionSummary = "";
    public List<string> MissingAudioFiles = [];
    public List<(string phraseId, string variableName)> SetVariables = [];
    public List<(string phrase, bool useRegex, string variableName)> CounterResetPhrases = [];
    public List<(string phraseId, string variableName)> PhraseClearVariables = [];
    public Dictionary<string, PhraseScopedValues> PhraseScoped = [];
    public List<TimerActionData> TimerActions = [];
  }

  /* Resolve a NAG audio file id to an EQLP sound name: look it up in the NAG database's
   * files-database.json map when available, map NAG's built-in "Speech ding" to alert1.wav,
   * and record unresolved/missing files for the import report. */
  private static string ResolveAudioFile(string audioFileId, List<string> missingAudioFiles)
  {
    var soundToPlay = _audioFileMap?.TryGetValue(audioFileId, out var resolvedName) == true ? resolvedName : audioFileId;
    if (soundToPlay == "Speech ding")
    {
      soundToPlay = "alert1.wav";
    }
    if (!TriggerStorePlatform.SoundExists(soundToPlay))
    {
      missingAudioFiles.Add(soundToPlay);
    }
    return soundToPlay;
  }

  // Shared parsing for NAG timer actions (actionType 3/4, 6, 10). Extracts common fields: timer
  // label (displayText), endEarlyPhrases, endingSoon/ended sub-action text and audio, duration,
  // restart behavior, colors, and overlayId — all onto this action's own TimerActionData.
  private static void ParseTimerActionFields(JsonElement action, bool handleNullDuration,
      TimerActionData timer, List<string> droppedFeatures, List<string> missingAudioFiles)
  {
    if (action.TryGetProperty("displayText", out var dt) && dt.GetString() is { Length: > 0 } timerText)
    {
      // NAG shows this text as the timer bar label; EQLP does so via AltTimerName.
      timer.AltTimerName = ConvertTemplates(timerText);
    }
    if (action.TryGetProperty("endEarlyPhrases", out var aeep) && aeep.ValueKind == JsonValueKind.Array)
    {
      foreach (var ee in aeep.EnumerateArray())
      {
        if (ee.TryGetProperty("phrase", out var ep) && ep.GetString() is { Length: > 0 } phrase)
        {
          var useRegex = ee.TryGetProperty("useRegEx", out var useRe) && ReadBool(useRe, false);
          timer.EndEarlyPhrases.Add((ApplyEndEarlyCaseSensitivity(ee, ConvertTemplates(phrase), useRegex, droppedFeatures), useRegex));
        }
      }
    }
    if (action.TryGetProperty("endingSoonDisplayText", out var esdt) && esdt.ValueKind == JsonValueKind.True &&
      action.TryGetProperty("endingSoonText", out var est) && est.GetString() is { Length: > 0 } etext)
    {
      timer.WarningTextToDisplay = ConvertTemplates(etext);
    }
    if (action.TryGetProperty("endingSoonSpeak", out var ess) && ess.ValueKind == JsonValueKind.True &&
      action.TryGetProperty("endingSoonSpeakPhrase", out var esp) && esp.GetString() is { Length: > 0 } stext)
    {
      timer.WarningTextToSpeak = ConvertTemplates(stext);
    }
    if (action.TryGetProperty("endedDisplayText", out var edt) && edt.ValueKind == JsonValueKind.True &&
      action.TryGetProperty("endedText", out var etdt) && etdt.GetString() is { Length: > 0 } edtext)
    {
      timer.EndTextToDisplay = ConvertTemplates(edtext);
    }
    if (action.TryGetProperty("endedSpeak", out var esk) && esk.ValueKind == JsonValueKind.True &&
      action.TryGetProperty("endedSpeakPhrase", out var espk) && espk.GetString() is { Length: > 0 } estext)
    {
      timer.EndTextToSpeak = ConvertTemplates(estext);
    }
    if (action.TryGetProperty("endingPlayAudio", out var epa) && epa.ValueKind == JsonValueKind.True &&
        action.TryGetProperty("endingPlayAudioFileId", out var epaf) && epaf.GetString() is { Length: > 0 } endingAudio)
    {
      timer.WarningSoundToPlay = ResolveAudioFile(endingAudio, missingAudioFiles);
    }
    if (action.TryGetProperty("endedPlayAudio", out var da) && da.ValueKind == JsonValueKind.True &&
        action.TryGetProperty("endedPlayAudioFileId", out var daf) && daf.GetString() is { Length: > 0 } endedAudio)
    {
      timer.EndSoundToPlay = ResolveAudioFile(endedAudio, missingAudioFiles);
    }
    if (action.TryGetProperty("endingDuration", out var edur) && edur.ValueKind is JsonValueKind.Number or JsonValueKind.String)
    {
      // ReadLong, not a raw cast: a finite double past long.MaxValue would unchecked-cast to
      // long.MinValue instead of keeping the default
      timer.WarningSeconds = ReadLong(edur, (long)timer.WarningSeconds);
    }
    if (action.TryGetProperty("duration", out var tdur))
    {
      if (handleNullDuration && tdur.ValueKind == JsonValueKind.Null)
      {
        // NAG null duration = indefinite timer ended by endEarlyPhrases; EQLP has no such
        // timer type. Import it disabled with the model's default duration — the end-early
        // phrases, warning and end data all still populate, so a user who knows the real
        // duration can set it and flip EnableTimer without re-importing.
        timer.Enabled = false;
        timer.DurationSeconds = 0.2; // Trigger.DurationSeconds model default
        droppedFeatures.Add("no set timer duration (timer left disabled)");
      }
      else if (tdur.ValueKind is JsonValueKind.Number or JsonValueKind.String)
      {
        timer.DurationSeconds = ReadNumber(tdur, timer.DurationSeconds);
      }
    }
    if (action.TryGetProperty("restartBehavior", out var rb) && rb.ValueKind is JsonValueKind.Number or JsonValueKind.String)
    {
      // NAG and EQLP number their restart options differently:
      //   NAG: 0=StartNewTimer, 1=RestartOnDuplicate (same display text),
      //        2=RestartTimer (all timers of this action), 3=DoNothing
      //   EQLP: 0=new entry, 1=clear all timers, 2=stop same display name then start,
      //         3=skip if any timer exists
      timer.TriggerAgainOption = ReadInt(rb, 0) switch
      {
        0 => 0,
        1 => 2,
        2 => 1,
        3 => 3,
        var v => v // unknown values pass through as-is
      };
    }
    if (action.TryGetProperty("useCustomColor", out var ucc) && ReadBool(ucc, false))
    {
      if (action.TryGetProperty("overrideTimerColor", out var otc) && otc.GetString() is { Length: > 0 } color)
      {
        timer.ActiveColor = ConvertColor(color);
      }
    }
    if (action.TryGetProperty("timerBackgroundColor", out var tbc) && tbc.GetString() is { Length: > 0 } bgColor)
    {
      timer.IdleColor = ConvertColor(bgColor);
    }
    if (action.TryGetProperty("overlayId", out var ov) && ov.GetString() is { Length: > 0 } overlayId)
    {
      if (!timer.Overlays.Contains(overlayId))
        timer.Overlays.Add(overlayId);
    }
  }

  // NAG end-early phrases are case-insensitive unless opted out per phrase. EQLP compiles every
  // regex pattern with IgnoreCase, so re-enable sensitivity for opted-out regex phrases (same
  // convention as capture phrases); non-regex case-sensitive phrases cannot be expressed and get
  // a dropped-feature note instead.
  private static string ApplyEndEarlyCaseSensitivity(JsonElement endEarlyPhrase, string convertedPhrase,
      bool useRegex, List<string> droppedFeatures)
  {
    var ignoreCase = !(endEarlyPhrase.TryGetProperty("ignoreCase", out var ic) && !ReadBool(ic, true));
    if (ignoreCase)
    {
      return convertedPhrase;
    }

    if (useRegex)
    {
      return "(?-i)" + convertedPhrase;
    }

    droppedFeatures.Add("case-sensitive non-regex end-early phrase(s) imported as case-insensitive");
    return convertedPhrase;
  }

  /* Merge trigger-level and a timer action's own end-early phrases (trigger-level first,
   * de-duplicated by phrase), then cap at EQLP's 3 EndEarlyPattern slots — anything beyond is
   * reported as dropped. One list per timer node: an action's endEarlyPhrases must not stop the
   * sibling nodes of other timers. */
  private static List<(string phrase, bool useRegex)> MergeEndEarlyPhrases(
      List<(string phrase, bool useRegex)> triggerLevel, List<(string phrase, bool useRegex)> actionLevel,
      List<string> droppedFeatures)
  {
    var merged = new List<(string phrase, bool useRegex)>(triggerLevel);
    foreach (var aep in actionLevel)
    {
      if (!merged.Any(x => x.phrase == aep.phrase))
      {
        merged.Add(aep);
      }
    }

    if (merged.Select(x => x.phrase).Distinct().Count() > 3)
    {
      droppedFeatures.Add("extra end-early phrases dropped (max 3)");
    }

    return merged.Take(3).ToList();
  }

  // EQLP supports at most 3 end-early patterns per trigger.
  private static void ApplyEndEarlyPatterns(Trigger triggerData, List<(string phrase, bool useRegex)> endEarly)
  {
    if (endEarly.Count > 0)
    {
      triggerData.EndEarlyPattern = endEarly[0].phrase;
      triggerData.EndUseRegex = endEarly[0].useRegex;
    }

    if (endEarly.Count > 1)
    {
      triggerData.EndEarlyPattern2 = endEarly[1].phrase;
      triggerData.EndUseRegex2 = endEarly[1].useRegex;
    }

    if (endEarly.Count > 2)
    {
      triggerData.EndEarlyPattern3 = endEarly[2].phrase;
      triggerData.EndUseRegex3 = endEarly[2].useRegex;
    }
  }

  // Resolve which capture phrases a NAG action targets: its "phrases" array, then a single
  // "phraseId", else null (unscoped — applies to every phrase). An empty "phrases" array means
  // no restriction, i.e. unscoped.
  private static List<string> ActionPhraseTargets(JsonElement action)
  {
    if (action.TryGetProperty("phrases", out var ap) && ap.ValueKind == JsonValueKind.Array)
    {
      var ids = new List<string>();
      foreach (var e in ap.EnumerateArray())
      {
        if (e.GetString() is { Length: > 0 } id)
        {
          ids.Add(id);
        }
      }
      return ids.Count > 0 ? ids : null;
    }

    if (action.TryGetProperty("phraseId", out var pid) && pid.GetString() is { Length: > 0 } single)
    {
      return [single];
    }

    return null;
  }

  /* Route one action's value to the EQLP field it feeds. Unscoped actions merge into the shared
   * trigger data (last wins, as before); actions scoped to specific phrases apply only to those
   * phrase nodes — otherwise every phrase node would inherit whatever the last scoped action set
   * (e.g. one phrase's audio leaking onto all of its siblings). */
  private static void RouteActionValue(string value, List<string> targets,
      Dictionary<string, PhraseScopedValues> phraseScoped, string field, Action<string> setGlobal)
  {
    if (value.Length == 0)
    {
      return;
    }

    if (targets is null)
    {
      setGlobal(value);
      return;
    }

    foreach (var target in targets)
    {
      if (!phraseScoped.TryGetValue(target, out var scoped))
      {
        phraseScoped[target] = scoped = new PhraseScopedValues();
      }

      switch (field)
      {
        case "display": scoped.TextToDisplay = value; break;
        case "speak": scoped.TextToSpeak = value; break;
        case "sound": scoped.SoundToPlay = value; break;
        case "share": scoped.TextToShare = value; break;
      }
    }
  }

  /* Non-timer action values scoped to specific capture phrases (see RouteActionValue). Applied
   * on top of the shared merge for exactly the matching phrase's nodes. */
  private sealed class PhraseScopedValues
  {
    public string TextToDisplay = "";
    public string TextToSpeak = "";
    public string SoundToPlay = "";
    public string TextToShare = "";
  }

  /* Parse a NAG trigger's actions into EQLP data. Unscoped non-timer actions (text, audio, TTS,
   * clipboard, counter, set/clear variable) merge into one shared Trigger — an EQLP node carries
   * them all; actions scoped to specific phrases are routed to those phrase nodes only.
   * Timer actions (3/4/6/10) are collected separately as TimerActionData: an EQLP trigger holds
   * exactly one timer, so ParseTrigger emits one node per NAG timer action and applies each
   * action's own fields to its node. */
  private static ParsedActions ParseActions(JsonElement actions, bool useRegEx)
  {
    var textToDisplay = "";
    var textToSpeak = "";
    var soundToPlay = "";
    var textToShare = "";
    // Colors set by non-timer actions (counters) — timer actions carry their own on TimerActionData.
    var activeColor = "";
    var idleColor = "";
    var selectedOverlays = new List<string>();
    var hasAction = false;
    var clearVariables = new List<string>();
    var phraseClearVariables = new List<(string phraseId, string variableName)>();
    // Non-timer action values scoped by the action's "phrases"/"phraseId" target list, keyed by
    // phraseId. Applied on top of the shared (unscoped) merge for exactly those phrase nodes.
    var phraseScoped = new Dictionary<string, PhraseScopedValues>();
    var setVariables = new List<(string phraseId, string variableName)>();
    var counterResetPhrases = new List<(string phrase, bool useRegex, string variableName)>();
    var counterVarName = "";
    // NAG counter idle-reset window (seconds); 0 = no counter in this trigger.
    var repeatedResetTime = 0.0;
    // One entry per NAG timer action — each becomes its own EQLP trigger node.
    var timerActions = new List<TimerActionData>();
    var droppedFeatures = new List<string>();
    var actionSummary = new List<string>();
    var missingAudioFiles = new List<string>();

    foreach (var action in actions.EnumerateArray())
    {
      var actionType = action.TryGetProperty("actionType", out var at) ? ReadInt(at, -1) : -1;

      // Skip blank/template actions that have no actionType (all fields null/default).
      // These appear in NAG data as empty template objects with no real behavior.
      if (actionType < 0)
      {
        continue;
      }

      switch (actionType)
      {
        case 0: // Text Overlay
          hasAction = true;
          if (action.TryGetProperty("displayText", out var dt) && dt.GetString() is { Length: > 0 } text)
          {
            RouteActionValue(ConvertTemplates(text), ActionPhraseTargets(action), phraseScoped, "display", v => textToDisplay = v);
          }
          // NAG's `duration` on a DisplayText action is only the number of seconds the
          // text stays on screen before auto-hiding (overlay.js: sendDisplayTextToOverlay).
          // It is deliberately NOT mapped to EnableTimer/DurationSeconds: EQLP would render
          // that as a visible countdown instead of a plain text notification, and EQLP has
          // no auto-hide for timerless text anyway. The text persists until cleared by a
          // clear action.
          // Collect overlayId for text overlay routing
          if (action.TryGetProperty("overlayId", out var ov0) && ov0.GetString() is { Length: > 0 } overlayId0)
          {
            if (!selectedOverlays.Contains(overlayId0))
              selectedOverlays.Add(overlayId0);
          }
          actionSummary.Add("Text");
          break;

        case 1: // Audio
          hasAction = true;
          if (action.TryGetProperty("audioFileId", out var af) && af.GetString() is { Length: > 0 } audio)
          {
            RouteActionValue(ResolveAudioFile(audio, missingAudioFiles), ActionPhraseTargets(action), phraseScoped, "sound", v => soundToPlay = v);
          }
          // Collect overlayId (NAG audio actions can reference overlays for positioning)
          if (action.TryGetProperty("overlayId", out var ov1) && ov1.GetString() is { Length: > 0 } overlayId1)
          {
            if (!selectedOverlays.Contains(overlayId1))
              selectedOverlays.Add(overlayId1);
          }
          actionSummary.Add("Audio");
          break;

        case 2: // TTS/Speech
          hasAction = true;
          if (action.TryGetProperty("displayText", out var st) && st.GetString() is { Length: > 0 } speak)
          {
            RouteActionValue(ConvertTemplates(speak), ActionPhraseTargets(action), phraseScoped, "speak", v => textToSpeak = v);
          }
          // Collect overlayId for TTS overlay routing
          if (action.TryGetProperty("overlayId", out var ov2) && ov2.GetString() is { Length: > 0 } overlayId2)
          {
            if (!selectedOverlays.Contains(overlayId2))
              selectedOverlays.Add(overlayId2);
          }
          actionSummary.Add("TTS");
          break;

        case 3: // Timer — fills up over time in NAG
          hasAction = true;
          {
            var timer = new TimerActionData { TimerType = 3 }; // EQLP Progress (fills up); Countdown would drain
            ParseTimerActionFields(action, handleNullDuration: true, timer, droppedFeatures, missingAudioFiles);
            timerActions.Add(timer);
          }
          actionSummary.Add("Timer");
          break;

        case 4: // Countdown — drains in NAG, optionally repeating
          hasAction = true;
          var repeatTimer = action.TryGetProperty("repeatTimer", out var rt) && ReadBool(rt, false);
          {
            var timer = new TimerActionData();
            if (repeatTimer)
            {
              timer.TimerType = 4; // EQLP Looping
              var repeatCount = action.TryGetProperty("repeatCount", out var rc) ? ReadInt(rc, 0) : 0;
              if (repeatCount > 0)
              {
                timer.TimesToLoop = repeatCount;
              }
              else
              {
                // NAG unlimited repeat — approximate with a large loop count.
                timer.TimesToLoop = UnlimitedRepeatLoops;
                droppedFeatures.Add("unlimited timer repeat (approximated)");
              }
            }
            else
            {
              timer.TimerType = 1; // EQLP Countdown (drains like NAG Countdown)
            }
            ParseTimerActionFields(action, handleNullDuration: true, timer, droppedFeatures, missingAudioFiles);
            timerActions.Add(timer);
          }
          actionSummary.Add(repeatTimer ? "Looping Timer" : "Timer");
          break;

        case 6: // DotTimer (older NAG versions called it "Timer with Remain") — per-target in NAG and drawn
          // filling up like Timers (width = perc * 100%). EQLP has no per-target timers or tick display; import
          // as a single filling Progress timer and report the divergence.
          hasAction = true;
          {
            var timer = new TimerActionData { TimerType = 3 }; // EQLP Progress (fills up like NAG DotTimer)
            ParseTimerActionFields(action, handleNullDuration: false, timer, droppedFeatures, missingAudioFiles);
            timerActions.Add(timer);
          }
          droppedFeatures.Add("dot timer approximated (per-target ticks and remain-after-end lost)");
          actionSummary.Add("Timer (partial)");
          break;

        case 9: // Clipboard/Chat Command
          hasAction = true;
          if (action.TryGetProperty("displayText", out var cb) && cb.GetString() is { Length: > 0 } clip)
          {
            RouteActionValue(ConvertTemplates(clip), ActionPhraseTargets(action), phraseScoped, "share", v => textToShare = v);
          }
          // Collect overlayId for clipboard overlay routing
          if (action.TryGetProperty("overlayId", out var ov9) && ov9.GetString() is { Length: > 0 } overlayId9)
          {
            if (!selectedOverlays.Contains(overlayId9))
              selectedOverlays.Add(overlayId9);
          }
          actionSummary.Add("Clipboard");
          break;

        case 10: // BeneficialTimer (older NAG versions called it "Buff Timer with Cast Time") — per-target in
          // NAG and drawn depleting like Countdowns. EQLP has no per-target timers; import as a single
          // depleting timer and report the divergence.
          hasAction = true;
          {
            var timer = new TimerActionData { TimerType = 1 }; // EQLP Countdown (depletes like NAG BeneficialTimer)
            ParseTimerActionFields(action, handleNullDuration: false, timer, droppedFeatures, missingAudioFiles);
            timerActions.Add(timer);
          }
          droppedFeatures.Add("per-target buff timer (imported as a single timer)");
          actionSummary.Add("Timer (partial)");
          break;

        case 7: // Clear Variable
          hasAction = true;
          if (action.TryGetProperty("variableName", out var vn) && vn.GetString() is { Length: > 0 } varName)
          {
            var clearedVarName = ConvertTemplates(varName);
            var actionPhraseId = action.TryGetProperty("phraseId", out var pid7) ? pid7.GetString() : null;
            // Determine which phrases this clear-variable action targets. If a "phrases"
            // array is present, route to each listed phrase. Otherwise fall back to
            // the single "phraseId" field, or treat as global if neither exists.
            var targetPhraseIds = new List<string>();
            if (action.TryGetProperty("phrases", out var ap7) && ap7.ValueKind == JsonValueKind.Array)
            {
              foreach (var ap in ap7.EnumerateArray())
              {
                var apStr = ap.GetString();
                if (!string.IsNullOrEmpty(apStr))
                  targetPhraseIds.Add(apStr);
              }
            }
            else if (!string.IsNullOrEmpty(actionPhraseId))
            {
              targetPhraseIds.Add(actionPhraseId);
            }

            if (targetPhraseIds.Count > 0)
            {
              // Phrase-specific clear: add a VariableAction to only the matching phrase triggers.
              // This handles cases like "Your X spell is interrupted" clearing SpellBeingCast,
              // where there's no timer and EndTimerClearVariables would never fire.
              foreach (var pid in targetPhraseIds)
                phraseClearVariables.Add((pid, clearedVarName));
            }
            else
            {
              // Global clear: applies when the trigger's timer ends naturally.
              clearVariables.Add(clearedVarName);
            }
          }
          // Capture displayText from clear-variable actions for phrase-specific TextToDisplay.
          // Some NAG clear-variable actions include display text (e.g., "Spell ${var} was interrupted.")
          // that should only be shown when the specific phrase(s) listed in the action's "phrases"
          // array match — NOT applied globally to all phrases. For example, phrase [0] ("You begin casting")
          // should NOT show an interrupt message just because phrase [3] has one.
          // The clear-variable action's display text (e.g. "Spell {var} was interrupted.") routes
          // through the same per-phrase mechanism as every other action: only the phrases listed
          // in its "phrases"/"phraseId" see it, unscoped applies to all. This is what keeps an
          // interrupt message off the begin-casting phrase.
          if (action.TryGetProperty("displayText", out var dt7) && dt7.GetString() is { Length: > 0 } clearDisplayText)
          {
            RouteActionValue(ConvertTemplates(clearDisplayText), ActionPhraseTargets(action), phraseScoped, "display", v => textToDisplay = v);
          }
          // NAG can attach an alert overlay + duration to a clear-variable action.
          // EQLP has no per-action overlay for non-timer actions, so report it as dropped.
          if (action.TryGetProperty("overlayId", out var cov7) && cov7.GetString() is { Length: > 0 })
            droppedFeatures.Add("clear variable action alert overlay");
          actionSummary.Add("clear variable");
          break;

        case 5: // Set Variable — store captured text from a phrase into a named variable
          {
            var svVarName = action.TryGetProperty("variableName", out var vn5) ? vn5.GetString() : null;
            var phraseId = action.TryGetProperty("phraseId", out var pid) ? pid.GetString() : null;
            if (!string.IsNullOrEmpty(svVarName))
            {
              // Record this mapping so the corresponding capture phrase's
              // numbered group can be converted to a named group.
              setVariables.Add((phraseId, svVarName));
              actionSummary.Add("set variable");
            }
            else
            {
              droppedFeatures.Add("set variable (no name)");
            }
          }
          break;

        case 8: // Counter — invisible in NAG (no timer component); an in-memory tally that
          // increments on match and resets after its duration elapses without a new increment.
          hasAction = true;
          if (action.TryGetProperty("displayText", out var cd) && cd.GetString() is { Length: > 0 } counterText)
          {
            // Only set textToDisplay from the counter's displayText if no prior
            // action (e.g. text overlay) has already provided a more descriptive label.
            // The counter's displayText doubles as the variable name regardless.
            if (string.IsNullOrEmpty(textToDisplay))
            {
              textToDisplay = ConvertTemplates(counterText);
            }
            counterVarName = counterText;
          }
          // NAG's counter duration is an idle-reset window, not a visible countdown —
          // map it to EQLP's RepeatedResetTime (resets {repeated}/{counter} after N
          // idle seconds). Do NOT set durationSeconds: that would create a timer
          // NAG never shows and would clobber another action's duration.
          if (action.TryGetProperty("duration", out var cdur) && cdur.ValueKind is JsonValueKind.Number or JsonValueKind.String)
          {
            repeatedResetTime = ReadNumber(cdur, repeatedResetTime);
          }
          // Map overlayId
          if (action.TryGetProperty("overlayId", out var cov8) && cov8.GetString() is { Length: > 0 } overlayId8)
          {
            if (!selectedOverlays.Contains(overlayId8))
              selectedOverlays.Add(overlayId8);
          }
          // Map colors (consistent with ParseTimerActionFields — check useCustomColor first)
          if (action.TryGetProperty("useCustomColor", out var ucc8) && ReadBool(ucc8, false))
          {
            if (action.TryGetProperty("overrideTimerColor", out var ctc) && ctc.GetString() is { Length: > 0 } color8)
            {
              activeColor = ConvertColor(color8);
            }
          }
          if (action.TryGetProperty("timerBackgroundColor", out var ctbc) && ctbc.GetString() is { Length: > 0 } bgColor8)
          {
            idleColor = ConvertColor(bgColor8);
          }
          // Map reset counter phrases — these become separate triggers that clear the variable
          if (action.TryGetProperty("resetCounterPhrases", out var rcp) && rcp.ValueKind == JsonValueKind.Array)
          {
            foreach (var rp in rcp.EnumerateArray())
            {
              if (rp.TryGetProperty("phrase", out var rpp) && rpp.GetString() is { Length: > 0 } resetPhrase)
              {
                var useRegex = rp.TryGetProperty("useRegEx", out var rpr) && ReadBool(rpr, false);
                counterResetPhrases.Add((ConvertTemplates(resetPhrase), useRegex, counterVarName));
              }
            }
          }
          actionSummary.Add("Counter");
          break;

        case 12: // Screen Glow - unsupported, skip
          droppedFeatures.Add("screen glow");
          Log.Debug($"Skipping unsupported action type 12 (Screen Glow) in trigger");
          break;

        default:
          // Types 11,13,14,15 - death recap display, clear all, stopwatch, lists - unsupported
          var skipNames = actionType switch
          {
            11 => "death recap display",
            13 => "clear all",
            14 => "stopwatch timer",
            15 => "list widget",
            _ => $"action type {actionType}"
          };
          // Include the variable name if available for more context
          var skipDetail = skipNames;
          if (action.TryGetProperty("variableName", out var vn2) && vn2.GetString() is { Length: > 0 } vname2)
          {
            skipDetail = $"{skipNames} ({vname2})";
          }
          droppedFeatures.Add(skipDetail);
          Log.Debug($"Skipping unsupported action type {actionType} in trigger");
          break;
      }
    }

    if (!hasAction)
    {
      return new ParsedActions
      {
        SkipReason = "No supported actions",
        DroppedFeatures = droppedFeatures,
        MissingAudioFiles = missingAudioFiles,
        ActionSummary = string.Join(", ", actionSummary),
        TimerActions = timerActions
      };
    }

    // Build the shared Trigger from the non-timer actions. Timer-specific fields (type, duration,
    // label, restart behavior, loop count, colors, end texts, end-early phrases) live on each
    // TimerActionData — ParseTrigger applies exactly one of them per emitted node.
    var triggerData = new Trigger
    {
      Pattern = "", // Overwritten by ParseTrigger after this method returns
      UseRegex = useRegEx,
      TextToDisplay = textToDisplay,
      TextToSpeak = textToSpeak,
      SoundToPlay = soundToPlay,
      TextToShare = textToShare,
      ActiveColor = activeColor,
      IdleColor = idleColor,
      SelectedOverlays = selectedOverlays.Count > 0 ? selectedOverlays : [],
      EndTimerClearVariables = clearVariables.Count > 0 ? string.Join(", ", clearVariables) : "",
      VariableActions = counterVarName.Length > 0
        ? new List<VariableAction> { new() { ActionType = 0, DataType = 1, VariableName = counterVarName, Step = 1 } }
        : new List<VariableAction>(),
    };

    // Keep the model default for non-counter triggers; only counters override it.
    if (repeatedResetTime > 0)
    {
      triggerData.RepeatedResetTime = repeatedResetTime;
    }

    return new ParsedActions
    {
      BaseTriggerData = triggerData,
      DroppedFeatures = droppedFeatures,
      ActionSummary = string.Join(", ", actionSummary),
      MissingAudioFiles = missingAudioFiles,
      SetVariables = setVariables,
      CounterResetPhrases = counterResetPhrases,
      PhraseClearVariables = phraseClearVariables,
      PhraseScoped = phraseScoped,
      TimerActions = timerActions
    };
  }

  private static string HtmlEncode(string value)
  {
    return string.IsNullOrEmpty(value) ? "" : System.Net.WebUtility.HtmlEncode(value);
  }

  // Convert dropped feature names into user-friendly descriptions for the HTML report
  private static string FormatDroppedFeaturesForReport(string reason)
  {
    var parts = reason.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    var friendly = new List<string>();
    foreach (var part in parts)
    {
      var trimmed = part.Trim();
      if (trimmed.StartsWith("set variable (", StringComparison.OrdinalIgnoreCase))
      {
        var varName = trimmed.Substring("set variable (".Length).TrimEnd(')');
        friendly.Add($"Set variable ({HtmlEncode(varName)}): stores captured text in a named variable, but EQLP requires regex named groups instead. Trigger works but stored values may be empty.");
      }
      else if (trimmed.StartsWith("class level filtering", StringComparison.OrdinalIgnoreCase))
      {
        friendly.Add("Class level filtering: trigger was restricted to specific class levels, which EQLP does not support.");
      }
      else if (trimmed.Equals("screen flash", StringComparison.OrdinalIgnoreCase))
      {
        friendly.Add("Screen flash: NAG visual screen flash effect has no EQLP equivalent. Other actions in this trigger are still imported.");
      }
      else if (trimmed.Equals("hotkey", StringComparison.OrdinalIgnoreCase))
      {
        friendly.Add("Hotkey: NAG hotkey triggers fire on keyboard input, but EQLP only supports chat-log-based triggers.");
      }
      else if (trimmed.Equals("global reset", StringComparison.OrdinalIgnoreCase))
      {
        friendly.Add("Global reset: NAG clears all variables globally; EQLP only supports per-trigger variable clearing via EndTimerClearVariables.");
      }
      else if (trimmed.Equals("list widget", StringComparison.OrdinalIgnoreCase))
      {
        friendly.Add("List widget: complex NAG UI component for managing lists of timers with enrollment and enumeration phrases. No EQLP equivalent.");
      }
      else if (trimmed.StartsWith("remain-after-ended timer", StringComparison.OrdinalIgnoreCase))
      {
        friendly.Add("Remain-after-ended timer: NAG keeps the timer visible after it ends; EQLP timers disappear when they complete.");
      }
      else if (trimmed.StartsWith("cast time tracking", StringComparison.OrdinalIgnoreCase))
      {
        friendly.Add("Cast time tracking: NAG adjusts timer duration based on spell cast time; EQLP uses fixed durations only.");
      }
      else if (trimmed.StartsWith("no set timer duration", StringComparison.OrdinalIgnoreCase))
      {
        friendly.Add("No set timer duration: this NAG timer has no fixed length; it runs until its stop phrase matches. Imported disabled (placeholder duration) with its stop-phrase, warning and end data populated — set the real duration on the timer and enable it.");
      }
      else
      {
        friendly.Add(HtmlEncode(trimmed));
      }
    }
    return string.Join("<br>", friendly);
  }

  private static ExportTriggerNode ParseOverlay(JsonElement element)
  {
    var name = element.GetProperty("name").GetString();
    if (string.IsNullOrEmpty(name))
    {
      return null;
    }

    var overlayId = element.GetProperty("overlayId").GetString() ?? "";
    var overlayType = element.TryGetProperty("overlayType", out var typeProp) ? typeProp.GetString() : "Timer";

    var isTimerOverlay = overlayType?.Equals("Timer", StringComparison.OrdinalIgnoreCase) == true;
    var isTextOverlay = overlayType?.Equals("Alert", StringComparison.OrdinalIgnoreCase) == true;

    var fontColor = element.TryGetProperty("fontColor", out var fc) ? fc.GetString() : "#ffffff";
    var backgroundColor = element.TryGetProperty("backgroundColor", out var bc) ? bc.GetString() : "#000000";
    var backgroundTransparency = element.TryGetProperty("backgroundTransparency", out var bt) ? ReadNumber(bt, 0) : 0;
    var timerColor = element.TryGetProperty("timerColor", out var tc) ? tc.GetString() : "#008000";
    var timerBackgroundColor = element.TryGetProperty("timerBackgroundColor", out var tbc) ? tbc.GetString() : null;
    var fontFamily = element.TryGetProperty("fontFamily", out var ff) ? ff.GetString() : "Segoe UI";
    var fontSize = element.TryGetProperty("fontSize", out var fs) ? ReadInt(fs, 12) : 12;
    var fontWeight = element.TryGetProperty("fontWeight", out var fw) ? ReadInt(fw, 400) : 400;
    var horizontalAlignment = element.TryGetProperty("horizontalAlignment", out var ha) ? ha.GetString() : "center";
    var verticalAlignment = element.TryGetProperty("verticalAlignment", out var va) ? va.GetString() : "bottom";

    // Parse textOverflow for TextOverlayWrap support (NAG whiteSpace=nowrap → EQLP TextOverlayWrap=false)
    var textOverlayWrap = true;
    if (element.TryGetProperty("textOverflow", out var to) && to.ValueKind == JsonValueKind.Object &&
        to.TryGetProperty("whiteSpace", out var ws) && ws.GetString() is { } whiteSpace &&
        whiteSpace.Equals("nowrap", StringComparison.OrdinalIgnoreCase))
    {
      textOverlayWrap = false;
    }

    // Map NAG timerSortType → EQLP SortBy.
    // NAG (overlay.js): 0=None, 1=Ascending (most time remaining first), 2=Descending (ending soonest first).
    // EQLP: 0=Trigger Time, 1=Remaining Time (ending soonest first), 2/3=Timer Name.
    // NAG Descending matches EQLP Remaining Time exactly; Ascending has no equivalent and is kept as 1
    // (reversed order vs NAG — reported via ConvertOverlays' overlayNotes).
    var nagSort = element.TryGetProperty("timerSortType", out var st) ? ReadInt(st, 0) : 0;
    var sortBy = nagSort == 2 ? 1 : nagSort;

    // Map showTextGlow → UseTextDropShadow (default true for backward compat)
    var useTextDropShadow = element.TryGetProperty("showTextGlow", out var stg) ? ReadBool(stg, true) : true;

    // No Id on the export node: EQLP node ids are always generated by the store (UUIDs) —
    // external systems' ids never become node ids. The NAG identity goes into Source, which
    // doubles as the re-import key: re-migrating a NAG database updates this overlay's stored
    // node in place instead of inserting a second copy.
    return new ExportTriggerNode
    {
      Name = name,
      OverlayData = new Overlay
      {
        Source = NagSourcePrefix + overlayId,
        Width = element.TryGetProperty("windowWidth", out var ww) ? ReadLong(ww, 300) : 300,
        Height = element.TryGetProperty("windowHeight", out var wh) ? ReadLong(wh, 400) : 400,
        Left = element.TryGetProperty("x", out var x) ? ReadLong(x, 100) : 100,
        Top = element.TryGetProperty("y", out var y) ? ReadLong(y, 200) : 200,
        FontFamily = fontFamily ?? "Segoe UI",
        FontSize = $"{fontSize}pt",
        FontWeight = ConvertFontWeight(fontWeight),
        FontColor = ConvertColor(fontColor),
        BackgroundColor = ConvertBackground(backgroundColor, backgroundTransparency),
        ActiveColor = ConvertColor(timerColor),
        IdleColor = timerBackgroundColor is not null ? ConvertColor(timerBackgroundColor) : "#FF8f1515",
        ResetColor = "#FF8f1515",
        HorizontalAlignment = ConvertHorizontalAlignment(horizontalAlignment),
        VerticalAlignment = ConvertVerticalAlignment(verticalAlignment),
        IsTimerOverlay = isTimerOverlay,
        IsTextOverlay = isTextOverlay,
        TextOverlayWrap = textOverlayWrap,
        SortBy = sortBy,
        ShowActive = true,
        ShowIdle = true,
        ShowReset = true,
        UseTextDropShadow = useTextDropShadow,
        FadeDelay = 10
      }
    };
  }
}
