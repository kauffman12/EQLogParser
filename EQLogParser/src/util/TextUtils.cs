using DotLiquid;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EQLogParser
{
  internal static class TextUtils
  {
    private const string BbCellHeader = "  [td]{0}    [/td]";
    private const string BbCellBody = "[td][right]{0}   [/right][/td]";
    private const string BbCellFirst = "[td]{0}[/td]";
    private const string BbRowStart = "[tr]";
    private const string BbRowEnd = "[/tr]\r\n";
    private const string BbTableStart = "[table]\r\n";
    private const string BbTableEnd = "[/table]\r\n";
    private const string BbTitle = "[b]{0}[/b]\r\n";
    private const string CsvStringCell = "\"{0}\"\t";
    private const string CsvNumberCell = "{0}\t";

    private const string BbGamparseSpellCount = "   --- {0} - {1}";

    private static readonly Dictionary<int, string> Roman = new()
    {
      { 400, "CD" }, { 100, "C" }, { 90, "XC" }, { 50, "L" }, { 40, "XL" }, { 10, "X" }, { 9, "IX" }, { 5, "V" }, { 4, "IV" }, { 1, "I" }
    };

    internal static bool SCompare(string s, int start, int count, string test) => s.AsSpan(start, count).SequenceEqual(test);
    internal static string ParseSpellOrNpc(string[] split, int index) => string.Join(" ", split, index, split.Length - index).Trim('.');
    internal static string ToLower(string name) => string.IsNullOrEmpty(name) ? "" : name.ToLower(CultureInfo.InvariantCulture);

    internal static string ToUpper(string name, CultureInfo culture = null)
    {
      culture ??= CultureInfo.InvariantCulture;
      return string.IsNullOrEmpty(name) ? "" : (char.ToUpper(name[0], culture) + (name.Length > 1 ? name[1..] : ""));
    }

    internal static string BuildCsv(List<string> header, List<List<object>> data, string title = null)
    {
      var sb = new StringBuilder();

      if (title != null)
      {
        sb.Append('"').Append(title).Append('"').AppendLine();
      }

      // header
      header.ForEach(item => sb.AppendFormat(CultureInfo.CurrentCulture, CsvStringCell, item));
      sb.AppendLine();

      // data
      data.ForEach(row =>
      {
        row.ToList().ForEach(item =>
        {
          sb.AppendFormat(CultureInfo.CurrentCulture, item is string ? CsvStringCell : CsvNumberCell, item);
        });

        sb.AppendLine();
      });

      return sb.ToString();
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
      matches = success ? [] : null;

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
  }
}
