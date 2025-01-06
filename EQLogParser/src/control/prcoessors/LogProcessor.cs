using log4net;
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;

namespace EQLogParser
{
  internal class LogProcessor : ILogProcessor
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private readonly string _fileName;
    private long _lineCount;
    private Task _readTask;
    private volatile bool _isDisposed;

    internal LogProcessor(string fileName)
    {
      _fileName = fileName ?? string.Empty;
      // setup the pre-processor block
      ChatManager.Instance.Init();
    }

    public void LinkTo(BlockingCollection<Tuple<string, double, bool>> collection)
    {
      _readTask = Task.Run(() =>
      {
        try
        {
          foreach (var data in collection.GetConsumingEnumerable())
          {
            if (!_isDisposed)
            {
              DoPreProcess(data.Item1, data.Item2, data.Item3);
            }
          }
        }
        catch (Exception ex)
        {
          Log.Error("Problem loading log file.", ex);
        }
        finally
        {
          collection?.Dispose();
        }
      });
    }

    private void DoPreProcess(string line, double dateTime, bool monitor)
    {
      var lineData = new LineData { Action = line[27..], BeginTime = dateTime, LineNumber = _lineCount };

      // avoid having other things parse chat by accident
      if (ChatLineParser.ParseChatType(lineData.Action) is { } chatType)
      {
        chatType.BeginTime = lineData.BeginTime;
        chatType.Text = line; // workaround for now?
        ChatManager.Instance.Add(chatType);

        if (!monitor || !TriggerManager.Instance.RunningFiles.ContainsKey(_fileName))
        {
          TriggerUtil.CheckQuickShare(chatType, lineData.Action, dateTime, null, null);
          GinaUtil.CheckGina(chatType, lineData.Action, dateTime, null, null);
        }
      }
      else
      {
        string doubleLine = null;
        double extraDouble = 0;

        // only if it's not a chat line check if two lines are on the same line
        if (lineData.Action.IndexOf('[') is var index and > -1 && lineData.Action.Length > (index + 28) &&
            lineData.Action[index + 25] == ']' && char.IsDigit(lineData.Action[index + 24]))
        {
          var original = lineData.Action;
          lineData.Action = original[..index];
          doubleLine = original[index..];

          if (DateUtil.ParseStandardDate(doubleLine) is var newDate && newDate != DateTime.MinValue)
          {
            extraDouble = DateUtil.ToDouble(newDate);
          }
        }

        if (PreLineParser.NeedProcessing(lineData))
        {
          // may as split once if most things use it
          lineData.Split = lineData.Action.Split(' ');
          if (!DamageLineParser.Process(lineData))
          {
            if (!HealingLineParser.Process(lineData))
            {
              if (!MiscLineParser.Process(lineData))
              {
                CastLineParser.Process(lineData);
              }
            }
          }

          _lineCount++;
        }

        if (doubleLine != null)
        {
          DoPreProcess(doubleLine, extraDouble, monitor);
        }
      }
    }

    public void Dispose()
    {
      if (!_isDisposed)
      {
        _isDisposed = true;

        try
        {
          _readTask.Wait(2000);
        }
        catch (Exception ex)
        {
          Log.Error("Log reading task not completed.", ex);
        }
      }
    }
  }
}
