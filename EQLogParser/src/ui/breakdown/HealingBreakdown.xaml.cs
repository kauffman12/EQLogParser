
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for HealTable.xaml
  /// </summary>
  public partial class HealBreakdown : BreakdownTable
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private static bool Running = false;
    private bool CurrentShowSpellsChoice = true;
    List<PlayerStats> PlayerStats = null;

    private List<string> ChoicesList = new List<string>() { "Breakdown By Spell", "Breakdown By Healed" };

    public HealBreakdown(CombinedStats currentStats)
    {
      InitializeComponent();
      titleLabel.Content = currentStats?.ShortTitle;
      choicesList.ItemsSource = ChoicesList;
      choicesList.SelectedIndex = 0;
    }

    public void Show(List<PlayerStats> selectedStats)
    {
      if (selectedStats != null)
      {
        PlayerStats = selectedStats;
        Display();
      }
    }

    private void Display()
    {
      if (Running == false)
      {
        Running = true;
        Dispatcher.InvokeAsync(() => (Application.Current.MainWindow as MainWindow)?.Busy(true));

        Task.Delay(20).ContinueWith(task =>
        {
          try
          {
            ObservableCollection<PlayerSubStats> list = new ObservableCollection<PlayerSubStats>();

            // initial load
            if (PlayerStats != null)
            {
              foreach (var playerStat in PlayerStats.AsParallel().OrderByDescending(stats => GetSortValue(stats)))
              {
                list.Add(playerStat);

                if (CurrentShowSpellsChoice)
                {
                  SortSubStats(playerStat.SubStats.Values.ToList()).ForEach(subStat => list.Add(subStat));
                }
                else
                {
                  SortSubStats(playerStat.SubStats2.Values.ToList()).ForEach(subStat => list.Add(subStat));
                }
              }
            }

            Dispatcher.InvokeAsync(() => dataGrid.ItemsSource = list, DispatcherPriority.Background);

            if (CurrentColumn != null)
            {
              Dispatcher.InvokeAsync(() => CurrentColumn.SortDirection = CurrentSortDirection);
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
