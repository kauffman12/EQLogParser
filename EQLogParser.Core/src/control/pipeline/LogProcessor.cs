using log4net;
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;

namespace EQLogParser
{
  // The per-line parsing pipeline. Moved out of the WPF project (Phase 0) so headless runs can
  // execute it in tests: chat archiving and quick-share triggering are injected as sinks instead
  // of calling ChatDB / TriggerUtil directly. Everything else is byte-identical to the old
  // EQLogParser/src/control/processors/LogProcessor.cs.
  internal class LogProcessor : ILogProcessor
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private readonly string _fileName;
    private readonly IChatSink _chatSink;
    private readonly ITriggerHook _triggerHook;
    private long _lineCount;
    private Task _readTask;
    private volatile bool _isDisposed;

    internal LogProcessor(string fileName, IChatSink chatSink, ITriggerHook triggerHook)
    {
      _fileName = fileName ?? string.Empty;
      _chatSink = chatSink ?? throw new ArgumentNullException(nameof(chatSink));
      _triggerHook = triggerHook ?? throw new ArgumentNullException(nameof(triggerHook));
    }

    // The consuming task once LinkTo has run. Headless callers must wait for this to finish
    // (full drain) before Dispose: Dispose sets _isDisposed, and the consumer stops at the next
    // line it checks — destroying it early drops the tail of the stream. App usage is unaffected
    // (LogReader keeps feeding long after LinkTo).
    internal Task Completion => _readTask;

    public void LinkTo(BlockingCollection<LogReaderItem> collection)
    {
      // start archive if enabled (app side channel; no-op headless)
      _chatSink.Init();

      _readTask = Task.Run(() =>
      {
        try
        {
          foreach (var data in collection.GetConsumingEnumerable())
          {
            if (_isDisposed) break;
            DoPreProcess(data.Line, data.Ts, data.IsMonitor);
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
      // IsMonitor rides with the line so downstream consumers (FCT) can drop replay records
      var lineData = new LineData { Action = line[27..], BeginTime = dateTime, LineNumber = _lineCount, IsMonitor = monitor };

      // avoid having other things parse chat by accident
      if (ChatLineParser.ParseChatType(lineData.Action) is { } chatType)
      {
        chatType.BeginTime = lineData.BeginTime;
        chatType.Text = line; // workaround for now?
        _chatSink.Add(chatType);

        if (!monitor)
        {
          _triggerHook.CheckQuickShare(chatType, lineData.Action, lineData.BeginTime);
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
            extraDouble = DateUtil.ToDotNetSeconds(newDate);
          }
        }

        if (PreLineParser.NeedProcessing(lineData, (name, time) => PlayerRegistry.Instance.AddVerifiedPlayer(name, time), PlayerRegistry.IsPossiblePlayerName, PlayerRegistry.Instance.AddMerc))
        {
          // may as well split once if most things use it
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
