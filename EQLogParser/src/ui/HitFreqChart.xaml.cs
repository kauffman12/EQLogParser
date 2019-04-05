using CsvHelper;
using CsvHelper.Configuration;
using LiveCharts;
using LiveCharts.Wpf;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for HitFreqChart.xaml
  /// </summary>
  public partial class HitFreqChart : UserControl
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private const string CRIT_HITTYPE = "Critical";
    private const string NON_CRIT_HITTYPE = "Non-Critical";
    private Dictionary<string, List<HitFreqChartData>> ChartData = null;
    private List<string> MinFreqs = new List<string>() { "Any Freq", "Freq > 1", "Freq > 2", "Freq > 3", "Freq > 4" };
    private static bool Updating = false;
    private int PageSize = 24;
    private List<int> YValues;
    private List<long> XValues;
    private List<long> XValuesDiff;
    private Dictionary<long, DIValue> DIMap;

    internal HitFreqChart()
    {
      InitializeComponent();
      minFreqList.ItemsSource = MinFreqs;
      minFreqList.SelectedIndex = 0;
    }

    internal void Update(Dictionary<string, List<HitFreqChartData>> chartData)
    {
      ChartData = chartData;
      List<string> players = chartData.Keys.ToList();
      playerList.ItemsSource = players;
      playerList.SelectedIndex = 0; // triggers event
    }

    private void SaveCSV_Click(object sender, RoutedEventArgs e)
    {
      var records = new List<FreqTable>();
      for (int i = 0; i < YValues.Count; i++)
      {
        FreqTable entry = new FreqTable();
        entry.HitValue = XValues[i];
        entry.Freq = YValues[i];
        entry.Diff = XValuesDiff[i];
        records.Add(entry);
      }

      StringWriter writer = null;
      CsvWriter csv = null;

      try
      {
        writer = new StringWriter();
        csv = new CsvWriter(writer);
        csv.Configuration.RegisterClassMap<FreqTableMap>();
        csv.WriteRecords(records);

        SaveFileDialog saveFileDialog = new SaveFileDialog();
        string filter = "CSV file (*.csv)|*.csv";
        saveFileDialog.Filter = filter;
        bool? result = saveFileDialog.ShowDialog();
        if (result == true)
        {
          File.WriteAllText(saveFileDialog.FileName, writer.ToString());
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
      finally
      {
        if (writer != null)
        {
          writer.Dispose();
        }
      }
    }

    private void UserSelectionChanged()
    {
      if (!Updating)
      {
        Updating = true;

        Task.Delay(20).ContinueWith(task =>
        {
          Dispatcher.InvokeAsync(() =>
          {
            try
            {
              string player = playerList.SelectedItem as string;
              string type = hitTypeList.SelectedItem as string;
              string critType = critTypeList.SelectedItem as string;

              if (player != null && type != null && critType != null && player.Length > 0 && type.Length > 0 && critType.Length > 0)
              {
                DIMap = null;
                var data = ChartData[player];
                int minFreq = GetMinFreq();
                HitFreqChartData first = data.Find(d => d.HitType == type);

                YValues = new List<int>();
                XValues = new List<long>();
                XValuesDiff = new List<long>();

                if (critType == CRIT_HITTYPE)
                {
                  for (int i = 0; i < first.CritYValues.Count; i++)
                  {
                    if (first.CritYValues[i] > minFreq)
                    {
                      YValues.Add(first.CritYValues[i]);
                      XValues.Add(first.CritXValues[i]);
                      XValuesDiff.Add(i > 0 ? Math.Abs(first.CritXValues[i] - first.CritXValues[i - 1]) : 0);
                    }
                  }
                }
                else
                {
                  for (int i = 0; i < first.NonCritYValues.Count; i++)
                  {
                    if (first.NonCritYValues[i] > minFreq)
                    {
                      YValues.Add(first.NonCritYValues[i]);
                      XValues.Add(first.NonCritXValues[i]);
                      XValuesDiff.Add(i > 0 ? Math.Abs(first.NonCritXValues[i] - first.NonCritXValues[i - 1]) : 0);
                    }
                  }
                }

                pageSlider.Value = 0;
                UpdatePageSize();
                DisplayPage();
              }

              Updating = false;
            }
            catch (ArgumentNullException ex)
            {
              Updating = false;
              LOG.Error(ex);
            }
            catch (InvalidOperationException ioe)
            {
              Updating = false;
              LOG.Error(ioe);
            }
          });
        }, TaskScheduler.Default);
      }
    }

    private void LookForDIValues()
    {
      DictionaryAddHelper<long, int> addHelper = new DictionaryAddHelper<long, int>();
      Dictionary<long, int> counts = new Dictionary<long, int>();
      List<long> unique = XValuesDiff.ToList();
      while (unique.Count > 0)
      {
        long value = unique[0];
        unique.RemoveAt(0);
        unique.ForEach(damage =>
        {
          if (Math.Abs(value - damage) <= 5 && damage > 20)
          {
            addHelper.Add(counts, value, 1);
          }
        });
      }

      int max = -1;
      long foundKey = -1;
      foreach (long key in counts.Keys)
      {
        if (counts[key] > max)
        {
          max = counts[key];
          foundKey = key;
        }
      }

      if (foundKey > -1)
      {
        int diCount = 0;
        DIMap = new Dictionary<long, DIValue>();
        for (int i = 0; i < XValuesDiff.Count; i++)
        {
          if (Math.Abs(XValuesDiff[i] - foundKey) <= 4)
          {
            diCount++;
            if (diCount == 1)
            {
              DIMap[XValues[i]] = new DIValue() { DI = diCount };
            }
            else
            {
              DIMap[XValues[i]] = new DIValue() { DI = diCount, Diff = XValuesDiff[i] };
            }
          }
        };

        if (DIMap.Count > 5)
        {
        }
      }
    }

    private void UpdatePageSize()
    {
      if (YValues != null)
      {
        PageSize = (int)Math.Round(lvcChart.ActualWidth / 49);
        pageSlider.Minimum = 0;
        int max = YValues.Count <= PageSize ? 0 : YValues.Count - PageSize;
        // happens after resize
        if (pageSlider.Value > max)
        {
          pageSlider.Value = max;
        }
        pageSlider.Maximum = max;
        pageSlider.IsEnabled = pageSlider.Maximum > 0;
        if (pageSlider.IsEnabled)
        {
          pageSlider.Focus();
        }
      }
    }

    private void DisplayPage()
    {
      if (YValues != null)
      {
        int page = (int)pageSlider.Value;
        ChartValues<int> yChartValues = new ChartValues<int>();
        List<string> xChartValues = new List<string>();

        int maxY = 1;
        for (int i = page; i < page + PageSize && i < YValues.Count; i++)
        {
          maxY = Math.Max(maxY, YValues[i]);
          yChartValues.Add(YValues[i]);
          xChartValues.Add(XValues[i].ToString() + (XValuesDiff[i] == 0 ? "" : " \n+" + XValuesDiff[i].ToString()));
        }

        var series = new SeriesCollection();
        var firstSeries = new ColumnSeries();
        firstSeries.Values = yChartValues;
        firstSeries.DataLabels = true;
        firstSeries.LabelPoint = point => point.Y.ToString();
        firstSeries.FontSize = 14;
        firstSeries.FontWeight = FontWeights.Bold;
        firstSeries.Foreground = new SolidColorBrush(Colors.White);
        firstSeries.MaxColumnWidth = 15;
        firstSeries.ColumnPadding = 8;
        firstSeries.ScalesXAt = 0;
        series.Add(firstSeries);

        lvcChart.DataTooltip = null;
        lvcChart.AxisX[0].Separator.StrokeThickness = 0;
        lvcChart.AxisX[0].Labels = xChartValues;
        lvcChart.AxisY[0].Labels = null;
        lvcChart.AxisY[0].Separator.Step = (maxY <= 10) ? 2 : double.NaN;
        lvcChart.Series = series;
        Helpers.ChartResetView(lvcChart);
      }
    }

    private int GetMinFreq()
    {
      int result = 1;
      string selected = minFreqList.SelectedItem as string;
      switch (selected)
      {
        case "Any Freq":
          result = 0;
          break;
        case "Freq > 1":
          result = 1;
          break;
        case "Freq > 2":
          result = 2;
          break;
        case "Freq > 3":
          result = 3;
          break;
        case "Freq > 4":
          result = 4;
          break;
      }
      return result;
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      UserSelectionChanged();
    }

    private void CritTypeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      string selectedCritType = critTypeList.SelectedItem as string;
      if (selectedCritType != null)
      {
        UpdateSelectedHitTypes(selectedCritType == NON_CRIT_HITTYPE);
        UserSelectionChanged();
      }
    }

    private void Chart_DoubleClick(object sender, MouseButtonEventArgs e)
    {
      Helpers.ChartResetView(lvcChart);
    }

    private void PlayerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      try
      {
        string selectedCritType = critTypeList.SelectedItem as string;
        string player = playerList.SelectedItem as string;
        var data = ChartData[player];

        bool canUseCrit = false;
        List<string> playerCritTypes = new List<string>();
        if (data.Any(d => d.CritYValues.Count > 0))
        {
          playerCritTypes.Add(CRIT_HITTYPE);
          canUseCrit = true;
        }

        if (data.Any(d => d.NonCritYValues.Count > 0))
        {
          playerCritTypes.Add(NON_CRIT_HITTYPE);
        }

        critTypeList.ItemsSource = playerCritTypes;
        critTypeList.SelectedIndex = playerCritTypes.Count == 1 ? 0 : canUseCrit ? 1 : 0;
        UpdateSelectedHitTypes(critTypeList.SelectedItem as string == NON_CRIT_HITTYPE);
        UserSelectionChanged();
      }
      catch (Exception ex)
      {
        LOG.Error(ex);
      }
    }

    private void UpdateSelectedHitTypes(bool useNonCrit)
    {
      try
      {
        string player = playerList.SelectedItem as string;
        if (player != null && player != "")
        {
          var data = ChartData[player];
          string selectedHitType = hitTypeList.SelectedItem as string;
          List<string> hitTypes;

          if (useNonCrit)
          {
            hitTypes = data.Where(d => d.NonCritYValues.Count > 0).Select(d => d.HitType).OrderBy(hitType => hitType).ToList();
          }
          else
          {
            hitTypes = data.Where(d => d.CritYValues.Count > 0).Select(d => d.HitType).OrderBy(hitType => hitType).ToList();
          }

          hitTypeList.ItemsSource = hitTypes;
          hitTypeList.SelectedItem = (selectedHitType != null && hitTypes.Contains(selectedHitType)) ? selectedHitType : hitTypes[0];
        }
      }
      catch (Exception ex)
      {
        LOG.Error(ex);
      }
    }

    private void PageSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
      DisplayPage();
    }

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e)
    {
      UpdatePageSize();
      DisplayPage();
    }

    private class DIValue
    {
      public int DI { get; set; }
      public long Diff { get; set; }
    }

    private class FreqTable
    {
      public long HitValue { get; set; }
      public int Freq { get; set; }
      public Nullable<long> Diff { get; set; }
    }

    private class FreqTableMap : ClassMap<FreqTable>
    {
      public FreqTableMap()
      {
        Map(m => m.HitValue).Name("Hit Value");
        Map(m => m.Freq).Name("Frequency");
        Map(m => m.Diff).Name("Difference");
      }
    }
  }

}
