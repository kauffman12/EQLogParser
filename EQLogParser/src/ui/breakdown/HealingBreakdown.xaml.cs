using System;
using System.Collections.Generic;
using System.Windows;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for HealTable.xaml
  /// </summary>
  public partial class HealBreakdown : BreakdownTable, IDisposable
  {
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

    private void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(dataGrid, titleLabel.Content.ToString());
    private void CreateImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(dataGrid, titleLabel);

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

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        dataGrid.Dispose();
        disposedValue = true;
      }
    }

    // This code added to correctly implement the disposable pattern.
    public void Dispose()
    {
      // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
      Dispose(true);
      // TODO: uncomment the following line if the finalizer is overridden above.
      GC.SuppressFinalize(this);
    }
    #endregion
  }
}
