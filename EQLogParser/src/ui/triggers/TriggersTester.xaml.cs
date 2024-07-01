using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TriggersTester.xaml
  /// </summary>
  public partial class TriggersTester : IDocumentContent
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private BlockingCollection<Tuple<string, double, bool>> _buffer;
    private TriggerConfig _theConfig;
    private bool _ready;

    public TriggersTester()
    {
      InitializeComponent();
    }

    private void EventsLogLoadingComplete(string obj)
    {
      theBasicLabel.Content = $"Current Player {{C}} " + (string.IsNullOrEmpty(ConfigUtil.PlayerName) ? "is not set" : "set to " + ConfigUtil.PlayerName);
    }

    private void TriggerConfigUpdateEvent(TriggerConfig config) => UpdateCharacterList(config);

    private void UpdateCharacterList(TriggerConfig config)
    {
      _theConfig = config;

      if (characterList != null)
      {
        if (!config.IsAdvanced)
        {
          characterList.Visibility = Visibility.Collapsed;
          theLabel.Visibility = Visibility.Collapsed;
          theBasicLabel.Visibility = Visibility.Visible;
        }
        else
        {
          characterList.Visibility = Visibility.Visible;
          theLabel.Visibility = Visibility.Visible;
          theBasicLabel.Visibility = Visibility.Collapsed;

          string selectedId = null;
          if (characterList.SelectedItem is TriggerCharacter selected)
          {
            selectedId = selected.Id;
          }

          var updatedSource = TriggerUtil.UpdateCharacterList(characterList.ItemsSource as List<TriggerCharacter>, config);
          if (updatedSource != null)
          {
            characterList.ItemsSource = updatedSource;
          }
          else
          {
            // workaround to get selected item to refresh
            characterList.SelectedIndex = -1;
          }

          if (characterList.Items.Count > 0)
          {
            if (selectedId != null && characterList.ItemsSource is List<TriggerCharacter> list)
            {
              var foundIndex = list.FindIndex(x => x.Id == selectedId);
              if (foundIndex > -1 && characterList.SelectedIndex != foundIndex)
              {
                characterList.SelectedIndex = foundIndex;
              }
            }

            if (characterList.SelectedIndex == -1 && characterList.Items.Count > 0)
            {
              characterList.SelectedIndex = 0;
            }
          }
        }
      }
    }

    private void ClearTextClick(object sender, RoutedEventArgs e)
    {
      if (testButton.Content.ToString() == "Run Test" && !string.IsNullOrEmpty(testTriggersBox.Text))
      {
        testTriggersBox.Text = "";
      }
    }

    private void TestTriggersClick(object sender, RoutedEventArgs e)
    {
      if (testButton.Content.ToString() == "Run Test")
      {
        try
        {
          if (testTriggersBox.Lines?.Count > 0)
          {
            if (characterList.Visibility != Visibility.Visible)
            {
              _buffer?.CompleteAdding();
              _buffer?.Dispose();
              _buffer = null;
              if (_theConfig != null)
              {
                _buffer = new BlockingCollection<Tuple<string, double, bool>>(new ConcurrentQueue<Tuple<string, double, bool>>());
                TriggerManager.Instance.SetTestProcessor(_theConfig, _buffer);
              }
            }
            else
            {
              if (characterList.SelectedItem is TriggerCharacter character)
              {
                _buffer?.CompleteAdding();
                _buffer?.Dispose();
                _buffer = null;
                _buffer = new BlockingCollection<Tuple<string, double, bool>>(new ConcurrentQueue<Tuple<string, double, bool>>());
                TriggerManager.Instance.SetTestProcessor(character, _buffer);
              }
              else
              {
                return;
              }
            }

            var allLines = testTriggersBox.Lines.ToList().Where(line => !string.IsNullOrEmpty(line.Text)
              && line.Text.Length > MainWindow.ActionIndex).Select(line => line.Text).ToList();
            if (allLines.Count > 0)
            {
              RunTest(allLines);
            }
          }
        }
        catch (Exception ex)
        {
          Log.Error(ex);
        }
      }
      else if (testButton.Content.ToString() == "Stop Test")
      {
        testButton.Content = "Stopping Test";
        clearButton.IsEnabled = false;
      }
    }

    private async void RunTest(List<string> allLines)
    {
      if (realTime.IsChecked == false)
      {
        characterList.IsEnabled = false;
        realTime.IsEnabled = false;
        clearButton.IsEnabled = false;
        allLines.ForEach(line =>
        {
          if (line.Length > MainWindow.ActionIndex)
          {
            var dateTime = DateUtil.ParseStandardDate(line);
            if (dateTime != DateTime.MinValue)
            {
              var beginTime = DateUtil.ToDouble(dateTime);
              _buffer?.Add(Tuple.Create(line, beginTime, true));
            }
          }
        });
        characterList.IsEnabled = true;
        realTime.IsEnabled = true;
        clearButton.IsEnabled = true;
      }
      else
      {
        testButton.Content = "Stop Test";
        clearButton.IsEnabled = false;
        characterList.IsEnabled = false;
        realTime.IsEnabled = false;

        await Task.Run(() =>
        {
          try
          {
            if (allLines.Count > 0)
            {
              var firstDate = DateUtil.ParseStandardDate(allLines.First());
              var lastDate = DateUtil.ParseStandardDate(allLines.Last());
              if (firstDate != DateTime.MinValue && lastDate != DateTime.MinValue)
              {
                var startTime = DateUtil.ToDouble(firstDate);
                var endTime = DateUtil.ToDouble(lastDate);
                var range = (int)(endTime - startTime + 1);
                if (range > 0)
                {
                  var dataIndex = 0;
                  var data = new List<string>[range];
                  data[dataIndex] = [];
                  foreach (var line in allLines)
                  {
                    var current = DateUtil.ParseStandardDate(line);
                    if (current != DateTime.MinValue)
                    {
                      var currentTime = DateUtil.ToDouble(current);
                      if (currentTime.Equals(startTime))
                      {
                        data[dataIndex].Add(line);
                      }
                      else
                      {
                        var diff = currentTime - startTime;
                        if (diff.Equals(1))
                        {
                          dataIndex++;
                          data[dataIndex] = [line];
                          startTime++;
                        }
                        else if (diff > 1)
                        {
                          for (var i = 1; i < diff; i++)
                          {
                            dataIndex++;
                            data[dataIndex] = [];
                          }

                          dataIndex++;
                          data[dataIndex] = [line];
                          startTime += diff;
                        }
                      }
                    }
                  }

                  var nowTime = DateUtil.ToDouble(DateTime.Now);
                  Dispatcher.Invoke(() =>
                  {
                    testStatus.Text = "| Time Remaining: " + data.Length + " seconds";
                    testStatus.Visibility = Visibility.Visible;
                  });

                  var count = 0;
                  var stop = false;
                  foreach (var list in data)
                  {
                    if (stop)
                    {
                      break;
                    }

                    Dispatcher.InvokeAsync(() =>
                    {
                      var content = testButton.Content;
                      if (content.ToString() == "Stopping Test")
                      {
                        stop = true;
                        TriggerManager.Instance.StopTestProcessor();
                      }
                    });

                    if (list != null)
                    {
                      if (list.Count == 0)
                      {
                        Thread.Sleep(1000);
                      }
                      else
                      {
                        var start = DateTime.Now;
                        foreach (var line in list)
                        {
                          if (!stop)
                          {
                            _buffer?.Add(Tuple.Create(line, nowTime, true));
                          }
                        }

                        var took = (DateTime.Now - start).Ticks;
                        var ticks = 10000000 - took;
                        Thread.Sleep(new TimeSpan(ticks));
                      }
                    }

                    nowTime++;
                    count++;
                    var remaining = data.Length - count;
                    Dispatcher.InvokeAsync(() => testStatus.Text = "| Time Remaining: " + remaining + " seconds");
                  }
                }
              }
            }
          }
          catch (Exception ex)
          {
            if (Application.Current != null)
            {
              Log.Error(ex);
            }
          }
          finally
          {
            Dispatcher.InvokeAsync(() =>
            {
              testStatus.Visibility = Visibility.Collapsed;
              testButton.Content = "Run Test";
              clearButton.IsEnabled = true;
              characterList.IsEnabled = true;
              realTime.IsEnabled = true;
            });
          }
        });
      }
    }

    private void ContentLoaded(object sender, RoutedEventArgs e)
    {
      if (VisualParent != null && !_ready)
      {
        if (TriggerStateManager.Instance.IsActive())
        {
          TriggerStateManager.Instance.TriggerConfigUpdateEvent += TriggerConfigUpdateEvent;
          MainActions.EventsLogLoadingComplete += EventsLogLoadingComplete;
          theBasicLabel.Content = $"Current Player {{C}} " + (string.IsNullOrEmpty(ConfigUtil.PlayerName) ? "is not set" : "set to " + ConfigUtil.PlayerName);

          if (TriggerStateManager.Instance.GetConfig() is { } config)
          {
            UpdateCharacterList(config);
          }
        }

        _ready = true;
      }
    }

    public void HideContent()
    {
      // nothing to do
    }

  }
}
