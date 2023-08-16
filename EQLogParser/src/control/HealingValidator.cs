using System.Collections.Generic;

namespace EQLogParser
{
  internal class HealingValidator
  {
    private bool AOEEnabled;
    private bool SwarmPetsEnabled;

    public HealingValidator()
    {
      AOEEnabled = MainWindow.IsAoEHealingEnabled;
      SwarmPetsEnabled = MainWindow.IsHealingSwarmPetsEnabled;
    }

    public bool IsValid(ActionBlock heal, HealRecord record, Dictionary<string, HashSet<string>> currentSpellCounts,
      Dictionary<double, Dictionary<string, HashSet<string>>> previousSpellCounts, Dictionary<string, byte> ignoreRecords)
    {
      if (!SwarmPetsEnabled)
      {
        if (record?.Healed?.EndsWith("`s pet") == true || record?.Healed?.EndsWith("`s ward") == true)
        {
          return false;
        }
      }

      // if AOEHealing is disabled then filter out AEs
      if (!AOEEnabled)
      {
        SpellData spellData;
        if (record.SubType != null && (spellData = DataManager.Instance.GetHealingSpellByName(record.SubType)) != null)
        {
          if (spellData.Target == (byte)SpellTarget.TARGETAE || spellData.Target == (byte)SpellTarget.NEARBYPLAYERSAE ||
            spellData.Target == (byte)SpellTarget.TARGETRINGAE || spellData.Target == (byte)SpellTarget.CASTERPBPLAYERS)
          {
            // just skip these entirely if AOEs are turned off
            return false;
          }
          else if ((spellData.Target == (byte)SpellTarget.CASTERGROUP || spellData.Target == (byte)SpellTarget.TARGETGROUP) && spellData.Mgb)
          {
            // need to count group AEs and if more than 6 are seen we need to ignore those
            // casts since they're from MGB and count as an AE
            var key = record.Healer + "|" + record.SubType;
            if (!currentSpellCounts.TryGetValue(key, out var value))
            {
              value = new HashSet<string>();
              currentSpellCounts[key] = value;
            }

            value.Add(record.Healed);

            var totals = new HashSet<string>();
            var temp = new List<double>();
            foreach (var timeKey in previousSpellCounts.Keys)
            {
              if (previousSpellCounts[timeKey].ContainsKey(key))
              {
                foreach (var item in previousSpellCounts[timeKey][key])
                {
                  totals.Add(item);
                }

                temp.Add(timeKey);
              }
            }

            foreach (var item in currentSpellCounts[key])
            {
              totals.Add(item);
            }

            if (totals.Count > 6)
            {
              ignoreRecords[heal.BeginTime + "|" + key] = 1;
              temp.ForEach(timeKey => ignoreRecords[timeKey + "|" + key] = 1);
            }
          }
        }
      }

      return true;
    }

    public bool IsHealingLimited()
    {
      return !AOEEnabled || !SwarmPetsEnabled;
    }
  }
}
