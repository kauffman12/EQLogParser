
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
  }
}
