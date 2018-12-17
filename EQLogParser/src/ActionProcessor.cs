using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser
{
  class ActionProcessor
  {
    public delegate void ProcessActionCallback(object data);
    private ConcurrentQueue<object> Queue = new ConcurrentQueue<object>();
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

    public void LowerPriority()
    {
      delayTime = 300;
    }

    public void AppendToQueue(object data)
    {
      Queue.Enqueue(data);
    }

    public long QueueSize()
    {
      return Queue.Count;
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

        while (!stopped && !Queue.IsEmpty && Queue.TryDequeue(out data))
        {
          callback(data);
        }

        if (Queue.IsEmpty)
        {
          Thread.Sleep(delayTime);
        }
      }
    }
  }
}
