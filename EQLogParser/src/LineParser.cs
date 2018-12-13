using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EQLogParser
{
  class LineParser
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    // counting this thing is really slow
    private static int DateCount = 0;
    private static ConcurrentDictionary<string, DateTime> DateTimeCache = new ConcurrentDictionary<string, DateTime>();

    private const int MIN_LINE_LENGTH = 33;
    private static Regex CheckEye = new Regex(@"^Eye of (\w+)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static Regex CheckLeader = new Regex(@"^(\w+) says, 'My leader is (\w+)\.'", RegexOptions.Singleline | RegexOptions.Compiled);
    private static Regex CheckDoTDamage = new Regex(@"^(.+) has taken (\d+) damage from (.+) by (\w+)\.", RegexOptions.Singleline | RegexOptions.Compiled);

    public static ConcurrentDictionary<string, bool> HitMap = new ConcurrentDictionary<string, bool>(
      new List<KeyValuePair<string, bool>>
    {
      new KeyValuePair<string, bool>("bash", true), new KeyValuePair<string, bool>("bit", true), new KeyValuePair<string, bool>("backstab", true),
      new KeyValuePair<string, bool>("claw", true), new KeyValuePair<string, bool>("crush", true), new KeyValuePair<string, bool>("frenzies", true),
      new KeyValuePair<string, bool>("frenzy", true), new KeyValuePair<string, bool>("gore", true), new KeyValuePair<string, bool>("hit", true),
      new KeyValuePair<string, bool>("kick", true), new KeyValuePair<string, bool>("maul", true), new KeyValuePair<string, bool>("punch", true),
      new KeyValuePair<string, bool>("pierce", true), new KeyValuePair<string, bool>("rend", true), new KeyValuePair<string, bool>("shoot", true),
      new KeyValuePair<string, bool>("slash", true), new KeyValuePair<string, bool>("slam", true), new KeyValuePair<string, bool>("slice", true),
      new KeyValuePair<string, bool>("smash", true), new KeyValuePair<string, bool>("sting", true), new KeyValuePair<string, bool>("strike", true),
      new KeyValuePair<string, bool>("bashes", true), new KeyValuePair<string, bool>("bites", true), new KeyValuePair<string, bool>("backstabs", true),
      new KeyValuePair<string, bool>("claws", true), new KeyValuePair<string, bool>("crushes", true), new KeyValuePair<string, bool>("gores", true),
      new KeyValuePair<string, bool>("hits", true), new KeyValuePair<string, bool>("kicks", true), new KeyValuePair<string, bool>("mauls", true),
      new KeyValuePair<string, bool>("punches", true), new KeyValuePair<string, bool>("pierces", true), new KeyValuePair<string, bool>("rends", true),
      new KeyValuePair<string, bool>("shoots", true), new KeyValuePair<string, bool>("slashes", true), new KeyValuePair<string, bool>("slams", true),
      new KeyValuePair<string, bool>("slices", true), new KeyValuePair<string, bool>("smashes", true), new KeyValuePair<string, bool>("stings", true),
      new KeyValuePair<string, bool>("strikes", true)
    });

    public static ConcurrentDictionary<string, string> HitAdditionalMap = new ConcurrentDictionary<string, string>(
      new List<KeyValuePair<string, string>>
    {
      new KeyValuePair<string, string>("frenzies", "frenzies on"), new KeyValuePair<string, string>("frenzy", "frenzy on")
    });

    public static ProcessLine KeepForProcessingState(string line)
    {
      ProcessLine pline = new ProcessLine() { Line = line, State = -1 };

      try
      {
        if (line.Length > MIN_LINE_LENGTH)
        {
          pline.ActionPart = pline.Line.Substring(27);
          pline.State = PreCheck(pline);

          if (pline.State >= 0)
          {
            pline.TimeString = pline.Line.Substring(1, 24);

            DateTime dateTime;
            if (!DateTimeCache.ContainsKey(pline.TimeString))
            {
              DateTime.TryParseExact(pline.TimeString, "ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dateTime);
              if (dateTime == DateTime.MinValue)
              {
                DateTime.TryParseExact(pline.TimeString, "ddd MMM  d HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dateTime);
              }

              DateTimeCache.TryAdd(pline.TimeString, dateTime);
            }
            else
            {
              dateTime = DateTimeCache[pline.TimeString];
            }

            // dont let it get too big but it coudl be re-used between different log files
            if (DateTimeCache.TryAdd(pline.TimeString, dateTime))
            {
              DateCount++;
            }

            if (DateCount > 20000)
            {
              DateTimeCache.Clear();
            }

            pline.CurrentTime = dateTime;
          }

        }
      }
      catch (Exception e)
      {
        LOG.Error(e);
      }

      return pline;
    }

    public static void CheckForShrink(ProcessLine pline)
    {
      if (IsPossiblePlayerName(pline.ActionPart, pline.OptionalIndex))
      {
        string test = pline.ActionPart.Substring(0, pline.OptionalIndex);
        DataManager.Instance.UpdateUnverifiedPetOrPlayer(test);
      }
    }

    public static void CheckForHeal(ProcessLine pline)
    {
      string healer = pline.ActionPart.Substring(0, pline.OptionalIndex);
      int space = pline.ActionPart.IndexOf(" ", pline.OptionalIndex + 8);
      string healed = pline.ActionPart.Substring(pline.OptionalIndex + 8, space - pline.OptionalIndex - 8);

      bool foundHealer = DataManager.Instance.CheckNameForPlayer(healer);
      bool foundHealed = DataManager.Instance.CheckNameForPlayer(healed) || DataManager.Instance.CheckNameForPlayer(healed);

      if (foundHealer && !foundHealed)
      {
        DataManager.Instance.UpdateUnverifiedPetOrPlayer(healed, true);
      }
      else if (!foundHealer && foundHealed)
      {
        DataManager.Instance.UpdateVerifiedPlayers(healer);
      }
    }

    public static void CheckForSlain(ProcessLine pline)
    {
      string test = pline.ActionPart.Substring(0, pline.OptionalIndex);
      if (!DataManager.Instance.CheckNameForPlayer(test) && !DataManager.Instance.CheckNameForPet(test))
      {
        if (!DataManager.Instance.RemoveActiveNonPlayer(test) && Char.IsUpper(test[0]))
        {
          DataManager.Instance.RemoveActiveNonPlayer(Char.ToLower(test[0]) + test.Substring(1));
        }
      }
    }

    public static void CheckForPetLeader(ProcessLine pline)
    {
      MatchCollection matches = CheckLeader.Matches(pline.ActionPart);
      if (matches.Count > 0 && matches[0].Groups.Count == 3)
      {
        string pet = matches[0].Groups[1].Value;
        string owner = matches[0].Groups[2].Value;

        DataManager.Instance.UpdateVerifiedPlayers(owner);
        DataManager.Instance.UpdateVerifiedPets(pet);
        DataManager.Instance.UpdatePetToPlayer(pet, owner);
      }
    }

    public static void CheckForPlayers(ProcessLine pline)
    {
      if (pline.State == 4)
      {
        DataManager.Instance.UpdateVerifiedPlayers(pline.ActionPart.Substring(19));
      }
      else if (pline.State == 3)
      {
        string name = pline.ActionPart.Substring(0, pline.OptionalIndex);
        DataManager.Instance.UpdateVerifiedPlayers(name);
      }
    }

    public static DamageRecord ParseDamage(string part)
    {
      DamageRecord record = null;

      record = ParseAllDamage(part);
      if (record != null)
      {
        // Needed to replace 'You' and 'you', etc
        bool replaced;
        record.Attacker = DataManager.Instance.ReplaceAttacker(record.Attacker, out replaced);

        bool isDefenderPet, isAttackerPet;
        CheckDamageRecordForPet(record, replaced, out isDefenderPet, out isAttackerPet);

        bool isDefenderPlayer;
        CheckDamageRecordForPlayer(record, replaced, out isDefenderPlayer);

        if (isDefenderPlayer || isDefenderPet || DataManager.Instance.CheckNameForUnverifiedPetOrPlayer(record.Defender))
        {
          if (record.Attacker != record.Defender)
          {
            DataManager.Instance.UpdateProbablyNotAPlayer(record.Attacker);
          }
          record = null;
        }
        else if (CheckEye.IsMatch(record.Defender))
        {
          record = null;
        }

        if (record != null && record.Attacker != record.Defender)
        {
          DataManager.Instance.UpdateProbablyNotAPlayer(record.Defender);
        }
      }

      return record;
    }

    private static void CheckDamageRecordForPet(DamageRecord record, bool replacedAttacker, out bool isDefenderPet, out bool isAttackerPet)
    {
      isDefenderPet = false;
      isAttackerPet = false;

      if (!replacedAttacker)
      {
        if (record.AttackerPetType != "")
        {
          DataManager.Instance.UpdateVerifiedPets(record.Attacker);
          isAttackerPet = true;
        }
        else
        {
          isAttackerPet = DataManager.Instance.CheckNameForPet(record.Attacker);
          if (isAttackerPet)
          {
            record.AttackerPetType = "pet";
          }
        }
      }

      if (record.DefenderPetType != "")
      {
        DataManager.Instance.UpdateVerifiedPets(record.Defender);
        isDefenderPet = true;
      }
      else
      {
        isDefenderPet = DataManager.Instance.CheckNameForPet(record.Defender);
      }
    }

    private static void CheckDamageRecordForPlayer(DamageRecord record, bool replacedAttacker, out bool isDefenderPlayer)
    {
      if (!replacedAttacker)
      {
        if (record.AttackerOwner != "")
        {
          DataManager.Instance.UpdateVerifiedPlayers(record.AttackerOwner);
        }

        if (record.DefenderOwner != "")
        {
          DataManager.Instance.UpdateVerifiedPlayers(record.DefenderOwner);
        }
      }

      isDefenderPlayer = (record.DefenderPetType == "" && DataManager.Instance.CheckNameForPlayer(record.Defender));
    }

    // still need to work on this
    private static int PreCheck(ProcessLine pline)
    {
      int result = -1;

      try
      {
        int index;
        if (pline.ActionPart.Contains(" damage")) // damage. for DD
        {
          return 0;
        }

        if (pline.ActionPart.Length < 75 && (index = pline.ActionPart.IndexOf(" has been slain by")) > -1)
        {
          pline.OptionalIndex = index;
          return 1;
        }

        if (pline.ActionPart.Length > 10 && pline.ActionPart.Length < 25 && (index = pline.ActionPart.IndexOf(" shrinks.")) > -1)
        {
          pline.OptionalIndex = index;
          return 2;
        }

        if (pline.ActionPart.Length < 34 && (index = pline.ActionPart.IndexOf(" tells the guild, ")) > -1)
        {
          int firstSpace = pline.ActionPart.IndexOf(" ");
          if (firstSpace > -1 && firstSpace == index)
          {
            pline.OptionalIndex = index;
            return 3;
          }
        }

        if (pline.ActionPart.Length < 35 && pline.ActionPart.StartsWith("Targeted (Player)"))
        {
          return 4;
        }

        if (pline.ActionPart.Length >= 29 && pline.ActionPart.Length < 60 && pline.ActionPart.Contains("My leader is"))
        {
          return 5;
        }

        if (pline.ActionPart.Length >= 24 && (index = pline.ActionPart.Substring(0, 24).IndexOf(" healed ")) > -1 && char.IsUpper(pline.ActionPart[index + 8]))
        {
          pline.OptionalIndex = index;
          return 6;
        }
      }
      catch (Exception e)
      {
        LOG.Error(e);
      }

      return result;
    }

    private static DamageRecord ParseAllDamage(string part)
    {
      DamageRecord record = null;

      try
      {
        bool found = false;
        string type = "";
        string attacker = "";
        string attackerOwner = "";
        string attackerPetType = "";
        string defender = "";
        string defenderPetType = "";
        string defenderOwner = "";
        int afterAction = -1;
        long damage = 0;
        string action = "";

        // find first space and see if we have a name in the first  second
        int firstSpace = part.IndexOf(" ");
        if (firstSpace > 0)
        {
          // check if name has a possessive
          if (firstSpace >= 2 && part.Substring(firstSpace - 2, 2) == "`s")
          {
            if (IsPossiblePlayerName(part, firstSpace - 2))
            {
              int len;
              if (IsPetOrMount(part, firstSpace + 1, out len))
              {
                string petType = part.Substring(firstSpace + 1, len);
                string owner = part.Substring(0, firstSpace - 2);

                int sizeSoFar = firstSpace + 1 + len + 1;
                if (part.Length > sizeSoFar)
                {
                  string player = part.Substring(0, sizeSoFar - 1);
                  int secondSpace = part.IndexOf(" ", sizeSoFar);
                  if (secondSpace > -1)
                  {
                    string testAction = part.Substring(sizeSoFar, secondSpace - sizeSoFar);
                    if (HitMap.ContainsKey(testAction))
                    {
                      if (HitAdditionalMap.ContainsKey(testAction))
                      {
                        type = HitAdditionalMap[testAction];
                      }
                      else
                      {
                        type = testAction;
                      }

                      action = "DD";
                      afterAction = sizeSoFar + type.Length + 1;
                      attackerPetType = petType;
                      attackerOwner = owner;
                      attacker = player;
                    }
                    else
                    {
                      if (testAction == "has" && part.Substring(sizeSoFar + 3, 7) == " taken ")
                      {
                        action = "DoT";
                        type = "DoT Tick";
                        afterAction = sizeSoFar + "has taken".Length + 1;
                        defenderPetType = petType;
                        defenderOwner = owner;
                        defender = player;
                      }
                    }
                  }
                }
              }
            }
          }
          else if (IsPossiblePlayerName(part, firstSpace))
          {
            int sizeSoFar = firstSpace + 1;
            int secondSpace = part.IndexOf(" ", sizeSoFar);
            if (secondSpace > -1)
            {
              string player = part.Substring(0, firstSpace);
              string testAction = part.Substring(sizeSoFar, secondSpace - sizeSoFar);
              if (HitMap.ContainsKey(testAction))
              {
                if (HitAdditionalMap.ContainsKey(testAction))
                {
                  type = HitAdditionalMap[testAction];
                }
                else
                {
                  type = testAction;
                }

                action = "DD";
                afterAction = sizeSoFar + type.Length + 1;
                attacker = player;
              }
              else
              {
                if (testAction == "has" && part.Substring(sizeSoFar + 3, 7) == " taken ")
                {
                  action = "DoT";
                  type = "DoT Tick";
                  afterAction = sizeSoFar + "has taken".Length + 1;
                  defender = player;
                }
              }
            }
          }

          if (action == "")
          {
            // only check if it's an NPC if it's a DoT and they're the defender
            int hasTakenIndex = part.IndexOf("has taken ", firstSpace + 1);
            if (hasTakenIndex > -1)
            {
              action = "DoT";
              defender = part.Substring(0, hasTakenIndex - 1);
              type = "DoT Tick";
              afterAction = hasTakenIndex + 10;
            }
          }

          if (type != "" && action != "" && part.Length > afterAction)
          {
            if (action == "DD")
            {
              int forIndex = part.IndexOf(" for ", afterAction);
              if (forIndex > -1)
              {
                defender = part.Substring(afterAction, forIndex - afterAction);
                int posessiveIndex = defender.IndexOf("`s ");
                if (posessiveIndex > -1)
                {
                  int len;
                  if (IsPetOrMount(defender, posessiveIndex + 3, out len))
                  {
                    if (IsPossiblePlayerName(defender, posessiveIndex))
                    {
                      defenderOwner = defender.Substring(0, posessiveIndex);
                      defenderPetType = defender.Substring(posessiveIndex + 3, len);
                    }
                  }
                }

                int dmgStart = afterAction + defender.Length + 5;
                if (part.Length > dmgStart)
                {
                  int afterDmg = part.IndexOf(" ", dmgStart);
                  if (afterDmg > -1)
                  {
                    damage = Utils.ParseLong(part.Substring(dmgStart, afterDmg - dmgStart));
                    if (damage != long.MaxValue)
                    {
                      int points;
                      if ((points = part.IndexOf(" points ", afterDmg)) > -1)
                      {
                        found = true;
                        if (part.Substring(points + 8, 6) == "of non")
                        {
                          type = "Direct Damage";
                        }
                      }
                    }
                  }
                }
              }
            }
            else if (action == "DoT")
            {
              //     @"^(.+) has taken (\d+) damage from (.+) by (\w+)\."
              // Kizant`s pet has taken
              int dmgStart = afterAction;
              int afterDmg = part.IndexOf(" ", dmgStart);
              if (afterDmg > -1)
              {
                damage = Utils.ParseLong(part.Substring(dmgStart, afterDmg - dmgStart));
                if (damage != long.MaxValue)
                {
                  if (part.Length > afterDmg + 12 && part.Substring(afterDmg, 12) == " damage from")
                  {
                    if (part.Substring(afterDmg + 13, 4) == "your")
                    {
                      attacker = "your";
                      action = "DoT";
                      found = true;
                    }
                    else
                    {
                      // Horizon of Destiny has taken 30812 damage from Strangulate Rk. III by Kimb.
                      // Warm Heart Flickers has taken 55896 damage from your Strangulate Rk. III.
                      int byIndex = part.IndexOf("by ", afterDmg + 12);
                      if (byIndex > -1)
                      {
                        int endIndex = part.IndexOf(".", byIndex + 3);
                        if (endIndex > -1)
                        {
                          string player = part.Substring(byIndex + 3, endIndex - byIndex - 3);
                          if (IsPossiblePlayerName(player, player.Length))
                          {
                            // damage parsed above
                            attacker = player;
                            action = "DoT";
                            found = true;
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
          }

          if (found)
          {
            record = new DamageRecord()
            {
              Attacker = attacker,
              Defender = defender,
              Type = Char.ToUpper(type[0]) + type.Substring(1),
              Action = action,
              Damage = damage,
              AttackerPetType = attackerPetType,
              AttackerOwner = attackerOwner,
              DefenderPetType = defenderPetType,
              DefenderOwner = defenderOwner,
              Modifiers = new Dictionary<string, byte>()
            };

            if (part[part.Length - 1] == ')')
            {
              // using 4 here since the shortest modifier should at least be 3 even in the future. probably.
              int firstParen = part.LastIndexOf('(', part.Length - 4);
              if (firstParen > -1)
              {
                foreach (string modifier in part.Substring(firstParen + 1, part.Length - 1 - firstParen - 1).Split(' '))
                {
                  record.Modifiers[modifier] = 1;
                }
              }
            }
          }
        }
      }
      catch (Exception e)
      {
        LOG.Error(e);
      }

      return record;
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

    private static bool IsPossiblePlayerName(string part, int stop)
    {
      bool found = stop < 3 ? false : true;
      for (int i = 0; found != false && i < stop; i++)
      {
        if (!Char.IsLetter(part, i))
        {
          found = false;
          break;
        }
      }

      return found;
    }
  }
}
