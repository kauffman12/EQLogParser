using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser
{
  internal class FileSearcher<T>
  {
    internal event Action<int> ProgressUpdated;
    internal event Action<List<T>, List<LinePosition>> ResultsReady;

    private readonly List<string> _filePaths;
    private readonly int _finishedCount;
    private readonly object _progressLock = new();
    private readonly object _postLock = new();
    private int _progressCount;
    private int _lastTotalPercent;

    internal FileSearcher(List<string> filePaths)
    {
      _filePaths = filePaths;
      _finishedCount = 100 * filePaths.Count;
    }

    internal async Task SearchLogsAsync(double start, TimeRange maxRange, Func<string, T> processor,
            int maxDegreeOfParallelism = 2, CancellationToken token = default)
    {
      var nextToPost = 0;
      var indexedResults = new SearchResult<T>[_filePaths.Count];
      var resultsStatus = new bool[_filePaths.Count];
      var semaphore = new SemaphoreSlim(maxDegreeOfParallelism);
      var tasks = new List<Task>();
      _lastTotalPercent = 0;

      for (var i = 0; i < _filePaths.Count; i++)
      {
        var index = i;
        var path = _filePaths[i];

        await semaphore.WaitAsync(CancellationToken.None);

        tasks.Add(Task.Run(async () =>
        {
          try
          {
            var result = await SearchAsync(path, start, maxRange, processor, token);
            indexedResults[index] = result;
          }
          finally
          {
            semaphore.Release();
          }

          lock (_postLock)
          {
            resultsStatus[index] = true;
            while (nextToPost < resultsStatus.Length && resultsStatus[nextToPost])
            {
              var result = indexedResults[nextToPost];
              if (result?.Lines != null && result.Lines.Count > 0)
              {
                ResultsReady?.Invoke(result.Lines, result.Positions);
                indexedResults[nextToPost] = null;
              }

              // Mark it consumed
              nextToPost++;
            }
          }

        }, CancellationToken.None));
      }

      await Task.WhenAll(tasks);
    }

    private async Task<SearchResult<T>> SearchAsync(string filePath, double start, TimeRange maxRange, Func<string, T> processor, CancellationToken token = default)
    {
      var lastPercent = 0;
      var results = new SearchResult<T>();

      try
      {
        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
          using var f = File.OpenRead(filePath);
          using var reader = FileUtil.GetStreamReader(f);

          string line = null;
          while ((line = await reader.ReadLineAsync(CancellationToken.None)) != null)
          {
            if (token.IsCancellationRequested)
            {
              break;
            }

            var percent = Math.Min(Convert.ToInt32((double)f.Position / f.Length * 100), 100);
            if (percent > 0 && percent % 5 == 0 && percent != lastPercent)
            {
              UpdateProgress(5);
              lastPercent = percent;
            }

            if (TimeRange.TimeCheck(line, start, maxRange, out var exceeds))
            {
              if (processor(line) is { } found)
              {
                results.Positions.Add(new LinePosition { File = filePath, Position = f.Position });
                results.Lines.Add(found);
              }
            }

            if (exceeds)
            {
              break;
            }
          }
        }
      }
      catch (Exception)
      {
        // ignore
      }

      var remaining = Math.Max(0, 100 - lastPercent);
      if (remaining > 0)
      {
        UpdateProgress(remaining);
      }

      void UpdateProgress(int value)
      {
        var sendUpdate = false;
        lock (_progressLock)
        {
          _progressCount += value;
          var totalPercent = (int)(_progressCount * 100.0 / _finishedCount);
          if (totalPercent % 5 == 0 && totalPercent != _lastTotalPercent)
          {
            sendUpdate = true;
            _lastTotalPercent = totalPercent;
          }
        }

        if (sendUpdate)
        {
          ProgressUpdated?.Invoke(_lastTotalPercent);
        }
      }

      return results;
    }

    private class SearchResult<U>
    {
      internal List<LinePosition> Positions = [];
      internal List<U> Lines = [];
    }

    internal class LinePosition
    {
      public string File { get; init; }
      public long Position { get; init; }
    }
  }
}
