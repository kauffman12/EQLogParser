using DotLiquid;
using log4net;
using Microsoft.VisualBasic.FileIO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace EQLogParser
{
  internal static class TextUtils
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private const string BbCellHeader = "  [td]{0}    [/td]";
    private const string BbCellBody = "[td][right]{0}   [/right][/td]";
    private const string BbCellFirst = "[td]{0}[/td]";
    private const string BbRowStart = "[tr]";
    private const string BbRowEnd = "[/tr]\r\n";
    private const string BbTableStart = "[table]\r\n";
    private const string BbTableEnd = "[/table]\r\n";
    private const string BbTitle = "[b]{0}[/b]\r\n";

    private const string BbGamparseSpellCount = "   --- {0} - {1}";

    private static readonly Dictionary<int, string> Roman = new()
    {
      { 400, "CD" }, { 100, "C" }, { 90, "XC" }, { 50, "L" }, { 40, "XL" }, { 10, "X" }, { 9, "IX" }, { 5, "V" }, { 4, "IV" }, { 1, "I" }
    };

    internal static bool SCompare(string s, int start, int count, string test) => s.AsSpan(start, count).Equals(test, StringComparison.OrdinalIgnoreCase);
    internal static string ParseSpellOrNpc(string[] split, int index) => string.Join(" ", split, index, split.Length - index).Trim('.');
    internal static string ToLower(string name) => string.IsNullOrEmpty(name) ? "" : name.ToLower(CultureInfo.InvariantCulture);

    internal static string ToUpper(string name, CultureInfo culture = null)
    {
      if (string.IsNullOrEmpty(name))
        return name;

      culture ??= CultureInfo.InvariantCulture;

      var chars = new char[name.Length];
      chars[0] = char.ToUpper(name[0], culture);
      for (var i = 1; i < name.Length; i++)
      {
        chars[i] = name[i];
      }
      return new string(chars);
    }

    internal static string BuildTsv(List<string> header, List<List<object>> data, string title = null)
    {
      var sb = new StringBuilder();

      if (title != null)
      {
        sb.Append('"').Append(EscapeTsv(title)).Append('"').AppendLine();
      }

      sb.AppendLine(string.Join('\t', header.Select(item => $"\"{EscapeTsv(item)}\"")));

      foreach (var row in data)
      {
        sb.AppendLine(string.Join('\t', row.Select(FormatTsvCell)));
      }

      return sb.ToString();
    }

    internal static List<string[]> ReadTsv(string data)
    {
      var rows = new List<string[]>();

      if (string.IsNullOrEmpty(data))
      {
        return rows;
      }

      using var reader = new StringReader(data);
      using var parser = new TextFieldParser(reader)
      {
        HasFieldsEnclosedInQuotes = true,
        TrimWhiteSpace = false
      };

      parser.SetDelimiters("\t");

      try
      {
        while (!parser.EndOfData)
        {
          rows.Add(parser.ReadFields() ?? []);
        }
      }
      catch (MalformedLineException ex)
      {
        Log.Error($"Invalid TSV data at line {parser.ErrorLineNumber}: {parser.ErrorLine}", ex);
      }

      return rows;
    }

    internal static string BuildBbCodeTable(List<string> header, List<List<object>> data, string title = null)
    {
      var sb = new StringBuilder();

      if (title != null)
      {
        sb.AppendFormat(CultureInfo.CurrentCulture, BbTitle, title);
      }

      // start table
      sb.Append(BbTableStart);

      // header
      sb.Append(BbRowStart);
      header.ForEach(item => sb.AppendFormat(CultureInfo.CurrentCulture, BbCellHeader, item));
      sb.Append(BbRowEnd);

      // data
      data.ForEach(row =>
      {
        sb.Append(BbRowStart);
        var first = row.FirstOrDefault();
        if (first != null)
        {
          sb.AppendFormat(CultureInfo.CurrentCulture, BbCellFirst, first);
        }

        row.Skip(1).ToList().ForEach(item => sb.AppendFormat(CultureInfo.CurrentCulture, BbCellBody, item));
        sb.Append(BbRowEnd);
      });

      // end table
      sb.Append(BbTableEnd);
      return sb.ToString();
    }

    internal static string BuildGamparseList(List<string> header, List<List<object>> data, string title = null)
    {
      var sb = new StringBuilder();

      if (title != null)
      {
        sb.AppendFormat(CultureInfo.CurrentCulture, BbTitle, title);
        sb.AppendLine();
      }

      // for each header minus the Totals
      var sorted = new SortedList<string, int>();
      for (var i = 1; i < header.Count - 1; i++)
      {
        sorted.Add(header[i], i);
      }

      foreach (var pair in sorted)
      {
        var updatedHeader = header[pair.Value].Replace("=", "-");
        sb.AppendFormat(CultureInfo.CurrentCulture, BbTitle, updatedHeader);

        data.OrderBy(row => row[0]).ToList().ForEach(row =>
        {
          if (row[pair.Value] != null && row[pair.Value].ToString()!.Length > 0 && row[pair.Value].ToString() != "0")
          {
            sb.AppendFormat(CultureInfo.CurrentCulture, BbGamparseSpellCount, row[0], row[pair.Value]);
            sb.AppendLine();
          }
        });
      }

      return sb.ToString();
    }

    internal static void SaveHtml(string selectedFileName, Dictionary<string, SummaryTable> tables)
    {
      var headerTemplate = Template.Parse(File.ReadAllText(@"data\html\header.html"));
      var tableKeys = tables.Keys.OrderBy(key => key);
      var tablechoices = new List<object>();

      foreach (var key in tableKeys)
      {
        tablechoices.Add(new { type = key, title = tables[key].GetTitle() });
      }

      var headerValue = headerTemplate.Render(Hash.FromAnonymousObject(new { tablechoices }));
      File.WriteAllText(selectedFileName, headerValue);

      var contentTemplate = Template.Parse(File.ReadAllText(@"data\html\content.html"));
      foreach (var key in tableKeys)
      {
        var headers = tables[key].GetHeaders();
        var playerStats = tables[key].GetPlayerStats();
        var isPetsCombined = tables[key].IsPetsCombined();

        var columns = headers.Select(header => header[1]).ToList();
        var rows = new List<object>();
        foreach (var stats in playerStats)
        {
          var data = new List<object>();
          foreach (var column in headers.Skip(1))
          {
            var value = stats.GetType().GetProperty(column[0])?.GetValue(stats, null);
            if (column[1].Contains('%'))
            {
              if (value is double and 0)
              {
                value = "-";
              }
            }
            else if (value is not string)
            {
              value = $"{value:n0}";
            }

            data.Add(value);
          }

          var isChild = stats.IsTopLevel == false && isPetsCombined;
          var row = new
          {
            rank = isChild ? "" : stats.Rank.ToString(),
            ischild = isChild,
            haschild = stats.Name.Contains(" +Pets"),
            data
          };

          rows.Add(row);
        }

        var content = contentTemplate.Render(Hash.FromAnonymousObject(new { columns, rows, tableid = key }));
        File.AppendAllText(selectedFileName, content);
      }

      var footer = File.ReadAllText(@"data\html\footer.html");
      File.AppendAllText(selectedFileName, footer);
    }

    internal static string Trim(string value)
    {
      if (value != null)
      {
        value = value.Trim();
        if (value.Length == 0)
        {
          value = null;
        }
      }

      return value;
    }

    internal static string IntToRoman(int value)
    {
      var roman = new StringBuilder();

      foreach (var item in Roman)
      {
        while (value >= item.Key)
        {
          roman.Append(item.Value);
          value -= item.Key;
        }
      }

      return roman.ToString();
    }

    internal static string GetSearchableTextFromStart(string input, int startIndex)
    {
      var span = input.AsSpan(startIndex);
      var validLength = 0;

      foreach (var t in span)
      {
        if (!char.IsLetter(t) && !char.IsDigit(t) && t != ' ')
        {
          break;
        }
        validLength++;
      }

      return validLength > 0 ? span[..validLength].ToString() : string.Empty;
    }

    [DebuggerHidden]
    internal static bool IsValidRegex(string pattern)
    {
      var pass = true;

      if (!string.IsNullOrEmpty(pattern))
      {
        try
        {
          new Regex(pattern);
        }
        catch (Exception)
        {
          pass = false;
        }
      }

      return pass;
    }

    internal static bool SnapshotMatches(MatchCollection mc, out Dictionary<string, string> matches)
    {
      matches = null;

      if (mc == null)
      {
        return false;
      }

      var success = mc.Count > 0;
      matches = success ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : null;

      foreach (Match m in mc)
      {
        if (m.Success)
        {
          for (var i = 1; i < m.Groups.Count; i++)
          {
            matches[m.Groups[i].Name] = m.Groups[i].Value;
          }
        }
      }

      return success;
    }

    internal static uint ParseUInt(string str, uint defValue = uint.MaxValue) => ParseUInt(str.AsSpan(), defValue);

    internal static uint ParseUInt(ReadOnlySpan<char> span, uint defValue = uint.MaxValue)
    {
      if (span.IsEmpty)
        return defValue;

      uint value = 0;

      foreach (var c in span)
      {
        var digit = (uint)(c - '0');

        if (digit > 9)
          return defValue;

        if (value > 429496729u || (value == 429496729u && digit > 5))
          return defValue;

        value = (value * 10) + digit;
      }

      return value;
    }

    internal static string ReplaceWholeWords(string input, IReadOnlyDictionary<string, string> replacements)
    {
      if (string.IsNullOrEmpty(input) || replacements == null || replacements.Count == 0)
      {
        return input;
      }

      var sb = new StringBuilder(input.Length);
      var word = new StringBuilder();

      foreach (var c in input)
      {
        if (char.IsLetterOrDigit(c))
        {
          // Build up a word
          word.Append(c);
        }
        else
        {
          // Non-word boundary reached → flush word if any
          if (word.Length > 0)
          {
            var w = word.ToString();
            if (replacements.TryGetValue(w, out var replacement))
              sb.Append(replacement);
            else
              sb.Append(w);

            word.Clear();
          }

          // Keep punctuation/whitespace as-is
          sb.Append(c);
        }
      }

      // Flush last word (if string ended with one)
      if (word.Length > 0)
      {
        var w = word.ToString();
        if (replacements.TryGetValue(w, out var replacement))
          sb.Append(replacement);
        else
          sb.Append(w);
      }

      return sb.ToString();
    }

    private static string FormatTsvCell(object item)
    {
      return item is string s
        ? $"\"{EscapeTsv(s)}\""
        : Convert.ToString(item, CultureInfo.CurrentCulture) ?? "";
    }

    private static string EscapeTsv(string value)
    {
      return value.Replace("\"", "\"\"");
    }
  }
}
