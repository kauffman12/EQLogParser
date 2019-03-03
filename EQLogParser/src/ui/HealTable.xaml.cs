
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for HealTable.xaml
  /// </summary>
  public partial class HealTable : BreakdownTable
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private CombinedHealStats CurrentStats;
    private static bool running = false;
    private bool CurrentShowSpellsChoice = true;
    private bool CurrentShowPets = false;
    List<PlayerStats> PlayerStats = null;

    private List<string> ChoicesList = new List<string>() { "Breakdown By Spell", "Breakdown By Healed" };

    public HealTable(MainWindow mainWindow, string title)
    {
      InitializeComponent();
      TheMainWindow = mainWindow;
      titleLabel.Content = title;
      choicesList.ItemsSource = ChoicesList;
      choicesList.SelectedIndex = 0;
    }

    public void Show(List<PlayerStats> selectedStats, CombinedHealStats currentStats)
    {
      if (selectedStats != null && currentStats != null)
      {
        CurrentStats = currentStats;
        PlayerStats = selectedStats;
        Display();
      }
    }

    private new void Display(List<PlayerStats> selectedStats = null)
    {
      if (running == false)
      {
        running = true;
        Dispatcher.InvokeAsync(() => TheMainWindow.Busy(true));

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
                  var filtered = playerStat.SubStats2.Values.Where(stat => CurrentShowPets || !DataManager.Instance.CheckNameForPet(stat.Name));
                  SortSubStats(filtered.ToList()).ForEach(subStat => list.Add(subStat));
                }
              }
            }

            Dispatcher.InvokeAsync(() => dataGrid.ItemsSource = list);

            if (CurrentColumn != null)
            {
              Dispatcher.InvokeAsync(() => CurrentColumn.SortDirection = CurrentSortDirection);
            }
          }
          catch (Exception ex)
          {
            LOG.Error(ex);
          }
          finally
          {
            Dispatcher.InvokeAsync(() => TheMainWindow.Busy(false));
            running = false;
          }
        });
      }
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (PlayerStats != null)
      {
        CurrentShowSpellsChoice = choicesList.SelectedIndex == 0;
        showPets.IsEnabled = choicesList.SelectedIndex != 0;
        Display();
      }
    }

    private void OptionsChange(object sender, System.Windows.RoutedEventArgs e)
    {
      if (PlayerStats != null)
      {
        CurrentShowPets = showPets.IsChecked.Value;
        Display();
      }
    }
  }
}
