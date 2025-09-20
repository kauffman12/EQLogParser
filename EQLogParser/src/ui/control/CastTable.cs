using log4net;
using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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
    private const string BeneficialSpellsType = "Beneficial Spells";
    private const string CastSpellsType = "Cast Spells";
    private const string DetSpellsType = "Detrimental Spells";
    private const string ProcSpellsType = "Hide Procs";
    private const string ReceivedSpellsType = "Received Spells";
    private const string SelfSpellsType = "Hide Spells Only You See";
    private SfDataGrid _theDataGrid;
    private Label _theTitleLabel;

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    internal void InitCastTable(SfDataGrid dataGrid, Label titleLabel, ComboBox selectedOptions, ComboBox selectedSpellRestrictions)
    {
      _theDataGrid = dataGrid;
      _theTitleLabel = titleLabel;

      var list = new List<ComboBoxItemDetails>
      {
        new() { IsChecked = true, Text = BeneficialSpellsType },
        new() { IsChecked = true, Text = CastSpellsType },
        new() { IsChecked = true, Text = DetSpellsType },
        new() { IsChecked = true, Text = ReceivedSpellsType }
      };

      selectedOptions.ItemsSource = list;
      UiElementUtil.SetComboBoxTitle(selectedOptions, Resource.SPELL_TYPES_SELECTED);

      list =
      [
        new ComboBoxItemDetails(isChecked: true, text: SelfSpellsType),
        new ComboBoxItemDetails { IsChecked = true, Text = ProcSpellsType }
      ];

      selectedSpellRestrictions.ItemsSource = list;
      UpdateRestrictionsTitle(selectedSpellRestrictions);
      MainActions.EventsThemeChanged += EventsThemeChanged;
    }

    protected void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(_theDataGrid, _theTitleLabel.Content.ToString());
    protected async void CreateImageClick(object sender, RoutedEventArgs e) => await DataGridUtil.CreateImageAsync(_theDataGrid, _theTitleLabel);
    protected async void CreateLargeImageClick(object sender, RoutedEventArgs e) => await DataGridUtil.CreateImageAsync(_theDataGrid, _theTitleLabel, true);

    protected void CopyBbCodeClick(object sender, RoutedEventArgs e)
    {
      try
      {
        var export = DataGridUtil.BuildExportData(_theDataGrid);
        var result = TextUtils.BuildBbCodeTable(export.Item1, export.Item2, _theTitleLabel.Content as string);
        Clipboard.SetDataObject(result);
      }
      catch (ArgumentNullException ane)
      {
        Clipboard.SetDataObject("EQLogParser Error: Failed to create BBCode\r\n");
        Log.Error(ane);
      }
      catch (ExternalException ex)
      {
        Log.Error(ex);
      }
    }

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
          case BeneficialSpellsType:
            if (CurrentShowBeneficialSpells != item.IsChecked)
            {
              changed = true;
            }

            CurrentShowBeneficialSpells = item.IsChecked;
            break;
          case CastSpellsType:
            if (CurrentShowCastSpells != item.IsChecked)
            {
              changed = true;
            }

            CurrentShowCastSpells = item.IsChecked;
            break;
          case DetSpellsType:
            if (CurrentShowDetSpells != item.IsChecked)
            {
              changed = true;
            }

            CurrentShowDetSpells = item.IsChecked;
            break;
          case ReceivedSpellsType:
            if (CurrentShowReceivedSpells != item.IsChecked)
            {
              changed = true;
            }

            CurrentShowReceivedSpells = item.IsChecked;
            break;
        }
      }

      UiElementUtil.SetComboBoxTitle(selected, Resource.SPELL_TYPES_SELECTED);
      return changed;
    }

    protected bool UpdateSelectedRestrictions(ComboBox selected)
    {
      var changed = false;
      foreach (var item in selected.Items.Cast<ComboBoxItemDetails>())
      {
        switch (item.Text)
        {
          case ProcSpellsType:
            if (CurrentShowProcs != !item.IsChecked)
            {
              changed = true;
            }

            CurrentShowProcs = !item.IsChecked;
            break;
          case SelfSpellsType:
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
      if (spellData == null)
      {
        return true;
      }

      // if nothing selected return nothing
      if (!CurrentShowAnySpellType)
      {
        return false;
      }

      // if i don't want to see procs then don't show any procs
      if (!CurrentShowProcs && spellData.Proc != 0)
      {
        return false;
      }

      // if i don't want to see self only received spells then never show them
      if (!CurrentShowSelfOnly && string.IsNullOrEmpty(spellData.LandsOnOther) && received)
      {
        return false;
      }

      // if i want beneficial spells but don't want detrimental then make sure it's beneficial
      if (CurrentShowBeneficialSpells && !CurrentShowDetSpells && !spellData.IsBeneficial)
      {
        return false;
      }

      // if i want detrimental spells but don't want beneficial then make sure it's detrimental
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

    private static void UpdateRestrictionsTitle(ComboBox selected)
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
      DataGridUtil.RefreshTableColumns(_theDataGrid);

      if (_theDataGrid?.View != null)
      {
        // toggle styles to get them to re-render
        foreach (var column in _theDataGrid.Columns)
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
    private bool _disposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!_disposedValue)
      {
        MainActions.EventsThemeChanged -= EventsThemeChanged;
        _theDataGrid?.Dispose();
        _disposedValue = true;
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
