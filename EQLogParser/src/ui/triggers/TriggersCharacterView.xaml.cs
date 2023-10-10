using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Input;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TriggersCharacterView.xaml
  /// </summary>
  public partial class TriggersCharacterView : UserControl, IDisposable
  {
    internal event Action<TriggerCharacter> SelectedCharacterEvent;

    // public to be referenced from xaml?
    public TriggersCharacterView()
    {
      InitializeComponent();
      TriggerStateManager.Instance.TriggerConfigUpdateEvent += TriggerConfigUpdateEvent;
    }

    internal void SetConfig(TriggerConfig config) => dataGrid.ItemsSource = config.Characters;
    internal TriggerCharacter GetSelectedCharacter() => dataGrid?.SelectedItem as TriggerCharacter;

    private void TriggerConfigUpdateEvent(TriggerConfig config)
    {
      if (dataGrid != null)
      {
        var updatedSource = TriggerUtil.UpdateCharacterList(dataGrid.ItemsSource as List<TriggerCharacter>, config);
        if (updatedSource != null)
        {
          dataGrid.ItemsSource = updatedSource;
        }
      }
    }

    private void AddClick(object sender, System.Windows.RoutedEventArgs e)
    {
      var configWindow = new TriggerPlayerConfigWindow();
      configWindow.ShowDialog();
    }

    private void DeleteClick(object sender, System.Windows.RoutedEventArgs e)
    {
      if (dataGrid?.SelectedItem is TriggerCharacter character)
      {
        var msgDialog = new MessageWindow($"Are you sure? {character.Name} will be Deleted!",
          Resource.TRIGGER_CHARACTER_DELETE, MessageWindow.IconType.Warn, "Yes");
        msgDialog.ShowDialog();
        if (msgDialog.IsYes1Clicked)
        {
          TriggerStateManager.Instance.DeleteCharacter(character.Id);
        }
      }
    }

    private void ModifyClick(object sender, System.Windows.RoutedEventArgs e)
    {
      if (dataGrid?.SelectedItem is TriggerCharacter character)
      {
        var configWindow = new TriggerPlayerConfigWindow(character);
        configWindow.ShowDialog();
      }
    }

    private void CharacterSelectionChanged(object sender, GridSelectionChangedEventArgs e)
    {
      if (dataGrid?.SelectedItem is TriggerCharacter character)
      {
        modifyCharacter.IsEnabled = true;
        deleteCharacter.IsEnabled = true;
        SelectedCharacterEvent?.Invoke(character);
      }
      else
      {
        modifyCharacter.IsEnabled = false;
        deleteCharacter.IsEnabled = false;
        SelectedCharacterEvent?.Invoke(null);
      }
    }

    private void CharacterCheckboxPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
      if (sender is CheckBox checkBox)
      {
        checkBox.IsChecked = !checkBox.IsChecked;
        e.Handled = true;

        if (checkBox.DataContext is TriggerCharacter character)
        {
          character.IsEnabled = checkBox.IsChecked == true;
          TriggerStateManager.Instance.UpdateCharacter(character);
        }
      }
    }

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        TriggerStateManager.Instance.TriggerConfigUpdateEvent -= TriggerConfigUpdateEvent;
        disposedValue = true;
        dataGrid?.Dispose();
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
