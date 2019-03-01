using System;
using System.Collections.Generic;
using System.Threading;

namespace EQLogParser
{
  class StatsBuilder
  {
    protected const string RAID_PLAYER = "Totals";
    protected const string TIME_FORMAT = "in {0}s";
    protected const string TOTAL_FORMAT = "{0} @{1}";
    protected const string PLAYER_FORMAT = "{0} = ";
    protected const string PLAYER_RANK_FORMAT = "{0}. {1} = ";

    protected static PlayerStats CreatePlayerStats(string name, string origName = null)
    {
      string className = "";
      origName = origName == null ? name : origName;

      if (!DataManager.Instance.CheckNameForPet(origName))
      {
        className = DataManager.Instance.GetPlayerClass(origName);
      }

      return new PlayerStats()
      {
        Name = name,
        ClassName = className,
        OrigName = origName,
        Percent = 100, // until something says otherwise
        BeginTimes = new List<DateTime>(),
        LastTimes = new List<DateTime>(),
        SubStats = new Dictionary<string, PlayerSubStats>(),
        TimeDiffs = new List<double>()
      };
    }

    protected static string FormatTitle(string targetTitle, string timeTitle, string damageTitle = "")
    {
      string result;
      result = targetTitle + " " + timeTitle;
      if (damageTitle != "")
      {
        result += ", " + damageTitle;
      }
      return result;
    }

    protected static void UpdateTimeDiffs(PlayerSubStats subStats, TimedAction action, double offset = 0)
    {
      int currentIndex = subStats.BeginTimes.Count - 1;
      if (currentIndex == -1)
      {
        subStats.BeginTimes.Add(action.BeginTime);
        subStats.LastTimes.Add(action.LastTime.AddSeconds(offset));
        subStats.TimeDiffs.Add(0); // update afterward
        currentIndex = 0;
      }
      else if (subStats.LastTimes[currentIndex] >= action.BeginTime)
      {
        var offsetLastTime = action.LastTime.AddSeconds(offset);
        if (offsetLastTime > subStats.LastTimes[currentIndex])
        {
          subStats.LastTimes[currentIndex] = offsetLastTime;
        }
      }
      else
      {
        subStats.BeginTimes.Add(action.BeginTime);
        subStats.LastTimes.Add(action.LastTime.AddSeconds(offset));
        subStats.TimeDiffs.Add(0); // update afterward
        currentIndex++;
      }

      subStats.TimeDiffs[currentIndex] = subStats.LastTimes[currentIndex].Subtract(subStats.BeginTimes[currentIndex]).TotalSeconds + 1;
    }
  }
}
