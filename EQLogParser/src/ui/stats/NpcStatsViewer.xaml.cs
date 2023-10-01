using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for NpcStatsViewer.xaml
  /// </summary>
  public partial class NpcStatsViewer : UserControl, IDisposable
  {
    private const string NODATA = "No Spell Resist Data Found";

    public NpcStatsViewer()
    {
      InitializeComponent();
      (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete += EventsLogLoadingComplete;
      // default these columns to descending
      var desc = new string[] { "Lowest", "Cold", "Corruption", "Disease", "Magic", "Fire", "Physical", "Poison", "Average", "Reflected" };
      dataGrid.SortColumnsChanging += (object s, GridSortColumnsChangingEventArgs e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      dataGrid.SortColumnsChanged += (object s, GridSortColumnsChangedEventArgs e) => DataGridUtil.SortColumnsChanged(s, e, desc);

      DataGridUtil.UpdateTableMargin(dataGrid);
      (Application.Current.MainWindow as MainWindow).EventsThemeChanged += EventsThemeChanged;
      Load();
    }

    private void Load()
    {
      var npcStatsRows = new Dictionary<string, NpcStatsRow>();
      foreach (var kv in DataManager.Instance.GetNpcResistStats())
      {
        if (!PlayerManager.Instance.IsPetOrPlayerOrMerc(kv.Key) && !PlayerManager.Instance.IsPetOrPlayerOrMerc(TextUtils.ToUpper(kv.Key)))
        {
          var row = new NpcStatsRow { Name = kv.Key };
          foreach (var resists in kv.Value)
          {
            var rate = GetRate(resists.Value.Landed, resists.Value.Resisted);

            switch (resists.Key)
            {
              case SpellResist.AVERAGE:
                row.Average = rate.Item1;
                row.AverageText = rate.Item2;
                row.AverageTotal = resists.Value.Landed + resists.Value.Resisted;
                break;
              case SpellResist.COLD:
                row.Cold = rate.Item1;
                row.ColdText = rate.Item2;
                row.ColdTotal = resists.Value.Landed + resists.Value.Resisted;
                break;
              case SpellResist.CORRUPTION:
                row.Corruption = rate.Item1;
                row.CorruptionText = rate.Item2;
                row.CorruptionTotal = resists.Value.Landed + resists.Value.Resisted;
                break;
              case SpellResist.DISEASE:
                row.Disease = rate.Item1;
                row.DiseaseText = rate.Item2;
                row.DiseaseTotal = resists.Value.Landed + resists.Value.Resisted;
                break;
              case SpellResist.FIRE:
                row.Fire = rate.Item1;
                row.FireText = rate.Item2;
                row.FireTotal = resists.Value.Landed + resists.Value.Resisted;
                break;
              case SpellResist.LOWEST:
                row.Lowest = rate.Item1;
                row.LowestText = rate.Item2;
                row.LowestTotal = resists.Value.Landed + resists.Value.Resisted;
                break;
              case SpellResist.MAGIC:
                row.Magic = rate.Item1;
                row.MagicText = rate.Item2;
                row.MagicTotal = resists.Value.Landed + resists.Value.Resisted;
                break;
              case SpellResist.PHYSICAL:
                row.Physical = rate.Item1;
                row.PhysicalText = rate.Item2;
                row.PhysicalTotal = resists.Value.Landed + resists.Value.Resisted;
                break;
              case SpellResist.POISON:
                row.Poison = rate.Item1;
                row.PoisonText = rate.Item2;
                row.PoisonTotal = resists.Value.Landed + resists.Value.Resisted;
                break;
            }
          }

          npcStatsRows[kv.Key] = row;
        }
      }

      foreach (var kv in DataManager.Instance.GetNpcTotalSpellCounts())
      {
        if (!npcStatsRows.TryGetValue(kv.Key, out var updateRow))
        {
          updateRow = new NpcStatsRow { Name = kv.Key };
        }

        var rate = GetRate(kv.Value.Landed, kv.Value.Reflected);
        updateRow.Reflected = rate.Item1;
        updateRow.ReflectedText = rate.Item2;
        updateRow.ReflectedTotal = kv.Value.Landed + kv.Value.Reflected;
      }

      dataGrid.ItemsSource = npcStatsRows.Values.OrderBy(row => row.Name).ToList();
      titleLabel.Content = npcStatsRows.Values.Count == 0 ? NODATA : "Spell Resists vs " + npcStatsRows.Count + " Unique NPCs";

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
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        (Application.Current.MainWindow as MainWindow).EventsThemeChanged -= EventsThemeChanged;
        (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete -= EventsLogLoadingComplete;
        dataGrid.Dispose();
        disposedValue = true;
      }
    }

    // This code added to correctly implement the disposable pattern.
    public void Dispose()
    {
      // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
      Dispose(true);
      // TODO: uncomment the following line if the finalizer is overridden above.
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
