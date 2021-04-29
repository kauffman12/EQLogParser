
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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
    private List<PlayerStats> PlayerStats = null;

    private List<string> ChoicesList = new List<string>() { "Breakdown By Spell", "Breakdown By Healed" };

    internal HealBreakdown(CombinedStats currentStats)
    {
      InitializeComponent();
      InitBreakdownTable(dataGrid, selectedColumns);
      titleLabel.Content = currentStats?.ShortTitle;
      choicesList.ItemsSource = ChoicesList;
      choicesList.SelectedIndex = 0;
    }

    internal void Show(List<PlayerStats> selectedStats)
    {
      if (selectedStats != null)
      {
        PlayerStats = selectedStats;
        Display();
      }
    }

    override internal void Display(List<PlayerStats> _ = null)
    {
      if (Running == false && PlayerStats != null)
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
