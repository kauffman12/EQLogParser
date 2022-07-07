using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for ReceivedHealingBreakdown.xaml
  /// </summary>
  public partial class ReceivedHealingBreakdown : BreakdownTable
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private static bool Running = false;
    private bool CurrentShowSpellsChoice = true;
    private List<PlayerStats> PlayerStats = null;

    private readonly List<string> ChoicesList = new List<string>() { "Breakdown By Spell", "Breakdown By Healer" };

    internal ReceivedHealingBreakdown()
    {
      InitializeComponent();
      //InitBreakdownTable(dataGrid, selectedColumns);
      choicesList.ItemsSource = ChoicesList;
      choicesList.SelectedIndex = 0;
    }

    internal void Init(CombinedStats currentStats, List<PlayerStats> selectedStats)
    {
      titleLabel.Content = currentStats?.ShortTitle;
      PlayerStats = selectedStats;
      Display();
    }

    internal void Display()
    {
      if (Running == false)
      {
        Running = true;
        choicesList.IsEnabled = false;

        Task.Delay(10).ContinueWith(task =>
        {
          try
          {
            if (PlayerStats != null)
            {
              ObservableCollection<PlayerSubStats> list = new ObservableCollection<PlayerSubStats>();
              List<PlayerStats> receivedHealing = new List<PlayerStats>();

              PlayerStats.ForEach(selected =>
              {
                if (selected.SubStats2.Count > 0)
                {
                  receivedHealing.AddRange(selected.SubStats2.Cast<PlayerStats>().ToList());
                }
              });

              foreach (var playerStat in receivedHealing.AsParallel().OrderByDescending(stats => stats))
              {
                Dispatcher.InvokeAsync(() =>
                {
                  list.Add(playerStat);

                  // Spells are kept under SubStats2 and Healers under SubStats1. Both are children in the parent's SubStats2 as the 'receivedHealing' attribute
                  if (CurrentShowSpellsChoice)
                  {
                    //SortSubStats(playerStat.SubStats2.ToList()).ForEach(subStat => list.Add(subStat));
                  }
                  else
                  {
                    //SortSubStats(playerStat.SubStats.ToList()).ForEach(subStat => list.Add(subStat));
                  }
                });
              }

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
            Dispatcher.InvokeAsync(() => choicesList.IsEnabled = true);
            Running = false;
          }
        }, TaskScheduler.Default);
      }
    }

    private void ListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (PlayerStats != null)
      {
        CurrentShowSpellsChoice = choicesList.SelectedIndex == 0;
        Display();
      }
    }
  }
}
