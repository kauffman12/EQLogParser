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
    private object QueueLock = new object();
    private ProcessActionCallback callback;
    private bool stopped = false;
    private int delayTime = 10;
    private string name;

    public ActionProcessor(string name, ProcessActionCallback callback)
    {
      this.name = name;
      this.callback = callback;
      Task.Run((() => Process()));
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
      stopped = true;
    }

    private void Process()
    {       
      while(!stopped)
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
          Thread.Sleep(delayTime);
        }
        else if (temp != null)
        {
          foreach (var item in temp)
          {
            if (stopped)
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
