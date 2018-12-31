using LiveCharts;
using LiveCharts.Wpf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;

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

    public HitFreqChart()
    {
      InitializeComponent();
      minFreqList.ItemsSource = MinFreqs;
      minFreqList.SelectedIndex = 0;
    }

    public void Update(Dictionary<string, List<HitFreqChartData>> chartData)
    {
      ChartData = chartData;
      List<string> players = chartData.Keys.ToList();
      playerList.ItemsSource = players;
      playerList.SelectedIndex = 0; // triggers event
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

              if (player != null && type != null && critType != null && player != "" && type != "" && critType != "")
              {
                var data = ChartData[player];
                int minFreq = GetMinFreq();
                HitFreqChartData first = data.Find(d => d.HitType == type);

                ChartValues<int> values = new ChartValues<int>();
                List<string> labels = new List<string>();

                if (critType == CRIT_HITTYPE)
                {
                  for (int i = 0; i < first.CritValues.Count; i++)
                  {
                    if (first.CritValues[i] > minFreq)
                    {
                      values.Add(first.CritValues[i]);
                      labels.Add(first.CritXAxisLabels[i]);
                    }
                  }
                }
                else
                {
                  for (int i = 0; i < first.NonCritValues.Count; i++)
                  {
                    if (first.NonCritValues[i] > minFreq)
                    {
                      values.Add(first.NonCritValues[i]);
                      labels.Add(first.NonCritXAxisLabels[i]);
                    }
                  }
                }

                var series = new SeriesCollection();
                var firstSeries = new ColumnSeries();
                firstSeries.Values = values;
                series.Add(firstSeries);
                lvcChart.AxisX[0].Labels = labels;
                lvcChart.AxisY[0].Labels = null;
                lvcChart.Series = series;
                Helpers.ChartResetView(lvcChart);
              }

              Updating = false;
            }
            catch(Exception ex)
            {
              Updating = false;
              LOG.Error(ex);
            }
          });
        });
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

        bool useNonCrit = true;
        List<string> playerCritTypes = new List<string>();
        if (data.Any(d => d.CritValues.Count > 0))
        {
          playerCritTypes.Add(CRIT_HITTYPE);
          if (selectedCritType != null && selectedCritType == CRIT_HITTYPE)
          {
            useNonCrit = false;
          }
        }

        if (data.Any(d => d.NonCritValues.Count > 0))
        {
          playerCritTypes.Add(NON_CRIT_HITTYPE);
        }

        critTypeList.ItemsSource = playerCritTypes;
        critTypeList.SelectedItem = useNonCrit ? NON_CRIT_HITTYPE : CRIT_HITTYPE;
        UpdateSelectedHitTypes(useNonCrit);
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
            hitTypes = data.Where(d => d.NonCritValues.Count > 0).Select(d => d.HitType).OrderBy(hitType => hitType).ToList();
          }
          else
          {
            hitTypes = data.Where(d => d.CritValues.Count > 0).Select(d => d.HitType).OrderBy(hitType => hitType).ToList();
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
  }
}
