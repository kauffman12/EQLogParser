
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
    private bool CurrentShowSpellsChoice = true;
    private List<PlayerStats> PlayerStats = null;

    private readonly List<string> ChoicesList = new List<string>() { "Breakdown By Spell", "Breakdown By Healed" };

    public HealBreakdown()
    {
      InitializeComponent();
      InitBreakdownTable(dataGrid, selectedColumns);
      choicesList.ItemsSource = ChoicesList;
      choicesList.SelectedIndex = 0;
    }

    internal void Init(CombinedStats currentStats, List<PlayerStats> selectedStats)
    {
      titleLabel.Content = currentStats?.ShortTitle;
      PlayerStats = selectedStats;
      Display();
    }

    internal override void Display(List<PlayerStats> _ = null)
    {
      try
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

        dataGrid.ItemsSource = list;
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
