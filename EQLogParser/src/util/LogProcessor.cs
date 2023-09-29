using System;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EQLogParser
{
  class LogProcessor : IDisposable
  {
    private readonly ActionBlock<Tuple<string, double, bool>> PreProcess;
    private readonly ActionBlock<LineData> Process;
    private readonly ActionBlock<LineData> Triggers;
    private long LineCount = 0;
    private ChatManager PlayerChatManager = new ChatManager();

    internal LogProcessor()
    {
      // Setup the pre-processor block
      var options = new ExecutionDataflowBlockOptions { BoundedCapacity = 20000 };
      PreProcess = new ActionBlock<Tuple<string, double, bool>>(data => DoPreProcess(data.Item1, data.Item2, data.Item3), options);
      Process = new ActionBlock<LineData>(data => DoProcess(data), options);
      Triggers = new ActionBlock<LineData>(data => TriggerManager.Instance.AddAction(data));
    }

    public void LinkTo(ISourceBlock<Tuple<string, double, bool>> sourceBlock)
    {
      // Establish the link and add the disposable to the group
      sourceBlock.LinkTo(PreProcess, new DataflowLinkOptions { PropagateCompletion = false });
    }

    private async Task DoPreProcess(string line, double dateTime, bool monitor)
    {
      var lineData = new LineData { Action = line.Substring(27), BeginTime = dateTime, LineNumber = LineCount };

      if (monitor)
      {
        // Triggers check everything
        Triggers.Post(lineData);
      }

      // avoid having other things parse chat by accident
      if (ChatLineParser.ParseChatType(lineData.Action, lineData.BeginTime) is ChatType chatType)
      {
        chatType.BeginTime = lineData.BeginTime;
        chatType.Text = line; // workaround for now?
        PlayerChatManager.Add(chatType);
      }
      else
      {
        string doubleLine = null;
        double extraDouble = 0;

        // only if it's not a chat line check if two lines are on the same line
        if (lineData.Action.IndexOf("[") is int index && index > -1 && lineData.Action.Length > (index + 28) && lineData.Action[index + 25] == ']' &&
          char.IsDigit(lineData.Action[index + 24]))
        {
          var original = lineData.Action;
          lineData.Action = original.Substring(0, index);
          doubleLine = original.Substring(index);

          if (DateUtil.CustomDateTimeParser("MMM dd HH:mm:ss yyyy", doubleLine, 5) is DateTime newDate && newDate != DateTime.MinValue)
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
        if (!CastLineParser.Process(lineData))
        {
          if (!HealingLineParser.Process(lineData))
          {
            MiscLineParser.Process(lineData);
          }
        }
      }
    }

    public void Dispose()
    {
      PlayerChatManager?.Dispose();
      PreProcess?.Complete();
      Process?.Complete();
    }
  }
}
