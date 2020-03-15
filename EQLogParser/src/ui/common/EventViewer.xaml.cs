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

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for EventViewer.xaml
  /// </summary>
  public partial class EventViewer : UserControl
  {
    private static bool Running = false;
    private static object CollectionLock = new object();
    private static string ZONE_EVENT = "Entered Area";
    private static string KILLSHOT_EVENT = "Kill Shot";
    private static string PLAYERSLAIN_EVENT = "Player Slain";
    private static string PLAYERKILL_EVENT = "Player Killing";
    private static string MEZBREAK_EVENT = "Mez Break";

    private ObservableCollection<EventRow> EventRows = new ObservableCollection<EventRow>();
    private ICollectionView EventView;

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
          result = CurrentShowMezBreaks && row.Event == MEZBREAK_EVENT || CurrentShowEnterZone && row.Event == ZONE_EVENT || CurrentShowKillShots && row.Event == KILLSHOT_EVENT ||
          CurrentShowPlayerKilling && row.Event == PLAYERKILL_EVENT || CurrentShowPlayerSlain && row.Event == PLAYERSLAIN_EVENT;
        }
        return result;
      });

      BindingOperations.EnableCollectionSynchronization(EventRows, CollectionLock);

      Load();
    }

    private void Load()
    {
      if (!Running)
      {
        Running = true;

        Task.Delay(75).ContinueWith(task =>
        {
          Helpers.SetBusy(true);

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
                if (!(PlayerManager.Instance.IsVerifiedPet(death.Killed) && !Helpers.IsPossiblePlayerName(death.Killed)))
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
                  else if (isTargetPlayer || (isActorNpc && !isTargetNpc && Helpers.IsPossiblePlayerName(death.Killed)))
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

          Helpers.SetBusy(false);

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

    private void OptionsChange(object sender, RoutedEventArgs e)
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

    private void RefreshMouseClick(object sender, MouseButtonEventArgs e)
    {
      Load();
    }
  }
}
