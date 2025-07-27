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
  public partial class TriggersCharacterView : IDisposable
  {
    internal event Action<List<TriggerCharacter>> SelectedCharacterEvent;
    private readonly DispatcherTimer _statusTimer;
    private TriggerConfig _lastConfig;

    // public to be referenced from xaml?
    public TriggersCharacterView()
    {
      InitializeComponent();
      _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
      {
        Interval = new TimeSpan(0, 0, 0, 2, 500),
      };

      _statusTimer.Tick += StatusTimerTick;
      TriggerStateManager.Instance.TriggerConfigUpdateEvent += TriggerConfigUpdateEvent;
      MainActions.EventsWindowStateChanged += EventsWindowStateChanged;
    }

    internal void SetConfig(TriggerConfig config)
    {
      dataGrid.ItemsSource = config.Characters;

      if (config.IsAdvanced)
      {
        _statusTimer.Start();
      }

      _lastConfig = config;
    }

    internal TriggerCharacter GetSelectedCharacter() => dataGrid?.SelectedItem as TriggerCharacter;
    private void StatusTimerTick(object sender, EventArgs e) => UpdateStatus();

    private async void EventsWindowStateChanged(WindowState newState)
    {
      await Dispatcher.InvokeAsync(() =>
      {
        if (newState == WindowState.Minimized)
        {
          _statusTimer?.Stop();

          if (dataGrid != null)
          {
            dataGrid.Visibility = Visibility.Collapsed;
          }
        }
        else
        {
          if (dataGrid != null)
          {
            dataGrid.Visibility = Visibility.Visible;
          }

          if (_lastConfig?.IsAdvanced == true && !_statusTimer.IsEnabled)
          {
            _statusTimer.Start();
          }
        }
      });
    }

    private async void UpdateStatus()
    {
      if (dataGrid?.ItemsSource is List<TriggerCharacter> characters)
      {
        var dataChanged = false;
        foreach (var reader in await TriggerManager.Instance.GetLogReadersAsync())
        {
          if (reader.GetProcessor() is TriggerProcessor processor && characters.FirstOrDefault(item => item.Id == processor.CurrentCharacterId) is { } character)
          {
            bool? update;
            if (reader.IsWaiting())
            {
              update = true;
            }
            else
            {
              var diff = DateTime.Now.Ticks - processor.GetActivityLastTicks();
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
      if (config.IsAdvanced)
      {
        if (dataGrid?.ItemsSource is List<TriggerCharacter> list)
        {
          if (TriggerUtil.UpdateCharacterList(list, config) is { } updatedSource)
          {
            dataGrid.ItemsSource = updatedSource;
          }
          else
          {
            RefreshData();
          }
        }

        if (!_statusTimer.IsEnabled)
        {
          _statusTimer.Start();
        }
      }
      else
      {
        _statusTimer.Stop();
      }

      _lastConfig = config;
    }

    private void AddClick(object sender, RoutedEventArgs e)
    {
      var configWindow = new TriggerPlayerConfigWindow();
      configWindow.ShowDialog();
    }

    private async void DeleteClick(object sender, RoutedEventArgs e)
    {
      if (dataGrid?.SelectedItem is TriggerCharacter character)
      {
        var msgDialog = new MessageWindow($"Are you sure? {character.Name} will be Deleted!",
          Resource.TRIGGER_CHARACTER_DELETE, MessageWindow.IconType.Warn, "Yes");
        msgDialog.ShowDialog();
        if (msgDialog.IsYes1Clicked)
        {
          await TriggerStateManager.Instance.DeleteCharacter(character.Id);
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

    private async void CharacterCheckboxPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
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

          await TriggerStateManager.Instance.UpdateCharacter(character);
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
        MainActions.EventsWindowStateChanged -= EventsWindowStateChanged;
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
