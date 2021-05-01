using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;

namespace EQLogParser
{
  class TextFormatUtils
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private const string BB_CELL_HEADER = "  [td]{0}    [/td]";
    private const string BB_CELL_BODY = "[td][right]{0}   [/right][/td]";
    private const string BB_CELL_FIRST = "[td]{0}[/td]";
    private const string BB_ROW_START = "[tr]";
    private const string BB_ROW_END = "[/tr]\r\n";
    private const string BB_TABLE_START = "[table]\r\n";
    private const string BB_TABLE_END = "[/table]\r\n";
    private const string BB_TITLE = "[b]{0}[/b]\r\n";
    private const string CSV_STRING_CELL = "\"{0}\"\t";
    private const string CSV_NUMBER_CELL = "{0}\t";

    private const string BB_GAMPARSE_SPELL_COUNT = "   --- {0} - {1}";

    private static readonly Dictionary<int, string> ROMAN = new Dictionary<int, string>()
    {
      { 400, "CD" }, { 100, "C" }, { 90, "XC" }, { 50, "L" }, { 40, "XL" }, { 10, "X" }, { 9, "IX" }, { 5, "V" }, { 4, "IV" }, { 1, "I" }
    };

    internal static string BuildCsv(List<string> header, List<List<object>> data, string title = null)
    {
      StringBuilder sb = new StringBuilder();

      if (title != null)
      {
        sb.Append('"').Append(title).Append('"').AppendLine();
      }

      // header
      header.ForEach(item => sb.AppendFormat(CultureInfo.CurrentCulture, CSV_STRING_CELL, item));
      sb.AppendLine();

      // data
      data.ForEach(row =>
      {
        row.ToList().ForEach(item =>
        {
          sb.AppendFormat(CultureInfo.CurrentCulture, item.GetType() == typeof(string) ? CSV_STRING_CELL : CSV_NUMBER_CELL, item);
        });

        sb.AppendLine();
      });

      return sb.ToString();
    }

    internal static string BuildBBCodeTable(List<string> header, List<List<object>> data, string title = null)
    {
      StringBuilder sb = new StringBuilder();

      if (title != null)
      {
        sb.AppendFormat(CultureInfo.CurrentCulture, BB_TITLE, title);
      }

      // start table
      sb.Append(BB_TABLE_START);

      // header
      sb.Append(BB_ROW_START);
      header.ForEach(item => sb.AppendFormat(CultureInfo.CurrentCulture, BB_CELL_HEADER, item));
      sb.Append(BB_ROW_END);

      // data
      data.ForEach(row =>
      {
        sb.Append(BB_ROW_START);
        var first = row.FirstOrDefault();
        if (first != null)
        {
          sb.AppendFormat(CultureInfo.CurrentCulture, BB_CELL_FIRST, first);
        }

        row.Skip(1).ToList().ForEach(item => sb.AppendFormat(CultureInfo.CurrentCulture, BB_CELL_BODY, item));
        sb.Append(BB_ROW_END);
      });

      // end table
      sb.Append(BB_TABLE_END);
      return sb.ToString();
    }

    internal static string BuildGamparseList(List<string> header, List<List<object>> data, string title = null)
    {
      StringBuilder sb = new StringBuilder();

      if (title != null)
      {
        sb.AppendFormat(CultureInfo.CurrentCulture, BB_TITLE, title);
        sb.AppendLine();
      }

      // for each header minus the Totals
      SortedList<string, int> sorted = new SortedList<string, int>();
      for (int i = 1; i < header.Count - 1; i++)
      {
        sorted.Add(header[i], i);
      }

      foreach (var pair in sorted)
      {
        string updatedHeader = header[pair.Value].Replace("=", "-");
        sb.AppendFormat(CultureInfo.CurrentCulture, BB_TITLE, updatedHeader);

        data.OrderBy(row => row[0]).ToList().ForEach(row =>
        {
          if (row[pair.Value] != null && row[pair.Value].ToString().Length > 0 && row[pair.Value].ToString() != "0")
          {
            sb.AppendFormat(CultureInfo.CurrentCulture, BB_GAMPARSE_SPELL_COUNT, row[0], row[pair.Value]);
            sb.AppendLine();
          }
        });
      }

      return sb.ToString();
    }

