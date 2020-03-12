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
    private const string CSV_CELL = "\"{0}\"\t";

    private const string BB_GAMPARSE_SPELL_COUNT = "   --- {0} - {1}";

    // Tab delimited CSV for copy/paste to excel
    internal static string BuildSpellCountCsv(List<string> header, List<List<string>> data, string title = null)
    {
      StringBuilder sb = new StringBuilder();

      if (title != null)
      {
        sb.Append('"').Append(title).Append('"').AppendLine();
      }

      var list = new List<string>
      {
        "Totals"
      };

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

    internal static string BuildCsv(List<string> header, List<List<string>> data, string title = null)
    {
      StringBuilder sb = new StringBuilder();

      if (title != null)
      {
        sb.Append('"').Append(title).Append('"').AppendLine();
      }

      // header
      header.ForEach(item => sb.AppendFormat(CultureInfo.CurrentCulture, CSV_CELL, item));
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
          if (!string.IsNullOrEmpty(row[pair.Value]) && (row[pair.Value] != "0"))
          {
            sb.AppendFormat(CultureInfo.CurrentCulture, BB_GAMPARSE_SPELL_COUNT, row[0], row[pair.Value]);
            sb.AppendLine();
          }
        });
      }

      return sb.ToString();
    }

    internal static SpellData ParseCustomSpellData(string line)
    {
      SpellData spellData = null;

      if (!string.IsNullOrEmpty(line))
      {
        string[] data = line.Split('^');
        if (data.Length >= 11)
        {
          int duration = int.Parse(data[3], CultureInfo.CurrentCulture) * 6; // as seconds
          int beneficial = int.Parse(data[4], CultureInfo.CurrentCulture);
          byte target = byte.Parse(data[6], CultureInfo.CurrentCulture);
          ushort classMask = ushort.Parse(data[7], CultureInfo.CurrentCulture);

          // deal with too big or too small values
          // all adps we care about is in the range of a few minutes
          if (duration > ushort.MaxValue)
          {
            duration = ushort.MaxValue;
          }
          else if (duration < 0)
          {
            duration = 0;
          }

          spellData = new SpellData()
          {
            ID = string.Intern(data[0]),
            Name = string.Intern(data[1]),
            NameAbbrv = Helpers.AbbreviateSpellName(data[1]),
            Level = ushort.Parse(data[2], CultureInfo.CurrentCulture),
            Duration = (ushort)duration,
            IsBeneficial = beneficial != 0,
            Target = target,
            MaxHits = ushort.Parse(data[5], CultureInfo.CurrentCulture),
            ClassMask = classMask,
            LandsOnYou = string.Intern(data[8]),
            LandsOnOther = string.Intern(data[9]),
            Damaging = byte.Parse(data[10], CultureInfo.CurrentCulture) == 1,
            IsProc = byte.Parse(data[11], CultureInfo.CurrentCulture) == 1,
            IsAdps = byte.Parse(data[13], CultureInfo.CurrentCulture) == 1
          };
        }
      }

      return spellData;
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

            var columns = headers.Select(header => header[1]).ToList();
            var rows = new List<object>();
            foreach (var stats in playerStats)
            {
              var data = new List<object>();
              foreach (var column in headers.Skip(1))
              {
                var value = stats.GetType().GetProperty(column[0]).GetValue(stats, null);
                if (column[1].Contains("%") && value is double doubleValue && doubleValue == 0)
                {
                  value = "-";
                }

                data.Add(value);
              }

              var row = new
              {
                rank = stats.Rank == 0 ? "" : stats.Rank.ToString(CultureInfo.CurrentCulture),
                ischild = stats.Rank == 0,
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
  }
}
