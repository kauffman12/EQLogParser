using System.Collections.Generic;

namespace EQLogParser
{
  internal class HealingValidator
  {
    private readonly bool _aoeEnabled;
    private readonly bool _swarmPetsEnabled;

    public HealingValidator()
    {
      _aoeEnabled = MainWindow.IsAoEHealingEnabled;
      _swarmPetsEnabled = MainWindow.IsHealingSwarmPetsEnabled;
    }

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
        if (record.SubType != null && (spellData = DataManager.Instance.GetHealingSpellByName(record.SubType)) != null)
        {
          if (spellData.Target == (byte)SpellTarget.Targetae || spellData.Target == (byte)SpellTarget.Nearbyplayersae ||
              spellData.Target == (byte)SpellTarget.Targetringae || spellData.Target == (byte)SpellTarget.Casterpbplayers)
          {
            // just skip these entirely if AOEs are turned off
            return false;
          }

          if ((spellData.Target == (byte)SpellTarget.Castergroup || spellData.Target == (byte)SpellTarget.Targetgroup) && spellData.Mgb)
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
