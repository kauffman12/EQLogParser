using System;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EQLogParser
{
  class LogProcessor : ILogProcessor
  {
    private readonly ActionBlock<Tuple<string, double, bool>> PreProcess;
    private readonly ActionBlock<LineData> Process;
    private long LineCount;

    internal LogProcessor()
    {
      // Setup the pre-processor block
      var options = new ExecutionDataflowBlockOptions { BoundedCapacity = 20000 };
      PreProcess = new ActionBlock<Tuple<string, double, bool>>(data => DoPreProcess(data.Item1, data.Item2, data.Item3), options);
      Process = new ActionBlock<LineData>(DoProcess, options);
      ChatManager.Instance.Init();
    }

    public void LinkTo(ISourceBlock<Tuple<string, double, bool>> sourceBlock)
    {
      sourceBlock.LinkTo(PreProcess, new DataflowLinkOptions { PropagateCompletion = false });
    }

    private async Task DoPreProcess(string line, double dateTime, bool monitor)
    {
      var lineData = new LineData { Action = line[27..], BeginTime = dateTime, LineNumber = LineCount };

      if (monitor)
      {
        if (TriggerStateManager.Instance.IsActive())
        {
          // Look for GINA entries in the log
          if (ConfigUtil.IfSetOrElse("TriggersWatchForGINA"))
          {
            GinaUtil.CheckGina(lineData);
          }
        }
      }

      // avoid having other things parse chat by accident
      if (ChatLineParser.ParseChatType(lineData.Action, lineData.BeginTime) is { } chatType)
      {
        chatType.BeginTime = lineData.BeginTime;
        chatType.Text = line; // workaround for now?
        ChatManager.Instance.Add(chatType);
      }
      else
      {
        string doubleLine = null;
        double extraDouble = 0;

        // only if it's not a chat line check if two lines are on the same line
        if (lineData.Action.IndexOf("[", StringComparison.Ordinal) is var index and > -1 && lineData.Action.Length > (index + 28) &&
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
          await Process.SendAsync(lineData);
          LineCount++;
        }

        if (doubleLine != null)
        {
          await DoPreProcess(doubleLine, extraDouble, monitor);
        }
      }
    }

    private void DoProcess(LineData lineData)
    {
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
    }

    public void Dispose()
    {
      PreProcess?.Complete();
      Process?.Complete();
    }
  }
}
