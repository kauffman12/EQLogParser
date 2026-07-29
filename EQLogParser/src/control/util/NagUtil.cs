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
}

internal static class NagUtil
{
  private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

  // Regex to find named capture groups: (?<name>...)
  private static readonly Regex NamedGroupPattern = new(@"\(\?<([a-zA-Z][^>]*?)>", RegexOptions.Compiled);

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

    // Already 8-char ARGB
    if (color.Length == 9 && color[1] != ',')
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
  private static readonly Regex DollarVarRegex = new(@"\$\{(\w+)\}", RegexOptions.Compiled);

  // NAG {VAR} in regex phrases that EQLP does NOT handle at runtime.
  // EQLP handles {S}/{s}, {N}/{n}, {TS}/{ts} via CheckOptions(). Everything else
  // (e.g., {C}, {c}, {LN}, {target}) must be replaced with a capture group to avoid regex errors.
  // Simple alphanumeric names become named groups (?<VAR>.+?) so they can be referenced in display text.
  // Names with spaces/special chars fall back to anonymous (.+?).
  // Also excludes (?<name>...) named groups and ${...} (already handled above).
  private static readonly Regex UnhandledVarRegex = new(
    @"(?<!\$)\{(?!S\d?|s\d?|N\d?|n\d?|TS|ts)[a-zA-Z][^}]*\}",
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

        case 1: // Contains check with pipe-separated values: {var} contains "val1|val2"
          {
            var value = cond.TryGetProperty("variableValue", out var vv) ? vv.GetString() : null;
            if (!string.IsNullOrEmpty(value))
            {
              parts.Add($"{{{varName}}} contains \"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
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

  // Combine multiple capture phrases into a single regex pattern using alternation
  private static string CombinePhrases(List<string> phrases, bool anyUseRegex)
  {
    if (phrases.Count == 1) return phrases[0];

    var escaped = new List<string>();
    foreach (var phrase in phrases)
    {
      if (anyUseRegex)
      {
        escaped.Add(phrase);
      }
      else
      {
        // Escape regex special chars for non-regex phrases
        escaped.Add(Regex.Escape(phrase));
      }
    }

    // Collect all named capture groups and rename conflicts
    var groupNames = new List<string>();
    foreach (var phrase in phrases)
    {
      foreach (Match m in NamedGroupPattern.Matches(phrase))
      {
        groupNames.Add(m.Groups[1].Value);
      }
    }

    // Check for duplicate group names and rename if needed
    var uniqueGroups = new HashSet<string>(groupNames, StringComparer.OrdinalIgnoreCase);
    if (uniqueGroups.Count < groupNames.Count)
    {
      var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      var renamed = new List<string>();
      foreach (var phrase in escaped)
      {
        var result = phrase;
        foreach (Match m in NamedGroupPattern.Matches(phrase))
        {
          var name = m.Groups[1].Value;
          if (!seen.TryGetValue(name, out var count))
          {
            seen[name] = 0;
          }

          seen[name]++;
          var newName = seen[name] > 1 ? $"{name}{seen[name]}" : name;
          result = result.Replace($"(?<{name}", $"(?<{newName}");
        }
        renamed.Add(result);
      }

      return "(?:" + string.Join("|", renamed) + ")";
    }

    return "(?:" + string.Join("|", escaped) + ")";
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
        var filesElem = doc.RootElement.GetProperty("files");
        foreach (var file in filesElem.EnumerateArray())
        {
          if (file.TryGetProperty("fileId", out var id) && file.TryGetProperty("fileName", out var name))
          {
            var fileId = id.GetString();
            var fileName = name.GetString();
            if (!string.IsNullOrEmpty(fileId) && !string.IsNullOrEmpty(fileName))
            {
              _audioFileMap[fileId] = fileName;
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
            DroppedFeatures = parsed.result.DroppedFeatures
          };
        }
      }
    }
    catch (Exception ex)
    {
      Log.Error("Error Parsing NAG Triggers", ex);
    }

    return (nodes, results, metadata);
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

    // Collect all phrases and check for regex usage
    var phrases = new List<string>();
    var anyUseRegex = false;
    foreach (var phrase in capturePhrases.EnumerateArray())
    {
      if (phrase.TryGetProperty("phrase", out var p) && p.GetString() is { Length: > 0 } text)
      {
        var useRegex = phrase.TryGetProperty("useRegEx", out var re) && re.GetBoolean();
        if (useRegex) anyUseRegex = true;

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
        // 4. {C}, {c}, {LN}, {target} and other unhandled vars — not supported by EQLP.
        //    Replace with named capture groups (?<VAR>.+?) so the captured value can be
        //    referenced in display text (e.g., {target} → {$target}). Simple alphanumeric
        //    names become named groups; complex names fall back to anonymous (.+?).
        //
        // For non-regex phrases containing NAG variable references ({VAR}), we must
        // also enable regex mode because these variables require capture groups to work.
        // 49 single-phrase triggers use {C} in non-regex phrases — without regex mode,
        // the literal text "{C}" would never match log lines. Note: {C}/{c} refers to
        // the player character name, same concept as EQLP's {c} (CharacterCode).
        var hasNagVars = Regex.IsMatch(text, @"(?<!\$)\{[A-Za-z]");
        if (useRegex || hasNagVars)
        {
          anyUseRegex = true;

          // Replace ${varName} with (?<varName>.+?) so it can be referenced in display text
          text = DollarVarRegex.Replace(text, m => $"(?<{m.Groups[1].Value}>.+?)");

          // Replace unhandled {VAR} patterns (not S/s/N/n/TS/ts and not named groups)
          // Simple names become named groups (?<VAR>.+?); complex names fall back to (.+?)
          text = UnhandledVarRegex.Replace(text, m =>
          {
            var varName = m.Groups[0].Value.Trim('{', '}');
            return char.IsLetterOrDigit(varName[0]) && varName.All(c => char.IsLetterOrDigit(c) || c == '_')
              ? $"(?<{varName}>.+?)"
              : "(.+?)";
          });

          // Do NOT call ConvertTemplates — leave {S}, {N}, {TS} as-is for EQLP runtime
          phrases.Add(text);
        }
        else
        {
          phrases.Add(ConvertTemplates(text));
        }
      }
      else if (phrase.TryGetProperty("useRegEx", out var re) && re.GetBoolean())
      {
        anyUseRegex = true;
      }
    }

    if (phrases.Count == 0)
    {
      return ([], new NagImportResult { TriggerName = name, TriggerId = triggerId, Status = "Skipped", Reason = "No valid phrases" });
    }

    // Build pattern: single phrase or combined via alternation
    var pattern = phrases.Count == 1 ? phrases[0] : CombinePhrases(phrases, anyUseRegex);
    var useRegEx = phrases.Count > 1 || anyUseRegex;

    // Parse actions and build Trigger data
    var parsed = ParseActions(element.GetProperty("actions"), score, useRegEx, useCooldown, cooldownDuration, databaseDirectory);
    if (parsed.triggerData is null)
    {
      return ([], new NagImportResult { TriggerName = name, TriggerId = triggerId, Status = "Skipped", Reason = parsed.reason ?? "No supported actions" });
    }

    var triggerData = parsed.triggerData;
    var droppedFeatures = parsed.droppedFeatures;

    // Add class level filtering as a dropped feature (no EQLP equivalent)
    if (hasClassLevels)
    {
      droppedFeatures.Add("class level filtering");
    }

    // Assign the computed pattern (was previously lost — ParseActions set Pattern = "")
    triggerData.Pattern = pattern;

    // Parse conditions into MatchVariableCondition
    if (element.TryGetProperty("conditions", out var conds))
    {
      var conditionStr = ParseConditions(conds);
      if (!string.IsNullOrEmpty(conditionStr))
      {
        triggerData.MatchVariableCondition = conditionStr;
      }
    }

    // Build import notes for Comments field (no longer embeds NAG ID — that's in OriginalId)
    var commentParts = new List<string>();
    if (!string.IsNullOrEmpty(comments))
      commentParts.Add($"Original: {comments}");
    if (droppedFeatures.Count > 0)
      commentParts.Add($"Dropped: {string.Join(", ", droppedFeatures)}");
    triggerData.Comments = commentParts.Count > 0 ? string.Join("\n", commentParts) : null;
    triggerData.Priority = ConvertScore(score);
    triggerData.LockoutTime = useCooldown ? cooldownDuration : 0;

    var node = new ExportTriggerNode
    {
      Id = Guid.NewGuid().ToString(),
      Name = name,
      OriginalId = triggerId,
      TriggerData = triggerData
    };

    // Parse end-early phrases — merge trigger-level and action-level (max 3 slots)
    var allEndEarlyPhrases = new List<string>();
    if (element.TryGetProperty("endEarlyPhrases", out var eep) && eep.ValueKind == JsonValueKind.Array)
    {
      foreach (var ee in eep.EnumerateArray())
      {
        if (ee.TryGetProperty("phrase", out var ep) && ep.GetString() is { Length: > 0 } phrase)
        {
          allEndEarlyPhrases.Add(ConvertTemplates(phrase));
        }
        if (allEndEarlyPhrases.Count >= 3) break;
      }
    }
    // Merge action-level end-early phrases (537 timer actions have these in real data)
    foreach (var aep in parsed.actionEndEarlyPhrases)
    {
      if (!allEndEarlyPhrases.Contains(aep))
        allEndEarlyPhrases.Add(aep);
      if (allEndEarlyPhrases.Count >= 3) break;
    }

    // Apply end-early patterns to the trigger
    if (allEndEarlyPhrases.Count > 0)
    {
      node.TriggerData.EndEarlyPattern = allEndEarlyPhrases[0];
      node.TriggerData.EndUseRegex = false;
    }
    if (allEndEarlyPhrases.Count > 1)
    {
      node.TriggerData.EndEarlyPattern2 = allEndEarlyPhrases[1];
      node.TriggerData.EndUseRegex2 = false;
    }
    if (allEndEarlyPhrases.Count > 2)
    {
      node.TriggerData.EndEarlyPattern3 = allEndEarlyPhrases[2];
      node.TriggerData.EndUseRegex3 = false;
    }

    // Determine import status and reason
    var status = droppedFeatures.Count > 0 ? "Partial" : "Imported";
    var reason = isSequential ? "Sequential capture method (not supported)" :
                 hasClassLevels ? "Class level filtering (not supported)" :
                 droppedFeatures.Count > 0 ? string.Join(", ", droppedFeatures) :
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
        ActionsSummary = parsed.actionSummary
      });
    }

    // Build actions summary
    var actionSummary = parsed.actionSummary;

    return ([node], new NagImportResult
    {
      TriggerName = name,
      TriggerId = triggerId,
      Status = status,
      Reason = reason,
      ActionsSummary = actionSummary,
      Score = score,
      DroppedFeatures = droppedFeatures
    });
  }

