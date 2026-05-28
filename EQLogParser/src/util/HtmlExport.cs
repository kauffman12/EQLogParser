using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DotLiquid;

namespace EQLogParser
{
  internal static class HtmlExport
  {
    internal static void SaveHtml(string selectedFileName, Dictionary<string, SummaryTable> tables)
    {
      var headerTemplate = Template.Parse(File.ReadAllText(@"data\html\header.html"));
      var tableKeys = tables.Keys.OrderBy(key => key);
      var tablechoices = new List<object>();

      foreach (var key in tableKeys)
      {
        tablechoices.Add(new { type = key, title = tables[key].GetTitle() });
      }

      var headerValue = headerTemplate.Render(Hash.FromAnonymousObject(new { tablechoices }), CultureInfo.CurrentCulture);
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

        var content = contentTemplate.Render(Hash.FromAnonymousObject(new { columns, rows, tableid = key }), CultureInfo.CurrentCulture);
        File.AppendAllText(selectedFileName, content);
      }

      var footer = File.ReadAllText(@"data\html\footer.html");
      File.AppendAllText(selectedFileName, footer);
    }
  }
}