    internal static void ExportAsHTML(Dictionary<string, SummaryTable> tables)
    {
      try
      {
        SaveFileDialog saveFileDialog = new SaveFileDialog();
        string filter = "EQLogParser Summary (*.html)|*.html";
        saveFileDialog.Filter = filter;

        var fileName = DateUtil.GetCurrentDate("MM-dd-yy") + " " + tables.Values.First().GetTargetTitle();
        saveFileDialog.FileName = string.Join("", fileName.Split(Path.GetInvalidFileNameChars()));

        if (saveFileDialog.ShowDialog().Value)
        {
          var headerTemplate = DotLiquid.Template.Parse(File.ReadAllText(@"data\html\header.html"));
          var tableKeys = tables.Keys.OrderBy(key => key);
          var tablechoices = new List<object>();

          foreach (var key in tableKeys)
          {
            tablechoices.Add(new { type = key, title = tables[key].GetTitle() });
          }

          var headerValue = headerTemplate.Render(DotLiquid.Hash.FromAnonymousObject(new { tablechoices }));
          File.WriteAllText(saveFileDialog.FileName, headerValue);
          headerValue = null;
          headerTemplate = null;

          var contentTemplate = DotLiquid.Template.Parse(File.ReadAllText(@"data\html\content.html"));
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
                var value = stats.GetType().GetProperty(column[0]).GetValue(stats, null);
                if (column[1].Contains("%"))
                {
                  if (value is double doubleValue && doubleValue == 0)
                  {
                    value = "-";
                  }
                }
                else if (!(value is string))
                {
                  value = $"{value:n0}";
                }

                data.Add(value);
              }

              var isChild = stats.IsTopLevel == false && isPetsCombined;
              var row = new
              {
                rank = isChild ? "" : stats.Rank.ToString(CultureInfo.CurrentCulture),
                ischild = isChild,
                haschild = stats.Name.Contains(" +Pets"),
                data
              };

              rows.Add(row);
            }

            var content = contentTemplate.Render(DotLiquid.Hash.FromAnonymousObject(new { columns, rows, tableid = key }));
            File.AppendAllText(saveFileDialog.FileName, content);
          }

          var footer = File.ReadAllText(@"data\html\footer.html");
          File.AppendAllText(saveFileDialog.FileName, footer);
        }
      }
      catch (IOException ex)
      {
        LOG.Error(ex);
      }
      catch (UnauthorizedAccessException uax)
      {
        LOG.Error(uax);
      }
      catch (SecurityException se)
      {
        LOG.Error(se);
      }
      catch (ArgumentNullException ane)
      {
        LOG.Error(ane);
      }
    }

    internal static string CapitalizeNpc(string name)
    {
      var result = name;
      if (!string.IsNullOrEmpty(name))
      {
        var split = name.Split(' ');
        if (split[0] != "a" && split[0] != "an")
        {
          for (int i = 0; i < split.Length; i++)
          {
            if (split[i] != "a" && split[i] != "an" && split[i] != "the" && split[i] != "of" && split[i] != "for" && split[i] != "with")
            {
              split[i] = ToUpper(split[i]);
            }
          }

          result = string.Join(" ", split);
        }
      }
      return result;
    }

    internal static string FlipCase(string name)
    {
      string result = name;
      if (!string.IsNullOrEmpty(name))
      {
        result = char.IsUpper(name[0]) ? ToLower(name) : ToUpper(name);
      }
      return result;
    }

    internal static string IntToRoman(int value)
    {
      var roman = new StringBuilder();

      foreach (var item in ROMAN)
      {
        while (value >= item.Key)
        {
          roman.Append(item.Value);
          value -= item.Key;
        }
      }

      return roman.ToString();
    }

    internal static string ToLower(string name) => char.ToLower(name[0], CultureInfo.CurrentCulture) + name.Substring(1);
    internal static string ToUpper(string name) => char.ToUpper(name[0], CultureInfo.CurrentCulture) + name.Substring(1);
  }
}
