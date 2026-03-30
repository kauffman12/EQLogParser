using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EQLogParser
{
  internal class TimerDataUtil
  {
    internal static bool CheckEndEarly(Regex endEarlyRegex, List<NumberOptions> options, string endEarlyPattern,
      string action, out Dictionary<string, string> earlyMatches)
    {
      earlyMatches = null;
      var endEarly = false;

      if (endEarlyRegex != null)
      {
        try
        {
          if (TextUtils.SnapshotMatches(endEarlyRegex.Matches(action), out earlyMatches) && TriggerUtil.CheckOptions(options, earlyMatches, out _))
          {
            endEarly = true;
          }
        }
        catch (RegexMatchTimeoutException)
        {
          // ignore
        }
      }
      else if (!string.IsNullOrEmpty(endEarlyPattern))
      {
        if (action.Contains(endEarlyPattern, StringComparison.OrdinalIgnoreCase))
        {
          endEarly = true;
        }
      }

      return endEarly;
    }

    // make sure each call is from within lock of TimerList
    internal static void CleanupTimerData(TimerData timerData)
    {
      timerData.CancelSource?.Cancel();
      timerData.CancelSource?.Dispose();
      timerData.CancelSource = null;
      timerData.Canceled = true;
      timerData.WarningSource?.Cancel();
      timerData.WarningSource?.Dispose();
      timerData.WarningSource = null;
      timerData.Warned = true;
    }

    // make sure each call is from within lock of TimerList
    internal static async Task CleanupTimersAsync(List<TimerData> timerList, Trigger trigger)
    {
      if (timerList.Count == 0)
      {
        return;
      }

      TimerData[] toRemove;

      lock (timerList)
      {
        foreach (var timerData in timerList)
        {
          CleanupTimerData(timerData);
        }

        toRemove = [.. timerList];
        timerList.Clear();
      }

      // stop timer
      foreach (var timerData in toRemove)
      {
        await TriggerOverlayManager.Instance.UpdateTimerAsync(trigger, timerData, TriggerOverlayManager.TimerStateChange.Stop);
      }
    }

    // avoid LINQ for performance
    internal static int FindTimerDataByDisplayName(List<TimerData> list, string name)
    {
      for (var i = 0; i < list.Count; i++)
      {
        if (string.Equals(list[i].DisplayName, name, StringComparison.OrdinalIgnoreCase))
        {
          return i;
        }
      }

      return -1;
    }
  }
}
