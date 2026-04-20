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
    private readonly bool _isDaily;
    private readonly DayOfWeek _day;
    private readonly TimeSpan _timeOfDay;

    public ArchiveScheduler(string dayName, int hour12, int minute, string ampm)
    {
      if (string.IsNullOrWhiteSpace(dayName))
      {
        Log.Error("Day name is required.");
        _enabled = false;
        return;
      }

      if (hour12 < 1 || hour12 > 12)
      {
        Log.Error($"Invalid hour: {hour12}");
        _enabled = false;
        return;
      }

      if (minute < 0 || minute > 59)
      {
        Log.Error($"Invalid minute: {minute}");
        _enabled = false;
        return;
      }

      if (!string.Equals(ampm, "AM", StringComparison.OrdinalIgnoreCase) &&
          !string.Equals(ampm, "PM", StringComparison.OrdinalIgnoreCase))
      {
        Log.Error($"Invalid AM/PM value: {ampm}");
        _enabled = false;
        return;
      }

      // Support special value: "Daily"
      if (dayName.Equals("Daily", StringComparison.OrdinalIgnoreCase))
      {
        _isDaily = true;
      }
      else if (!Enum.TryParse(dayName, true, out DayOfWeek parsedDay))
      {
        Log.Error($"Invalid day name: {dayName}");
        _enabled = false;
        return;
      }
      else
      {
        _day = parsedDay;
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
      if (delay < TimeSpan.Zero)
      {
        delay = TimeSpan.Zero;
      }

      Log.Info("Log Archive Schedule created for " + next);
      _timer ??= new Timer(OnTimerElapsed);
      _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void OnTimerElapsed(object state)
    {
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
              ScheduleNextRun(); // Reschedule for next occurrence
            }
          }
        }
      });
    }

    private DateTime GetNextOccurrence(DateTime now)
    {
      var next = now.Date.Add(_timeOfDay);

      if (_isDaily)
      {
        // If today's time has already passed, schedule for tomorrow
        if (next <= now)
        {
          next = next.AddDays(1);
        }

        return next;
      }

      var daysUntil = (_day - now.DayOfWeek + 7) % 7;
      next = next.AddDays(daysUntil);

      // If it's the target day but time has already passed, go to next week
      if (next <= now)
      {
        next = next.AddDays(7);
      }

      return next;
    }

    public void Dispose()
    {
      lock (_lock)
      {
        _enabled = false;
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer?.Dispose();
        _timer = null;
      }
    }
  }
}
