using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for HealTable.xaml
  /// </summary>
  public partial class HealBreakdown
  {
    private bool CurrentShowSpellsChoice = true;
    private List<PlayerStats> PlayerStats;
    private string Title;
    private string Setting;

    private readonly List<string> ChoicesList = new() { "Breakdown By Spell", "Breakdown By Healed" };
    private readonly List<string> ReceivedChoicesList = new() { "Breakdown By Spell", "Breakdown By Healer" };

    public HealBreakdown()
    {
      InitializeComponent();
      dataGrid.IsEnabled = false;
      UIElementUtil.SetEnabled(controlPanel.Children, false);
      InitBreakdownTable(titleLabel, dataGrid, selectedColumns);
    }

    internal void Init(CombinedStats currentStats, List<PlayerStats> selectedStats, bool received = false)
    {
      Title = currentStats?.ShortTitle;
      PlayerStats = selectedStats;
      Setting = (received ? "Received" : "") + "HealingBreakdownShowSpells";
      CurrentShowSpellsChoice = ConfigUtil.IfSet(Setting);
      choicesList.ItemsSource = received ? ReceivedChoicesList : ChoicesList;
      choicesList.SelectedIndex = CurrentShowSpellsChoice ? 0 : 1;
      Display();
    }

    private void Display()
    {
      Task.Delay(100).ContinueWith(task =>
      {
        Dispatcher.InvokeAsync(() =>
        {
          if (CurrentShowSpellsChoice)
          {
            dataGrid.ChildPropertyName = "SubStats";
          }
          else
          {
            dataGrid.ChildPropertyName = "SubStats2";
          }

          titleLabel.Content = Title;
          dataGrid.IsEnabled = true;
          UIElementUtil.SetEnabled(controlPanel.Children, true);
          dataGrid.ItemsSource = null;
          dataGrid.ItemsSource = PlayerStats;
        });
      });
    }

    private void ListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (PlayerStats != null)
      {
        CurrentShowSpellsChoice = choicesList.SelectedIndex == 0;
        titleLabel.Content = "Loading...";
        dataGrid.ItemsSource = null;
        dataGrid.IsEnabled = false;
        UIElementUtil.SetEnabled(controlPanel.Children, false);
        ConfigUtil.SetSetting(Setting, CurrentShowSpellsChoice.ToString());
        Display();
      }
    }
  }
}
