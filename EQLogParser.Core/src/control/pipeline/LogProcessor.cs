using log4net;
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
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
    private long _processedLines;
    private Task _readTask;
    private volatile bool _isDisposed;

        /*
         * Bound ONCE because converting an instance method group allocates a fresh delegate per call (~610 MB across a 10 M line capture). The neighbours
         * stay as they are because that asymmetry is measured: a non-capturing lambda and a static method group are compiler-cached at 0 bytes.
         */
    private readonly Action<string> _addMerc;

        /*
         * The stream this processor feeds: every line is parsed on the consumer task, which binds this lane for the whole run so raises land on this
         * capture only. Null means ParseLane.Main.
         */
    private readonly ParseLane _lane;

    internal LogProcessor(string fileName, IChatSink chatSink, ITriggerHook triggerHook, ParseLane lane = null)
    {
      _fileName = fileName ?? string.Empty;
      _chatSink = chatSink ?? throw new ArgumentNullException(nameof(chatSink));
      _triggerHook = triggerHook ?? throw new ArgumentNullException(nameof(triggerHook));
      _lane = lane ?? ParseLane.Main;
      _addMerc = PlayerRegistry.Instance.AddMerc;
    }

    // The consuming task once LinkTo has run. Headless callers must wait for this to finish
    // (full drain) before Dispose: Dispose sets _isDisposed, and the consumer stops at the next
    // line it checks — destroying it early drops the tail of the stream. App usage is unaffected
    // (LogReader keeps feeding long after LinkTo).
    internal Task Completion => _readTask;

    // A batch is acknowledged only after its last line has passed through the parser.
    internal long ProcessedLines => Interlocked.Read(ref _processedLines);

        /*
         * The stream arrives in batches because the thread crossing costs, not the parse (docs/DesignNotes.md → "The reader hands the parser one line at a
         * time…"). The batch shape must stay invisible downstream: lines go to DoPreProcess one at a time in arrival order on this thread, which is what
         * keeps the cross-line carries working.
         */
    public void LinkTo(BlockingCollection<LogReaderItem[]> collection)
    {
      // start archive if enabled (app side channel; no-op headless)
      _chatSink.Init();

      _readTask = Task.Run(() =>
      {
        // Bind for the WHOLE run and restore what was there in the finally: this is a pooled thread, and a lane left
        // bound after the stream ends would follow the thread into whatever task runs on it next (ParseLane).
        var priorLane = ParseLane.BindCurrentThread(_lane);
        _lane.CharmCasts.Clear();
        try
        {
          foreach (var batch in collection.GetConsumingEnumerable())
          {
            // Checked per LINE, inside the batch: a dispose must stop where it lands rather than swallow the
            // rest of whatever bundle the reader happened to hand over (the drain law on Completion).
            foreach (var data in batch)
            {
              if (_isDisposed) break;

              /*
               * One line, not one file. The four combat parsers each own a per-line catch with a capped voice, but this step does more than call
               * them: it slices line[27..] before anything checks the length again, runs the chat classifier, hands the chat type to the archive sink,
               * and asks the trigger hook about quick-share — none of which is inside those catches. A fault in any of it used to reach the catch around
               * this whole loop, which logs "Problem loading log file." once and stops reading: one malformed line at minute nine cost the remaining five
               * hours, every board and identity verdict with it, and the only clue was a stack trace at the top of the log.
               */
              try
              {
                DoPreProcess(data.Line, data.Ts, data.IsMonitor);
              }
              catch (Exception ex) when (!_isDisposed)
              {
                /*
                 * Not ThreadBoundary.Note, on purpose: a capped message has to carry everything the reader needs ONCE (RepeatCap's rule), and here that
                 * is the offending text — without it the fault is unfalsifiable. The key is still site + exception type, so one shape costs one line and
                 * the count behind it stays in RepeatCap.
                 */
                if (RepeatCap.SpeaksOnce($"lane line {ex.GetType().Name}"))
                  Log.Error($"a line faulted outside the parsers' own guards and was skipped (this kind repeats silently, counted): "
                            + $"{ex.Message} | line: {RepeatCap.Clip(data.Line)}", ex);
              }
            }

            if (_isDisposed) break;
            Interlocked.Add(ref _processedLines, batch.Length);
          }
        }
        catch (Exception ex)
        {
          Log.Error("Problem loading log file.", ex);
        }
        finally
        {
          ParseLane.BindCurrentThread(priorLane);
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

        if (PreLineParser.NeedProcessing(lineData, (name, time) => PlayerRegistry.Instance.AddVerifiedPlayer(name, time), PlayerRegistry.IsPossiblePlayerName, _addMerc))
        {
                    /*
                     * No eager Split: the word view builds itself on first read by a parser that needs words (LineData.Words). That call used to be ~374 B and
                     * 29 % of ingest stage time on every recognized line.
                     */
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
          _readTask?.Wait(2000);
        }
        catch (Exception ex)
        {
          Log.Error("Log reading task not completed.", ex);
        }
      }
    }
  }
}
