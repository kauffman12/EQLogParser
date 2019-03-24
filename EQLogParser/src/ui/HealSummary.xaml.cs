
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for HealSummary.xaml
  /// </summary>
  public partial class HealSummary : SummaryTable
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private CombinedHealStats CurrentHealStats = null;
    private bool Ready = false;

    public HealSummary()
    {
      InitializeComponent();
      InitSummaryTable(title, dataGrid);

      // AE healing
      bool bValue;
      string value = DataManager.Instance.GetApplicationSetting("IncludeAEHealing");
      includeAEHealing.IsChecked = value == null || bool.TryParse(value, out bValue) && bValue;

      HealStatsManager.Instance.EventsGenerationStatus += Instance_EventsGenerationStatus;
      Ready = true;
    }

    ~HealSummary()
    {
      HealStatsManager.Instance.EventsGenerationStatus -= Instance_EventsGenerationStatus;
    }

    internal bool IsAEHealingEnabled()
    {
      return includeAEHealing.IsChecked.Value;
    }

    private void Instance_EventsGenerationStatus(object sender, StatsGenerationEvent e)
    {
      Dispatcher.InvokeAsync(() =>
      {
        switch (e.State)
        {
          case "STARTED":
            TheMainWindow.Busy(true);
            title.Content = "Calculating HPS...";
            dataGrid.ItemsSource = null;
            break;
          case "COMPLETED":
            CurrentHealStats = e.CombinedStats as CombinedHealStats;

            if (CurrentHealStats == null)
            {
              title.Content = NODATA_TABLE_LABEL;
            }
            else
            {
              includeAEHealing.IsEnabled = e.IsAEHealingAvailable;
              title.Content = CurrentHealStats.FullTitle;
              dataGrid.ItemsSource = new ObservableCollection<PlayerStats>(CurrentHealStats.StatsList);
            }

            TheMainWindow.Busy(false);
            UpdateDataGridMenuItems();
            break;
          case "NONPC":
            title.Content = DEFAULT_TABLE_LABEL;
            TheMainWindow.Busy(false);
            UpdateDataGridMenuItems();
            break;
        }
      });
    }

    protected void DataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      var selected = GetSelectedStats();
      HealStatsManager.Instance.FireSelectionEvent(selected);
      FireSelectionChangedEvent(selected);
      UpdateDataGridMenuItems();
    }

    protected override void ShowBreakdown(List<PlayerStats> selected)
    {
      if (selected.Count > 0)
      {
        var healTable = new HealBreakdown(TheMainWindow, CurrentHealStats);
        healTable.Show(selected);
        Helpers.OpenNewTab(TheMainWindow.dockSite, "healWindow", "Healing Breakdown", healTable);
      }
    }

    protected override void ShowSpellCasts(List<PlayerStats> selected)
    {
      if (selected.Count > 0)
      {
        var spellTable = new SpellCountTable(TheMainWindow, CurrentHealStats.ShortTitle);
        spellTable.ShowSpells(selected, CurrentHealStats);
        Helpers.OpenNewTab(TheMainWindow.dockSite, "spellCastsWindow", "Spell Counts", spellTable);
      }
    }

    private void UpdateDataGridMenuItems()
    {
      if (CurrentHealStats != null && CurrentHealStats.StatsList.Count > 0)
      {
        menuItemSelectAll.IsEnabled = dataGrid.SelectedItems.Count < dataGrid.Items.Count;
        menuItemUnselectAll.IsEnabled = dataGrid.SelectedItems.Count > 0;
        menuItemShowBreakdown.IsEnabled = menuItemShowSpellCasts.IsEnabled = true;
        UpdateClassMenuItems(menuItemShowBreakdown, dataGrid, CurrentHealStats.UniqueClasses);
        UpdateClassMenuItems(menuItemShowSpellCasts, dataGrid, CurrentHealStats.UniqueClasses);
      }
      else
      {
        menuItemUnselectAll.IsEnabled = menuItemSelectAll.IsEnabled = menuItemShowBreakdown.IsEnabled = menuItemShowSpellCasts.IsEnabled = false;
      }
    }

    private void IncludeAEHealingChanged(object sender, RoutedEventArgs e)
    {
      if (Ready)
      {
        bool isAEHealingEnabled = includeAEHealing.IsChecked.Value == true;
        DataManager.Instance.SetApplicationSetting("IncludeAEHealing", isAEHealingEnabled.ToString());

        if (CurrentHealStats != null && CurrentHealStats.RaidStats != null)
        {
          Task.Run(() => HealStatsManager.Instance.RebuildTotalStats(CurrentHealStats.RaidStats, isAEHealingEnabled));
        }
      }
    }
  }
}
