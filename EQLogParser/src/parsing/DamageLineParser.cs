using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EQLogParser
{
  class DamageLineParser
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    public static event EventHandler<DamageProcessedEvent> EventsDamageProcessed;
    public static event EventHandler<ResistProcessedEvent> EventsResistProcessed;
    public static event EventHandler<string> EventsLineProcessed;

    private enum ParseType { HASTAKEN, YOUHAVETAKEN, POINTSOF, UNKNOWN };
    private static readonly DateUtil DateUtil = new DateUtil();
    private static readonly Regex CheckEyeRegex = new Regex(@"^Eye of (\w+)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Dictionary<string, string> SpellTypeCache = new Dictionary<string, string>();

    private static readonly Dictionary<string, string> HitMap = new Dictionary<string, string>()
    {
      { "bash", "bashes" }, { "backstab", "backstabs" }, { "bit", "bites" }, { "claw", "claws" }, { "crush", "crushes" },
      { "frenzy", "frenzies" }, { "gore", "gores" }, { "hit", "hits" }, { "kick", "kicks" }, { "learn", "learns" },
      { "maul", "mauls" }, { "punch", "punches" }, { "pierce", "pierces" }, { "rend", "rends" }, { "shoot", "shoots" },
      { "slash", "slashes" }, { "slam", "slams" }, { "slice", "slices" }, { "smash", "smashes" }, { "sting", "stings" },
      { "strike", "strikes" }, { "sweep", "sweeps" }
    };

    private static readonly Dictionary<string, string> HitAdditionalMap = new Dictionary<string, string>()
    {
      { "frenzy", "frenzies" }, { "frenzies", "frenzies" },
    };

    static DamageLineParser()
    {
      // add two way mapping
      HitMap.Keys.ToList().ForEach(key => HitMap[HitMap[key]] = HitMap[key]);
    }

    public static void Process(string line)
    {
      try
      {
        int index;
        if (line.Length >= 40 && line.IndexOf(" damage", Parsing.ACTIONINDEX + 13, StringComparison.Ordinal) > -1)
        {
          var actionPart = line.Substring(Parsing.ACTIONINDEX);
          var timeString = line.Substring(1, 24);
          var currentTime = DateUtil.ParseDate(timeString, out double precise);

          DamageRecord record = ParseDamage(actionPart, out bool isPlayerDamage);
          if (record != null)
          {
            DamageProcessedEvent e = new DamageProcessedEvent() { Record = record, IsPlayerDamage = isPlayerDamage, TimeString = timeString, BeginTime = currentTime };
            EventsDamageProcessed(record, e);
          }
        }
        else if (line.Length >= 49 && (index = line.IndexOf(", but miss", Parsing.ACTIONINDEX + 22, StringComparison.Ordinal)) > -1)
        {
          var actionPart = line.Substring(Parsing.ACTIONINDEX);
          var timeString = line.Substring(1, 24);
          var currentTime = DateUtil.ParseDate(timeString, out double precise);

          DamageRecord record = ParseMiss(actionPart, index, out bool isPlayerMiss);
          if (record != null)
          {
            DamageProcessedEvent e = new DamageProcessedEvent() { Record = record, IsPlayerDamage = isPlayerMiss, TimeString = timeString, BeginTime = currentTime };
            EventsDamageProcessed(record, e);
          }
        }
        else if (line.Length > 30 && line.Length < 102 && (index = line.IndexOf(" slain ", Parsing.ACTIONINDEX, StringComparison.Ordinal)) > -1)
        {
          var actionPart = line.Substring(Parsing.ACTIONINDEX);
          var timeString = line.Substring(1, 24);
          var currentTime = DateUtil.ParseDate(timeString, out double precise);
          HandleSlain(actionPart, currentTime, index - Parsing.ACTIONINDEX);
        }
        else if (line.Length >= 40 && line.Length < 110 && (index = line.IndexOf(" resisted your ", Parsing.ACTIONINDEX, StringComparison.Ordinal)) > -1)
        {
          var actionPart = line.Substring(Parsing.ACTIONINDEX);
          var timeString = line.Substring(1, 24);
          var currentTime = DateUtil.ParseDate(timeString, out double precise);
          HandleResist(actionPart, currentTime, index - Parsing.ACTIONINDEX);
        }
      }
      catch (ArgumentNullException ne)
      {
        LOG.Error(ne);
      }
      catch (NullReferenceException nr)
      {
        LOG.Error(nr);
      }
      catch (ArgumentOutOfRangeException aor)
      {
        LOG.Error(aor);
      }
      catch (ArgumentException ae)
      {
        LOG.Error(ae);
      }

      EventsLineProcessed(line, line);
    }

    private static void HandleSlain(string part, double currentTime, int optionalIndex)
    {
      string test = null;
      if (part.Length > 16 && part.StartsWith("You have slain ", StringComparison.Ordinal) && part[part.Length-1] == '!')
      {
        test = part.Substring(15, part.Length - 15 - 1);
      }
      else if (optionalIndex > 9)
      {
        test = part.Substring(0, optionalIndex - 9);
        if (test.Length == 4 && test[3] == ' ')
        {
          test = DataManager.Instance.PlayerName;
        }
      }

      // Gotcharms has been slain by an animated mephit!
      if (test != null && test.Length > 0)
      {
        if (DataManager.Instance.CheckNameForPlayer(test) || DataManager.Instance.CheckNameForPet(test))
        {
          int byIndex = part.IndexOf(" by ", StringComparison.Ordinal);
          if (byIndex > -1)
          {
            int len = part.Length - byIndex - 4 - 1;
            string npc = (len + byIndex + 4) <= part.Length ? part.Substring(byIndex + 4, len) : "";
            DataManager.Instance.AddPlayerDeath(test, npc, currentTime);
          }
        }
        else if (!DataManager.Instance.RemoveActiveNonPlayer(test) && char.IsUpper(test[0]))
        {
          DataManager.Instance.RemoveActiveNonPlayer(char.ToLower(test[0], CultureInfo.CurrentCulture) + test.Substring(1));
        }
      }
    }

    private static void HandleResist(string part, double currentTime, int optionalIndex)
    {
      // [Mon Feb 11 20:00:28 2019] An inferno flare resisted your Frostreave Strike III!
      string defender = part.Substring(0, optionalIndex);
      string spell = part.Substring(optionalIndex + 15, part.Length - optionalIndex - 15 - 1);

      ResistRecord record = new ResistRecord() { Spell = spell };
      ResistProcessedEvent e = new ResistProcessedEvent() { Record = record, BeginTime = currentTime };
      EventsResistProcessed(defender, e);
    }

    private static DamageRecord ParseMiss(string actionPart, int index, out bool isPlayerMiss)
    {
      // [Mon Aug 05 02:05:12 2019] An enchanted Syldon stalker tries to crush YOU, but misses! (Strikethrough)
      // [Sat Aug 03 00:20:57 2019] You try to crush a Kar`Zok soldier, but miss! (Riposte Strikethrough)
      DamageRecord record = null;

      string withoutMods = actionPart;
      int modifiersIndex = -1;
      if (actionPart[actionPart.Length - 1] == ')')
      {
        // using 4 here since the shortest modifier should at least be 3 even in the future. probably.
        modifiersIndex = actionPart.LastIndexOf('(', actionPart.Length - 4);
        if (modifiersIndex > -1)
        {
          withoutMods = actionPart.Substring(0, modifiersIndex);
        }
      }

      int tryStartIndex;
      int hitStartIndex = -1;
      int missesIndex = index - Parsing.ACTIONINDEX;

      if (withoutMods[0] == 'Y' && withoutMods[1] == 'o' && withoutMods[2] == 'u')
      {
        tryStartIndex = 3;
        hitStartIndex = 11;
      }
      else
      {
        tryStartIndex = withoutMods.IndexOf(" tries to ", StringComparison.Ordinal);
        if (tryStartIndex > -1)
        {
          hitStartIndex = tryStartIndex + 10;
        }
      }

      if (tryStartIndex > -1 && hitStartIndex > -1 && tryStartIndex < missesIndex)
      {
        int hitEndIndex = withoutMods.IndexOf(" ", hitStartIndex, StringComparison.Ordinal);
        if (hitEndIndex > -1)
        {
          string hit = withoutMods.Substring(hitStartIndex, hitEndIndex - hitStartIndex);

          bool additional;
          string subType = GetTypeFromHit(hit, out additional);
          if (subType != null)
          {
            int hitLength = hit.Length + (additional ? 3 : 0);

            // check for pets
            string attacker = withoutMods.Substring(0, tryStartIndex);
            int defStart = hitStartIndex + hitLength + 1;
            int missesEnd = missesIndex - defStart;

            if (missesEnd > 0)
            {
              string defender = withoutMods.Substring(defStart, missesEnd);
              HasOwner(attacker, out string attackerOwner);
              HasOwner(defender, out string defenderOwner);

              if (attacker != null && defender != null)
              {
                record = BuildRecord(attacker, defender, 0, attackerOwner, defenderOwner, subType, Labels.MISS);
              }

              if (record != null && modifiersIndex > -1)
              {
                record.ModifiersMask = LineModifiersParser.Parse(actionPart.Substring(modifiersIndex + 1, actionPart.Length - 1 - modifiersIndex - 1));
              }
            }
          }
        }
      }

      return ValidateDamage(record, out isPlayerMiss);
    }

    private static DamageRecord ParseDamage(string actionPart, out bool isPlayerDamage)
    {
      DamageRecord record = null;
      ParseType parseType = ParseType.UNKNOWN;

      string withoutMods = actionPart;
      int modifiersIndex = -1;
      if (actionPart[actionPart.Length - 1] == ')')
      {
        // using 4 here since the shortest modifier should at least be 3 even in the future. probably.
        modifiersIndex = actionPart.LastIndexOf('(', actionPart.Length - 4);
        if (modifiersIndex > -1)
        {
          withoutMods = actionPart.Substring(0, modifiersIndex);
        }
      }

      int pointsIndex = -1;
      int forIndex = -1;
      int fromIndex = -1;
      int byIndex = -1;
      int takenIndex = -1;
      int hitIndex = -1;
      int extraIndex = -1;
      int isAreIndex = -1;
      bool nonMelee = false;

      List<string> nameList = new List<string>();
      StringBuilder builder = new StringBuilder();
      var data = withoutMods.Split(' ');

      for (int i = 0; i < data.Length; i++)
      {
        switch (data[i])
        {
          case "taken":
            takenIndex = i;

            int test1 = i - 1;
            if (test1 > 0 && data[test1] == "has")
            {
              parseType = ParseType.HASTAKEN;

              int test2 = i + 2;
              if (data.Length > test2 && data[test2] == "extra" && data[test2 - 1] == "an")
              {
                extraIndex = test2;
              }
            }
            else if (test1 >= 1 && data[test1] == "have" && data[test1 - 1] == "You")
            {
              parseType = ParseType.YOUHAVETAKEN;
            }
            break;
          case "by":
            byIndex = i;
            break;
          case "non-melee":
            nonMelee = true;
            break;
          case "is":
          case "are":
            isAreIndex = i;
            break;
          case "for":
            int next = i + 1;
            if (data.Length > next && data[next].Length > 0 && char.IsNumber(data[next][0]))
            {
              forIndex = i;
            }
            break;
          case "from":
            fromIndex = i;
            break;
          case "points":
            int ofIndex = i + 1;
            if (ofIndex < data.Length && data[ofIndex] == "of")
            {
              parseType = ParseType.POINTSOF;
              pointsIndex = i;
            }
            break;
          default:
            if (HitMap.ContainsKey(data[i]))
            {
              hitIndex = i;
            }
            break;
        }
      }

      if (parseType == ParseType.POINTSOF && forIndex > -1 && forIndex < pointsIndex && hitIndex > -1)
      {
        record = ParsePointsOf(data, nonMelee, forIndex, byIndex, hitIndex, builder, nameList);
      }
      else if (parseType == ParseType.HASTAKEN && takenIndex < fromIndex && fromIndex > -1)
      {
        record = ParseHasTaken(data, takenIndex, fromIndex, byIndex, builder);
      }
      else if (parseType == ParseType.POINTSOF && extraIndex > -1 && takenIndex > -1 && takenIndex < fromIndex)
      {
        record = ParseExtra(data, takenIndex, extraIndex, fromIndex, nameList);
      }
      // there are more messages without a specificied attacker or spell but do these first
      else if (parseType == ParseType.YOUHAVETAKEN && takenIndex > -1 && fromIndex > -1 && byIndex > fromIndex)
      {
        record = ParseYouHaveTaken(data, takenIndex, fromIndex, byIndex, builder);
      }
      else if (parseType == ParseType.POINTSOF && isAreIndex > -1 && byIndex > isAreIndex && forIndex > byIndex)
      {
        record = ParseDS(data, isAreIndex, byIndex, forIndex);
      }

      if (record != null && modifiersIndex > -1)
      {
        record.ModifiersMask = LineModifiersParser.Parse(actionPart.Substring(modifiersIndex + 1, actionPart.Length - 1 - modifiersIndex - 1));
      }

      record = ValidateDamage(record, out isPlayerDamage);
      return record;
    }

    private static DamageRecord ValidateDamage(DamageRecord record, out bool isPlayerDamage)
    {
      isPlayerDamage = true;
      
      if (record != null)
      {
        // handle riposte separately
        if (LineModifiersParser.IsRiposte(record.ModifiersMask))
        {
          record.SubType = Labels.RIPOSTE;
        }

        if (CheckEyeRegex.IsMatch(record.Defender) || record.Defender.EndsWith("chest", StringComparison.Ordinal) || record.Defender.EndsWith("satchel", StringComparison.Ordinal))
        {
          record = null;
        }
        else
        {
          // Needed to replace 'You' and 'you', etc
          record.Attacker = DataManager.Instance.ReplacePlayer(record.Attacker, record.Defender, out bool replaced);
          record.Defender = DataManager.Instance.ReplacePlayer(record.Defender, record.Attacker, out _);

          if (record.Attacker == record.Defender)
          {
            record = null;
          }
          else
          {
            CheckDamageRecordForPet(record, replaced, out bool isDefenderPet, out bool isAttackerPet);
            CheckDamageRecordForPlayer(record, replaced, out bool isDefenderPlayer, out bool isAttackerPlayer);

            if (isDefenderPlayer || isDefenderPet)
            {
              if (!isAttackerPlayer && !isAttackerPet)
              {
                DataManager.Instance.UpdateProbablyNotAPlayer(record.Attacker);
              }

              // main spot where attacker is not a player or pet
              isPlayerDamage = false;
            }

            if ((isAttackerPlayer || isAttackerPet) && !isDefenderPlayer && !isDefenderPet)
            {
              DataManager.Instance.UpdateProbablyNotAPlayer(record.Defender);
            }
            else if (!isAttackerPlayer && !isAttackerPet && !isDefenderPlayer && !isDefenderPet)
            {
              var isDefenderProbablyNotAPlayer = DataManager.Instance.UpdateProbablyNotAPlayer(record.Defender, false);
              var isAttackerProbablyNotAPlayer = DataManager.Instance.UpdateProbablyNotAPlayer(record.Attacker, false);

              if (isDefenderProbablyNotAPlayer && !isAttackerProbablyNotAPlayer)
              {
                DataManager.Instance.UpdateUnVerifiedPetOrPlayer(record.Attacker);
              }
              else if (!isDefenderProbablyNotAPlayer && isAttackerProbablyNotAPlayer)
              {
                DataManager.Instance.UpdateUnVerifiedPetOrPlayer(record.Defender);
                isPlayerDamage = false;
              }
              else if(isDefenderProbablyNotAPlayer && isAttackerProbablyNotAPlayer)
              {
                record = null;
              }
            }
          }
        }
      }

      return record;
    }

    private static void CheckDamageRecordForPet(DamageRecord record, bool replacedAttacker, out bool isDefenderPet, out bool isAttackerPet)
    {
      isAttackerPet = false;

      if (!replacedAttacker)
      {
        if (!string.IsNullOrEmpty(record.AttackerOwner))
        {
          DataManager.Instance.UpdateVerifiedPets(record.Attacker);
          isAttackerPet = true;
        }
        else
        {
          isAttackerPet = DataManager.Instance.CheckNameForPet(record.Attacker);

          if (isAttackerPet)
          {
            record.AttackerOwner = Labels.UNASSIGNED;
          }
        }
      }

      if (!string.IsNullOrEmpty(record.DefenderOwner))
      {
        DataManager.Instance.UpdateVerifiedPets(record.Defender);
        isDefenderPet = true;
      }
      else
      {
        isDefenderPet = DataManager.Instance.CheckNameForPet(record.Defender);
      }
    }

    private static void CheckDamageRecordForPlayer(DamageRecord record, bool replacedAttacker, out bool isDefenderPlayer, out bool isAttackerPlayer)
    {
      isAttackerPlayer = false;

      if (!replacedAttacker)
      {
        if (!string.IsNullOrEmpty(record.AttackerOwner))
        {
          DataManager.Instance.UpdateVerifiedPlayers(record.AttackerOwner);
          isAttackerPlayer = true;
        }
        else if (LineModifiersParser.IsCrit(record.ModifiersMask) && (record.Type == Labels.DD || record.Type == Labels.DOT))
        {
          // only players can crit a spell
          DataManager.Instance.UpdateVerifiedPlayers(record.Attacker);
          isAttackerPlayer = true;
        }

        if (!string.IsNullOrEmpty(record.DefenderOwner))
        {
          DataManager.Instance.UpdateVerifiedPlayers(record.DefenderOwner);
        }
      }

      isDefenderPlayer = string.IsNullOrEmpty(record.DefenderOwner) && DataManager.Instance.CheckNameForPlayer(record.Defender);
    }

    private static DamageRecord ParseDS(string[] data, int isAreIndex, int byIndex, int forIndex)
    {
      DamageRecord record = null;

      string defender = string.Join(" ", data, 0, isAreIndex);
      uint damage = StatsUtil.ParseUInt(data[forIndex + 1]);

      string attacker;
      if (data[byIndex + 1] == "YOUR")
      {
        attacker = "you";
      }
      else
      {
        attacker = string.Join(" ", data, byIndex + 1, forIndex - byIndex - 2);
        attacker = attacker.Substring(0, attacker.Length - 2);
      }

      // check for pets
      HasOwner(attacker, out string attackerOwner);
      HasOwner(defender, out string defenderOwner);

      if (attacker != null && defender != null)
      {
        record = BuildRecord(attacker, defender, damage, attackerOwner, defenderOwner, null, Labels.DS);
      }

      return record;
    }

    private static DamageRecord ParseYouHaveTaken(string[] data, int takenIndex, int fromIndex, int byIndex, StringBuilder builder)
    {
      DamageRecord record = null;

      string defender = "you";
      string attacker = ReadStringToPeriod(data, byIndex, builder);
      string spell = string.Join(" ", data, fromIndex + 1, byIndex - fromIndex - 1);
      uint damage = StatsUtil.ParseUInt(data[takenIndex + 1]);

      // check for pets
      HasOwner(attacker, out string attackerOwner);

      if (attacker != null && defender != null)
      {
        record = BuildRecord(attacker, defender, damage, attackerOwner, null, spell, Labels.DD);
      }

      return record;
    }

    private static DamageRecord ParseExtra(string[] data, int takenIndex, int extraIndex, int fromIndex, List<string> nameList)
    {
      DamageRecord record = null;

      uint damage = StatsUtil.ParseUInt(data[extraIndex + 1]);
      string defender = string.Join(" ", data, 0, takenIndex - 1);

      string attacker = null;
      string spell = null;

      if (data.Length > fromIndex + 1)
      {
        int person = fromIndex + 1;
        if (data[person] == "your")
        {
          attacker = "you";
        }
        else
        {
          int len = data[person].Length;
          if (len > 2 && data[person][len-2] == '\'' && data[person][len-1] == 's')
          {
            attacker = data[person].Substring(0, len - 2);
          }
        }

        if (attacker != null)
        {
          nameList.Clear();
          for (int i = person + 1; i < data.Length; i++)
          {
            if (data[i] == "spell.")
            {
              break;
            }
            else
            {
              nameList.Add(data[i]);
            }
          }

          spell = string.Join(" ", nameList);
        }
      }

      // check for pets
      HasOwner(attacker, out string attackerOwner);
      HasOwner(defender, out string defenderOwner);

      if (attacker != null && defender != null)
      {
        record = BuildRecord(attacker, defender, damage, attackerOwner, defenderOwner, spell, Labels.BANE);
      }

      return record;
    }

    private static DamageRecord ParseHasTaken(string[] data, int takenIndex, int fromIndex, int byIndex, StringBuilder builder)
    {
      DamageRecord record = null;

      uint damage = StatsUtil.ParseUInt(data[takenIndex + 1]);
      string defender = string.Join(" ", data, 0, takenIndex - 1);

      string spell = null;
      string attacker = null;
      if (byIndex > -1 && fromIndex < byIndex)
      {
        spell = string.Join(" ", data, fromIndex + 1, byIndex - fromIndex - 1);
        attacker = ReadStringToPeriod(data, byIndex, builder);
      }
      else if (data[fromIndex + 1] == "your")
      {
        spell = ReadStringToPeriod(data, fromIndex + 1, builder);
        attacker = "you";
      }

      if (attacker != null && spell != null)
      {
        // check for pets
        HasOwner(attacker, out string attackerOwner);
        HasOwner(defender, out string defenderOwner);

        if (attacker != null && defender != null)
        {
          record = BuildRecord(attacker, defender, damage, attackerOwner, defenderOwner, spell, GetTypeFromSpell(spell, Labels.DOT));
        }
      }

      return record;
    }

    private static DamageRecord ParsePointsOf(string[] data, bool isNonMelee, int forIndex, int byIndex, int hitIndex, StringBuilder builder, List<string> nameList)
    {
      DamageRecord record = null;
      uint damage = StatsUtil.ParseUInt(data[forIndex + 1]);
      string spell = null;
      string attacker = null;

      if (byIndex > 1)
      {
        spell = ReadStringToPeriod(data, byIndex, builder);
      }

      // before hit
      nameList.Clear();
      for (int i = hitIndex - 1; i >= 0; i--)
      {
        if (data[hitIndex].EndsWith(".", StringComparison.Ordinal))
        {
          break;
        }
        else
        {
          nameList.Insert(0, data[i]);
        }
      }

      if (nameList.Count > 0)
      {
        attacker = string.Join(" ", nameList);
      }

      bool additional;
      string type = GetTypeFromHit(data[hitIndex], out additional);

      if (additional)
      {
        hitIndex++; // multi-word hit value
      }

      string defender = string.Join(" ", data, hitIndex + 1, forIndex - hitIndex - 1);

      // check for pets
      HasOwner(attacker, out string attackerOwner);
      HasOwner(defender, out string defenderOwner);

      // some new special cases
      if (!string.IsNullOrEmpty(spell) && spell.StartsWith("Elemental Conversion", StringComparison.Ordinal))
      {
        DataManager.Instance.UpdateVerifiedPets(defender);
      }
      else if (attacker != null && defender != null)
      {
        if ((isNonMelee || !string.IsNullOrEmpty(spell)) && type.StartsWith("hit", StringComparison.OrdinalIgnoreCase))
        {
          type = GetTypeFromSpell(spell, Labels.DD);
        }

        record = BuildRecord(attacker, defender, damage, attackerOwner, defenderOwner, spell, type);
      }

      return record;
    }

    private static DamageRecord BuildRecord(string attacker, string defender, uint damage, string attackerOwner, string defenderOwner, string subType, string type)
    {
      if (attacker.EndsWith("'s corpse", StringComparison.Ordinal))
      {
        attacker = attacker.Substring(0, attacker.Length - 9);
      }

      DamageRecord record = new DamageRecord()
      {
        Attacker = string.Intern(FixName(attacker)),
        Defender = string.Intern(FixName(defender)),
        Type = string.Intern(type),
        Total = damage,
        ModifiersMask = -1
      };

      if (attackerOwner != null)
      {
        record.AttackerOwner = string.Intern(attackerOwner);
      }

      if (defenderOwner != null)
      {
        record.DefenderOwner = string.Intern(defenderOwner);
      }

      // set sub type if spell is available
      record.SubType = string.IsNullOrEmpty(subType) ? record.Type : string.Intern(subType);
      return record;
    }

    private static string ReadStringToPeriod(string[] data, int byIndex, StringBuilder builder)
    {
      string result = null;

      if (byIndex > 1)
      {
        builder.Clear();
        for (int i = byIndex + 1; i < data.Length; i++)
        {
          int len = data[i].Length;
          builder.Append(data[i]);
          if ((len >= 1 && data[i][len-1] == '.') && !(len >= 3 && data[i][len - 2] == 'k' && data[i][len - 3] == 'R'))
          {
            builder.Remove(builder.Length - 1, 1);
            break;
          }
          else
          {
            builder.Append(" ");
          }
        }

        result = builder.ToString();
      }

      return result;
    }

    private static bool HasOwner(string name, out string owner)
    {
      bool hasOwner = false;
      owner = null;

      if (!string.IsNullOrEmpty(name))
      {
        int posessiveIndex = name.IndexOf("`s ", StringComparison.Ordinal);
        if (posessiveIndex > -1)
        {
          if (IsPetOrMount(name, posessiveIndex + 3, out _))
          {
            if (Helpers.IsPossiblePlayerName(name, posessiveIndex))
            {
              owner = name.Substring(0, posessiveIndex);
              hasOwner = true;
            }
          }
        }
      }

      return hasOwner;
    }

    private static string FixName(string name)
    {
      string result;
      if (name.Length >= 2 && name[0] == 'A' && name[1] == ' ')
      {
        result = "a " + name.Substring(2);
      }
      else if (name.Length >= 3 && name[0] == 'A' && name[1] == 'n' && name[2] == ' ')
      {
        result = "an " + name.Substring(3);
      }
      else
      {
        result = name;
      }

      return result;
    }

    private static string GetTypeFromSpell(string name, string type)
    {
      if (name == null || !SpellTypeCache.TryGetValue(name, out string result))
      {
        if (name != null)
        {
          string spellName = Helpers.AbbreviateSpellName(name);
          SpellData data = DataManager.Instance.GetSpellByAbbrv(spellName);
          result = (data != null && data.IsProc) ? Labels.PROC : type;
          SpellTypeCache[name] = result;
        }
        else
        {
          result = type;
        }
      }

      return result;
    }

    private static bool IsPetOrMount(string part, int start, out int len)
    {
      bool found = false;
      len = -1;

      int end = 2;
      if (part.Length >= (start + ++end) && part.Substring(start, 3) == "pet" ||
        part.Length >= (start + ++end) && part.Substring(start, 4) == "ward" && !(part.Length > (start + 5) && part[start + 5] != 'e') ||
        part.Length >= (start + ++end) && part.Substring(start, 5) == "Mount" ||
        part.Length >= (start + ++end) && part.Substring(start, 6) == "warder")
      {
        found = true;
        len = end;
      }
      return found;
    }

    private static string GetTypeFromHit(string hit, out bool additional)
    {
      additional = false;

      if (HitAdditionalMap.TryGetValue(hit, out string type))
      {
        additional = true;
      }
      else
      {
        HitMap.TryGetValue(hit, out type);
      }

      if (!string.IsNullOrEmpty(type) && type.Length > 1)
      {
        type = char.ToUpper(type[0], CultureInfo.CurrentCulture) + type.Substring(1);
      }
      
      return type;
    }
  }
}
