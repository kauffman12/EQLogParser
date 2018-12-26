using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EQLogParser
{
  class NpcDamageManager
  {
    public DateTime LastUpdateTime { get; set; }

    private List<DamageAtTime> DamageTimeLine;
    private DamageAtTime DamageAtThisTime = null;
    private const int NPC_DEATH_TIME = 25;
    private long CurrentNpcID = 0;
    private int CurrentFightID = 0;

    public NpcDamageManager()
    {
      DamageTimeLine = new List<DamageAtTime>();
      DamageLineParser.EventsDamageProcessed += HandleDamageProcessed;
      DataManager.Instance.EventsClearedActiveData += (sender, cleared) =>
      {
        CurrentFightID = 0;
        DamageTimeLine = new List<DamageAtTime>();
      };
    }

    ~NpcDamageManager()
    {
      DamageLineParser.EventsDamageProcessed -= HandleDamageProcessed;
    }

    private void HandleDamageProcessed(object sender, DamageProcessedEvent processed)
    {
      if (processed.Record != null && LastUpdateTime != DateTime.MinValue)
      {
        TimeSpan diff = processed.ProcessLine.CurrentTime.Subtract(LastUpdateTime);
        if (diff.TotalSeconds > 120)
        {
          CurrentFightID++;
          DataManager.Instance.AddNonPlayerMapBreak(Helpers.FormatTimeSpan(diff));
        }
      }

      AddOrUpdateNpc(processed.Record, processed.ProcessLine.CurrentTime, processed.ProcessLine.TimeString.Substring(4, 15));
    }

    private void AddOrUpdateNpc(DamageRecord record, DateTime currentTime, String origTimeString)
    {
      NonPlayer npc = Get(record, currentTime, origTimeString);

      // assume npc has been killed and create new entry
      if (currentTime.Subtract(npc.LastTime).TotalSeconds > NPC_DEATH_TIME)
      {
        DataManager.Instance.RemoveActiveNonPlayer(npc.CorrectMapKey);
        npc = Get(record, currentTime, origTimeString);
      }

      if (!npc.DamageMap.ContainsKey(record.Attacker))
      {
        npc.DamageMap.Add(record.Attacker, new DamageStats()
        {
          BeginTime = currentTime,
          Owner = "",
          IsPet = false,
          HitMap = new Dictionary<string, Hit>()
        });
      }

      npc.LastTime = currentTime;
      LastUpdateTime = currentTime;

      // update basic stats
      DamageStats stats = npc.DamageMap[record.Attacker];
      if (!stats.HitMap.ContainsKey(record.Type))
      {
        stats.HitMap[record.Type] = new Hit() { Count = 0, Max = 0, TotalDamage = 0 };
      }

      stats.Count++;
      stats.TotalDamage += record.Damage;
      stats.Max = (stats.Max < record.Damage) ? record.Damage : stats.Max;
      stats.HitMap[record.Type].Count++;
      stats.HitMap[record.Type].TotalDamage += record.Damage;
      stats.HitMap[record.Type].Max = (stats.HitMap[record.Type].Max < record.Damage) ? record.Damage : stats.HitMap[record.Type].Max;

      UpdateModifiers(stats, record);
      stats.LastTime = currentTime;

      if (record.AttackerPetType != "")
      {
        stats.IsPet = true;
        stats.Owner = record.AttackerOwner;
      }

      if (DamageAtThisTime == null)
      {
        DamageAtThisTime = new DamageAtTime() { CurrentTime= currentTime, PlayerDamage = new Dictionary<string, long>(), FightID = CurrentFightID };
        DamageTimeLine.Add(DamageAtThisTime);
      }
      else if (currentTime.Subtract(DamageAtThisTime.CurrentTime).TotalSeconds >= 1) // EQ granular to 1 second
      {
        DamageAtThisTime = new DamageAtTime() { CurrentTime = currentTime, PlayerDamage = new Dictionary<string, long>(), FightID = CurrentFightID };
        DamageTimeLine.Add(DamageAtThisTime);
      }

      if (!DamageAtThisTime.PlayerDamage.ContainsKey(record.Attacker))
      {
        DamageAtThisTime.PlayerDamage[record.Attacker] = 0;
      }

      DamageAtThisTime.PlayerDamage[record.Attacker] += record.Damage;
      DamageAtThisTime.CurrentTime = currentTime;

      DataManager.Instance.UpdateIfNewNonPlayerMap(npc.CorrectMapKey, npc);
    }

    public DPSChartData GetDPSValues(CombinedStats combined, List<PlayerStats> stats)
    {
      Dictionary<int, DateTime> firstTimes = new Dictionary<int, DateTime>();
      Dictionary<int, DateTime> lastTimes = new Dictionary<int, DateTime>();
      DamageAtTimeComparer comparer = new DamageAtTimeComparer();
      int interval = combined.TimeDiff < 12 ? 1 : (int) combined.TimeDiff / 12;

      stats = stats.OrderBy(item => item.Name).ToList();
      // establish range of time for each fight counting all players
      stats.ForEach(s =>
      {
        foreach (var fightId in s.BeginTimes.Keys)
        {
          if (!firstTimes.ContainsKey(fightId))
          {
            firstTimes[fightId] = s.BeginTimes[fightId];
          }
          else if (firstTimes[fightId] > s.BeginTimes[fightId])
          {
            firstTimes[fightId] = s.BeginTimes[fightId];
          }

          if (!lastTimes.ContainsKey(fightId))
          {
            lastTimes[fightId] = s.LastTimes[fightId];
          }
          else if (lastTimes[fightId] < s.LastTimes[fightId])
          {
            lastTimes[fightId] = s.LastTimes[fightId];
          }
        }
      });

      List<string> labels = new List<string>();
      DictionaryAddHelper<string, long> addHelper = new DictionaryAddHelper<string, long>();
      Dictionary<string, List<long>> playerValues = new Dictionary<string, List<long>>();

      foreach (var fightId in firstTimes.Keys.OrderBy(key => key))
      {
        DateTime firstTime = firstTimes[fightId];
        DateTime lastTime = lastTimes[fightId];
        DateTime currentTime = firstTime;

        Dictionary<string, long> playerTotals = new Dictionary<string, long>();
        int index = DamageTimeLine.BinarySearch(new DamageAtTime() { CurrentTime = firstTime }, comparer);

        labels.Add(Helpers.FormatDateTime(firstTime));
        while (currentTime <= lastTime)
        {
          while (DamageTimeLine.Count > index && DamageTimeLine[index].CurrentTime <= currentTime) 
          {
            Dictionary<string, long> playerDamage = DamageTimeLine[index].PlayerDamage;
            foreach(var stat in stats)
            {
              if (combined.Children.ContainsKey(stat.Name))
              {
                combined.Children[stat.Name].ForEach(child =>
                {
                  if (playerDamage.ContainsKey(child.Name))
                  {
                    addHelper.Add(playerTotals, stat.Name, playerDamage[child.Name]);
                  }
                });
              }
              else if (playerDamage.ContainsKey(stat.Name))
              {
                addHelper.Add(playerTotals, stat.Name, playerDamage[stat.Name]);
              }
            }

            index++;
          }

          foreach (var stat in stats)
          {
            if (!playerValues.ContainsKey(stat.Name))
            {
              playerValues[stat.Name] = new List<long>();
            }

            long dps = 0;
            if (playerTotals.ContainsKey(stat.Name))
            {
              dps = (long) Math.Round(playerTotals[stat.Name] / ((currentTime - firstTime).TotalSeconds + 1));
            }

            playerValues[stat.Name].Add(dps);
          }

          labels.Add(Helpers.FormatDateTime(currentTime));

          if (currentTime == lastTime)
          {
            break;
          }
          else
          {
            currentTime = currentTime.AddSeconds(interval);
            if (currentTime > lastTime)
            {
              currentTime = lastTime;
            }
          }
        }
      }

      // scale down if too many points
      int scale = -1;
      var firstPlayer = playerValues.Values.First();
      if (firstPlayer.Count > 20)
      {
        scale = firstPlayer.Count / 20;
      }

      if (scale > 1)
      {
        Dictionary<string, List<long>> newPlayerValues = new Dictionary<string, List<long>>();
        Parallel.ForEach(playerValues.Keys, (player) =>
        {
          var newList = playerValues[player].Where((item, i) => i % scale == 0).ToList();

          lock(newPlayerValues)
          {
            newPlayerValues[player] = newList;
          }
        });

        playerValues = newPlayerValues;
        labels = labels.Where((item, i) => i % scale == 0).ToList();
      }

      return new DPSChartData() { Values = playerValues, XAxisLabels = labels };
    }

    private void UpdateModifiers(DamageStats stats, DamageRecord record)
    {
      bool wild = false;
      bool rampage = false;
      foreach (string modifier in record.Modifiers.Keys)
      {
        switch (modifier)
        {
          case "Bow": // Double Bow Shot
            stats.DoubleBowShotCount++;
            stats.HitMap[record.Type].DoubleBowShotCount++;
            break;
          case "Flurry":
            stats.FlurryCount++;
            break;
          case "Lucky":
            stats.LuckyCount++;
            stats.TotalLuckyDamage += record.Damage;
            stats.HitMap[record.Type].LuckyCount++;
            stats.HitMap[record.Type].TotalLuckyDamage += record.Damage;
            // workaround to keep better keep track of avg base crit damage
            stats.TotalCritDamage -= record.Damage;
            stats.HitMap[record.Type].TotalCritDamage -= record.Damage;
            break;
          case "Critical":
          case "Crippling": // Crippling Blow
          case "Deadly":    // Deadly Strike
            stats.CritCount++;
            stats.TotalCritDamage += record.Damage;
            stats.HitMap[record.Type].CritCount++;
            stats.HitMap[record.Type].TotalCritDamage += record.Damage;
            break;
          case "Rampage":
            rampage = true;
            break;
          case "Twincast":
            stats.TwincastCount++;
            stats.HitMap[record.Type].TwincastCount++;
            break;
          case "Undead":
            stats.SlayUndeadCount++;
            break;
          case "Wild":
            wild = true;
            break;
        }
      }

      if (rampage && !wild)
      {
        stats.RampageCount++;
      }
      else if (rampage && wild)
      {
        stats.WildRampageCount++;
      }
    }

    private NonPlayer Get(DamageRecord record, DateTime currentTime, String origTimeString)
    {
      NonPlayer npc = DataManager.Instance.GetNonPlayer(record.Defender);

      if (npc == null && Char.IsUpper(record.Defender[0]) && record.Action == "DoT")
      {
        // DoTs will show upper case when they shouldn't because they start a sentence
        npc = DataManager.Instance.GetNonPlayer(Char.ToLower(record.Defender[0]) + record.Defender.Substring(1));
      }
      else if (npc == null && Char.IsLower(record.Defender[0]) && record.Action == "DD")
      {
        // DDs deal with having to work around DoTs
        npc = DataManager.Instance.GetNonPlayer(Char.ToUpper(record.Defender[0]) + record.Defender.Substring(1));
      }

      if (npc == null)
      {
        npc = new NonPlayer()
        {
          Name = record.Defender,
          BeginTimeString = origTimeString,
          BeginTime = currentTime,
          LastTime = currentTime,
          DamageMap = new Dictionary<string, DamageStats>(),
          ID = CurrentNpcID++,
          FightID = CurrentFightID,
          CorrectMapKey = record.Defender
        };
      }

      return npc;
    }

    private class DamageAtTimeComparer : IComparer<DamageAtTime>
    {
      public int Compare(DamageAtTime x, DamageAtTime y)
      {
        return x.CurrentTime.CompareTo(y.CurrentTime);
      }
    }
  }
}
