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
    private bool _currentShowSpellsChoice = true;
    private List<PlayerStats> _playerStats;
    private string _title;
    private string _setting;

    private readonly List<string> _choicesList = ["Breakdown By Spell", "Breakdown By Healed"];
    private readonly List<string> _receivedChoicesList = ["Breakdown By Spell", "Breakdown By Healer"];

    public HealBreakdown()
    {
      InitializeComponent();
      dataGrid.IsEnabled = false;
      UiElementUtil.SetEnabled(controlPanel.Children, false);
      InitBreakdownTable(titleLabel, dataGrid, selectedColumns);
    }

    internal void Init(CombinedStats currentStats, List<PlayerStats> selectedStats, bool received = false)
    {
      _title = currentStats?.ShortTitle;
      _playerStats = selectedStats;
      _setting = (received ? "Received" : "") + "HealingBreakdownShowSpells";
      _currentShowSpellsChoice = ConfigUtil.IfSet(_setting);
      choicesList.ItemsSource = received ? _receivedChoicesList : _choicesList;
      choicesList.SelectedIndex = _currentShowSpellsChoice ? 0 : 1;
      Display();
    }

    private void Display()
    {
      Task.Delay(100).ContinueWith(_ =>
      {
        Dispatcher.InvokeAsync(() =>
        {
          dataGrid.ChildPropertyName = _currentShowSpellsChoice ? "SubStats" : "SubStats2";
          titleLabel.Content = _title;
          dataGrid.IsEnabled = true;
          UiElementUtil.SetEnabled(controlPanel.Children, true);
          dataGrid.ItemsSource = null;
          dataGrid.ItemsSource = _playerStats;
        });
      });
    }

    private void ListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (_playerStats != null)
      {
        _currentShowSpellsChoice = choicesList.SelectedIndex == 0;
        titleLabel.Content = "Loading...";
        dataGrid.ItemsSource = null;
        dataGrid.IsEnabled = false;
        UiElementUtil.SetEnabled(controlPanel.Children, false);
        ConfigUtil.SetSetting(_setting, _currentShowSpellsChoice);
        Display();
      }
    }
  }
}
