using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for RandomViewer.xaml
  /// </summary>
  public partial class RandomViewer : UserControl, IDisposable
  {
    private readonly DispatcherTimer ReloadTimer;
    private double CurrentTimeLimit = 300;
    private RandomRecord LastHandled;

    public RandomViewer()
    {
      InitializeComponent();

      MainActions.EventsLogLoadingComplete += LogLoadingComplete;
      RecordManager.Instance.RecordsUpdatedEvent += RecordsUpdated;
      DataGridUtil.UpdateTableMargin(dataGrid);
      MainActions.EventsThemeChanged += EventsThemeChanged;

      ReloadTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 2000) };
      ReloadTimer.Tick += (_, _) =>
      {
        GetLatest();
        ReloadTimer.Stop();

        var sections = dataGrid.ItemsSource as List<dynamic>;
        UpdateTotals(sections);

        var expanded = new Dictionary<object, bool>();
        foreach (var node in dataGrid.View.Nodes)
        {
          if (node.IsExpanded)
          {
            expanded[node.Item] = true;
          }
        }

        dataGrid.View.Refresh();

        foreach (var node in dataGrid.View.Nodes)
        {
          if (!node.IsExpanded && expanded.ContainsKey(node.Item))
          {
            dataGrid.ExpandNode(node);
          }
        }
      };

      Load();
    }

    private void RecordsUpdated(string type)
    {
      if (type == RecordManager.RANDOM_RECORDS && !ReloadTimer.IsEnabled)
      {
        ReloadTimer.Start();
      }
    }

    internal void TreeGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DataGridUtil.EnableMouseSelection(sender, e);
    private void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(dataGrid, titleLabel.Content.ToString());
    private void CreateImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(dataGrid, titleLabel);
    private void LogLoadingComplete(string _) => Load();
    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);

    private void GetLatest()
    {
      var found = false;
      var sections = dataGrid.ItemsSource as List<dynamic>;

      foreach (var (beginTime, record) in RecordManager.Instance.GetAllRandoms())
      {
        if (LastHandled == null || found)
        {
          UpdateSection(beginTime, record, sections);
          LastHandled = record;
          found = true;
        }
        else if (record == LastHandled)
        {
          found = true;
        }
      }
    }

    private void Load()
    {
      var sections = new List<dynamic>();
      foreach (var (beginTime, record) in RecordManager.Instance.GetAllRandoms())
      {
        UpdateSection(beginTime, record, sections);
        LastHandled = record;
      }

      UpdateTotals(sections);
      dataGrid.ItemsSource = sections;
    }

    private dynamic CreateChild(double beginTime, RandomRecord record)
    {
      var child = new ExpandoObject() as dynamic;
      child.Player = record.Player;
      child.Rolled = record.Rolled;
      child.To = record.To;
      child.From = record.From;
      child.RollTime = beginTime;
      return child;
    }

    private void UpdateSection(double beginTime, RandomRecord record, List<dynamic> sections)
    {
      var type = $"{record.From} to {record.To}";
      var section = sections.LastOrDefault(section => section.Type == type);
      if (section != null && (beginTime - section.StartTime) <= CurrentTimeLimit)
      {
        foreach (var child in section.Children)
        {
          if (child.Player == record.Player)
          {
            section.RolledTwice.Add(child.Player);
          }
        }

        section.Children.Add(CreateChild(beginTime, record));

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
        section = new ExpandoObject();
        section.From = record.From;
        section.To = record.To;
        section.Type = type;
        section.StartTime = beginTime;
        section.Rolled = record.Rolled;
        section.RollTime = beginTime;
        section.Winners = new HashSet<string>();
        section.Winners.Add(record.Player);
        section.Children = new List<dynamic>();
        section.Children.Add(CreateChild(beginTime, record));
        section.RolledTwice = new HashSet<string>();
        sections.Add(section);
      }
    }

    private void UpdateTotals(List<dynamic> sections)
    {
      foreach (var section in sections)
      {
        section.Player = "Highest Roll: " + string.Join(" + ", section.Winners).Trim();

        if (section.RolledTwice.Count > 0)
        {
          section.Player += ", Rolled Multiple: " + string.Join(" + ", section.RolledTwice).Trim();
        }

        var duration = DateUtil.ToDouble(DateTime.Now) - section.StartTime;
        if (duration < CurrentTimeLimit)
        {
          section.Duration = "Remaining: " + DateUtil.FormatSimpleMS((long)(CurrentTimeLimit - duration) * TimeSpan.TicksPerSecond);

          if (!ReloadTimer.IsEnabled)
          {
            ReloadTimer.Start();
          }
        }
        else
        {
          section.Duration = "Time Limit Reached";
        }

        section.Count = section.Children.Count;
      }

      if (sections.Count > 0)
      {
        titleLabel.Content = sections.Count + " Sets of Randoms Found";
      }
      else
      {
        titleLabel.Content = "No Randoms Found";
      }
    }

    private void OptionsChanged(object sender, EventArgs e)
    {
      if (dataGrid?.View != null)
      {
        switch (randomDurations.SelectedIndex)
        {
          case 0:
            CurrentTimeLimit = 600;
            break;
          case 1:
            CurrentTimeLimit = 300;
            break;
          case 2:
            CurrentTimeLimit = 240;
            break;
          case 3:
            CurrentTimeLimit = 180;
            break;
          case 4:
            CurrentTimeLimit = 120;
            break;
        }

        Load();
      }
    }

    #region IDisposable Support
    private bool DisposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!DisposedValue)
      {
        ReloadTimer?.Stop();
        MainActions.EventsThemeChanged -= EventsThemeChanged;
        MainActions.EventsLogLoadingComplete -= LogLoadingComplete;
        RecordManager.Instance.RecordsUpdatedEvent -= RecordsUpdated;
        dataGrid.Dispose();
        DisposedValue = true;
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
