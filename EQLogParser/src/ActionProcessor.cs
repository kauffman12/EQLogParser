using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser
{
  class ActionProcessor
  {
    public const int DELAY_TIME = 200;
    public delegate void ProcessActionCallback(object data);
    private ConcurrentQueue<object> Queue = new ConcurrentQueue<object>();
    private ConcurrentQueue<object> Priority = new ConcurrentQueue<object>();
    private ProcessActionCallback callback;
    private static bool stopped = false;

    public ActionProcessor(ProcessActionCallback callback)
    {
      this.callback = callback;
      Task.Run((() => Process()));
    }

    public void AppendToQueue(object data)
    {
      Queue.Enqueue(data);
    }

    public void PrependToQueue(object data)
    {
      Priority.Enqueue(data);
    }

    public long QueueSize()
    {
      return Queue.Count + Priority.Count;
    }

    public void Stop()
    {
      stopped = true;
    }

    private void Process()
    {
      while(!stopped)
      {
        object data;

        while (!stopped && !Priority.IsEmpty && Priority.TryDequeue(out data))
        {
          callback(data);
        }

        if (!stopped && !Queue.IsEmpty && Queue.TryDequeue(out data))
        {
          callback(data);
        }

        if (Priority.IsEmpty && Queue.IsEmpty)
        {
          Thread.Sleep(DELAY_TIME);
        }
      }
    }
  }
}
