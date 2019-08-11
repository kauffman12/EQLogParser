using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser
{
  class ActionProcessor<T>
  {
    public delegate void ProcessActionCallback(T data);
    private List<T> Queue = new List<T>();
    private List<T> temp = null;
    private readonly object QueueLock = new object();
    private readonly ProcessActionCallback callback;
    private bool Stopped = false;
    private readonly int DelayTime = 10;
    private readonly string Name;

    public ActionProcessor(string name, ProcessActionCallback callback)
    {
      Name = name;
      this.callback = callback;
      Task.Run(() => Process());
    }

    public void Add(T data)
    {
      lock (QueueLock)
      {
        Queue.Add(data);
      }
    }

    public void Add(List<T> list)
    {
      lock (QueueLock)
      {
        Queue.AddRange(list);
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

    private void Process()
    {       
      while(!Stopped)
      {
        bool needSleep = false;

        lock (QueueLock)
        {
          if (Queue.Count > 0)
          {
            temp = Queue;
            Queue = new List<T>();
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
          }
        }
      }
    }
  }
}
