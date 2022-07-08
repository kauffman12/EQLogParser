using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TankingBreakdown.xaml
  /// </summary>
  public partial class TankingBreakdown : BreakdownTable
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private List<PlayerStats> PlayerStats;
    private PlayerStats RaidStats;
    private static bool Running = false;

    public TankingBreakdown()
    {
      InitializeComponent();
      //InitBreakdownTable(dataGrid, selectedColumns);
    }

    internal void Init(CombinedStats currentStats, List<PlayerStats> selectedStats)
    {
      titleLabel.Content = currentStats?.ShortTitle;
      RaidStats = currentStats?.RaidStats;
      PlayerStats = selectedStats;
      Display();
    }

    internal void Display()
    {
      if (Running == false && RaidStats != null)
      {
        Running = true;
        Task.Delay(10).ContinueWith(task =>
        {
          try
          {
            if (PlayerStats != null)
            {
              ObservableCollection<PlayerSubStats> list = new ObservableCollection<PlayerSubStats>();

              //foreach (var playerStat in PlayerStats.AsParallel().OrderByDescending(stats => GetSortValue(stats)))
              //{
              //  list.Add(playerStat);
                //SortSubStats(playerStat.SubStats.ToList()).ForEach(subStat => list.Add(subStat));
              //}

              Dispatcher.InvokeAsync(() => dataGrid.ItemsSource = list);

              if (CurrentColumn != null)
              {
                Dispatcher.InvokeAsync(() => CurrentColumn.SortDirection = CurrentSortDirection);
              }
            }
          }
          catch (ArgumentNullException ane)
          {
            LOG.Error(ane);
          }
          catch (NullReferenceException nre)
          {
            LOG.Error(nre);
          }
          catch (ArgumentOutOfRangeException aro)
          {
            LOG.Error(aro);
          }
          finally
          {
            Running = false;
          }
        }, TaskScheduler.Default);
      }
    }
  }
}
