using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  public class CastTable : UserControl, IDisposable
  {
    protected bool CurrentShowAnySpellType = true;
    protected bool CurrentShowBeneficialSpells = true;
    protected bool CurrentShowCastSpells = true;
    protected bool CurrentShowDetSpells = true;
    protected bool CurrentShowReceivedSpells = true;
    protected bool CurrentShowSelfOnly;
    protected bool CurrentShowProcs;
    private const string BENEFICIAL_SPELLS_TYPE = "Beneficial Spells";
    private const string CAST_SPELLS_TYPE = "Cast Spells";
    private const string DET_SPELLS_TYPE = "Detrimental Spells";
    private const string PROC_SPELLS_TYPE = "Hide Procs";
    private const string RECEIVED_SPELLS_TYPE = "Received Spells";
    private const string SELF_SPELLS_TYPE = "Hide Spells Only You See";
    private SfDataGrid TheDataGrid;
    private Label TheTitleLabel;

    internal void InitCastTable(SfDataGrid dataGrid, Label titleLabel, ComboBox selectedOptions, ComboBox selectedSpellRestrictions)
    {
      TheDataGrid = dataGrid;
      TheTitleLabel = titleLabel;

      var list = new List<ComboBoxItemDetails>();
      list.Add(new ComboBoxItemDetails { IsChecked = true, Text = BENEFICIAL_SPELLS_TYPE });
      list.Add(new ComboBoxItemDetails { IsChecked = true, Text = CAST_SPELLS_TYPE });
      list.Add(new ComboBoxItemDetails { IsChecked = true, Text = DET_SPELLS_TYPE });
      list.Add(new ComboBoxItemDetails { IsChecked = true, Text = RECEIVED_SPELLS_TYPE });
      selectedOptions.ItemsSource = list;
      UIElementUtil.SetComboBoxTitle(selectedOptions, list.Sum(item => item.IsChecked ? 1 : 0), Resource.SPELL_TYPES_SELECTED);

      list = new List<ComboBoxItemDetails>();
      list.Add(new ComboBoxItemDetails { IsChecked = true, Text = SELF_SPELLS_TYPE });
      list.Add(new ComboBoxItemDetails { IsChecked = true, Text = PROC_SPELLS_TYPE });
      selectedSpellRestrictions.ItemsSource = list;
      UpdateRestrictionsTitle(selectedSpellRestrictions);

      (Application.Current.MainWindow as MainWindow).EventsThemeChanged += EventsThemeChanged;
    }

    protected void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(TheDataGrid, TheTitleLabel.Content.ToString());
    protected void CreateImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(TheDataGrid, TheTitleLabel);

    protected bool UpdateSelectedCastTypes(ComboBox selected)
    {
      var changed = false;
      var count = 0;
      CurrentShowAnySpellType = false;
      foreach (var item in selected.Items.Cast<ComboBoxItemDetails>())
      {
        if (item.IsChecked)
        {
          CurrentShowAnySpellType = true;
          count++;
        }

        switch (item.Text)
        {
          case BENEFICIAL_SPELLS_TYPE:
            if (CurrentShowBeneficialSpells != item.IsChecked)
            {
              changed = true;
            }

            CurrentShowBeneficialSpells = item.IsChecked;
            break;
          case CAST_SPELLS_TYPE:
            if (CurrentShowCastSpells != item.IsChecked)
            {
              changed = true;
            }

            CurrentShowCastSpells = item.IsChecked;
            break;
          case DET_SPELLS_TYPE:
            if (CurrentShowDetSpells != item.IsChecked)
            {
              changed = true;
            }

            CurrentShowDetSpells = item.IsChecked;
            break;
          case RECEIVED_SPELLS_TYPE:
            if (CurrentShowReceivedSpells != item.IsChecked)
            {
              changed = true;
            }

            CurrentShowReceivedSpells = item.IsChecked;
            break;
        }
      }

      UIElementUtil.SetComboBoxTitle(selected, count, Resource.SPELL_TYPES_SELECTED);
      return changed;
    }

    protected bool UpdateSelectedRestrictions(ComboBox selected)
    {
      var changed = false;
      var count = 0;
      foreach (var item in selected.Items.Cast<ComboBoxItemDetails>())
      {
        if (item.IsChecked)
        {
          count++;
        }

        switch (item.Text)
        {
          case PROC_SPELLS_TYPE:
            if (CurrentShowProcs != !item.IsChecked)
            {
              changed = true;
            }

            CurrentShowProcs = !item.IsChecked;
            break;
          case SELF_SPELLS_TYPE:
            if (CurrentShowSelfOnly != !item.IsChecked)
            {
              changed = true;
            }

            CurrentShowSelfOnly = !item.IsChecked;
            break;
        }
      }

      UpdateRestrictionsTitle(selected);
      return changed;
    }

    internal bool PassFilters(SpellData spellData, bool received)
    {
      // if nothing selected return nothing
      if (!CurrentShowAnySpellType)
      {
        return false;
      }

      // if i dont want to see procs then dont show any procs
      if (!CurrentShowProcs && spellData.Proc != 0)
      {
        return false;
      }

      // if i dont want to see self only received spells then never show them
      if (!CurrentShowSelfOnly && string.IsNullOrEmpty(spellData.LandsOnOther) && received)
      {
        return false;
      }

      // if i want beneficial spells but dont want detrimental then make sure it's beneficial
      if (CurrentShowBeneficialSpells && !CurrentShowDetSpells && !spellData.IsBeneficial)
      {
        return false;
      }

      // if i want detrimental spells but dont want beneficial then make sure it's detrimental
      if (CurrentShowDetSpells && !CurrentShowBeneficialSpells && spellData.IsBeneficial)
      {
        return false;
      }

      // if i want cast spells but not received spells then make sure it's not a received spell
      if (CurrentShowCastSpells && !CurrentShowReceivedSpells && received)
      {
        return false;
      }

      // if i want received spells but not cast spells then make sure it's not a cast spell
      if (CurrentShowReceivedSpells && !CurrentShowCastSpells && !received)
      {
        return false;
      }

      return true;
    }

    private void UpdateRestrictionsTitle(ComboBox selected)
    {
      if (selected.Items[0] is ComboBoxItemDetails onlyYou && selected.Items[1] is ComboBoxItemDetails procs)
      {
        string title;
        if (!procs.IsChecked && !onlyYou.IsChecked)
        {
          title = "Hide Nothing";
        }
        else if (procs.IsChecked && onlyYou.IsChecked)
        {
          title = "Hide Procs, Spells Only";
        }
        else if (procs.IsChecked)
        {
          title = "Hide Procs";
        }
        else
        {
          title = "Hide Spells Only";
        }

        onlyYou.SelectedText = title;
      }

      selected.SelectedIndex = -1;
      selected.SelectedItem = selected.Items[0];
    }

    private void EventsThemeChanged(string _)
    {
      if (TheDataGrid?.View != null)
      {
        // toggle styles to get them to re-render
        foreach (var column in TheDataGrid.Columns)
        {
          if (column.CellStyle != null)
          {
            var style = column.CellStyle;
            column.CellStyle = null;
            column.CellStyle = style;
          }
        }
      }
    }

    #region IDisposable Support
    private bool disposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        (Application.Current.MainWindow as MainWindow).EventsThemeChanged -= EventsThemeChanged;
        TheDataGrid?.Dispose();
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
