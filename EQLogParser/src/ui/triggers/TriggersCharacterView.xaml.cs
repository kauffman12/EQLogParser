using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TriggersCharacterView.xaml
  /// </summary>
  public partial class TriggersCharacterView : IDisposable
  {
    internal event Action<List<TriggerCharacter>> SelectedCharacterEvent;
    private readonly DispatcherTimer _statusTimer;

    // public to be referenced from xaml?
    public TriggersCharacterView()
    {
      InitializeComponent();
      _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
      {
        Interval = new TimeSpan(0, 0, 0, 0, 2000),
      };

      _statusTimer.Tick += StatusTimerTick;
      _statusTimer.Start();
      TriggerStateManager.Instance.TriggerConfigUpdateEvent += TriggerConfigUpdateEvent;
    }

    internal void SetConfig(TriggerConfig config) => dataGrid.ItemsSource = config.Characters;
    internal TriggerCharacter GetSelectedCharacter() => dataGrid?.SelectedItem as TriggerCharacter;
    private void StatusTimerTick(object sender, EventArgs e) => UpdateStatus();

    private async void UpdateStatus()
    {
      if (dataGrid?.ItemsSource is List<TriggerCharacter> characters)
      {
        var dataChanged = false;
        foreach (var reader in await TriggerManager.Instance.GetLogReadersAsync())
        {
          if (reader.GetProcessor() is TriggerProcessor processor && characters.FirstOrDefault(item => item.Id == processor?.CurrentCharacterId) is { } character)
          {
            bool? update;
            if (reader.IsWaiting())
            {
              update = true;
            }
            else
            {
              var diff = DateTime.UtcNow.Ticks - processor.ActivityLastTicks;
              update = (diff / TimeSpan.TicksPerSecond > 120) ? null : false;
            }

            if (character.IsWaiting != update)
            {
              character.IsWaiting = update;
              dataChanged = true;
            }
          }
        }

        if (dataChanged)
        {
          RefreshData();
        }
      }
    }

    private void TriggerConfigUpdateEvent(TriggerConfig config)
    {
      if (dataGrid != null)
      {
        var updatedSource = TriggerUtil.UpdateCharacterList(dataGrid.ItemsSource as List<TriggerCharacter>, config);
        if (updatedSource != null)
        {
          dataGrid.ItemsSource = updatedSource;
        }
        else
        {
          RefreshData();
        }
      }
    }

    private void AddClick(object sender, RoutedEventArgs e)
    {
      var configWindow = new TriggerPlayerConfigWindow();
      configWindow.ShowDialog();
    }

    private void DeleteClick(object sender, RoutedEventArgs e)
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

    private void ModifyClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid?.SelectedItem is TriggerCharacter character)
      {
        var configWindow = new TriggerPlayerConfigWindow(character);
        configWindow.ShowDialog();
      }
    }

    private void CharacterSelectionChanged(object sender, GridSelectionChangedEventArgs e)
    {
      if (dataGrid?.SelectedItems?.Cast<TriggerCharacter>().ToList() is { Count: > 0 } characters)
      {
        modifyCharacter.IsEnabled = characters.Count == 1;
        deleteCharacter.IsEnabled = characters.Count == 1;
        SelectedCharacterEvent?.Invoke(characters);
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

          if (character.IsEnabled)
          {
            character.IsWaiting = true;
            RefreshData();
          }

          TriggerStateManager.Instance.UpdateCharacter(character);
        }
      }
    }

    private void RefreshData()
    {
      if (dataGrid?.View != null)
      {
        var selected = dataGrid.SelectedIndex;
        dataGrid.View.Refresh();
        dataGrid.SelectedIndex = selected;
      }
    }

    #region IDisposable Support
    private bool _disposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!_disposedValue)
      {
        _statusTimer.Stop();
        _statusTimer.Tick -= StatusTimerTick;
        TriggerStateManager.Instance.TriggerConfigUpdateEvent -= TriggerConfigUpdateEvent;
        _disposedValue = true;
        dataGrid?.Dispose();
      }
    }

    // This code added to correctly implement the disposable pattern.
    public void Dispose()
    {
      // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
      Dispose(true);
      GC.SuppressFinalize(this);
    }
    #endregion
  }
}
