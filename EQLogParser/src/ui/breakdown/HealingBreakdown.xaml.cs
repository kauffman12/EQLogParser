using Syncfusion.UI.Xaml.TreeGrid;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  public partial class HealBreakdown
  {
    private readonly List<string> _choicesList = ["Breakdown By Spell", "Breakdown By Healed"];
    private readonly List<string> _receivedChoicesList = ["Breakdown By Spell", "Breakdown By Healer"];
    private readonly List<string> _topSpellsList = ["Showing Top 3 Spells", "Showing Top 5 Spells", "Showing Top 10 Spells", "Showing All Spells"];
    private readonly List<string> _topHealedList = ["Showing Top 3 Healed", "Showing Top 5 Healed", "Showing Top 10 Healed", "Showing All Healed"];
    private readonly List<string> _topHealerList = ["Showing Top 3 Healers", "Showing Top 5 Healers", "Showing Top 10 Healers", "Showing All Healers"];

    private bool _received;
    private bool _currentShowSpellsChoice = true;
    private int _currentShowTop;
    private List<PlayerStats> _playerStats;
    private string _setting;
    private string _title;

    public HealBreakdown()
    {
      InitializeComponent();
      InitBreakdownTable(titleLabel, dataGrid, selectedColumns);
    }

    internal void Init(CombinedStats currentStats, List<PlayerStats> selectedStats, bool received = false)
    {
      _received = received;
      _playerStats = selectedStats;
      _title = currentStats?.ShortTitle;
      _setting = (received ? "Received" : "") + "HealingBreakdownShowSpells";
      _currentShowSpellsChoice = ConfigUtil.IfSet(_setting);
      choicesList.ItemsSource = received ? _receivedChoicesList : _choicesList;
      choicesList.SelectedIndex = _currentShowSpellsChoice ? 0 : 1;
      UpdateOptionsList();
    }

    private void ListSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
      if (_playerStats != null && dataGrid?.View != null)
      {
        _currentShowSpellsChoice = choicesList.SelectedIndex == 0;
        titleLabel.Content = "Loading...";
        ConfigUtil.SetSetting(_setting, _currentShowSpellsChoice);
        UpdateOptionsList();
        dataGrid.View.Refresh();
        dataGrid.ExpandAllNodes();
        dataGrid.SortColumnDescriptions.Clear();
        titleLabel.Content = _title;
      }
    }

    private void DataGridRequestTreeItems(object sender, TreeGridRequestTreeItemsEventArgs e)
    {
      if (e.ParentItem == null)
      {
        e.ChildItems = _playerStats;
      }
      else if (e.ParentItem is PlayerStats { } stats)
      {
        var list = _currentShowSpellsChoice ? stats.SubStats : stats.SubStats2;
        var sorted = list.OrderByDescending(stats => stats.Total);

        if (_currentShowTop == 0)
        {
          e.ChildItems = sorted.Take(3);
        }
        else if (_currentShowTop == 1)
        {
          e.ChildItems = sorted.Take(5);
        }
        else if (_currentShowTop == 2)
        {
          e.ChildItems = sorted.Take(10);
        }
        else
        {
          e.ChildItems = sorted;
        }
      }
      else
      {
        e.ChildItems = new List<PlayerStats>();
      }
    }

    private void UpdateOptionsList()
    {
      List<string> options;
      var previousIndex = optionsList.SelectedIndex >= 0 ? optionsList.SelectedIndex : 0;

      if (_received)
      {
        options = _currentShowSpellsChoice ? _topSpellsList : _topHealerList;
      }
      else
      {
        options = _currentShowSpellsChoice ? _topSpellsList : _topHealedList;
      }

      if (options != null && options != optionsList.ItemsSource)
      {
        optionsList.ItemsSource = options;
        optionsList.SelectedIndex = previousIndex;
      }

      _currentShowTop = optionsList.SelectedIndex;
    }
  }
}
