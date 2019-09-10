using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LiveCharts;
using LiveCharts.Defaults;
using LiveCharts.Wpf;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for GanttExample.xaml
  /// </summary>
  public partial class GanttChart : UserControl, INotifyPropertyChanged
  {
    private double _from;
    private double _to;
    private readonly ChartValues<GanttPoint> _values;

    public GanttChart(List<List<ActionBlock>> groups)
    {
      InitializeComponent();
      var now = DateTime.Now;

      var res = DataManager.Instance.GetReceivedSpellsDuring(groups.First().First().BeginTime, groups.Last().Last().BeginTime);

      var test = new List<IAction>();
      res.ForEach(group =>
      {
        var actions = group.Actions.Where(action => action is ReceivedSpell spell && spell.Receiver == "Bleve" && 
          spell.SpellData.IsLongDuration && spell.SpellData.IsBeneficial && !string.IsNullOrEmpty(spell.SpellData.LandsOnOther));
        test.AddRange(actions);
      });

      lvcChart.Hoverable = false;
      lvcChart.DisableAnimations = true;
      lvcChart.DataTooltip = null;

      _values = new ChartValues<GanttPoint>
            {
                new GanttPoint(now.AddSeconds(30).Ticks, now.AddSeconds(60).Ticks),
                new GanttPoint(now.AddSeconds(30).Ticks, now.AddSeconds(60).Ticks),
                new GanttPoint(now.AddSeconds(30).Ticks, now.AddSeconds(120).Ticks),
                new GanttPoint(now.AddSeconds(30).Ticks, now.AddSeconds(120).Ticks),
                new GanttPoint(now.AddSeconds(30).Ticks, now.AddSeconds(230).Ticks),
            };

      Series = new SeriesCollection
            {
                new RowSeries
                {
                    Values = _values,
                    LabelsPosition = BarLabelPosition.Parallel,
                    DataLabels = true
                }
            };

      Formatter = value => new DateTime((long)value).ToString("hh:mm:ss");

      var labels = new List<string>();
      labels.Add("Killaas");
      labels.Add("Killaas");
      labels.Add("Bard");
      labels.Add("Bard");
      labels.Add("Kuvani");

      Labels = labels.ToArray();

      ResetZoomOnClick(null, null);

      DataContext = this;
    }

    public SeriesCollection Series { get; set; }
    public Func<double, string> Formatter { get; set; }
    public string[] Labels { get; set; }

    public double From
    {
      get { return _from; }
      set
      {
        _from = value;
        OnPropertyChanged(nameof(From));
      }
    }

    public double To
    {
      get { return _to; }
      set
      {
        _to = value;
        OnPropertyChanged(nameof(To));
      }
    }

    private void ResetZoomOnClick(object sender, RoutedEventArgs e)
    {
      From = _values.First().StartPoint;
      To = _values.Last().EndPoint;
    }

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName = null)
    {
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
  }
}
