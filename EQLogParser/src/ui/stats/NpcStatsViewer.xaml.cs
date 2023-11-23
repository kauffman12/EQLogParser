using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for NpcStatsViewer.xaml
  /// </summary>
  public partial class NpcStatsViewer : IDisposable
  {
    private const string NODATA = "No Spell Resist Data Found";

    public NpcStatsViewer()
    {
      InitializeComponent();
      MainActions.EventsLogLoadingComplete += EventsLogLoadingComplete;
      // default these columns to descending
      var desc = new[] { "Lowest", "Cold", "Corruption", "Disease", "Magic", "Fire", "Physical", "Poison", "Average", "Reflected" };
      dataGrid.SortColumnsChanging += (s, e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      dataGrid.SortColumnsChanged += (s, e) => DataGridUtil.SortColumnsChanged(s, e, desc);

      DataGridUtil.UpdateTableMargin(dataGrid);
      MainActions.EventsThemeChanged += EventsThemeChanged;
      Load();
    }

    private void Load()
    {
      var npcStatsRows = new Dictionary<string, NpcStatsRow>();
      foreach (var stats in RecordManager.Instance.GetAllNpcResistStats())
      {
        var upperNpc = TextUtils.ToUpper(stats.Npc);
        if (!PlayerManager.Instance.IsPetOrPlayerOrMerc(stats.Npc) && !PlayerManager.Instance.IsPetOrPlayerOrMerc(upperNpc))
        {
          var count = 0u;
          var reflectedCount = 0u;
          var row = new NpcStatsRow { Name = upperNpc };
          foreach (var resists in stats.ByResist)
          {
            if (resists.Key == SpellResist.Reflected)
            {
              reflectedCount = resists.Value.Resisted;
            }
            else
            {
              count += resists.Value.Landed + resists.Value.Resisted;
              var rate = GetRate(resists.Value.Landed, resists.Value.Resisted);
              switch (resists.Key)
              {
                case SpellResist.Average:
                  row.Average = rate.Item1;
                  row.AverageText = rate.Item2;
                  row.AverageTotal = resists.Value.Landed + resists.Value.Resisted;
                  break;
                case SpellResist.Cold:
                  row.Cold = rate.Item1;
                  row.ColdText = rate.Item2;
                  row.ColdTotal = resists.Value.Landed + resists.Value.Resisted;
                  break;
                case SpellResist.Corruption:
                  row.Corruption = rate.Item1;
                  row.CorruptionText = rate.Item2;
                  row.CorruptionTotal = resists.Value.Landed + resists.Value.Resisted;
                  break;
                case SpellResist.Disease:
                  row.Disease = rate.Item1;
                  row.DiseaseText = rate.Item2;
                  row.DiseaseTotal = resists.Value.Landed + resists.Value.Resisted;
                  break;
                case SpellResist.Fire:
                  row.Fire = rate.Item1;
                  row.FireText = rate.Item2;
                  row.FireTotal = resists.Value.Landed + resists.Value.Resisted;
                  break;
                case SpellResist.Lowest:
                  row.Lowest = rate.Item1;
                  row.LowestText = rate.Item2;
                  row.LowestTotal = resists.Value.Landed + resists.Value.Resisted;
                  break;
                case SpellResist.Magic:
                  row.Magic = rate.Item1;
                  row.MagicText = rate.Item2;
                  row.MagicTotal = resists.Value.Landed + resists.Value.Resisted;
                  break;
                case SpellResist.Physical:
                  row.Physical = rate.Item1;
                  row.PhysicalText = rate.Item2;
                  row.PhysicalTotal = resists.Value.Landed + resists.Value.Resisted;
                  break;
                case SpellResist.Poison:
                  row.Poison = rate.Item1;
                  row.PoisonText = rate.Item2;
                  row.PoisonTotal = resists.Value.Landed + resists.Value.Resisted;
                  break;
              }
            }
          }

          if (reflectedCount > 0)
          {
            var reflectRate = GetRate(count, reflectedCount);
            row.Reflected = reflectRate.Item1;
            row.ReflectedText = reflectRate.Item2;
            row.ReflectedTotal = count;
          }

          npcStatsRows[upperNpc] = row;
        }
      }

      dataGrid.ItemsSource = npcStatsRows.Values.OrderBy(row => row.Name).ToList();
      titleLabel.Content = npcStatsRows.Values.Count == 0 ? NODATA : "Spell Resists vs " + npcStatsRows.Count + " Unique NPCs";
      return;

      Tuple<double, string> GetRate(uint landed, uint notLanded)
      {
        Tuple<double, string> results;
        var computed = 0.0;
        var total = landed + notLanded;

        if (landed == 0 && total > 0)
        {
          computed = 1.0;
        }
        else if (total > 0)
        {
          computed = notLanded / (double)total;
        }

        if (total > 0)
        {
          computed = Math.Round(computed * 100, 2);
          var computedString = string.Format(CultureInfo.CurrentCulture, "{0} ({1}/{2})", computed, notLanded, total);
          results = new Tuple<double, string>(computed, computedString);
        }
        else
        {
          results = new Tuple<double, string>(0.0, "-");
        }

        return results;
      }
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(dataGrid, titleLabel.Content.ToString());
    private void CreateImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(dataGrid, titleLabel);
    private void CreateLargeImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(dataGrid, titleLabel, true);
    private void RefreshClick(object sender, RoutedEventArgs e) => Load();
    private void EventsLogLoadingComplete(string _) => Load();
    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);

    #region IDisposable Support
    private bool DisposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!DisposedValue)
      {
        MainActions.EventsThemeChanged -= EventsThemeChanged;
        MainActions.EventsLogLoadingComplete -= EventsLogLoadingComplete;
        dataGrid.Dispose();
        DisposedValue = true;
      }
    }

    // This code added to correctly implement the disposable pattern.
    public void Dispose()
    {
      // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
      Dispose(true);
      GC.SuppressFinalize(this);
    }
    #endregion
  }

  public class NpcStatsRow
  {
    public string Name { get; set; }
    public double Average { get; set; } = -1.0;
    public string AverageText { get; set; } = "-";
    public uint AverageTotal { get; set; }
    public string TooltipText { get; set; }
    public double Cold { get; set; } = -1.0;
    public string ColdText { get; set; } = "-";
    public uint ColdTotal { get; set; }
    public double Corruption { get; set; } = -1.0;
    public string CorruptionText { get; set; } = "-";
    public uint CorruptionTotal { get; set; }
    public double Disease { get; set; } = -1.0;
    public string DiseaseText { get; set; } = "-";
    public uint DiseaseTotal { get; set; }
    public double Fire { get; set; } = -1.0;
    public string FireText { get; set; } = "-";
    public uint FireTotal { get; set; }
    public double Lowest { get; set; } = -1.0;
    public string LowestText { get; set; } = "-";
    public uint LowestTotal { get; set; }
    public double Magic { get; set; } = -1.0;
    public string MagicText { get; set; } = "-";
    public uint MagicTotal { get; set; }
    public double Physical { get; set; } = -1.0;
    public string PhysicalText { get; set; } = "-";
    public uint PhysicalTotal { get; set; }
    public double Poison { get; set; } = -1.0;
    public string PoisonText { get; set; } = "-";
    public uint PoisonTotal { get; set; }
    public double Reflected { get; set; } = -1.0;
    public string ReflectedText { get; set; } = "-";
    public uint ReflectedTotal { get; set; }
  }
}
