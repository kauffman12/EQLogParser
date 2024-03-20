using System.Collections.Generic;

namespace EQLogParser
{
  internal class HealingValidator
  {
    private readonly bool _aoeEnabled = MainWindow.IsAoEHealingEnabled;
    private readonly bool _swarmPetsEnabled = MainWindow.IsHealingSwarmPetsEnabled;

    public bool IsValid(double beginTime, HealRecord record, Dictionary<string, HashSet<string>> currentSpellCounts,
      Dictionary<double, Dictionary<string, HashSet<string>>> previousSpellCounts, Dictionary<string, byte> ignoreRecords)
    {
      if (!_swarmPetsEnabled)
      {
        if (record?.Healed?.EndsWith("`s pet") == true || record?.Healed?.EndsWith("`s ward") == true)
        {
          return false;
        }
      }

      // if AOEHealing is disabled then filter out AEs
      if (!_aoeEnabled)
      {
        SpellData spellData;
        if (record?.SubType != null && (spellData = DataManager.Instance.GetHealingSpellByName(record.SubType)) != null)
        {
          if (spellData.Target is (byte)SpellTarget.Targetae or (byte)SpellTarget.Nearbyplayersae
              or (byte)SpellTarget.Targetringae or (byte)SpellTarget.Casterpbplayers)
          {
            // just skip these entirely if AOEs are turned off
            return false;
          }

          if (spellData.Target is (byte)SpellTarget.Castergroup or (byte)SpellTarget.Targetgroup && spellData.Mgb)
          {
            // need to count group AEs and if more than 6 are seen we need to ignore those
            // casts since they're from MGB and count as an AE
            var key = record.Healer + "|" + record.SubType;
            if (!currentSpellCounts.TryGetValue(key, out var current))
            {
              current = [];
              currentSpellCounts[key] = current;
            }

            current.Add(record.Healed);

            var totals = new HashSet<string>();
            var temp = new List<double>();
            foreach (var timeKey in previousSpellCounts.Keys)
            {
              if (previousSpellCounts[timeKey].TryGetValue(key, out var value))
              {
                foreach (var item in value)
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
              ignoreRecords[beginTime + "|" + key] = 1;
              temp.ForEach(timeKey => ignoreRecords[timeKey + "|" + key] = 1);
            }
          }
        }
      }

      return true;
    }

    public bool IsHealingLimited()
    {
      return !_aoeEnabled || !_swarmPetsEnabled;
    }
  }
}
