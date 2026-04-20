using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  internal class AdpsTracker
  {
    private static AdpsTracker _instance;
    internal static AdpsTracker Instance
    {
      get => _instance ??= new();
      set => _instance = value;
    }

    internal uint MyNukeCritRateMod { get; private set; }
    internal uint MyDoTCritRateMod { get; private set; }

    internal Dictionary<string, Dictionary<string, uint>> AdpsValues => _adpsValues;
    internal List<string> AdpsKeys => _adpsKeys;

    private readonly List<string> _adpsKeys = ["#DoTCritRate", "#NukeCritRate"];
    private readonly object _adpsLock = new();
    private readonly Dictionary<string, Dictionary<string, uint>> _adpsValues = [];
    private readonly Dictionary<string, Dictionary<string, uint>> _adpsActive = [];
    private readonly Dictionary<string, HashSet<SpellData>> _adpsLandsOn = [];
    private readonly Dictionary<string, HashSet<SpellData>> _adpsWearOff = [];

    internal AdpsTracker()
    {
      _adpsKeys.ForEach(k => _adpsActive[k] = []);
      _adpsKeys.ForEach(k => _adpsValues[k] = []);

      var key = "";
      foreach (var line in ConfigUtil.ReadList(@"data\adpsMeter.txt"))
      {
        if (!string.IsNullOrEmpty(line) && line.Trim() is { Length: > 0 } trimmed)
        {
          if (trimmed[0] != '#' && !string.IsNullOrEmpty(key))
          {
            if (trimmed.Split('|') is { Length: > 0 } multiple)
            {
              foreach (var spellLine in multiple)
              {
                if (spellLine.Split('=') is { Length: 2 } list && uint.TryParse(list[1], out var rate))
                {
                  if (GetAdpsByName(list[0]) is { } spellData)
                  {
                    _adpsValues[key][spellData.NameAbbrv] = rate;

                    if (!_adpsWearOff.TryGetValue(spellData.WearOff, out _))
                    {
                      _adpsWearOff[spellData.WearOff] = [];
                    }

                    _adpsWearOff[spellData.WearOff].Add(spellData);

                    if (!_adpsLandsOn.TryGetValue(spellData.LandsOnYou, out _))
                    {
                      _adpsLandsOn[spellData.LandsOnYou] = [];
                    }

                    _adpsLandsOn[spellData.LandsOnYou].Add(spellData);
                  }
                }
              }
            }
          }
          else if (_adpsKeys.Contains(trimmed))
          {
            key = trimmed;
          }
        }
      }
    }

    internal void UpdateLandsOnYou(string landsOn, uint rate)
    {
      lock (_adpsLock)
      {
        _adpsActive["#DoTCritRate"][landsOn] = rate;
        Recalculate();
      }
    }

    internal void UpdateAdps(SpellData spellData)
    {
      lock (_adpsLock)
      {
        foreach (var key in _adpsKeys)
        {
          if (_adpsValues[key].TryGetValue(spellData.NameAbbrv, out var value))
          {
            var landsOn = string.IsNullOrEmpty(spellData.LandsOnYou) ? spellData.Name : spellData.LandsOnYou;
            _adpsActive[key][landsOn] = value;
          }
        }
        Recalculate();
      }
    }

    internal void RemoveWearOff(SpellData spellData)
    {
      lock (_adpsLock)
      {
        var msg = string.IsNullOrEmpty(spellData.LandsOnYou) ? spellData.NameAbbrv : spellData.LandsOnYou;
        foreach (var key in _adpsKeys)
        {
          var dict = _adpsActive[key];
          var foundKey = dict.Keys.FirstOrDefault(k => k.Contains(msg, StringComparison.OrdinalIgnoreCase));
          if (foundKey != null)
          {
            dict.Remove(foundKey);
          }
        }
        Recalculate();
      }
    }

    internal void RemoveSongSpells()
    {
      lock (_adpsLock)
      {
        foreach (var key in _adpsKeys)
        {
          foreach (var landsOn in _adpsActive[key].Keys.ToArray())
          {
            if (_adpsLandsOn.TryGetValue(landsOn, out var value))
            {
              if (value.Any(spellData => spellData.SongWindow))
              {
                _adpsActive[key].Remove(landsOn);
              }
            }
          }
        }
        Recalculate();
      }
    }

    internal void Clear()
    {
      lock (_adpsLock)
      {
        foreach (var key in _adpsKeys)
        {
          _adpsActive[key].Clear();
        }
        MyDoTCritRateMod = 0;
        MyNukeCritRateMod = 0;
      }
    }

    internal void SetCritRateMods(uint dot, uint nuke)
    {
      MyDoTCritRateMod = dot;
      MyNukeCritRateMod = nuke;
    }

    internal IReadOnlyCollection<SpellData> GetLandsOnSpells(string landsOnKey)
    {
      return _adpsLandsOn.TryGetValue(landsOnKey, out var list) && list.Count > 0 ? list : null;
    }

    internal IReadOnlyCollection<SpellData> GetWearOffSpells(string wearOffKey)
    {
      return _adpsWearOff.TryGetValue(wearOffKey, out var list) && list.Count > 0 ? list : null;
    }

    private void Recalculate()
    {
      MyDoTCritRateMod = (uint)_adpsActive[_adpsKeys[0]].Sum(kv => kv.Value);
      MyNukeCritRateMod = (uint)_adpsActive[_adpsKeys[1]].Sum(kv => kv.Value);
    }

    private static SpellData GetAdpsByName(string name)
    {
      return DataManager.Instance.GetSpellDataByName(name);
    }
  }
}
