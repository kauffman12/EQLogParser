using Syncfusion.Data;
using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace EQLogParser
{
  public partial class EventViewer : IDocumentContent
  {
    public ObservableCollection<EventRow> EventData { get; set; }

    private const string ZoneEvent = "Entered Area";
    private const string KillShotEvent = "Kill Shot";
    private const string PlayerSlainEvent = "Player Slain";
    private const string PlayerKillEvent = "Player Killing";
    private const string MezBreakEvent = "Mez Break";

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
      EventData = [];
      DataContext = this;

      var list = new List<ComboBoxItemDetails>
      {
        new() { IsChecked = true, Text = ZoneEvent },
        new() { IsChecked = true, Text = KillShotEvent },
        new() { IsChecked = true, Text = MezBreakEvent },
        new() { IsChecked = true, Text = PlayerKillEvent },
        new() { IsChecked = true, Text = PlayerSlainEvent }
      };

      selectedOptions.ItemsSource = list;
      UiElementUtil.SetComboBoxTitle(selectedOptions, Resource.EVENT_TYPES_SELECTED);
      MainActions.EventsThemeChanged += EventsThemeChanged;

      eventFilter.Text = Resource.EVENT_FILTER_TEXT;
      _filterTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 750) };
      _filterTimer.Tick += (_, _) =>
      {
        _filterTimer.Stop();
        if (_currentFilterText != eventFilter.Text)
        {
          _currentFilterText = eventFilter.Text;
          Refresh();
        }
      };
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(dataGrid, titleLabel.Content.ToString());
    private async void CreateImageClick(object sender, RoutedEventArgs e) => await DataGridUtil.CreateImageAsync(dataGrid, titleLabel);
    private void RefreshClick(object sender, RoutedEventArgs e) => Load();
    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);

    private void Load()
    {
      var list = new List<EventRow>();
      foreach (var (beginTime, record) in RecordManager.Instance.GetAllDeaths())
      {
        if (!(PlayerManager.Instance.IsVerifiedPet(record.Killed) && !PlayerManager.IsPossiblePlayerName(record.Killed)))
        {
          var isActorNpc = DataManager.Instance.IsLifetimeNpc(record.Killer) || DataManager.Instance.IsKnownNpc(record.Killer);
          var isTargetNpc = DataManager.Instance.IsLifetimeNpc(record.Killed) || DataManager.Instance.IsKnownNpc(record.Killed);
          var isActorPlayer = PlayerManager.Instance.IsPetOrPlayerOrSpell(record.Killer);
          var isTargetPlayer = PlayerManager.Instance.IsPetOrPlayerOrMerc(record.Killed);

          var text = KillShotEvent;
          if (isTargetPlayer && isActorPlayer)
          {
            text = PlayerKillEvent;
          }
          else if (isTargetPlayer || (isActorNpc && !isTargetNpc && PlayerManager.IsPossiblePlayerName(record.Killed)))
          {
            text = PlayerSlainEvent;
          }

          list.Add(new EventRow { BeginTime = beginTime, Actor = record.Killer, Target = record.Killed, Event = text });
        }
      }

      foreach (var (beginTime, record) in RecordManager.Instance.GetAllMezBreaks())
      {
        list.Add(new EventRow { BeginTime = beginTime, Actor = record.Breaker, Target = record.Awakened, Event = MezBreakEvent });
      }

      foreach (var (beginTime, record) in RecordManager.Instance.GetAllZoning())
      {
        list.Add(new EventRow { BeginTime = beginTime, Actor = ConfigUtil.PlayerName, Event = ZoneEvent, Target = record.Zone });
      }

      UiUtil.UpdateObservable(list, EventData);

      if (dataGrid.View != null)
      {
        dataGrid.View.Filter = obj =>
        {
          var result = false;
          if (obj is EventRow row)
          {
            result = (_currentShowMezBreaks && row.Event == MezBreakEvent) || (_currentShowEnterZone && row.Event == ZoneEvent) || (_currentShowKillShots &&
              row.Event == KillShotEvent) || (_currentShowPlayerKilling && row.Event == PlayerKillEvent) || (_currentShowPlayerSlain && row.Event == PlayerSlainEvent);

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

        dataGrid.View.Refresh();
      }

      UpdateTitle();
    }

    private void UpdateTitle() => titleLabel.Content = EventData.Count == 0 ? "No Events Found" : EventData.Count + " Events Found";

    private void Refresh()
    {
      dataGrid?.View?.RefreshFilter();
      UpdateTitle();
    }

    private void FilterOptionChange(object sender, EventArgs e)
    {
      if (eventFilterModifier?.SelectedIndex > -1 && eventFilterModifier.SelectedIndex != _currentFilterModifier)
      {
        _currentFilterModifier = eventFilterModifier.SelectedIndex;
        Refresh();
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
            case MezBreakEvent:
              _currentShowMezBreaks = item.IsChecked;
              count += item.IsChecked ? 1 : 0;
              break;
            case PlayerKillEvent:
              _currentShowPlayerKilling = item.IsChecked;
              count += item.IsChecked ? 1 : 0;
              break;
            case PlayerSlainEvent:
              _currentShowPlayerSlain = item.IsChecked;
              count += item.IsChecked ? 1 : 0;
              break;
            case KillShotEvent:
              _currentShowKillShots = item.IsChecked;
              count += item.IsChecked ? 1 : 0;
              break;
          }
        }

        UiElementUtil.SetComboBoxTitle(selectedOptions, Resource.EVENT_TYPES_SELECTED);
        Refresh();
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

    private void EventsLogLoadingComplete(string file, bool open) => Load();

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
      EventData.Clear();
      _ready = false;
    }

    private void AutoGeneratingColumn(object sender, AutoGeneratingColumnArgs e)
    {
      var mapping = e.Column.MappingName;
      if (mapping == "BeginTime")
      {
        e.Column.SortMode = DataReflectionMode.Value;
        e.Column.DisplayBinding = new Binding
        {
          Path = new PropertyPath(mapping),
          Converter = new DateTimeConverter()
        };
        e.Column.TextAlignment = TextAlignment.Center;
        e.Column.Width = MainActions.CurrentDateTimeWidth;
        e.Column.HeaderText = "Time";
      }
      else if (mapping == "Target")
      {
        e.Column.Width = MainActions.CurrentNpcWidth;
      }
      else
      {
        e.Column.Width = DataGridUtil.CalculateMinGridHeaderWidth(e.Column.HeaderText);
      }
    }
  }

  public class EventRow
  {
    public double BeginTime { get; set; }
    public string Actor { get; set; }
    public string Event { get; set; }
    public string Target { get; set; }
  }
}
