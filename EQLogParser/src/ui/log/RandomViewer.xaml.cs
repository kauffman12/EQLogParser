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
    private RandomRecord LastHandled = null;

    public RandomViewer()
    {
      InitializeComponent();

      (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete += LogLoadingComplete;
      DataManager.Instance.EventsNewRandomRecord += EventsNewRandomRecord;
      DataGridUtil.UpdateTableMargin(dataGrid);
      (Application.Current.MainWindow as MainWindow).EventsThemeChanged += EventsThemeChanged;

      ReloadTimer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 0, 0, 2000) };
      ReloadTimer.Tick += (sender, e) =>
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

    private void EventsNewRandomRecord(object sender, RandomRecord e)
    {
      if (!ReloadTimer.IsEnabled)
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
      var allRandoms = DataManager.Instance.GetAllRandoms();
      var sections = dataGrid.ItemsSource as List<dynamic>;

      foreach (var block in allRandoms)
      {
        foreach (var random in block.Actions.Cast<RandomRecord>())
        {
          if (LastHandled == null || found)
          {
            UpdateSection(block, random, sections);
            LastHandled = random;
            found = true;
          }
          else if (random == LastHandled)
          {
            found = true;
          }
        }
      }
    }

    private void Load()
    {
      var sections = new List<dynamic>();
      var allRandoms = DataManager.Instance.GetAllRandoms();
      foreach (var block in allRandoms)
      {
        foreach (var random in block.Actions.Cast<RandomRecord>())
        {
          UpdateSection(block, random, sections);
          LastHandled = random;
        }
      }

      UpdateTotals(sections);
      dataGrid.ItemsSource = sections;
    }

    private dynamic CreateChild(double beginTime, RandomRecord random)
    {
      var child = new ExpandoObject() as dynamic;
      child.Player = random.Player;
      child.Rolled = random.Rolled;
      child.To = random.To;
      child.From = random.From;
      child.RollTime = beginTime;
      return child;
    }

    private void UpdateSection(ActionGroup block, RandomRecord random, List<dynamic> sections)
    {
      var type = random.From + " to " + random.To;
      var section = sections.LastOrDefault(section => section.Type == type);
      if (section != null && (block.BeginTime - section.StartTime) <= CurrentTimeLimit)
      {
        foreach (var child in section.Children)
        {
          if (child.Player == random.Player)
          {
            section.RolledTwice.Add(child.Player);
          }
        }

        section.Children.Add(CreateChild(block.BeginTime, random));

        if (section.Rolled < random.Rolled)
        {
          section.Rolled = random.Rolled;
          section.Winners.Clear();
          section.Winners.Add(random.Player);
          section.RollTime = block.BeginTime;
        }
        else if (section.Rolled == random.Rolled)
        {
          section.Winners.Add(random.Player);
          section.RollTime = block.BeginTime;
        }
      }
      else
      {
        section = new ExpandoObject();
        section.From = random.From;
        section.To = random.To;
        section.Type = type;
        section.StartTime = block.BeginTime;
        section.Rolled = random.Rolled;
        section.RollTime = block.BeginTime;
        section.Winners = new HashSet<string>();
        section.Winners.Add(random.Player);
        section.Children = new List<dynamic>();
        section.Children.Add(CreateChild(block.BeginTime, random));
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
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        ReloadTimer?.Stop();
        (Application.Current.MainWindow as MainWindow).EventsThemeChanged -= EventsThemeChanged;
        (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete -= LogLoadingComplete;
        DataManager.Instance.EventsNewRandomRecord -= EventsNewRandomRecord;
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
}
