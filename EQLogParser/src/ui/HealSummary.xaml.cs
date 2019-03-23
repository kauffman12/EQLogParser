
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

      Ready = true;
    }

    internal void UpdateStats(List<NonPlayer> npcList, bool rebuild = false)
    {
      if (UpdateStatsTask == null && (rebuild || (npcList != null && npcList.Count > 0)))
      {
        TheMainWindow.Busy(true);
        title.Content = "Calculating HPS...";

        string name = npcList?.First().Name;
        bool showAE = includeAEHealing.IsChecked.Value;
        UpdateStatsTask = new Task(() =>
        {
          try
          {
            if (rebuild)
            {
              CurrentHealStats = HealStatsBuilder.ComputeHealStats(CurrentHealStats.RaidStats, showAE);
            }
            else
            {
              CurrentHealStats = HealStatsBuilder.BuildTotalStats(name, npcList, showAE);
            }
 
            Dispatcher.InvokeAsync((() =>
            {
              if (CurrentHealStats == null)
              {
                title.Content = NODATA_TABLE_LABEL;
                dataGrid.ItemsSource = null;
              }
              else
              {
                includeAEHealing.IsEnabled = HealStatsBuilder.IsAEHealingAvailable;
                title.Content = CurrentHealStats.FullTitle;
                dataGrid.ItemsSource = new ObservableCollection<PlayerStats>(CurrentHealStats.StatsList);
              }

              UpdateDataGridMenuItems();
            }));
          }
          catch (Exception ex)
          {
            LOG.Error(ex);
          }
          finally
          {
            Dispatcher.InvokeAsync(() =>
            {
              UpdateStatsTask = null;
              TheMainWindow.Busy(false);
            });
          }
        });

        UpdateStatsTask.Start();
      }
      else if (dataGrid.ItemsSource is ObservableCollection<PlayerStats> damageList)
      {
        CurrentHealStats = null;
        dataGrid.ItemsSource = null;
        title.Content = DEFAULT_TABLE_LABEL;
        UpdateDataGridMenuItems();
      }
    }

    protected void DataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      var selected = GetSelectedStats();
      HealStatsBuilder.FireSelectionEvent(selected);
      FireSelectionChangedEvent(selected);
      UpdateDataGridMenuItems();
    }

    protected override void ShowBreakdown(List<PlayerStats> selected)
    {
      if (selected.Count > 0)
      {
        var healTable = new HealBreakdown(TheMainWindow, CurrentHealStats.ShortTitle);
        healTable.Show(selected, CurrentHealStats);
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
        UpdateStats(null, true);
        DataManager.Instance.SetApplicationSetting("IncludeAEHealing", includeAEHealing.IsChecked.Value.ToString());
      }
    }
  }
}
