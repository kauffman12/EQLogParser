using log4net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for TriggersTester.xaml
  /// </summary>
  public partial class TriggersTester : UserControl
  {
    private static readonly ILog LOG = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    public TriggersTester()
    {
      InitializeComponent();

      (Application.Current.MainWindow as MainWindow).Closing += TriggersTesterClosing;
    }

    private void TriggersTesterClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
      if (testButton.Content.ToString() == "Stop Test")
      {
        testButton.Content = "Stopping Test";
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
            var allLines = testTriggersBox.Lines.ToList().Where(line => !string.IsNullOrEmpty(line.Text) && line.Text.Length > MainWindow.ACTION_INDEX)
              .Select(line => line.Text).ToList();
            if (allLines.Count > 0)
            {
              RunTest(allLines, realTime.IsChecked == true);
            }
          }
        }
        catch (Exception ex)
        {
          LOG.Error(ex);
        }
      }
      else if (testButton.Content.ToString() == "Stop Test")
      {
        testButton.Content = "Stopping Test";
      }
    }

    private void RunTest(List<string> allLines, bool realTime)
    {
      if (!realTime)
      {
        allLines.ForEach(line =>
        {
          if (line.Length > MainWindow.ACTION_INDEX)
          {
            var dateTime = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", line, 5);
            if (dateTime != DateTime.MinValue)
            {
              var beginTime = DateUtil.ToDouble(dateTime);
              TriggerManager.Instance.AddAction(new LineData { Line = line, Action = line.Substring(MainWindow.ACTION_INDEX), BeginTime = beginTime });
            }
          }
        });
      }
      else
      {
        testButton.Content = "Stop Test";

        Task.Run(() =>
        {
          try
          {
            if (allLines.Count > 0)
            {
              var firstDate = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", allLines.First(), 5);
              var lastDate = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", allLines.Last(), 5);
              if (firstDate != DateTime.MinValue && lastDate != DateTime.MinValue)
              {
                var startTime = DateUtil.ToDouble(firstDate);
                var endTime = DateUtil.ToDouble(lastDate);
                var range = (int)(endTime - startTime + 1);
                if (range > 0)
                {
                  var data = new List<string>[range];

                  var dataIndex = 0;
                  data[dataIndex] = new List<string>();
                  foreach (var line in allLines)
                  {
                    var current = DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", line, 5);
                    if (current != DateTime.MinValue)
                    {
                      var currentTime = DateUtil.ToDouble(current);
                      if (currentTime == startTime)
                      {
                        data[dataIndex].Add(line);
                      }
                      else
                      {
                        var diff = currentTime - startTime;
                        if (diff == 1)
                        {
                          dataIndex++;
                          data[dataIndex] = new List<string>();
                          data[dataIndex].Add(line);
                          startTime++;
                        }
                        else if (diff > 1)
                        {
                          for (var i = 1; i < diff; i++)
                          {
                            dataIndex++;
                            data[dataIndex] = new List<string>();
                          }

                          dataIndex++;
                          data[dataIndex] = new List<string>();
                          data[dataIndex].Add(line);
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
                    Dispatcher.InvokeAsync(() =>
                    {
                      var content = testButton.Content;
                      if (content.ToString() == "Stopping Test")
                      {
                        stop = true;
                      }
                    });

                    if (stop)
                    {
                      break;
                    }

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
                          var action = line.Substring(MainWindow.ACTION_INDEX);
                          TriggerManager.Instance.AddAction(new LineData { Line = line, Action = action, BeginTime = nowTime });
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
              LOG.Error(ex);
            }
          }
          finally
          {
            Dispatcher.InvokeAsync(() =>
            {
              testStatus.Visibility = Visibility.Collapsed;
              testButton.Content = "Run Test";
            });
          }
        });
      }
    }
  }
}
