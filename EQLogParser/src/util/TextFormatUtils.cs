
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace EQLogParser
{
  class TextFormatUtils
  {
    private const string BB_CELL_HEADER = "  [td]{0}    [/td]";
    private const string BB_CELL_BODY = "[td][right]{0}   [/right][/td]";
    private const string BB_CELL_FIRST = "[td]{0}[/td]";
    private const string BB_ROW_START = "[tr]";
    private const string BB_ROW_END = "[/tr]\r\n";
    private const string BB_TABLE_START = "[table]\r\n";
    private const string BB_TABLE_END = "[/table]\r\n";
    private const string BB_TITLE = "[b]{0}[/b]\r\n";
    private const string CSV_CELL = "\"{0}\"\t";

    private const string BB_GAMPARSE_SPELL_COUNT = "   --- {0} - {1}";

    // Tab delimited CSV for copy/paste to excel
    internal static string BuildCsv(List<string> header, List<List<string>> data, string title = null)
    {
      StringBuilder sb = new StringBuilder();

      if (title != null)
      {
        sb.Append('"').Append(title).Append('"').AppendLine();
      }

      var list = new List<string>();
      list.Add("Totals");

      // header
      header.ForEach(item =>
      {
        var nameCounts = item.Split('=');
        sb.AppendFormat(CultureInfo.CurrentCulture, CSV_CELL, (nameCounts.Length == 2) ? nameCounts[0].Trim() : item);

        // keep track of totals
        if (nameCounts.Length == 2)
        {
          list.Add(nameCounts[1].Trim());
        }
      });

      // print totals
      sb.AppendLine();
      list.ForEach(item => sb.AppendFormat(CultureInfo.CurrentCulture, CSV_CELL, item.Trim()));
      sb.AppendLine();

      // data
      data.ForEach(row =>
      {
        row.ToList().ForEach(item => sb.AppendFormat(CultureInfo.CurrentCulture, CSV_CELL, item));
        sb.AppendLine();
      });

      return sb.ToString();
    }

    internal static string BuildBBCodeTable(List<string> header, List<List<string>> data, string title = null)
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

    internal static string BuildGamparseList(List<string> header, List<List<string>> data, string title = null)
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
          if (!string.IsNullOrEmpty(row[pair.Value]) && row[pair.Value] != "0")
          {
            sb.AppendFormat(CultureInfo.CurrentCulture, BB_GAMPARSE_SPELL_COUNT, row[0], row[pair.Value]);
            sb.AppendLine();
          }
        });
      }

      return sb.ToString();
    }
  }
}
