using log4net;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser
{
  public class ArchiveScheduler : IDisposable
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private Timer _timer;
    private readonly object _lock = new();
    private bool _enabled;

    private readonly DayOfWeek _day;
    private readonly TimeSpan _timeOfDay;

    public ArchiveScheduler(string dayName, int hour12, int minute, string ampm)
    {
      if (!Enum.TryParse(dayName, true, out DayOfWeek parsedDay))
      {
        Log.Error($"Invalid day name: {dayName}");
        _enabled = false;
        return;
      }

      // Normalize hour to 24h
      var hour24 = hour12 % 12;
      if (ampm.Equals("PM", StringComparison.OrdinalIgnoreCase))
      {
        hour24 += 12;
      }

      var newTime = new TimeSpan(hour24, minute, 0);

      lock (_lock)
      {
        _day = parsedDay;
        _timeOfDay = newTime;
        _enabled = true;

        ScheduleNextRun();
      }
    }

    private void ScheduleNextRun()
    {
      var now = DateTime.Now;
      var next = GetNextOccurrence(now);

      var delay = next - now;
      if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

      Log.Info("Log Archive Schedule created for " + next);
      _timer ??= new Timer(OnTimerElapsed);
      _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void OnTimerElapsed(object state)
    {
      // Run async to not block timer thread
      Task.Run(async () =>
      {
        try
        {
          if (_enabled)
          {
            var logFiles = await FileUtil.GetOpenLogFilesAsync();
            await FileUtil.ArchiveNowAsync(logFiles);
          }
        }
        finally
        {
          lock (_lock)
          {
            if (_enabled)
            {
              ScheduleNextRun(); // Reschedule for next week
            }
          }
        }
      });
    }

    private DateTime GetNextOccurrence(DateTime now)
    {
      // Start from today at the target time
      var next = DateTime.Today.Add(_timeOfDay);

      // If it's not the correct day yet, or the time already passed today
      var daysUntil = (_day - now.DayOfWeek + 7) % 7;
      if (daysUntil == 0 && next <= now)
      {
        daysUntil = 7;
      }

      next = next.AddDays(daysUntil);
      return next;
    }

    public void Dispose()
    {
      lock (_lock)
      {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _timer?.Dispose();
        _enabled = false;
      }
    }
  }
}
