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
  /// Interaction logic for EventViewer.xaml
  /// </summary>
  public partial class EventViewer : UserControl, IDisposable
  {
    private const string ZONE_EVENT = "Entered Area";
    private const string KILLSHOT_EVENT = "Kill Shot";
    private const string PLAYERSLAIN_EVENT = "Player Slain";
    private const string PLAYERKILL_EVENT = "Player Killing";
    private const string MEZBREAK_EVENT = "Mez Break";

    private readonly DispatcherTimer FilterTimer;
    private bool CurrentShowMezBreaks = true;
    private bool CurrentShowEnterZone = true;
    private bool CurrentShowKillShots = true;
    private bool CurrentShowPlayerKilling = true;
    private bool CurrentShowPlayerSlain = true;
    private int CurrentFilterModifier;
    private string CurrentFilterText = Resource.EVENT_FILTER_TEXT;

    public EventViewer()
    {
      InitializeComponent();

      MainActions.EventsLogLoadingComplete += EventsLogLoadingComplete;

      var list = new List<ComboBoxItemDetails>
      {
        new() { IsChecked = true, Text = ZONE_EVENT },
        new() { IsChecked = true, Text = KILLSHOT_EVENT },
        new() { IsChecked = true, Text = MEZBREAK_EVENT },
        new() { IsChecked = true, Text = PLAYERKILL_EVENT },
        new() { IsChecked = true, Text = PLAYERSLAIN_EVENT }
      };

      selectedOptions.ItemsSource = list;
      UIElementUtil.SetComboBoxTitle(selectedOptions, list.Count, Resource.EVENT_TYPES_SELECTED);
      DataGridUtil.UpdateTableMargin(dataGrid);
      MainActions.EventsThemeChanged += EventsThemeChanged;

      eventFilter.Text = Resource.EVENT_FILTER_TEXT;
      FilterTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 750) };
      FilterTimer.Tick += (_, _) =>
      {
        FilterTimer.Stop();
        if (CurrentFilterText != eventFilter.Text)
        {
          CurrentFilterText = eventFilter.Text;
          UpdateTitleAndRefresh();
        }
      };

      Load();
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(dataGrid, titleLabel.Content.ToString());
    private void CreateImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(dataGrid, titleLabel);
    private void RefreshClick(object sender, RoutedEventArgs e) => Load();
    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);

    private void Load()
    {
      var rows = new List<EventRow>();
      DataManager.Instance.GetDeathsDuring(0, double.MaxValue).ForEach(block =>
      {
        block.Actions.ForEach(action =>
        {
          if (action is DeathRecord death)
          {
            if (!(PlayerManager.Instance.IsVerifiedPet(death.Killed) && !PlayerManager.IsPossiblePlayerName(death.Killed)))
            {
              var isActorNpc = DataManager.Instance.IsLifetimeNpc(death.Killer) || DataManager.Instance.IsKnownNpc(death.Killer);
              var isTargetNpc = DataManager.Instance.IsLifetimeNpc(death.Killed) || DataManager.Instance.IsKnownNpc(death.Killed);
              var isActorPlayer = PlayerManager.Instance.IsPetOrPlayerOrSpell(death.Killer);
              var isTargetPlayer = PlayerManager.Instance.IsPetOrPlayerOrMerc(death.Killed);

              var text = KILLSHOT_EVENT;
              if (isTargetPlayer && isActorPlayer)
              {
                text = PLAYERKILL_EVENT;
              }
              else if (isTargetPlayer || (isActorNpc && !isTargetNpc && PlayerManager.IsPossiblePlayerName(death.Killed)))
              {
                text = PLAYERSLAIN_EVENT;
              }

              rows.Add(new EventRow { Time = block.BeginTime, Actor = death.Killer, Target = death.Killed, Event = text });
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
            rows.Add(new EventRow { Time = block.BeginTime, Actor = mezBreak.Breaker, Target = mezBreak.Awakened, Event = MEZBREAK_EVENT });
          }
          else if (action is ZoneRecord zone)
          {
            rows.Add(new EventRow { Time = block.BeginTime, Actor = ConfigUtil.PlayerName, Event = ZONE_EVENT, Target = zone.Zone });
          }
        });
      });

      dataGrid.ItemsSource = rows;
      UpdateTitleAndRefresh();
    }

    private void UpdateTitleAndRefresh()
    {
      dataGrid?.View?.RefreshFilter();
      var count = dataGrid?.View != null ? dataGrid.View.Records.Count : 0;
      titleLabel.Content = count == 0 ? "No Events Found" : count + " Events Found";
    }

    private void ItemsSourceChanged(object sender, GridItemsSourceChangedEventArgs e)
    {
      dataGrid.View.Filter = obj =>
      {
        var result = false;
        if (obj is EventRow row)
        {
          result = (CurrentShowMezBreaks && row.Event == MEZBREAK_EVENT) || (CurrentShowEnterZone && row.Event == ZONE_EVENT) || (CurrentShowKillShots &&
            row.Event == KILLSHOT_EVENT) || (CurrentShowPlayerKilling && row.Event == PLAYERKILL_EVENT) || (CurrentShowPlayerSlain && row.Event == PLAYERSLAIN_EVENT);

          if (result && !string.IsNullOrEmpty(CurrentFilterText) && CurrentFilterText != Resource.EVENT_FILTER_TEXT)
          {
            if (CurrentFilterModifier == 0)
            {
              result = row.Actor?.IndexOf(CurrentFilterText, StringComparison.OrdinalIgnoreCase) > -1 ||
                       row.Target?.IndexOf(CurrentFilterText, StringComparison.OrdinalIgnoreCase) > -1;
            }
            else if (CurrentFilterModifier == 1)
            {
              result = row.Actor?.IndexOf(CurrentFilterText, StringComparison.OrdinalIgnoreCase) == -1 &&
                       row.Target?.IndexOf(CurrentFilterText, StringComparison.OrdinalIgnoreCase) == -1;
            }
            else if (CurrentFilterModifier == 2)
            {
              result = row.Actor?.Equals(CurrentFilterText, StringComparison.OrdinalIgnoreCase) == true ||
                       row.Target?.Equals(CurrentFilterText, StringComparison.OrdinalIgnoreCase) == true;
            }
          }
        }
        return result;
      };

      UpdateTitleAndRefresh();
    }

    private void FilterOptionChange(object sender, EventArgs e)
    {
      if (eventFilterModifier?.SelectedIndex > -1 && eventFilterModifier.SelectedIndex != CurrentFilterModifier)
      {
        CurrentFilterModifier = eventFilterModifier.SelectedIndex;
        UpdateTitleAndRefresh();
      }
    }

    private void SelectOptions(object sender, EventArgs e)
    {
      if (selectedOptions?.Items != null)
      {
        var count = 0;
        foreach (var item in selectedOptions.Items.Cast<ComboBoxItemDetails>())
        {
          switch (item.Text)
          {
            case ZONE_EVENT:
              CurrentShowEnterZone = item.IsChecked;
              count += item.IsChecked ? 1 : 0;
              break;
            case MEZBREAK_EVENT:
              CurrentShowMezBreaks = item.IsChecked;
              count += item.IsChecked ? 1 : 0;
              break;
            case PLAYERKILL_EVENT:
              CurrentShowPlayerKilling = item.IsChecked;
              count += item.IsChecked ? 1 : 0;
              break;
            case PLAYERSLAIN_EVENT:
              CurrentShowPlayerSlain = item.IsChecked;
              count += item.IsChecked ? 1 : 0;
              break;
            case KILLSHOT_EVENT:
              CurrentShowKillShots = item.IsChecked;
              count += item.IsChecked ? 1 : 0;
              break;
          }
        }

        UIElementUtil.SetComboBoxTitle(selectedOptions, count, Resource.EVENT_TYPES_SELECTED);
        UpdateTitleAndRefresh();
      }
    }

    private void FilterKeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.Escape)
      {
        eventFilter.Text = Resource.EVENT_FILTER_TEXT;
        eventFilter.FontStyle = FontStyles.Italic;
        dataGrid.Focus();
      }
    }

    private void FilterGotFocus(object sender, RoutedEventArgs e)
    {
      if (eventFilter.Text == Resource.EVENT_FILTER_TEXT)
      {
        eventFilter.Text = "";
        eventFilter.FontStyle = FontStyles.Normal;
      }
    }

    private void FilterLostFocus(object sender, RoutedEventArgs e)
    {
      if (string.IsNullOrEmpty(eventFilter.Text))
      {
        eventFilter.Text = Resource.EVENT_FILTER_TEXT;
        eventFilter.FontStyle = FontStyles.Italic;
      }
    }

    private void FilterTextChanged(object sender, TextChangedEventArgs e)
    {
      FilterTimer?.Stop();
      FilterTimer?.Start();
    }

    private void EventsLogLoadingComplete(string _) => Load();

    #region IDisposable Support
    private bool disposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        MainActions.EventsThemeChanged -= EventsThemeChanged;
        MainActions.EventsLogLoadingComplete -= EventsLogLoadingComplete;
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

  internal class EventRow
  {
    public double Time { get; set; }
    public string Actor { get; set; }
    public string Target { get; set; }
    public string Event { get; set; }
  }
}
