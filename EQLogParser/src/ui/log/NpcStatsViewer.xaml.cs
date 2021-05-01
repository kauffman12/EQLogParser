using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for NpcStatsViewer.xaml
  /// </summary>
  public partial class NpcStatsViewer : UserControl, IDisposable
  {
    private const string NODATA = "No NPC Stats Found";

    public NpcStatsViewer()
    {
      InitializeComponent();
      (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete += NpcStatsViewer_EventsLogLoadingComplete;
      dataGrid.Sorting += CustomSorting;
      Load();
    }

    private void Load()
    {
      var npcStatsRows = new Dictionary<string, NpcStatsRow>();
      foreach (var kv in DataManager.Instance.GetNpcResistStats())
      {
        if (!PlayerManager.Instance.IsPetOrPlayer(kv.Key) && !PlayerManager.Instance.IsPetOrPlayer(TextFormatUtils.ToUpper(kv.Key)))
        {
          var row = new NpcStatsRow { Name = TextFormatUtils.CapitalizeNpc(kv.Key) };
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
        if (!npcStatsRows.TryGetValue(kv.Key, out NpcStatsRow updateRow))
        {
          updateRow = new NpcStatsRow { Name = TextFormatUtils.CapitalizeNpc(kv.Key) };
        }

        var rate = GetRate(kv.Value.Landed, kv.Value.Reflected);
        updateRow.Reflected = rate.Item1;
        updateRow.ReflectedText = rate.Item2;
        updateRow.ReflectedTotal = kv.Value.Landed + kv.Value.Reflected;
      }

      dataGrid.ItemsSource = npcStatsRows.Values.OrderBy(row => row.Name).ToList();
      titleLabel.Content = npcStatsRows.Values.Count == 0 ? NODATA : "Your Spell Stats for " + npcStatsRows.Count + " Unique NPCs";

      Tuple<double, string> GetRate(uint landed, uint notLanded)
      {
        Tuple<double, string> results;
        double computed = 0.0;
        uint total = landed + notLanded;

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
          string computedString = string.Format(CultureInfo.CurrentCulture, "{0} ({1}/{2})", computed, notLanded, total);
          results = new Tuple<double, string>(computed, computedString);
        }
        else
        {
          results = new Tuple<double, string>(0.0, "-");
        }

        return results;
      }
    }

    private void LoadingRow(object sender, DataGridRowEventArgs e)
    {
      // set header count
      if (e.Row != null)
      {
        e.Row.Header = (e.Row.GetIndex() + 1).ToString(CultureInfo.CurrentCulture);
      }
    }

    private void CustomSorting(object sender, DataGridSortingEventArgs e)
    {
      if (e.Column.Header != null && e.Column.Header.ToString() != "Name" && dataGrid.ItemsSource != null)
      {
        e.Handled = true;
        var direction = e.Column.SortDirection ?? ListSortDirection.Descending;

        string field = (e.Column.Header as string).Split(' ')[0];
        if (dataGrid.ItemsSource is List<NpcStatsRow> data)
        {
          data.Sort(new NpcStatsRowComparer(field, direction == ListSortDirection.Descending));
          dataGrid.Items.Refresh();
        }

        e.Column.SortDirection = direction == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
      }
    }

    private void NpcStatsViewer_EventsLogLoadingComplete(object sender, bool e) => Load();
    private void RefreshMouseClick(object sender, MouseButtonEventArgs e) => Load();

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        if (disposing)
        {
          // TODO: dispose managed state (managed objects).
        }

        (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete -= NpcStatsViewer_EventsLogLoadingComplete;
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

  public class NpcStatsRowComparer : IComparer<NpcStatsRow>
  {
    private readonly bool Ascending;
    private readonly string Column;

    public NpcStatsRowComparer(string column, bool ascending)
    {
      Ascending = ascending;
      Column = column;
    }

    public int Compare(NpcStatsRow x, NpcStatsRow y)
    {
      int result = 0;

      if (Column != null && x != null && y != null)
      {
        switch (Column)
        {
          case "Chromatic":
            result = x.Lowest.CompareTo(y.Lowest);
            result = result != 0 ? result : x.LowestTotal.CompareTo(y.LowestTotal);
            break;
          case "Cold":
            result = x.Cold.CompareTo(y.Cold);
            result = result != 0 ? result : x.ColdTotal.CompareTo(y.ColdTotal);
            break;
          case "Corruption":
            result = x.Corruption.CompareTo(y.Corruption);
            result = result != 0 ? result : x.CorruptionTotal.CompareTo(y.CorruptionTotal);
            break;
          case "Disease":
            result = x.Disease.CompareTo(y.Disease);
            result = result != 0 ? result : x.DiseaseTotal.CompareTo(y.DiseaseTotal);
            break;
          case "Fire":
            result = x.Fire.CompareTo(y.Fire);
            result = result != 0 ? result : x.FireTotal.CompareTo(y.FireTotal);
            break;
          case "Magic":
            result = x.Magic.CompareTo(y.Magic);
            result = result != 0 ? result : x.MagicTotal.CompareTo(y.MagicTotal);
            break;
          case "Physical":
            result = x.Physical.CompareTo(y.Physical);
            result = result != 0 ? result : x.PhysicalTotal.CompareTo(y.PhysicalTotal);
            break;
          case "Poison":
            result = x.Poison.CompareTo(y.Poison);
            result = result != 0 ? result : x.PoisonTotal.CompareTo(y.PoisonTotal);
            break;
          case "Prismatic":
            result = x.Average.CompareTo(y.Average);
            result = result != 0 ? result : x.AverageTotal.CompareTo(y.AverageTotal);
            break;
          case "Reflected":
            result = x.Reflected.CompareTo(y.Reflected);
            result = result != 0 ? result : x.ReflectedTotal.CompareTo(y.ReflectedTotal);
            break;
        }
      }

      if (Ascending)
      {
        result *= -1;
      }

      return result;
    }
  }

  public class NpcStatsRow
  {
    public string Name { get; set; }
    public double Average { get; set; }
    public string AverageText { get; set; } = "-";
    public uint AverageTotal { get; set; }
    public string TooltipText { get; set; }
    public double Cold { get; set; }
    public string ColdText { get; set; } = "-";
    public uint ColdTotal { get; set; }
    public double Corruption { get; set; }
    public string CorruptionText { get; set; } = "-";
    public uint CorruptionTotal { get; set; }
    public double Disease { get; set; }
    public string DiseaseText { get; set; } = "-";
    public uint DiseaseTotal { get; set; }
    public double Fire { get; set; }
    public string FireText { get; set; } = "-";
    public uint FireTotal { get; set; }
    public double Lowest { get; set; }
    public string LowestText { get; set; } = "-";
    public uint LowestTotal { get; set; }
    public double Magic { get; set; }
    public string MagicText { get; set; } = "-";
    public uint MagicTotal { get; set; }
    public double Physical { get; set; }
    public string PhysicalText { get; set; } = "-";
    public uint PhysicalTotal { get; set; }
    public double Poison { get; set; }
    public string PoisonText { get; set; } = "-";
    public uint PoisonTotal { get; set; }
    public double Reflected { get; set; }
    public string ReflectedText { get; set; } = "-";
    public uint ReflectedTotal { get; set; }
  }
}
