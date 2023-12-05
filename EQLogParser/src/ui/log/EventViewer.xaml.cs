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
  public partial class EventViewer : IDocumentContent
  {
    private const string ZoneEvent = "Entered Area";
    private const string KillshotEvent = "Kill Shot";
    private const string PlayerslainEvent = "Player Slain";
    private const string PlayerkillEvent = "Player Killing";
    private const string MezbreakEvent = "Mez Break";

    private readonly DispatcherTimer _filterTimer;
    private bool _currentShowMezBreaks = true;
    private bool _currentShowEnterZone = true;
    private bool _currentShowKillShots = true;
    private bool _currentShowPlayerKilling = true;
    private bool _currentShowPlayerSlain = true;
    private int _currentFilterModifier;
    private string _currentFilterText = Resource.EVENT_FILTER_TEXT;
    private bool _ready;

    public EventViewer()
    {
      InitializeComponent();

      var list = new List<ComboBoxItemDetails>
      {
        new() { IsChecked = true, Text = ZoneEvent },
        new() { IsChecked = true, Text = KillshotEvent },
        new() { IsChecked = true, Text = MezbreakEvent },
        new() { IsChecked = true, Text = PlayerkillEvent },
        new() { IsChecked = true, Text = PlayerslainEvent }
      };

      selectedOptions.ItemsSource = list;
      UiElementUtil.SetComboBoxTitle(selectedOptions, list.Count, Resource.EVENT_TYPES_SELECTED);
      DataGridUtil.UpdateTableMargin(dataGrid);
      MainActions.EventsThemeChanged += EventsThemeChanged;

      eventFilter.Text = Resource.EVENT_FILTER_TEXT;
      _filterTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 750) };
      _filterTimer.Tick += (_, _) =>
      {
        _filterTimer.Stop();
        if (_currentFilterText != eventFilter.Text)
        {
          _currentFilterText = eventFilter.Text;
          UpdateTitleAndRefresh();
        }
      };
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(dataGrid, titleLabel.Content.ToString());
    private void CreateImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(dataGrid, titleLabel);
    private void RefreshClick(object sender, RoutedEventArgs e) => Load();
    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);

    private void Load()
    {
      var rows = new List<EventRow>();
      foreach (var (beginTime, record) in RecordManager.Instance.GetAllDeaths())
      {
        if (!(PlayerManager.Instance.IsVerifiedPet(record.Killed) && !PlayerManager.IsPossiblePlayerName(record.Killed)))
        {
          var isActorNpc = DataManager.Instance.IsLifetimeNpc(record.Killer) || DataManager.Instance.IsKnownNpc(record.Killer);
          var isTargetNpc = DataManager.Instance.IsLifetimeNpc(record.Killed) || DataManager.Instance.IsKnownNpc(record.Killed);
          var isActorPlayer = PlayerManager.Instance.IsPetOrPlayerOrSpell(record.Killer);
          var isTargetPlayer = PlayerManager.Instance.IsPetOrPlayerOrMerc(record.Killed);

          var text = KillshotEvent;
          if (isTargetPlayer && isActorPlayer)
          {
            text = PlayerkillEvent;
          }
          else if (isTargetPlayer || (isActorNpc && !isTargetNpc && PlayerManager.IsPossiblePlayerName(record.Killed)))
          {
            text = PlayerslainEvent;
          }

          rows.Add(new EventRow { Time = beginTime, Actor = record.Killer, Target = record.Killed, Event = text });
        }
      }

      foreach (var (beginTime, record) in RecordManager.Instance.GetAllMezBreaks())
      {
        rows.Add(new EventRow { Time = beginTime, Actor = record.Breaker, Target = record.Awakened, Event = MezbreakEvent });
      }

      foreach (var (beginTime, record) in RecordManager.Instance.GetAllZoning())
      {
        rows.Add(new EventRow { Time = beginTime, Actor = ConfigUtil.PlayerName, Event = ZoneEvent, Target = record.Zone });
      }

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
      if (dataGrid.ItemsSource != null)
      {
        dataGrid.View.Filter = obj =>
        {
          var result = false;
          if (obj is EventRow row)
          {
            result = (_currentShowMezBreaks && row.Event == MezbreakEvent) || (_currentShowEnterZone && row.Event == ZoneEvent) || (_currentShowKillShots &&
              row.Event == KillshotEvent) || (_currentShowPlayerKilling && row.Event == PlayerkillEvent) || (_currentShowPlayerSlain && row.Event == PlayerslainEvent);

            if (result && !string.IsNullOrEmpty(_currentFilterText) && _currentFilterText != Resource.EVENT_FILTER_TEXT)
            {
              if (_currentFilterModifier == 0)
              {
                result = row.Actor?.IndexOf(_currentFilterText, StringComparison.OrdinalIgnoreCase) > -1 ||
                         row.Target?.IndexOf(_currentFilterText, StringComparison.OrdinalIgnoreCase) > -1;
              }
              else if (_currentFilterModifier == 1)
              {
                result = row.Actor?.IndexOf(_currentFilterText, StringComparison.OrdinalIgnoreCase) == -1 &&
                         row.Target?.IndexOf(_currentFilterText, StringComparison.OrdinalIgnoreCase) == -1;
              }
              else if (_currentFilterModifier == 2)
              {
                result = row.Actor?.Equals(_currentFilterText, StringComparison.OrdinalIgnoreCase) == true ||
                         row.Target?.Equals(_currentFilterText, StringComparison.OrdinalIgnoreCase) == true;
              }
            }
          }
          return result;
        };

        UpdateTitleAndRefresh();
      }
    }

    private void FilterOptionChange(object sender, EventArgs e)
    {
      if (eventFilterModifier?.SelectedIndex > -1 && eventFilterModifier.SelectedIndex != _currentFilterModifier)
      {
        _currentFilterModifier = eventFilterModifier.SelectedIndex;
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
            case ZoneEvent:
              _currentShowEnterZone = item.IsChecked;
              count += item.IsChecked ? 1 : 0;
              break;
            case MezbreakEvent:
              _currentShowMezBreaks = item.IsChecked;
              count += item.IsChecked ? 1 : 0;
              break;
            case PlayerkillEvent:
              _currentShowPlayerKilling = item.IsChecked;
              count += item.IsChecked ? 1 : 0;
              break;
            case PlayerslainEvent:
              _currentShowPlayerSlain = item.IsChecked;
              count += item.IsChecked ? 1 : 0;
              break;
            case KillshotEvent:
              _currentShowKillShots = item.IsChecked;
              count += item.IsChecked ? 1 : 0;
              break;
          }
        }

        UiElementUtil.SetComboBoxTitle(selectedOptions, count, Resource.EVENT_TYPES_SELECTED);
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
      _filterTimer?.Stop();
      _filterTimer?.Start();
    }

    private void EventsLogLoadingComplete(string _) => Load();

    private void ContentLoaded(object sender, RoutedEventArgs e)
    {
      if (VisualParent != null && !_ready)
      {
        MainActions.EventsLogLoadingComplete += EventsLogLoadingComplete;
        Load();
        _ready = true;
      }
    }

    public void HideContent()
    {
      MainActions.EventsLogLoadingComplete -= EventsLogLoadingComplete;
      dataGrid.ItemsSource = null;
      _ready = false;
    }
  }

  internal class EventRow
  {
    public double Time { get; set; }
    public string Actor { get; set; }
    public string Target { get; set; }
    public string Event { get; set; }
  }
}
