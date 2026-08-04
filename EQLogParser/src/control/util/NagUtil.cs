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

public class NagImportResult
{
  public string TriggerName { get; set; }
  public string TriggerId { get; set; }
  public string Status { get; set; } // "Imported", "Partial", "Skipped"
  public string Reason { get; set; } // For skipped/partial: which features were unsupported
  public string FolderPath { get; set; }
  public string ActionsSummary { get; set; }
  public double Score { get; set; } = 0.5;
  public List<string> DroppedFeatures { get; set; }
  public List<string> MissingAudioFiles { get; set; } = [];
}

/// <summary>
/// Metadata about a NAG trigger, keyed by NAG triggerId. Used for character state import
/// and other profile-level operations that need to correlate NAG IDs with imported EQLP nodes.
/// </summary>
public class NagTriggerMetadata
{
  public string TriggerName { get; set; }
  public string FolderPath { get; set; }
  public double Score { get; set; }
  public string ActionsSummary { get; set; }
  public List<string> DroppedFeatures { get; set; }
  public List<string> MissingAudioFiles { get; set; } = [];
}

internal static class NagUtil
{
  private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

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

  // NAG template syntax: ${var} or {groupName} → EQLP: {$var} or {$groupName}
  private static readonly Regex VarPattern = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);
  private static readonly Regex GroupPattern = new(@"(?<!\$)\{([a-zA-Z][^}]*)\}", RegexOptions.Compiled);

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
  //   ${SpellBeingCast} → (?<SpellBeingCast>.+?) (handled by DollarVarRegex above)
  private static readonly Regex UnhandledVarRegex = new(
    @"(?<!\$)\{(?!S\d?|s\d?|N\d?|n\d?|TS|ts|[Cc])[a-zA-Z][^}]*\}",
    RegexOptions.Compiled);

  // Convert NAG template syntax to EQLP syntax
  private static string ConvertTemplates(string input)
  {
    if (string.IsNullOrEmpty(input))
    {
      return input;
    }

    // ${var} → {$var} ($$ = literal $, $1 = capture group)
    input = VarPattern.Replace(input, "{$$1}");

    // {groupName} → {$groupName} (but not already converted {$...})
    input = GroupPattern.Replace(input, "{$1}");

    return input;
  }

  // NAG score (0-1, higher = more important) → EQLP Priority (lower = more important)
  private static long ConvertScore(double score)
  {
    // Map: score 1.0 → priority 1, score 0.0 → priority 5
    var inverted = 1.0 - score;
    return Math.Clamp((long)(inverted * 4) + 1, 1, 5);
  }

  // Parse NAG conditions into EQLP MatchVariableCondition syntax
  private static string ParseConditions(JsonElement element)
  {
    if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
    {
      return null;
    }

    var parts = new List<string>();
    foreach (var cond in element.EnumerateArray())
    {
      // Only handle conditionType == 1 (variable-based conditions)
      if (!cond.TryGetProperty("conditionType", out var ct) || ct.GetInt32() != 1)
      {
        continue;
      }

      var varName = cond.TryGetProperty("variableName", out var vn) ? vn.GetString() : null;
      if (string.IsNullOrEmpty(varName))
      {
        continue;
      }

      var operatorType = cond.TryGetProperty("operatorType", out var ot) && ot.ValueKind != JsonValueKind.Null
        ? ot.GetInt32()
        : -1;

      switch (operatorType)
      {
        case 16: // Equality check: {var} = "value"
          {
            var value = cond.TryGetProperty("variableValue", out var vv) ? vv.GetString() : null;
            if (!string.IsNullOrEmpty(value))
            {
              parts.Add($"{{{varName}}} = \"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
            }
          }
          break;

        case 1: // Contains check — NAG pipe-separated values mean OR, not literal substring
          {
            var value = cond.TryGetProperty("variableValue", out var vv) ? vv.GetString() : null;
            if (!string.IsNullOrEmpty(value))
            {
              // NAG uses | to separate multiple values meaning "contains any of".
              // EQLP's contains operator does a literal substring check, so we need
              // to split on | and create separate contains clauses joined by ||.
              var values = value.Split('|', StringSplitOptions.RemoveEmptyEntries);
              if (values.Length > 1)
              {
                var escaped = values.Select(v => v.Replace("\\", "\\\\").Replace("\"", "\\\"")).Select(v => $"{{{varName}}} contains \"{v}\"");
                parts.Add(string.Join(" || ", escaped));
              }
              else
              {
                parts.Add($"{{{varName}}} contains \"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
              }
            }
          }
          break;

        case 2: // Existence check (truthy): {var} standalone
          {
            parts.Add($"{{{varName}}}");
          }
          break;

        default:
          Log.Debug($"Skipping condition with unknown operatorType {operatorType}");
          break;
      }
    }

    return parts.Count > 0 ? string.Join(" && ", parts) : null;
  }

  // Resolve audio file ID to filename using files-database.json if available
  private static Dictionary<string, string> _audioFileMap;
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

  internal static (List<ExportTriggerNode> nodes, List<NagImportResult> results, Dictionary<string, NagTriggerMetadata> metadata) ConvertTriggers(string json, string databaseDirectory = null)
  {
    var nodes = new List<ExportTriggerNode>();
    var results = new List<NagImportResult>();
    var metadata = new Dictionary<string, NagTriggerMetadata>();

    try
    {
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
      foreach (var trigger in triggers.EnumerateArray())
      {
        var parsed = ParseTrigger(trigger, databaseDirectory);
        foreach (var n in parsed.nodes)
        {
          // Set folder path on the result for reporting
          if (trigger.TryGetProperty("folderId", out var fid) &&
              fid.GetString() is { } folderId &&
              folderPaths.TryGetValue(folderId, out var fpath))
          {
            parsed.result.FolderPath = fpath;
          }
          else
          {
            parsed.result.FolderPath = "(root)";
          }

          // Wrap trigger node in folder hierarchy if not at root level
          if (parsed.result.FolderPath != "(root)")
          {
            var wrapped = WrapInFolderHierarchy(parsed.result.FolderPath, n);
            nodes.Add(wrapped);
          }
          else
          {
            nodes.Add(n);
          }
        }
        results.Add(parsed.result);

        // Build metadata dictionary keyed by NAG triggerId for profile-level operations
        if (!string.IsNullOrEmpty(parsed.result.TriggerId) && !metadata.ContainsKey(parsed.result.TriggerId))
        {
          metadata[parsed.result.TriggerId] = new NagTriggerMetadata
          {
            TriggerName = parsed.result.TriggerName,
            FolderPath = parsed.result.FolderPath,
            Score = parsed.result.Score,
            ActionsSummary = parsed.result.ActionsSummary,
            DroppedFeatures = parsed.result.DroppedFeatures,
            MissingAudioFiles = parsed.result.MissingAudioFiles
          };
        }
      }
    }
    catch (Exception ex)
    {
      Log.Error("Error Parsing NAG Triggers", ex);
    }

    // Wrap all nodes in a root node — consistent with GINA export format
    // so the first Import() overload skips the root and processes folders correctly
    var rootNode = new ExportTriggerNode { Nodes = nodes };
    return (new List<ExportTriggerNode> { rootNode }, results, metadata);
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

  // Wrap a trigger node in the folder hierarchy (creates parent folder nodes)
  private static ExportTriggerNode WrapInFolderHierarchy(string folderPath, ExportTriggerNode triggerNode)
  {
    var parts = folderPath.Split('/');
    ExportTriggerNode current = null;

    for (var i = parts.Length - 1; i >= 0; i--)
    {
      if (current == null)
      {
        // leaf: trigger node goes inside the last folder
        var folderNode = new ExportTriggerNode { Name = parts[i], Nodes = [triggerNode] };
        current = folderNode;
      }
      else
      {
        // wrap in parent folder
        var folderNode = new ExportTriggerNode { Name = parts[i], Nodes = [current] };
        current = folderNode;
      }
    }

    return current ?? triggerNode;
  }

  private static (List<ExportTriggerNode> nodes, NagImportResult result) ParseTrigger(JsonElement element, string databaseDirectory)
  {
    var name = element.GetProperty("name").GetString();
    if (string.IsNullOrEmpty(name))
    {
      return ([], new NagImportResult { TriggerName = name ?? "(null)", Status = "Skipped", Reason = "Missing name" });
    }

    // Skip dev-only triggers
    if (element.TryGetProperty("onlyExecuteInDev", out var devProp) && devProp.GetBoolean())
    {
      return ([], new NagImportResult { TriggerName = name, TriggerId = element.GetProperty("triggerId").GetString(), Status = "Skipped", Reason = "Dev-only trigger" });
    }

    var triggerId = element.GetProperty("triggerId").GetString() ?? "";
    var comments = element.TryGetProperty("comments", out var c) ? c.GetString() : null;
    var score = element.TryGetProperty("score", out var s) ? s.GetDouble() : 0.5;
    var useCooldown = element.TryGetProperty("useCooldown", out var cd) && cd.GetBoolean();
    var cooldownDuration = element.TryGetProperty("cooldownDuration", out var cdDur) ? cdDur.GetDouble() : 0;

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
      return ([], new NagImportResult { TriggerName = name, TriggerId = triggerId, Status = "Skipped", Reason = "No capture phrases" });
    }

    // Collect all phrases — each becomes its own EQLP trigger (no alternation combining).
    // This avoids named group collisions when multiple phrases share capture group names.
    var phrases = new List<(string pattern, bool useRegex)>();
    foreach (var phrase in capturePhrases.EnumerateArray())
    {
      if (phrase.TryGetProperty("phrase", out var p) && p.GetString() is { Length: > 0 } text)
      {
        var useRegex = phrase.TryGetProperty("useRegEx", out var re) && re.GetBoolean();

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
            m.Groups[1].Value.Equals("Character", StringComparison.OrdinalIgnoreCase)
              ? "{c}"
              : $"(?<{m.Groups[1].Value}>.+?)");

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

          // Do NOT call ConvertTemplates — leave {S}, {N}, {TS} as-is for EQLP runtime
          phrases.Add((text, useRegex));
        }
        else
        {
          phrases.Add((ConvertTemplates(text), false));
        }
      }
      else if (phrase.TryGetProperty("useRegEx", out var re2) && re2.GetBoolean())
      {
        // Phrase text was empty/null but useRegEx is set — skip it
      }
    }

    if (phrases.Count == 0)
    {
      return ([], new NagImportResult { TriggerName = name, TriggerId = triggerId, Status = "Skipped", Reason = "No valid phrases" });
    }

    // Parse actions once — shared across all phrase-based triggers
    var parsed = ParseActions(element.GetProperty("actions"), score, phrases.Any(p => p.useRegex), useCooldown, cooldownDuration, databaseDirectory);
    if (parsed.triggerData is null)
    {
      return ([], new NagImportResult { TriggerName = name, TriggerId = triggerId, Status = "Skipped", Reason = parsed.reason ?? "No supported actions" });
    }

    var baseTriggerData = parsed.triggerData;
    var droppedFeatures = parsed.droppedFeatures;

    // Add class level filtering as a dropped feature (no EQLP equivalent)
    if (hasClassLevels)
    {
      droppedFeatures.Add("class level filtering");
    }

    // Parse conditions into MatchVariableCondition (shared across all phrase-triggers)
    string conditionStr = null;
    if (element.TryGetProperty("conditions", out var conds))
    {
      conditionStr = ParseConditions(conds);
    }

    // Build Comments field: preserve original NAG comments as-is.
    // Only append notes about dropped features when something was actually lost in conversion.
    var commentParts = new List<string>();
    if (!string.IsNullOrEmpty(comments))
      commentParts.Add(comments);
    if (droppedFeatures.Count > 0)
      commentParts.Add($"EQLP Import Notes: {string.Join(", ", droppedFeatures)}");
    var triggerComments = commentParts.Count > 0 ? string.Join("\n", commentParts) : null;

    // Build one EQLP trigger per capture phrase (no regex alternation combining)
    var nodes = new List<ExportTriggerNode>();
    for (var i = 0; i < phrases.Count; i++)
    {
      var (pattern, useRegEx) = phrases[i];
      var triggerData = baseTriggerData.Clone();

      // Assign the pattern for this phrase
      triggerData.Pattern = pattern;
      triggerData.UseRegex = useRegEx;

      // Apply conditions
      if (!string.IsNullOrEmpty(conditionStr))
      {
        triggerData.MatchVariableCondition = conditionStr;
      }

      // Apply shared metadata
      triggerData.Comments = triggerComments;
      triggerData.Priority = ConvertScore(score);
      triggerData.LockoutTime = useCooldown ? cooldownDuration : 0;

      var triggerName = phrases.Count > 1 ? $"{name} #{i + 1}" : name;

      nodes.Add(new ExportTriggerNode
      {
        Id = Guid.NewGuid().ToString(),
        Name = triggerName,
        OriginalId = triggerId,
        TriggerData = triggerData
      });
    }

    // Parse end-early phrases — merge trigger-level and action-level (max 3 slots)
    // Each entry tracks (phrase, useRegEx) so EndUseRegex is set correctly
    var allEndEarlyPhrases = new List<(string phrase, bool useRegex)>();
    if (element.TryGetProperty("endEarlyPhrases", out var eep) && eep.ValueKind == JsonValueKind.Array)
    {
      foreach (var ee in eep.EnumerateArray())
      {
        if (ee.TryGetProperty("phrase", out var ep) && ep.GetString() is { Length: > 0 } phrase)
        {
          var useRegex = ee.TryGetProperty("useRegEx", out var useRe) && useRe.GetBoolean();
          allEndEarlyPhrases.Add((ConvertTemplates(phrase), useRegex));
        }
        if (allEndEarlyPhrases.Count >= 3) break;
      }
    }
    // Merge action-level end-early phrases (537 timer actions have these in real data)
    var actionEep = parsed.actionEndEarlyPhrases;
    for (var idx = 0; idx < actionEep.phrases.Count && allEndEarlyPhrases.Count < 3; idx++)
    {
      var aep = actionEep.phrases[idx];
      if (!allEndEarlyPhrases.Any(x => x.phrase == aep))
      {
        allEndEarlyPhrases.Add((aep, actionEep.regexFlags[idx]));
      }
    }

    // Apply end-early patterns to ALL phrase-triggers (shared across them)
    foreach (var node in nodes)
    {
      if (allEndEarlyPhrases.Count > 0)
      {
        node.TriggerData.EndEarlyPattern = allEndEarlyPhrases[0].phrase;
        node.TriggerData.EndUseRegex = allEndEarlyPhrases[0].useRegex;
      }
      if (allEndEarlyPhrases.Count > 1)
      {
        node.TriggerData.EndEarlyPattern2 = allEndEarlyPhrases[1].phrase;
        node.TriggerData.EndUseRegex2 = allEndEarlyPhrases[1].useRegex;
      }
      if (allEndEarlyPhrases.Count > 2)
      {
        node.TriggerData.EndEarlyPattern3 = allEndEarlyPhrases[2].phrase;
        node.TriggerData.EndUseRegex3 = allEndEarlyPhrases[2].useRegex;
      }
    }

    // Determine import status and reason
    // Triggers with missing audio files are Partial (imported but incomplete)
    var hasMissingAudio = parsed.missingAudioFiles?.Count > 0;
    var status = hasMissingAudio || droppedFeatures.Count > 0 ? "Partial" : "Imported";
    var reason = isSequential ? "Sequential capture method (not supported)" :
                 hasClassLevels ? "Class level filtering (not supported)" :
                 droppedFeatures.Count > 0 ? string.Join(", ", droppedFeatures) :
                 hasMissingAudio ? $"{parsed.missingAudioFiles.Count} missing audio file(s)" :
                 null;

    // Sequential capture triggers cannot be reliably converted — skip them entirely
    if (isSequential)
    {
      return ([], new NagImportResult
      {
        TriggerName = name,
        TriggerId = triggerId,
        Status = "Skipped",
        Reason = "Sequential capture method (not supported)",
        ActionsSummary = parsed.actionSummary,
        MissingAudioFiles = parsed.missingAudioFiles
      });
    }

    // Build actions summary
    var actionSummary = parsed.actionSummary;

    return (nodes, new NagImportResult
    {
      TriggerName = name,
      TriggerId = triggerId,
      Status = status,
      Reason = reason,
      ActionsSummary = actionSummary,
      Score = score,
      DroppedFeatures = droppedFeatures,
      MissingAudioFiles = parsed.missingAudioFiles
    });
  }

  private static (Trigger triggerData, List<string> droppedFeatures, string reason, string actionSummary, (List<string> phrases, List<bool> regexFlags) actionEndEarlyPhrases, List<string> missingAudioFiles) ParseActions(
      JsonElement actions, double score, bool useRegEx, bool useCooldown, double cooldownDuration, string databaseDirectory)
  {
    var textToDisplay = "";
    var textToSpeak = "";
    var soundToPlay = "";
    var textToShare = "";
    var durationSeconds = 0.0;
    var timerType = 0;
    var triggerAgainOption = -1;
    var warningSeconds = 0L;
    var activeColor = "";
    var selectedOverlays = new List<string>();
    var actionEndEarlyPhrases = new List<string>();
    var actionEndEarlyUseRegex = new List<bool>();
    var warningTextToDisplay = "";
    var warningTextToSpeak = "";
    var endTextToDisplay = "";
    var endTextToSpeak = "";
    var hasAction = false;
    var clearVariables = new List<string>();
    var droppedFeatures = new List<string>();
    var actionSummary = new List<string>();
    var missingAudioFiles = new List<string>();

    foreach (var action in actions.EnumerateArray())
    {
      var actionType = action.TryGetProperty("actionType", out var at) ? at.GetInt32() : -1;

      switch (actionType)
      {
        case 0: // Text Overlay
          hasAction = true;
          if (action.TryGetProperty("displayText", out var dt) && dt.GetString() is { Length: > 0 } text)
          {
            textToDisplay = ConvertTemplates(text);
          }
          if (action.TryGetProperty("duration", out var dur) && dur.ValueKind is JsonValueKind.Number or JsonValueKind.String)
          {
            durationSeconds = dur.GetDouble();
          }
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
            // Try to resolve via files-database.json
            soundToPlay = _audioFileMap?.TryGetValue(audio, out var resolvedName) == true ? resolvedName : audio;
            // Map NAG's "Speech ding" (ShortWarningPing) to EQLP's built-in alert1.wav
            if (soundToPlay == "Speech ding")
            {
              soundToPlay = "alert1.wav";
            }
            // Track if the resolved file doesn't exist in data/sounds/
            if (!TriggerUtil.SoundFileExists(soundToPlay))
            {
              missingAudioFiles.Add(soundToPlay);
            }
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
            textToSpeak = ConvertTemplates(speak);
          }
          // Collect overlayId for TTS overlay routing
          if (action.TryGetProperty("overlayId", out var ov2) && ov2.GetString() is { Length: > 0 } overlayId2)
          {
            if (!selectedOverlays.Contains(overlayId2))
              selectedOverlays.Add(overlayId2);
          }
          actionSummary.Add("TTS");
          break;

        case 3: // Timer (Countdown)
        case 4: // Repeating Timer
          hasAction = true;
          timerType = actionType == 4 ? 4 : 1;
          if (action.TryGetProperty("displayText", out var td) && td.GetString() is { Length: > 0 } timerText)
          {
            textToDisplay = ConvertTemplates(timerText);
          }
          // Collect action-level endEarlyPhrases for dynamic-duration timers
          if (action.TryGetProperty("endEarlyPhrases", out var aeep) && aeep.ValueKind == JsonValueKind.Array)
          {
            foreach (var ee in aeep.EnumerateArray())
            {
              if (ee.TryGetProperty("phrase", out var ep) && ep.GetString() is { Length: > 0 } phrase)
              {
                actionEndEarlyPhrases.Add(ConvertTemplates(phrase));
                actionEndEarlyUseRegex.Add(ee.TryGetProperty("useRegEx", out var useRe) && useRe.GetBoolean());
              }
            }
          }
          // Map ending/ended sub-action text to EQLP warning/end fields.
          // NAG uses boolean flags (endingSoonDisplayText, endingSoonSpeak, etc.) to indicate
          // whether the feature is enabled, with actual text in separate fields (endingSoonText,
          // endingSoonSpeakPhrase, endedText, endedSpeakPhrase).
          if (action.TryGetProperty("endingSoonDisplayText", out var esdt) && esdt.ValueKind == JsonValueKind.True &&
            action.TryGetProperty("endingSoonText", out var est) && est.GetString() is { Length: > 0 } etext)
          {
            warningTextToDisplay = ConvertTemplates(etext);
          }
          if (action.TryGetProperty("endingSoonSpeak", out var ess) && ess.ValueKind == JsonValueKind.True &&
            action.TryGetProperty("endingSoonSpeakPhrase", out var esp) && esp.GetString() is { Length: > 0 } stext)
          {
            warningTextToSpeak = ConvertTemplates(stext);
          }
          if (action.TryGetProperty("endedDisplayText", out var edt) && edt.ValueKind == JsonValueKind.True &&
            action.TryGetProperty("endedText", out var etdt) && etdt.GetString() is { Length: > 0 } edtext)
          {
            endTextToDisplay = ConvertTemplates(edtext);
          }
          if (action.TryGetProperty("endedSpeak", out var esk) && esk.ValueKind == JsonValueKind.True &&
            action.TryGetProperty("endedSpeakPhrase", out var espk) && espk.GetString() is { Length: > 0 } estext)
          {
            endTextToSpeak = ConvertTemplates(estext);
          }
          if (action.TryGetProperty("duration", out var tdur))
          {
            if (tdur.ValueKind == JsonValueKind.Null)
            {
              // NAG null duration = indefinite timer ended by endEarlyPhrases.
              // EQLP requires a fixed DurationSeconds; default to 60s and rely on
              // EndEarlyPattern(s) to stop the timer when the spell fades.
              durationSeconds = 60.0;
              droppedFeatures.Add("indefinite timer duration (defaulted to 60s)");
            }
            else if (tdur.ValueKind is JsonValueKind.Number or JsonValueKind.String)
            {
              durationSeconds = tdur.GetDouble();
            }
          }
          if (action.TryGetProperty("restartBehavior", out var rb) && rb.ValueKind is JsonValueKind.Number or JsonValueKind.String)
          {
            triggerAgainOption = rb.GetInt32();
          }
          if (action.TryGetProperty("useCustomColor", out var ucc) && ucc.GetBoolean())
          {
            if (action.TryGetProperty("overrideTimerColor", out var otc) && otc.GetString() is { Length: > 0 } color)
            {
              activeColor = ConvertColor(color);
            }
          }
          // Collect overlayId
          if (action.TryGetProperty("overlayId", out var ov) && ov.GetString() is { Length: > 0 } overlayId)
          {
            if (!selectedOverlays.Contains(overlayId))
              selectedOverlays.Add(overlayId);
          }
          actionSummary.Add(actionType == 4 ? "Looping Timer" : "Timer");
          break;

        case 6: // Timer with Remain (remain-after-ended)
          hasAction = true;
          timerType = 1;
          if (action.TryGetProperty("displayText", out var td6) && td6.GetString() is { Length: > 0 } timerText6)
          {
            textToDisplay = ConvertTemplates(timerText6);
          }
          // Collect action-level endEarlyPhrases
          if (action.TryGetProperty("endEarlyPhrases", out var aeep6) && aeep6.ValueKind == JsonValueKind.Array)
          {
            foreach (var ee in aeep6.EnumerateArray())
            {
              if (ee.TryGetProperty("phrase", out var ep6) && ep6.GetString() is { Length: > 0 } phrase6)
              {
                actionEndEarlyPhrases.Add(ConvertTemplates(phrase6));
                actionEndEarlyUseRegex.Add(ee.TryGetProperty("useRegEx", out var useRe6) && useRe6.GetBoolean());
              }
            }
          }
          // Map ending/ended sub-action text to EQLP warning/end fields
          if (action.TryGetProperty("endingSoonDisplayText", out var esdt6) && esdt6.ValueKind == JsonValueKind.True &&
            action.TryGetProperty("endingSoonText", out var est6) && est6.GetString() is { Length: > 0 } etext6)
          {
            warningTextToDisplay = ConvertTemplates(etext6);
          }
          if (action.TryGetProperty("endingSoonSpeak", out var ess6) && ess6.ValueKind == JsonValueKind.True &&
            action.TryGetProperty("endingSoonSpeakPhrase", out var esp6) && esp6.GetString() is { Length: > 0 } stext6)
          {
            warningTextToSpeak = ConvertTemplates(stext6);
          }
          if (action.TryGetProperty("endedDisplayText", out var edt6) && edt6.ValueKind == JsonValueKind.True &&
            action.TryGetProperty("endedText", out var etdt6) && etdt6.GetString() is { Length: > 0 } edtext6)
          {
            endTextToDisplay = ConvertTemplates(edtext6);
          }
          if (action.TryGetProperty("endedSpeak", out var esk6) && esk6.ValueKind == JsonValueKind.True &&
            action.TryGetProperty("endedSpeakPhrase", out var espk6) && espk6.GetString() is { Length: > 0 } estext6)
          {
            endTextToSpeak = ConvertTemplates(estext6);
          }
          if (action.TryGetProperty("duration", out var tdur6) && tdur6.ValueKind is JsonValueKind.Number or JsonValueKind.String)
          {
            durationSeconds = tdur6.GetDouble();
          }
          if (action.TryGetProperty("overlayId", out var ov6) && ov6.GetString() is { Length: > 0 } overlayId6)
          {
            if (!selectedOverlays.Contains(overlayId6))
              selectedOverlays.Add(overlayId6);
          }
          droppedFeatures.Add("remain-after-ended timer");
          actionSummary.Add("Timer (partial)");
          break;

        case 9: // Clipboard/Chat Command
          hasAction = true;
          if (action.TryGetProperty("displayText", out var cb) && cb.GetString() is { Length: > 0 } clip)
          {
            textToShare = ConvertTemplates(clip);
          }
          // Collect overlayId for clipboard overlay routing
          if (action.TryGetProperty("overlayId", out var ov9) && ov9.GetString() is { Length: > 0 } overlayId9)
          {
            if (!selectedOverlays.Contains(overlayId9))
              selectedOverlays.Add(overlayId9);
          }
          actionSummary.Add("Clipboard");
          break;

        case 10: // Buff Timer with Cast Time
          hasAction = true;
          timerType = 1;
          if (action.TryGetProperty("displayText", out var td10) && td10.GetString() is { Length: > 0 } timerText10)
          {
            textToDisplay = ConvertTemplates(timerText10);
          }
          // Collect action-level endEarlyPhrases
          if (action.TryGetProperty("endEarlyPhrases", out var aeep10) && aeep10.ValueKind == JsonValueKind.Array)
          {
            foreach (var ee in aeep10.EnumerateArray())
            {
              if (ee.TryGetProperty("phrase", out var ep10) && ep10.GetString() is { Length: > 0 } phrase10)
              {
                actionEndEarlyPhrases.Add(ConvertTemplates(phrase10));
                actionEndEarlyUseRegex.Add(ee.TryGetProperty("useRegEx", out var useRe10) && useRe10.GetBoolean());
              }
            }
          }
          // Map ending/ended sub-action text to EQLP warning/end fields
          if (action.TryGetProperty("endingSoonDisplayText", out var esdt10) && esdt10.ValueKind == JsonValueKind.True &&
            action.TryGetProperty("endingSoonText", out var est10) && est10.GetString() is { Length: > 0 } etext10)
          {
            warningTextToDisplay = ConvertTemplates(etext10);
          }
          if (action.TryGetProperty("endingSoonSpeak", out var ess10) && ess10.ValueKind == JsonValueKind.True &&
            action.TryGetProperty("endingSoonSpeakPhrase", out var esp10) && esp10.GetString() is { Length: > 0 } stext10)
          {
            warningTextToSpeak = ConvertTemplates(stext10);
          }
          if (action.TryGetProperty("endedDisplayText", out var edt10) && edt10.ValueKind == JsonValueKind.True &&
            action.TryGetProperty("endedText", out var etdt10) && etdt10.GetString() is { Length: > 0 } edtext10)
          {
            endTextToDisplay = ConvertTemplates(edtext10);
          }
          if (action.TryGetProperty("endedSpeak", out var esk10) && esk10.ValueKind == JsonValueKind.True &&
            action.TryGetProperty("endedSpeakPhrase", out var espk10) && espk10.GetString() is { Length: > 0 } estext10)
          {
            endTextToSpeak = ConvertTemplates(estext10);
          }
          if (action.TryGetProperty("duration", out var tdur10) && tdur10.ValueKind is JsonValueKind.Number or JsonValueKind.String)
          {
            durationSeconds = tdur10.GetDouble();
          }
          if (action.TryGetProperty("overlayId", out var ov10) && ov10.GetString() is { Length: > 0 } overlayId10)
          {
            if (!selectedOverlays.Contains(overlayId10))
              selectedOverlays.Add(overlayId10);
          }
          droppedFeatures.Add("cast time tracking");
          actionSummary.Add("Timer (partial)");
          break;

        case 7: // Clear Variable — map to EndTimerClearVariables
          hasAction = true;
          if (action.TryGetProperty("variableName", out var vn) && vn.GetString() is { Length: > 0 } varName)
          {
            clearVariables.Add(ConvertTemplates(varName));
          }
          actionSummary.Add("clear variable");
          break;

        case 12: // Screen Flash - unsupported, skip
          droppedFeatures.Add("screen flash");
          Log.Debug($"Skipping unsupported action type 12 (Screen Flash) in trigger");
          break;

        default:
          // Types 5,7,8,11,13,14,15 - variables, counters, buffs, lists - unsupported
          var skipNames = actionType switch
          {
            5 => "set variable",
            8 => "counter",
            11 => "hotkey",
            13 => "global reset",
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
      return (null, droppedFeatures, "No supported actions", null, ([], []), missingAudioFiles);
    }

    // Build the Trigger object with all parsed data
    return (new Trigger
    {
      Pattern = "", // Overwritten by ParseTrigger after this method returns
      UseRegex = useRegEx,
      TextToDisplay = textToDisplay,
      TextToSpeak = textToSpeak,
      SoundToPlay = soundToPlay,
      TextToShare = textToShare,
      EnableTimer = durationSeconds > 0,
      DurationSeconds = durationSeconds,
      TimerType = timerType,
      TriggerAgainOption = triggerAgainOption >= 0 ? triggerAgainOption : 0,
      WarningSeconds = warningSeconds,
      ActiveColor = activeColor,
      SelectedOverlays = selectedOverlays.Count > 0 ? selectedOverlays : [],
      WarningTextToDisplay = warningTextToDisplay,
      WarningTextToSpeak = warningTextToSpeak,
      EndTextToDisplay = endTextToDisplay,
      EndTextToSpeak = endTextToSpeak,
      EndTimerClearVariables = clearVariables.Count > 0 ? string.Join(", ", clearVariables) : "",
    }, droppedFeatures, null, string.Join(", ", actionSummary), (actionEndEarlyPhrases, actionEndEarlyUseRegex), missingAudioFiles);
  }

  internal static void WriteImportReportHtml(List<NagImportResult> results, string outputPath)
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
      var imported = results.Count(r => r.Status == "Imported");
      var partial = results.Count(r => r.Status == "Partial");
      var skipped = results.Count(r => r.Status == "Skipped");

      sb.AppendLine($"<h1>NAG Import Report</h1>");
      sb.AppendLine($"<div class=\"summary\">\n<div class=\"stat imported\"><span class=\"num\">{imported}</span><span class=\"label\">Success</span></div>\n<div class=\"stat partial\"><span class=\"num\">{partial}</span><span class=\"label\">Partial</span></div>\n<div class=\"stat skipped\"><span class=\"num\">{skipped}</span><span class=\"label\">Skipped</span></div>\n<div class=\"stat\"><span class=\"num\">{total}</span><span class=\"label\">Total</span></div>\n</div>");

      sb.AppendLine("<table>\n<thead>\n<tr><th>Trigger</th><th>Status</th><th class=\"folder-col\">Folder Path</th><th class=\"actions-col\">Actions</th><th class=\"reason-col\">Details / Reason</th><th>Missing Audio</th></tr>\n</thead>\n<tbody>");

      // Sort: Skipped first, then Partial, then Success (Imported)
      var sorted = results.OrderBy(r => r.Status switch
      {
        "Skipped" => 0,
        "Partial" => 1,
        "Imported" => 2,
        _ => 3
      }).ToList();

      foreach (var r in sorted)
      {
        var badgeClass = r.Status switch
        {
          "Imported" => "badge-imported",
          "Partial" => "badge-partial",
          "Skipped" => "badge-skipped",
          _ => ""
        };
        // Display "Success" instead of "Imported" in the report
        var displayStatus = r.Status == "Imported" ? "Success" : r.Status;
        var badge = $"<span class=\"badge {badgeClass}\">{displayStatus}</span>";
        var folder = string.IsNullOrEmpty(r.FolderPath) || r.FolderPath == "(root)"
          ? "<em>(root)</em>" : $"<span class=\"folder\">{HtmlEncode(r.FolderPath)}</span>";
        var actions = HtmlEncode(r.ActionsSummary ?? "");
        var reason = string.IsNullOrEmpty(r.Reason) ? "—" : HtmlEncode(r.Reason);
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

  private static string HtmlEncode(string value)
  {
    return string.IsNullOrEmpty(value) ? "" : System.Net.WebUtility.HtmlEncode(value);
  }

  internal static List<ExportTriggerNode> ConvertOverlays(string json)
  {
    var result = new List<ExportTriggerNode>();

    try
    {
      using var doc = JsonDocument.Parse(json);
      var overlays = doc.RootElement.GetProperty("overlays");

      foreach (var overlay in overlays.EnumerateArray())
      {
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

    // Skip FCT (Fight Combat Tracker) overlays — no EQLP equivalent
    if (overlayType?.Equals("FCT", StringComparison.OrdinalIgnoreCase) == true)
    {
      return null;
    }

    var fontColor = element.TryGetProperty("fontColor", out var fc) ? fc.GetString() : "#ffffff";
    var backgroundColor = element.TryGetProperty("backgroundColor", out var bc) ? bc.GetString() : "#000000";
    var backgroundTransparency = element.TryGetProperty("backgroundTransparency", out var bt) ? bt.GetDouble() : 0;
    var timerColor = element.TryGetProperty("timerColor", out var tc) ? tc.GetString() : "#008000";
    var timerBackgroundColor = element.TryGetProperty("timerBackgroundColor", out var tbc) ? tbc.GetString() : null;
    var fontFamily = element.TryGetProperty("fontFamily", out var ff) ? ff.GetString() : "Segoe UI";
    var fontSize = element.TryGetProperty("fontSize", out var fs) ? fs.GetInt32() : 12;
    var fontWeight = element.TryGetProperty("fontWeight", out var fw) ? fw.GetInt32() : 400;
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

    // Map timerSortType → SortBy (0=none, 1=alphabetical, 2=time remaining)
    var sortBy = element.TryGetProperty("timerSortType", out var st) ? st.GetInt32() : 0;

    // Map showTextGlow → UseTextDropShadow (default true for backward compat)
    var useTextDropShadow = element.TryGetProperty("showTextGlow", out var stg) ? stg.GetBoolean() : true;

    return new ExportTriggerNode
    {
      Id = overlayId,
      Name = name,
      OverlayData = new Overlay
      {
        Source = $"nag:{overlayId}",
        Width = element.TryGetProperty("windowWidth", out var ww) ? ww.GetInt64() : 300,
        Height = element.TryGetProperty("windowHeight", out var wh) ? wh.GetInt64() : 400,
        Left = element.TryGetProperty("x", out var x) ? x.GetInt64() : 100,
        Top = element.TryGetProperty("y", out var y) ? y.GetInt64() : 200,
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