  private static (Trigger triggerData, List<string> droppedFeatures, string reason, string actionSummary, List<string> actionEndEarlyPhrases) ParseActions(
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
    var hasAction = false;
    var droppedFeatures = new List<string>();
    var actionSummary = new List<string>();

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
              }
            }
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

        case 12: // Screen Flash - unsupported, skip
          droppedFeatures.Add("screen flash");
          Log.Debug($"Skipping unsupported action type 12 (Screen Flash) in trigger");
          break;

        default:
          // Types 5,7,8,11,13,14,15 - variables, counters, buffs, lists - unsupported
          var skipNames = actionType switch
          {
            5 => "set variable",
            7 => "clear variable",
            8 => "counter",
            11 => "hotkey",
            13 => "global reset",
            15 => "list widget",
            _ => $"action type {actionType}"
          };
          droppedFeatures.Add(skipNames);
          Log.Debug($"Skipping unsupported action type {actionType} in trigger");
          break;
      }
    }

    if (!hasAction)
    {
      return (null, droppedFeatures, "No supported actions", null, []);
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
    }, droppedFeatures, null, string.Join(", ", actionSummary), actionEndEarlyPhrases);
  }

  internal static void WriteImportReport(List<NagImportResult> results, string outputPath)
  {
    try
    {
      var sb = new StringBuilder();
      sb.AppendLine("TriggerName,TriggerId,Status,Reason,FolderPath,ActionsSummary");
      foreach (var r in results)
      {
        // Escape CSV fields that contain commas or quotes
        var name = EscapeCsv(r.TriggerName);
        var id = EscapeCsv(r.TriggerId);
        var status = EscapeCsv(r.Status);
        var reason = EscapeCsv(r.Reason);
        var folder = EscapeCsv(r.FolderPath);
        var summary = EscapeCsv(r.ActionsSummary);
        sb.AppendLine($"{name},{id},{status},{reason},{folder},{summary}");
      }
      File.WriteAllText(outputPath, sb.ToString());
    }
    catch (Exception ex)
    {
      Log.Error("Error writing import report", ex);
    }
  }

  private static string EscapeCsv(string value)
  {
    if (string.IsNullOrEmpty(value)) return "";
    if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
    {
      return $"\"{value.Replace("\"", "\"\"")}\"";
    }
    return value;
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
    var fontFamily = element.TryGetProperty("fontFamily", out var ff) ? ff.GetString() : "Segoe UI";
    var fontSize = element.TryGetProperty("fontSize", out var fs) ? fs.GetInt32() : 12;
    var fontWeight = element.TryGetProperty("fontWeight", out var fw) ? fw.GetInt32() : 400;
    var horizontalAlignment = element.TryGetProperty("horizontalAlignment", out var ha) ? ha.GetString() : "center";
    var verticalAlignment = element.TryGetProperty("verticalAlignment", out var va) ? va.GetString() : "bottom";

    // Parse textOverflow for NoTextWrap support (NAG whiteSpace=nowrap → EQLP NoTextWrap=true)
    var noTextWrap = false;
    if (element.TryGetProperty("textOverflow", out var to) && to.ValueKind == JsonValueKind.Object &&
        to.TryGetProperty("whiteSpace", out var ws) && ws.GetString() is { } whiteSpace &&
        whiteSpace.Equals("nowrap", StringComparison.OrdinalIgnoreCase))
    {
      noTextWrap = true;
    }

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
        IdleColor = "#FF8f1515",
        ResetColor = "#FF8f1515",
        HorizontalAlignment = ConvertHorizontalAlignment(horizontalAlignment),
        VerticalAlignment = ConvertVerticalAlignment(verticalAlignment),
        IsTimerOverlay = isTimerOverlay,
        IsTextOverlay = isTextOverlay,
        NoTextWrap = noTextWrap,
        ShowActive = true,
        ShowIdle = true,
        ShowReset = true,
        UseTextDropShadow = true,
        FadeDelay = 10
      }
    };
  }
}
