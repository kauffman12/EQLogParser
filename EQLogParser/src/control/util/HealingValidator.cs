using System.Collections.Generic;

namespace EQLogParser
{
  internal class HealingValidator
  {
    private readonly bool _aoeEnabled;
    private readonly bool _swarmPetsEnabled;

    public HealingValidator()
    {
      _aoeEnabled = AppSettings.IsAoEHealingEnabled;
      _swarmPetsEnabled = AppSettings.IsHealingSwarmPetsEnabled;
    }

    public HealingValidator(bool aoeEnabled, bool swarmPetsEnabled)
    {
      _aoeEnabled = aoeEnabled;
      _swarmPetsEnabled = swarmPetsEnabled;
    }

    /// <summary>
    /// Validates if the heal record should be processed based on current settings.
    /// Note: AOE detection requires EQDataStore lookup which is app-specific.
    /// This overload handles basic validation (swarm pets only).
    /// </summary>
    public bool IsValid(double beginTime, HealRecord record, Dictionary<string, byte> ignoreRecords)
    {
      if (record is null)
      {
        return false;
      }

      if (!_swarmPetsEnabled)
      {
        if (record.Healed?.EndsWith("`s pet") is true || record.Healed?.EndsWith("`s ward") is true)
        {
          return false;
        }
      }

      return true;
    }

    /// <summary>
    /// Full validation with AOE detection. Requires EQDataStore for spell lookups.
    /// This is the app-specific overload that delegates to the main project's logic.
    /// </summary>
    public bool IsValid(double beginTime, HealRecord record, Dictionary<string, HashSet<string>> currentSpellCounts,
      Dictionary<double, Dictionary<string, HashSet<string>>> previousSpellCounts, Dictionary<string, byte> ignoreRecords)
    {
      if (record is null)
      {
        return false;
      }

      if (!_swarmPetsEnabled)
      {
        if (record.Healed?.EndsWith("`s pet") is true || record.Healed?.EndsWith("`s ward") is true)
        {
          return false;
        }
      }

      // if AOEHealing is disabled then filter out AEs
      if (!_aoeEnabled)
      {
        SpellData spellData;
        if (record.SubType != null && (spellData = EQDataStore.Instance.GetHealingSpellByName(record.SubType)) != null)
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

    /// <summary>
    /// Checks if healing is limited (AOE or swarm pets disabled).
    /// </summary>
    public bool IsHealingLimited()
    {
      return !_aoeEnabled || !_swarmPetsEnabled;
    }
  }
}
