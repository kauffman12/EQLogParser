using System.Collections.Generic;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for HealTable.xaml
  /// </summary>
  public partial class HealBreakdown : BreakdownTable
  {
    private bool CurrentShowSpellsChoice = true;
    private List<PlayerStats> PlayerStats = null;

    private readonly List<string> ChoicesList = new List<string>() { "Breakdown By Spell", "Breakdown By Healed" };
    private readonly List<string> ReceivedChoicesList = new List<string>() { "Breakdown By Spell", "Breakdown By Healer" };

    public HealBreakdown()
    {
      InitializeComponent();
      InitBreakdownTable(titleLabel, dataGrid, selectedColumns);
    }

    internal void Init(CombinedStats currentStats, List<PlayerStats> selectedStats, bool received = false)
    {
      titleLabel.Content = currentStats?.ShortTitle;
      PlayerStats = selectedStats;
      choicesList.ItemsSource = received ? ReceivedChoicesList : ChoicesList;
      choicesList.SelectedIndex = 0;
      Display();
    }

    private void Display()
    {
      if (CurrentShowSpellsChoice)
      {
        dataGrid.ChildPropertyName = "SubStats";
      }
      else
      {
        dataGrid.ChildPropertyName = "SubStats2";
      }

      dataGrid.ItemsSource = null;
      dataGrid.ItemsSource = PlayerStats;
    }

    private void ListSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
      if (PlayerStats != null)
      {
        CurrentShowSpellsChoice = choicesList.SelectedIndex == 0;
        Display();
      }
    }
  }
}
