using LiteDB;
using log4net;
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;

namespace EQLogParser
{
  internal class LiteDbTaskQueue
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private readonly BlockingCollection<Func<Task>> _taskQueue = new();
    private readonly LiteDatabase _database;
    private readonly TaskCompletionSource<bool> _cts = new();
    private const int MaxRetries = 3;
    private bool _ready;

    internal LiteDbTaskQueue(LiteDatabase database)
    {
      _database = database;
      _ready = true;
      Task.Run(ProcessQueue);
    }

    public Task<T> Enqueue<T>(Func<Task<T>> taskGenerator)
    {
      return EnqueueInternal(taskGenerator);
    }

    public Task Enqueue(Func<Task> taskGenerator)
    {
      return EnqueueInternal(async () =>
      {
        await taskGenerator();
        return true;
      });
    }

    internal Task EnqueueTransaction(Func<Task> taskGenerator)
    {
      return EnqueueTransactionInternal(async () =>
      {
        await taskGenerator();
        return true;
      });
    }

    internal Task<T> EnqueueTransaction<T>(Func<Task<T>> taskGenerator)
    {
      return EnqueueTransactionInternal(taskGenerator);
    }

    internal async Task Stop()
    {
      _taskQueue.CompleteAdding();
      await _cts.Task;
      _taskQueue.Dispose();
      if (_database.BeginTrans())
      {
        _database.Checkpoint();
        _database.Commit();
      }

      _database.Dispose();
    }

    private Task<T> EnqueueInternal<T>(Func<Task<T>> taskGenerator)
    {
      var tcs = new TaskCompletionSource<T>();
      if (!_ready)
      {
        tcs.SetCanceled();
        return tcs.Task;
      }

      _taskQueue.Add(async () =>
      {
        try
        {
          var result = await taskGenerator();
          tcs.SetResult(result);
        }
        catch (Exception ex)
        {
          tcs.SetException(ex);
        }
      });

      return tcs.Task;
    }

    private Task<T> EnqueueTransactionInternal<T>(Func<Task<T>> taskGenerator)
    {
      var tcs = new TaskCompletionSource<T>();
      if (!_ready)
      {
        tcs.SetCanceled();
        return tcs.Task;
      }

      _taskQueue.Add(async () =>
      {
        var retryCount = 0;
        while (retryCount < MaxRetries)
        {
          if (_database.BeginTrans())
          {
            try
            {
              var result = await taskGenerator();
              _database.Commit();
              tcs.SetResult(result);
            }
            catch (Exception ex)
            {
              _database.Rollback();
              Log.Error("Trigger DB update failed. Rolling back changes.", ex);
              tcs.SetException(ex);
            }

            break; // Break out of the while loop if transaction starts and completes
          }

          if (++retryCount > MaxRetries)
          {
            var error = new InvalidOperationException("Trigger DB transaction failed after maximum retries.");
            Log.Warn(error.Message);
            tcs.SetException(error);
          }
          else
          {
            await Task.Delay(100);
          }
        }
      });

      return tcs.Task;
    }


    private async Task ProcessQueue()
    {
      foreach (var taskGenerator in _taskQueue.GetConsumingEnumerable())
      {
        if (_taskQueue.IsCompleted) break;
        await taskGenerator();
      }

      _ready = false;
      _cts.SetResult(true);
    }
  }
}
