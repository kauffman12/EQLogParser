using Syncfusion.UI.Xaml.TreeGrid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace EQLogParser
{
  public partial class RandomViewer : IDocumentContent
  {
    public ObservableCollection<RandomRow> RandomData { get; set; }
    private readonly DispatcherTimer _reloadTimer;
    private double _currentTimeLimit = 300;
    private RandomRecord _lastHandled;
    private bool _ready;

    public RandomViewer()
    {
      InitializeComponent();
      RandomData = [];
      DataContext = this;
      MainActions.EventsThemeChanged += EventsThemeChanged;
      _reloadTimer = UiUtil.CreateTimer(ReloadTimerTick, 1500);
    }

    private void ReloadTimerTick(object sender, EventArgs e)
    {
      var found = false;
      foreach (var (beginTime, record) in RecordManager.Instance.GetAllRandoms())
      {
        if (_lastHandled == null || found)
        {
          UpdateSection(beginTime, record);
          _lastHandled = record;
          found = true;
        }
        else if (record == _lastHandled)
        {
          found = true;
        }
      }

      var remaining = UpdateTotals();
      var expanded = new Dictionary<object, bool>();
      foreach (var node in dataGrid.View.Nodes)
      {
        if (node.IsExpanded)
        {
          expanded[node.Item] = true;
        }
      }

      var triggerUpdate = dataGrid.Columns[3].CellStyle;
      dataGrid.Columns[3].CellStyle = null;
      dataGrid.Columns[3].CellStyle = triggerUpdate;
      foreach (var node in dataGrid.View.Nodes)
      {
        if (!node.IsExpanded && expanded.ContainsKey(node.Item))
        {
          dataGrid.ExpandNode(node);
        }
      }

      if (remaining)
      {
        if (!_reloadTimer.IsEnabled)
        {
          _reloadTimer.Start();
        }
      }
      else
      {
        _reloadTimer.Stop();
      }
    }

    private void RecordsUpdated(string type)
    {
      if (type == RecordManager.RandomRecords && !_reloadTimer.IsEnabled)
      {
        _reloadTimer.Start();
      }
    }

    internal void TreeGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DataGridUtil.EnableMouseSelection(sender, e);
    private void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(dataGrid, titleLabel.Content.ToString());
    private async void CreateImageClick(object sender, RoutedEventArgs e) => await DataGridUtil.CreateImageAsync(dataGrid, titleLabel);
    private void LogLoadingComplete(string _) => Load();
    private void EventsThemeChanged(string _)
    {
      if (dataGrid?.View != null)
      {
        DataGridUtil.RefreshTableColumns(dataGrid);
      }
    }

    private void Load()
    {
      RandomData.Clear();
      foreach (var (beginTime, record) in RecordManager.Instance.GetAllRandoms())
      {
        UpdateSection(beginTime, record);
        _lastHandled = record;
      }

      if (UpdateTotals())
      {
        _reloadTimer.Start();
      }
    }

    private static RandomRow CreateChild(double beginTime, RandomRecord record, HashSet<string> winnersRef)
    {
      return new RandomRow
      {
        Details = record.Player,
        Rolled = record.Rolled,
        To = record.To,
        From = record.From,
        RollTime = beginTime,
        Winners = winnersRef,
        Highest = ""
      };
    }

    private void UpdateSection(double beginTime, RandomRecord record)
    {
      var type = $"{record.From} to {record.To}";
      if (RandomData.LastOrDefault(row => row.Type == type) is { } section)
      {
        if ((beginTime - section.BeginTime) <= _currentTimeLimit)
        {
          foreach (var child in section.Children)
          {
            if (child.Details == record.Player)
            {
              section.RolledTwice.Add(child.Details);
            }
          }

          section.Children.Add(CreateChild(beginTime, record, section.Winners));

          if (section.Rolled < record.Rolled)
          {
            section.Rolled = record.Rolled;
            section.Winners.Clear();
            section.Winners.Add(record.Player);
            section.RollTime = beginTime;
          }
          else if (section.Rolled == record.Rolled)
          {
            section.Winners.Add(record.Player);
            section.RollTime = beginTime;
          }
        }
        else
        {
          NewSection();
        }
      }
      else
      {
        NewSection();
      }

      return;
      void NewSection()
      {
        var newSection = new RandomRow
        {
          From = record.From,
          To = record.To,
          Type = type,
          BeginTime = beginTime,
          Rolled = record.Rolled,
          RollTime = beginTime,
          Winners = [record.Player]
        };
        newSection.Children = [CreateChild(beginTime, record, newSection.Winners)];
        newSection.RolledTwice = [];
        RandomData.Add(newSection);
      }
    }

    private bool UpdateTotals()
    {
      var remaining = false;
      foreach (var section in RandomData)
      {
        section.Highest = string.Join(" + ", section.Winners).Trim();
        var duration = DateUtil.ToDouble(DateTime.Now) - section.BeginTime;
        if (duration < _currentTimeLimit)
        {
          section.Duration = DateUtil.FormatSimpleMs((long)(_currentTimeLimit - duration) * TimeSpan.TicksPerSecond);
          remaining = true;
        }
        else
        {
          section.Duration = "Limit Reached";
        }

        if (section.RolledTwice.Count > 0)
        {
          section.Details = "Rolled Multiple: " + string.Join(" + ", section.RolledTwice).Trim();
        }

        section.Count = section.Children.Count.ToString();
      }

      if (RandomData.Count > 0)
      {
        titleLabel.Content = RandomData.Count + " Sets of Randoms Found";
      }
      else
      {
        titleLabel.Content = "No Randoms Found";
      }

      return remaining;
    }

    private void OptionsChanged(object sender, EventArgs e)
    {
      if (_ready)
      {
        switch (randomDurations.SelectedIndex)
        {
          case 0:
            _currentTimeLimit = 600;
            break;
          case 1:
            _currentTimeLimit = 300;
            break;
          case 2:
            _currentTimeLimit = 240;
            break;
          case 3:
            _currentTimeLimit = 180;
            break;
          case 4:
            _currentTimeLimit = 120;
            break;
        }

        Load();
      }
    }

    private void ContentLoaded(object sender, RoutedEventArgs e)
    {
      if (VisualParent != null && !_ready)
      {
        MainActions.EventsLogLoadingComplete += LogLoadingComplete;
        RecordManager.Instance.RecordsUpdatedEvent += RecordsUpdated;
        Load();
        _ready = true;
      }
    }

    public void HideContent()
    {
      _reloadTimer?.Stop();
      MainActions.EventsLogLoadingComplete -= LogLoadingComplete;
      RecordManager.Instance.RecordsUpdatedEvent -= RecordsUpdated;
      RandomData.Clear();
      _ready = false;
    }

    private void AutoGeneratingColumn(object sender, TreeGridAutoGeneratingColumnEventArgs e)
    {
      var mapping = e.Column.MappingName;
      if (mapping is "BeginTime" or "RollTime")
      {
        var column = new TreeGridTextColumn
        {
          DisplayBinding = new Binding
          {
            Path = new PropertyPath(mapping),
            Converter = new DateTimeConverter()
          },
          TextAlignment = TextAlignment.Center,
          Width = MainActions.CurrentDateTimeWidth,
          HeaderText = (mapping == "BeginTime") ? "Start Time" : "Roll Time"
        };

        e.Column = column;
      }
      else if (mapping is "Count" or "From" or "To" or "Rolled")
      {
        if (mapping == "Count")
        {
          e.Column.HeaderText = "# Rolls";
        }

        e.Column.TextAlignment = TextAlignment.Right;
        e.Column.Width = MainActions.CurrentMediumWidth;
      }
      else if (mapping == "Highest")
      {
        var cellStyle = new Style
        {
          BasedOn = (Style)Application.Current.FindResource(typeof(TreeGridCell)),
          TargetType = typeof(TreeGridCell)
        };

        var randomStyleBinding = new Binding
        {
          Converter = new RandomPlayerStyleConverter()
        };

        cellStyle.Setters.Add(new Setter(BackgroundProperty, randomStyleBinding));
        cellStyle.Setters.Add(new Setter(BorderBrushProperty, new DynamicResourceExtension("BorderAlt")));
        cellStyle.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        e.Column.CellStyle = cellStyle;
        e.Column.HeaderText = "Highest Rolls";
        e.Column.Width = DataGridUtil.CalculateMinGridHeaderWidth(e.Column.HeaderText);
      }
      else if (mapping == "Details")
      {
        e.Column.HeaderText = "Player Details";
        e.Column.Width = MainActions.CurrentItemWidth;
      }
      else if (mapping == "Duration")
      {
        e.Column.HeaderText = "Time Remaining";
        e.Column.TextAlignment = TextAlignment.Center;
        e.Column.Width = DataGridUtil.CalculateMinGridHeaderWidth(e.Column.HeaderText);
      }
      else
      {
        e.Cancel = true;
      }
    }
  }

  public class RandomRow : INotifyPropertyChanged
  {
    private double _beginTime;
    private string _duration;
    private string _count;
    private string _highest;
    private int _from;
    private int _to;
    private int _rolled;
    private double _rollTime;
    private string _type;
    private string _details;

    public event PropertyChangedEventHandler PropertyChanged;

    protected void OnPropertyChanged(string propertyName)
    {
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public double BeginTime
    {
      get => _beginTime;
      set
      {
        if (!_beginTime.Equals(value))
        {
          _beginTime = value;
          OnPropertyChanged(nameof(BeginTime));
        }
      }
    }

    public string Duration
    {
      get => _duration;
      set
      {
        if (_duration != value)
        {
          _duration = value;
          OnPropertyChanged(nameof(Duration));
        }
      }
    }

    public string Count
    {
      get => _count;
      set
      {
        if (_count != value)
        {
          _count = value;
          OnPropertyChanged(nameof(Count));
        }
      }
    }

    public string Highest
    {
      get => _highest;
      set
      {
        if (_highest != value)
        {
          _highest = value;
          OnPropertyChanged(nameof(Highest));
        }
      }
    }

    public int From
    {
      get => _from;
      set
      {
        if (_from != value)
        {
          _from = value;
          OnPropertyChanged(nameof(From));
        }
      }
    }

    public int To
    {
      get => _to;
      set
      {
        if (_to != value)
        {
          _to = value;
          OnPropertyChanged(nameof(To));
        }
      }
    }

    public int Rolled
    {
      get => _rolled;
      set
      {
        if (_rolled != value)
        {
          _rolled = value;
          OnPropertyChanged(nameof(Rolled));
        }
      }
    }

    public double RollTime
    {
      get => _rollTime;
      set
      {
        if (!_rollTime.Equals(value))
        {
          _rollTime = value;
          OnPropertyChanged(nameof(RollTime));
        }
      }
    }

    public string Type
    {
      get => _type;
      set
      {
        if (_type != value)
        {
          _type = value;
          OnPropertyChanged(nameof(Type));
        }
      }
    }

    public string Details
    {
      get => _details;
      set
      {
        if (_details != value)
        {
          _details = value;
          OnPropertyChanged(nameof(Details));
        }
      }
    }

    // Other properties that do not require notification
    public HashSet<string> Winners { get; set; }
    public HashSet<string> RolledTwice { get; set; }
    public ObservableCollection<RandomRow> Children { get; set; }
  }
}
