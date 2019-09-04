
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TankingBreakdown.xaml
  /// </summary>
  public partial class TankingBreakdown : BreakdownTable
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    List<PlayerStats> PlayerStats;
    private PlayerStats RaidStats;
    private static bool Running = false;

    public TankingBreakdown(CombinedStats currentStats)
    {
      InitializeComponent();
      titleLabel.Content = currentStats?.ShortTitle;
      RaidStats = currentStats?.RaidStats;
    }

    public void Show(List<PlayerStats> selectedStats)
    {
      if (selectedStats != null)
      {
        PlayerStats = selectedStats;
        Display();
      }
    }

    protected override void Display(List<PlayerStats> _ = null)
    {
      if (Running == false && RaidStats != null)
      {
        Running = true;
        Task.Delay(10).ContinueWith(task =>
        {
          try
          {
            Helpers.SetBusy(true);

            if (PlayerStats != null)
            {
              ObservableCollection<PlayerSubStats> list = new ObservableCollection<PlayerSubStats>();

              foreach (var playerStat in PlayerStats.AsParallel().OrderByDescending(stats => GetSortValue(stats)))
              {
                list.Add(playerStat);
                SortSubStats(playerStat.SubStats.Values.ToList()).ForEach(subStat => list.Add(subStat));
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
            Helpers.SetBusy(false);
            Running = false;
          }
        }, TaskScheduler.Default);
      }
    }
  }
}
