using Syncfusion.Data;
using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;

namespace EQLogParser
{
  public partial class NpcStatsViewer : IDocumentContent
  {
    public ObservableCollection<NpcStatsRow> NpcStatsData { get; set; }
    private const string Nodata = "No Spell Resist Data Found";
    private bool _ready;

    public NpcStatsViewer()
    {
      InitializeComponent();
      NpcStatsData = [];
      DataContext = this;

      // default these columns to descending
      var desc = new[] { "LowestText", "ColdText", "CorruptionText", "DiseaseText", "MagicText", "FireText", "PhysicalText",
        "PoisonText", "AverageText", "ReflectedText" };
      dataGrid.SortColumnsChanging += (s, e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      dataGrid.SortColumnsChanged += (s, e) => DataGridUtil.SortColumnsChanged(s, e, desc);

      // custom compare for number fields
      foreach (var d in desc)
      {
        dataGrid.SortComparers.Add(new SortComparer
        {
          PropertyName = d,
          Comparer = new ResistComparer<NpcStatsRow>(d[..^4])
        });
      }

      MainActions.EventsThemeChanged += EventsThemeChanged;
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
          var row = new NpcStatsRow { Npc = upperNpc };
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

      UiUtil.UpdateObservable(npcStatsRows.Values.AsEnumerable(), NpcStatsData);
      dataGrid.View?.Refresh();
      titleLabel.Content = npcStatsRows.Values.Count == 0 ? Nodata : "Spell Resists vs " + npcStatsRows.Count + " Unique NPCs";
      return;

      static Tuple<double, string> GetRate(uint landed, uint notLanded)
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
    private async void CreateImageClick(object sender, RoutedEventArgs e) => await DataGridUtil.CreateImageAsync(dataGrid, titleLabel);
    private async void CreateLargeImageClick(object sender, RoutedEventArgs e) => await DataGridUtil.CreateImageAsync(dataGrid, titleLabel, true);
    private void RefreshClick(object sender, RoutedEventArgs e) => Load();
    private void EventsLogLoadingComplete(string file, bool open) => Load();
    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);

    private void ContentLoaded(object sender, RoutedEventArgs e)
    {
      if (VisualParent != null && !_ready)
      {
        MainActions.EventsLogLoadingComplete += EventsLogLoadingComplete;
        Load();
        _ready = true;
      }
    }

    public void HideContent()
    {
      MainActions.EventsLogLoadingComplete -= EventsLogLoadingComplete;
      NpcStatsData.Clear();
      _ready = false;
    }

    private void AutoGeneratingColumn(object sender, AutoGeneratingColumnArgs e)
    {
      if (e.Column.MappingName == "Npc")
      {
        e.Column.Width = MainActions.CurrentNpcWidth;
      }
      else if (e.Column.MappingName?.EndsWith("Text", StringComparison.OrdinalIgnoreCase) != true)
      {
        e.Cancel = true;
      }
      else
      {
        if (e.Column.MappingName == "ReflectedText")
        {
          e.Column.HeaderText = "Reflected %";
        }
        else if (e.Column.MappingName?.Length >= 4)
        {
          e.Column.HeaderText = e.Column.MappingName[..^4] + " Resist %";
        }

        e.Column.TextAlignment = TextAlignment.Right;
        e.Column.Width = DataGridUtil.CalculateMinGridHeaderWidth(e.Column.HeaderText);
      }
    }

    private class ResistComparer<T>(string field) : IComparer<object>
    {
      private readonly PropertyInfo _propertyInfo = typeof(T).GetProperty(field);
      private readonly PropertyInfo _totalInfo = typeof(T).GetProperty(field + "Total");

      public int Compare(object x, object y)
      {
        if (_propertyInfo == null || x is not NpcStatsRow || y is not NpcStatsRow r2)
          return 0;

        var valueX = _propertyInfo.GetValue(x);
        var valueY = _propertyInfo.GetValue(y);

        if (valueX == null || valueY == null)
        {
          return 0;
        }

        if (valueX is IComparable comparableX)
        {
          var result = comparableX.CompareTo(valueY);
          if (result == 0 && valueX is double and 0)
          {
            var totalX = _totalInfo.GetValue(x);
            var totalY = _totalInfo.GetValue(y);
            if (totalX is IComparable totalComparableX)
            {
              return totalComparableX.CompareTo(totalY);
            }
          }

          return result;
        }

        return 0;
      }
    }
  }

  public class NpcStatsRow
  {
    public string Npc { get; set; }
    public double Average { get; set; } = -1.0;
    public string AverageText { get; set; } = "-";
    public uint AverageTotal { get; set; }
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
