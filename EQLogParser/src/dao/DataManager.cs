using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EQLogParser
{
  internal enum SpellClass
  {
    WAR = 1, CLR = 2, PAL = 4, RNG = 8, SHD = 16, DRU = 32, MNK = 64, BRD = 128, ROG = 256,
    SHM = 512, NEC = 1024, WIZ = 2048, MAG = 4096, ENC = 8192, BST = 16384, BER = 32768
  }

  internal enum SpellTarget
  {
    LOS = 1, CASTERAE = 2, CASTERGROUP = 3, CASTERPB = 4, SINGLETARGET = 5, SELF = 6, TARGETAE = 8,
    NEARBYPLAYERSAE = 40, DIRECTIONAE = 42, TARGETRINGAE = 45
  }

  class DataManager
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    internal static DataManager Instance = new DataManager();
    internal event EventHandler<Fight> EventsNewInactiveFight;
    internal event EventHandler<string> EventsRemovedFight;
    internal event EventHandler<Fight> EventsNewFight;
    internal event EventHandler<Fight> EventsRefreshFights;
    internal event EventHandler<bool> EventsClearedActiveData;

    private static readonly SpellAbbrvComparer AbbrvComparer = new SpellAbbrvComparer();
    private static readonly TimedActionComparer TAComparer = new TimedActionComparer();

    private readonly List<ActionBlock> AllDamageBlocks = new List<ActionBlock>();
    private readonly List<ActionBlock> AllHealBlocks = new List<ActionBlock>();
    private readonly List<ActionBlock> AllSpellCastBlocks = new List<ActionBlock>();
    private readonly List<ActionBlock> AllReceivedSpellBlocks = new List<ActionBlock>();
    private readonly List<ActionBlock> AllResists = new List<ActionBlock>();
    private readonly List<ActionBlock> AllLootBlocks = new List<ActionBlock>();
    private readonly List<TimedAction> AllSpecialActions = new List<TimedAction>();

    private readonly Dictionary<string, byte> AllNpcs = new Dictionary<string, byte>();
    private readonly Dictionary<string, SpellData> AllUniqueSpellsCache = new Dictionary<string, SpellData>();
    private readonly Dictionary<string, List<SpellData>> PosessiveLandsOnOthers = new Dictionary<string, List<SpellData>>();
    private readonly Dictionary<string, List<SpellData>> NonPosessiveLandsOnOthers = new Dictionary<string, List<SpellData>>();
    private readonly Dictionary<string, List<SpellData>> LandsOnYou = new Dictionary<string, List<SpellData>>();
    private readonly Dictionary<string, SpellData> SpellsNameDB = new Dictionary<string, SpellData>();
    private readonly Dictionary<string, SpellData> SpellsAbbrvDB = new Dictionary<string, SpellData>();
    private readonly Dictionary<string, SpellClass> SpellsToClass = new Dictionary<string, SpellClass>();

    private readonly ConcurrentDictionary<string, byte> AllUniqueSpellCasts = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, Fight> ActiveFights = new ConcurrentDictionary<string, Fight>();
    private readonly ConcurrentDictionary<string, byte> LifetimeFights = new ConcurrentDictionary<string, byte>();
    private readonly Dictionary<string, Dictionary<string, byte>> UnHandledLines = new Dictionary<string, Dictionary<string, byte>>();

    private DataManager()
    {
      DictionaryListHelper<string, SpellData> helper = new DictionaryListHelper<string, SpellData>();
      var spellList = new List<SpellData>();

      ConfigUtil.ReadList(@"data\spells.txt").ForEach(line =>
      {
        try
        {
          var spellData = TextFormatUtils.ParseCustomSpellData(line);
          if (spellData != null)
          {
            spellList.Add(spellData);
            SpellsNameDB[spellData.Spell] = spellData;

            if (!SpellsAbbrvDB.ContainsKey(spellData.SpellAbbrv))
            {
              SpellsAbbrvDB[spellData.SpellAbbrv] = spellData;
            }
            else if (string.Compare(SpellsAbbrvDB[spellData.SpellAbbrv].Spell, spellData.Spell, true, CultureInfo.CurrentCulture) < 0)
            {
              // try to keep the newest version
              SpellsAbbrvDB[spellData.SpellAbbrv] = spellData;
            }

            if (spellData.LandsOnOther.StartsWith("'s ", StringComparison.Ordinal))
            {
              helper.AddToList(PosessiveLandsOnOthers, spellData.LandsOnOther.Substring(3), spellData);
            }
            else if (spellData.LandsOnOther.Length > 1)
            {
              helper.AddToList(NonPosessiveLandsOnOthers, spellData.LandsOnOther.Substring(1), spellData);
            }

            if (spellData.LandsOnYou.Length > 0) // just do stuff in common
            {
              helper.AddToList(LandsOnYou, spellData.LandsOnYou, spellData);
            }
          }
        }
        catch (OverflowException ex)
        {
          LOG.Error("Error reading spell data", ex);
        }
      });

      Dictionary<string, byte> keepOut = new Dictionary<string, byte>();
      var classEnums = Enum.GetValues(typeof(SpellClass)).Cast<SpellClass>().ToList();
      spellList.ForEach(spell =>
      {
        // exact match meaning class-only spell that are of certain target types
        var tgt = (SpellTarget)spell.Target;
        if ((tgt == SpellTarget.SELF || (spell.Level <= 250 && (tgt == SpellTarget.SINGLETARGET || tgt == SpellTarget.LOS))) && classEnums.Contains((SpellClass)spell.ClassMask))
        {
          // these need to be unique and keep track if a conflict is found
          if (SpellsToClass.ContainsKey(spell.Spell))
          {
            SpellsToClass.Remove(spell.Spell);
            keepOut[spell.Spell] = 1;
          }
          else if (!keepOut.ContainsKey(spell.Spell))
          {
            SpellsToClass[spell.Spell] = (SpellClass)spell.ClassMask;
          }
        }
      });

      // load NPCs
      ConfigUtil.ReadList(@"data\npcs.txt").ForEach(line => AllNpcs[line.Trim()] = 1);

      PlayerManager.Instance.EventsNewTakenPetOrPlayerAction += (sender, name) => RemoveFight(name);
      PlayerManager.Instance.EventsNewVerifiedPlayer += (sender, name) => RemoveFight(name);
      PlayerManager.Instance.EventsNewVerifiedPet += (sender, name) => RemoveFight(name);
    }

    internal void Clear()
    {
      lock (this)
      {
        ActiveFights.Clear();
        LifetimeFights.Clear();
        AllSpellCastBlocks.Clear();
        AllUniqueSpellCasts.Clear();
        AllUniqueSpellsCache.Clear();
        AllReceivedSpellBlocks.Clear();
        AllResists.Clear();
        AllDamageBlocks.Clear();
        AllHealBlocks.Clear();
        AllLootBlocks.Clear();
        AllSpecialActions.Clear();
        EventsClearedActiveData?.Invoke(this, true);
      }
    }

    internal void AddUnhandledLine(string source, string line)
    {
      if (ConfigUtil.Debug)
      {
        var actionPart = line.Substring(LineParsing.ACTIONINDEX);

        lock (UnHandledLines)
        {
          if (!UnHandledLines.TryGetValue(source, out Dictionary<string, byte> cache))
          {
            cache = new Dictionary<string, byte>();
            UnHandledLines[source] = cache;
          }

          cache[actionPart] = 1;
        }
      }
    }

    internal List<string> GetUnhandledLines()
    {
      var list = new List<string>();

      if (ConfigUtil.Debug)
      {
        foreach (var keypair in UnHandledLines)
        {
          if (list.Count == 0)
          {
            list.AddRange(keypair.Value.Keys);
          }
          else
          {
            list = list.Intersect(keypair.Value.Keys).ToList();
          }
        }

        UnHandledLines.Clear();
      }

      return list;
    }

    internal void AddSpecial(TimedAction action)
    {
      if (action is PlayerDeath || action is SpecialSpell)
      {
        lock (AllSpecialActions)
        {
          AllSpecialActions.Add(action);
        }
      }
    }

    internal void AddDamageRecord(DamageRecord record, double beginTime)
    {
      // ReplacePlayer is done in the line parser already
      AddAction(AllDamageBlocks, record, beginTime);
    }

    internal void AddResistRecord(ResistRecord record, double beginTime)
    {
      // Resists are only seen by current player
      AddAction(AllResists, record, beginTime);
    }

    internal void AddHealRecord(HealRecord record, double beginTime)
    {
      record.Healer = PlayerManager.Instance.ReplacePlayer(record.Healer, record.Healed);
      record.Healed = PlayerManager.Instance.ReplacePlayer(record.Healed, record.Healer);
      AddAction(AllHealBlocks, record, beginTime);
    }

    internal void AddLootRecord(LootRecord record, double beginTime)
    {
      AddAction(AllLootBlocks, record, beginTime);
    }

    internal void HandleSpellInterrupt(string player, string spell, double beginTime)
    {
      for (int i = AllSpellCastBlocks.Count - 1; i >= 0 && beginTime - AllSpellCastBlocks[i].BeginTime <= 5; i--)
      {
        int index = AllSpellCastBlocks[i].Actions.FindLastIndex(action => ((SpellCast)action).Spell == spell && ((SpellCast)action).Caster == player);
        if (index > -1)
        {
          AllSpellCastBlocks[i].Actions.RemoveAt(index);
          break;
        }
      }
    }

    internal void AddSpellCast(SpellCast cast, double beginTime)
    {
      if (SpellsNameDB.ContainsKey(cast.Spell))
      {
        AddAction(AllSpellCastBlocks, cast, beginTime);

        string abbrv = Helpers.AbbreviateSpellName(cast.Spell);
        if (abbrv != null)
        {
          AllUniqueSpellCasts[abbrv] = 1;
        }

        if (SpellsToClass.TryGetValue(cast.Spell, out SpellClass theClass))
        {
          PlayerManager.Instance.UpdatePlayerClassFromSpell(cast, theClass);
        }
      }
    }

    internal void AddReceivedSpell(ReceivedSpell received, double beginTime)
    {
      AddAction(AllReceivedSpellBlocks, received, beginTime);
    }

    internal List<ActionBlock> GetCastsDuring(double beginTime, double endTime)
    {
      return SearchActions(AllSpellCastBlocks, beginTime, endTime);
    }

    internal List<ActionBlock> GetDamageDuring(double beginTime, double endTime)
    {
      return SearchActions(AllDamageBlocks, beginTime, endTime);
    }

    internal List<ActionBlock> GetHealsDuring(double beginTime, double endTime)
    {
      return SearchActions(AllHealBlocks, beginTime, endTime);
    }

    internal List<ActionBlock> GetResistsDuring(double beginTime, double endTime)
    {
      return SearchActions(AllResists, beginTime, endTime);
    }

    internal List<ActionBlock> GetReceivedSpellsDuring(double beginTime, double endTime)
    {
      return SearchActions(AllReceivedSpellBlocks, beginTime, endTime);
    }

    internal List<TimedAction> GetSpecialsDuring(double beginTime, double endTime)
    {
      var result = new List<TimedAction>();

      List<TimedAction> sorted = null;
      lock (AllSpecialActions)
      {
        sorted = AllSpecialActions.AsParallel().OrderBy(timed => timed.BeginTime).ToList();
      }
 
      if (sorted != null)
      {
        int start = sorted.FindIndex(timed => timed.BeginTime >= beginTime);
        int end = sorted.FindLastIndex(timed => timed.BeginTime <= endTime);

        if (start > -1 && end > -1 && end > start)
        {
          result = sorted.GetRange(start, end - start + 1);
        }
      }

      return result;
    }

    internal List<ActionBlock> GetAllLoot()
    {
      return AllLootBlocks.ToList();
    }

    internal SpellData GetSpellByAbbrv(string abbrv)
    {
      SpellData result = null;
      if (abbrv.Length > 0 && abbrv != Labels.UNKSPELL && SpellsAbbrvDB.ContainsKey(abbrv))
      {
        result = SpellsAbbrvDB[abbrv];
      }
      return result;
    }

    internal SpellData GetSpellByName(string name)
    {
      SpellData result = null;
      if (name.Length > 0 && name != Labels.UNKSPELL && SpellsNameDB.ContainsKey(name))
      {
        result = SpellsNameDB[name];
      }
      return result;
    }

    internal Fight GetFight(string name)
    {
      ActiveFights.TryGetValue(name, out Fight npc);
      return npc;
    }

    internal SpellData GetNonPosessiveLandsOnOther(string value, out List<SpellData> output)
    {
      SpellData result = null;
      if (NonPosessiveLandsOnOthers.TryGetValue(value, out output))
      {
        result = FindByLandsOn(value, output);
      }
      return result;
    }

    internal SpellData GetPosessiveLandsOnOther(string value, out List<SpellData> output)
    {
      SpellData result = null;
      if (PosessiveLandsOnOthers.TryGetValue(value, out output))
      {
        result = FindByLandsOn(value, output);
      }
      return result;
    }

    internal SpellData GetLandsOnYou(string value, out List<SpellData> output)
    {
      SpellData result = null;
      if (LandsOnYou.TryGetValue(value, out output))
      {
        result = FindByLandsOn(value, output);
      }
      return result;
    }

    internal bool IsKnownNpc(string npc)
    {
      bool found = false;
      if (!string.IsNullOrEmpty(npc))
      {
        found = AllNpcs.ContainsKey(npc.ToLower(CultureInfo.CurrentCulture));
      }
      return found; 
    }

    private SpellData FindByLandsOn(string value, List<SpellData> output)
    {
      SpellData result = null;
      if (output.Count == 1)
      {
        result = output[0];
      }
      else if (output.Count > 1)
      {
        if (!AllUniqueSpellsCache.TryGetValue(value, out result))
        {
          result = output.Find(spellData => AllUniqueSpellCasts.ContainsKey(spellData.SpellAbbrv));
          if (result == null)
          {
            // one more thing, if all the abbrviations look the same then we know the spell
            // even if the version is wrong. grab the last one
            var distinct = output.Distinct(AbbrvComparer).ToList();
            if (distinct.Count == 1)
            {
              result = distinct.Last();
            }
            else
            {
              // see if the spells look similar and pick one
              bool found = true;
              var data = distinct.First();
              foreach (var spell in distinct.Skip(1))
              {
                if (spell.IsBeneficial != data.IsBeneficial || spell.ClassMask != data.ClassMask || spell.Target != data.Target ||
                  spell.Damaging != data.Damaging)
                {
                  found = false;
                  break;
                }
              }

              if (found)
              {
                result = distinct.Last();
              }
            }
          }

          AllUniqueSpellsCache[value] = result;
        }
      }
      return result;
    }

    internal bool RemoveActiveFight(string name)
    {
      bool removed = ActiveFights.TryRemove(name, out Fight fight);

      if (removed)
      {
        EventsNewInactiveFight?.Invoke(this, fight);
      }

      return removed;
    }

    internal void UpdateIfNewFightMap(string name, Fight fight, bool changed)
    {
      if (!LifetimeFights.ContainsKey(name))
      {
        LifetimeFights[name] = 1;
      }

      if (!ActiveFights.ContainsKey(name))
      {
        ActiveFights[name] = fight;
        EventsNewFight?.Invoke(this, fight);
      }
      else if (changed)
      {
        EventsRefreshFights?.Invoke(this, fight);
      }
    }

    private void RemoveFight(string name)
    {
      bool removed = ActiveFights.TryRemove(name, out Fight npc);
      removed = LifetimeFights.TryRemove(name, out byte bnpc) || removed;

      if (removed)
      {
        EventsRemovedFight?.Invoke(this, name);
      }
    }

    private static List<ActionBlock> SearchActions(List<ActionBlock> allActions, double beginTime, double endTime)
    {
      ActionBlock startBlock = new ActionBlock() { BeginTime = beginTime };
      ActionBlock endBlock = new ActionBlock() { BeginTime = endTime + 1 };

      int startIndex = allActions.BinarySearch(startBlock, TAComparer);
      if (startIndex < 0)
      {
        startIndex = Math.Abs(startIndex) - 1;
      }

      int endIndex = allActions.BinarySearch(endBlock, TAComparer);
      if (endIndex < 0)
      {
        endIndex = Math.Abs(endIndex) - 1;
      }

      int last = endIndex - startIndex;
      return last > 0 ? allActions.GetRange(startIndex, last) : new List<ActionBlock>();
    }

    private static void AddAction(List<ActionBlock> blockList, IAction action, double beginTime)
    {
      if (blockList.LastOrDefault() is ActionBlock last && last.BeginTime == beginTime)
      {
        last.Actions.Add(action);
      }
      else
      {
        var newSegment = new ActionBlock() { BeginTime = beginTime };
        newSegment.Actions.Add(action);
        blockList.Add(newSegment);
      }
    }

    private class SpellAbbrvComparer : IEqualityComparer<SpellData>
    {
      public bool Equals(SpellData x, SpellData y)
      {
        return x.SpellAbbrv == y.SpellAbbrv;
      }

      public int GetHashCode(SpellData obj)
      {
        return obj.SpellAbbrv.GetHashCode();
      }
    }

    private class TimedActionComparer : IComparer<TimedAction>
    {
      public int Compare(TimedAction x, TimedAction y)
      {
        return x.BeginTime.CompareTo(y.BeginTime);
      }
    }
  }
}