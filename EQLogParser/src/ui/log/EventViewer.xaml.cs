using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for EventViewer.xaml
  /// </summary>
  public partial class EventViewer : UserControl, IDisposable
  {
    private static bool Running = false;
    private static object CollectionLock = new object();
    private const string ZONE_EVENT = "Entered Area";
    private const string KILLSHOT_EVENT = "Kill Shot";
    private const string PLAYERSLAIN_EVENT = "Player Slain";
    private const string PLAYERKILL_EVENT = "Player Killing";
    private const string MEZBREAK_EVENT = "Mez Break";

    private ObservableCollection<EventRow> EventRows = new ObservableCollection<EventRow>();
    private ICollectionView EventView;
    private DispatcherTimer FilterTimer;
    private bool CurrentShowMezBreaks = true;
    private bool CurrentShowEnterZone = true;
    private bool CurrentShowKillShots = true;
    private bool CurrentShowPlayerKilling = true;
    private bool CurrentShowPlayerSlain = true;

    public EventViewer()
    {
      InitializeComponent();

      dataGrid.ItemsSource = EventView = CollectionViewSource.GetDefaultView(EventRows);

      EventView.Filter = new Predicate<object>(obj =>
      {
        bool result = false;
        if (obj is EventRow row)
        {
          result = CurrentShowMezBreaks && row.Event == MEZBREAK_EVENT || CurrentShowEnterZone && row.Event == ZONE_EVENT || CurrentShowKillShots && 
            row.Event == KILLSHOT_EVENT || CurrentShowPlayerKilling && row.Event == PLAYERKILL_EVENT || CurrentShowPlayerSlain && row.Event == PLAYERSLAIN_EVENT;

          if (result && !string.IsNullOrEmpty(eventFilter.Text) && eventFilter.Text != Properties.Resources.EVENT_FILTER_TEXT)
          {
            if (eventFilterModifier.SelectedIndex == 0)
            {
              result = row.Actor?.IndexOf(eventFilter.Text, StringComparison.OrdinalIgnoreCase) > -1 || row.Target?.IndexOf(eventFilter.Text, StringComparison.OrdinalIgnoreCase) > -1;
            }
            else if (eventFilterModifier.SelectedIndex == 1)
            {
              result = row.Actor?.IndexOf(eventFilter.Text, StringComparison.OrdinalIgnoreCase) == -1 && row.Target?.IndexOf(eventFilter.Text, StringComparison.OrdinalIgnoreCase) == -1;
            }
            else if (eventFilterModifier.SelectedIndex == 2)
            {
              result = row.Actor?.Equals(eventFilter.Text, StringComparison.OrdinalIgnoreCase) == true || row.Target?.Equals(eventFilter.Text, StringComparison.OrdinalIgnoreCase) == true;
            }
          }
        }
        return result;
      });

      BindingOperations.EnableCollectionSynchronization(EventRows, CollectionLock);
      (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete += EventViewer_EventsLogLoadingComplete;

      eventFilter.Text = Properties.Resources.EVENT_FILTER_TEXT;
      FilterTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 500) };
      FilterTimer.Tick += (sender, e) =>
      {
        FilterTimer.Stop();
        UpdateUI(true);
      };

      Load();
    }

    private void Load()
    {
      if (!Running)
      {
        Running = true;

        Task.Delay(75).ContinueWith(task =>
        {
          lock (CollectionLock)
          {
            EventRows.Clear();
          }

          var rows = new List<EventRow>();
          DataManager.Instance.GetDeathsDuring(0, double.MaxValue).ForEach(block =>
          {
            block.Actions.ForEach(action =>
            {
              if (action is DeathRecord death)
              {
                if (!(PlayerManager.Instance.IsVerifiedPet(death.Killed) && !PlayerManager.Instance.IsPossiblePlayerName(death.Killed)))
                {
                  var isActorNpc = DataManager.Instance.IsLifetimeNpc(death.Killer) || DataManager.Instance.IsKnownNpc(death.Killer);
                  var isTargetNpc = DataManager.Instance.IsLifetimeNpc(death.Killed) || DataManager.Instance.IsKnownNpc(death.Killed);
                  var isActorPlayer = PlayerManager.Instance.IsPetOrPlayerOrSpell(death.Killer);
                  var isTargetPlayer = PlayerManager.Instance.IsPetOrPlayer(death.Killed);

                  string text = KILLSHOT_EVENT;
                  if (isTargetPlayer && isActorPlayer)
                  {
                    text = PLAYERKILL_EVENT;
                  }
                  else if (isTargetPlayer || (isActorNpc && !isTargetNpc && PlayerManager.Instance.IsPossiblePlayerName(death.Killed)))
                  {
                    text = PLAYERSLAIN_EVENT;
                  }

                  rows.Add(new EventRow() { Time = block.BeginTime, Actor = death.Killer, Target = death.Killed, Event = text });
                }
              }
            });
          });

          DataManager.Instance.GetMiscDuring(0, double.MaxValue).ForEach(block =>
          {
            block.Actions.ForEach(action =>
            {
              if (action is MezBreakRecord mezBreak)
              {
                rows.Add(new EventRow() { Time = block.BeginTime, Actor = mezBreak.Breaker, Target = mezBreak.Awakened, Event = MEZBREAK_EVENT });
              }
              else if (action is ZoneRecord zone)
              {
                rows.Add(new EventRow() { Time = block.BeginTime, Actor = ConfigUtil.PlayerName, Event = ZONE_EVENT, Target = zone.Zone });
              }
            });
          });

          rows.Sort((a, b) => a.Time.CompareTo(b.Time));
          rows.ForEach(row => EventRows.Add(row));

          Dispatcher.InvokeAsync(() =>
          {
            UpdateUI(rows.Count > 0);
            Running = false;
          });

        }, TaskScheduler.Default);
      }
    }

    private void UpdateUI(bool enable)
    {
      (dataGrid.ItemsSource as ICollectionView)?.Refresh();
      titleLabel.Content = dataGrid.Items.Count == 0 ? "No Events Found" : dataGrid.Items.Count + " Events Found";

      if (showMezBreaks.IsEnabled != enable)
      {
        showMezBreaks.IsEnabled = showEnterZone.IsEnabled = showKillShots.IsEnabled = showPlayerKillsPlayer.IsEnabled = showPlayerSlain.IsEnabled = enable;
      }
    }

    private void LoadingRow(object sender, DataGridRowEventArgs e)
    {
      // set header count
      if (e.Row != null)
      {
        e.Row.Header = (e.Row.GetIndex() + 1).ToString(CultureInfo.CurrentCulture);
      }
    }

    private void OptionsChange(object sender, EventArgs e)
    {
      if (dataGrid?.ItemsSource != null)
      {
        CurrentShowMezBreaks = showMezBreaks.IsChecked.Value;
        CurrentShowEnterZone = showEnterZone.IsChecked.Value;
        CurrentShowKillShots = showKillShots.IsChecked.Value;
        CurrentShowPlayerKilling = showPlayerKillsPlayer.IsChecked.Value;
        CurrentShowPlayerSlain = showPlayerSlain.IsChecked.Value;
        UpdateUI(true);
      }
    }

    private void Filter_KeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.Escape)
      {
        eventFilter.Text = Properties.Resources.EVENT_FILTER_TEXT;
        eventFilter.FontStyle = FontStyles.Italic;
        dataGrid.Focus();
      }
    }

    private void Filter_GotFocus(object sender, RoutedEventArgs e)
    {
      if (eventFilter.Text == Properties.Resources.EVENT_FILTER_TEXT)
      {
        eventFilter.Text = "";
        eventFilter.FontStyle = FontStyles.Normal;
      }
    }

    private void Filter_LostFocus(object sender, RoutedEventArgs e)
    {
      if (string.IsNullOrEmpty(eventFilter.Text))
      {
        eventFilter.Text = Properties.Resources.EVENT_FILTER_TEXT;
        eventFilter.FontStyle = FontStyles.Italic;
      }
    }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
      FilterTimer?.Stop();
      FilterTimer?.Start();
    }

    private void EventViewer_EventsLogLoadingComplete(object sender, bool e) => Load();
    private void RefreshMouseClick(object sender, MouseButtonEventArgs e) => Load();

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        if (disposing)
        {
          // TODO: dispose managed state (managed objects).
        }

        (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete -= EventViewer_EventsLogLoadingComplete;
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
