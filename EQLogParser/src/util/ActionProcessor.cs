using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser
{
  class ActionProcessor
  {
    public delegate void ProcessActionCallback(LineData data);
    private List<LineData> Queue = new List<LineData>();
    private List<LineData> temp = null;
    private readonly object QueueLock = new object();
    private readonly ProcessActionCallback callback;
    private bool Stopped = false;
    private readonly int DelayTime = 10;
    private long LinesAdded = 0;
    private long LinesProcessed = 0;

    public ActionProcessor(ProcessActionCallback callback)
    {
      this.callback = callback;
      Task.Run(() => Process());
    }

    public void Add(LineData data)
    {
      lock (QueueLock)
      {
        Queue.Add(data);
        LinesAdded++;
      }
    }

    public long Size()
    {
      long count = 0;
      lock (QueueLock)
      {
        count = Queue.Count;
      }
      return count;
    }

    public void Stop()
    {
      Stopped = true;
    }

    public double GetPercentComplete()
    {
      return LinesAdded > 0 ? Math.Round((double)LinesProcessed / LinesAdded * 100, 1) : 100.0;
    }

    private void Process()
    {
      while (!Stopped)
      {
        bool needSleep = false;

        lock (QueueLock)
        {
          if (Queue.Count > 0)
          {
            temp = Queue;
            Queue = new List<LineData>();
          }
          else
          {
            needSleep = true;
          }
        }

        if (needSleep)
        {
          Thread.Sleep(DelayTime);
        }
        else if (temp != null)
        {
          foreach (var item in temp)
          {
            if (Stopped)
            {
              break;
            }

            callback(item);
            LinesProcessed++;
          }
        }
      }
    }
  }
}
