
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TankingBreakdown.xaml
  /// </summary>
  public partial class TankingBreakdown : BreakdownTable
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private PlayerStats RaidStats;
    private static bool Running = false;

    public TankingBreakdown(CombinedStats currentStats)
    {
      InitializeComponent();
      RaidStats = currentStats.RaidStats;
    }

    public void Show(List<PlayerStats> selectedStats)
    {
      if (selectedStats != null)
      {
        Display(selectedStats);
      }
    }

    private new void Display(List<PlayerStats> selectedStats = null)
    {
      if (Running == false && RaidStats != null)
      {
        Running = true;
        Dispatcher.InvokeAsync(() => (Application.Current.MainWindow as MainWindow)?.Busy(true));

        Task.Delay(5).ContinueWith(task =>
        {
          try
          {
            ObservableCollection<PlayerSubStats> list = new ObservableCollection<PlayerSubStats>();

            // initial load
            if (selectedStats != null)
            {
              foreach (var playerStat in selectedStats.AsParallel().OrderByDescending(stats => GetSortValue(stats)))
              {
                Dispatcher.InvokeAsync(() =>
                {
                  list.Add(playerStat);
                  SortSubStats(playerStat.SubStats.Values.ToList()).ForEach(subStat => list.Add(subStat));
                });
              }

              Dispatcher.InvokeAsync(() => playerDamageDataGrid.ItemsSource = list);

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
            Dispatcher.InvokeAsync(() => (Application.Current.MainWindow as MainWindow)?.Busy(false));
            Running = false;
          }
        }, TaskScheduler.Default);
      }
    }
  }
}
