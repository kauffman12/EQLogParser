using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace EQLogParser
{
  class DamageLineParser
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    public static event EventHandler<DamageProcessedEvent> EventsDamageProcessed;
    public static event EventHandler<string> EventsLineProcessed; 

    private const int ACTION_PART_INDEX = 27;
    private static DateUtil DateUtil = new DateUtil();
    private static Regex CheckEye = new Regex(@"^Eye of (\w+)", RegexOptions.Singleline | RegexOptions.Compiled);

    private static Dictionary<string, byte> HitMap = new Dictionary<string, byte>()
    {
      { "bash", 1 }, { "bit", 1 }, { "backstab", 1 }, { "claw", 1 }, { "crush", 1 }, { "frenzies", 1 },
      { "frenzy", 1 }, { "gore", 1 }, { "hit", 1 }, { "kick", 1 }, { "maul", 1 }, { "punch", 1 },
      { "pierce", 1 }, { "rend", 1 }, { "shoot", 1 }, { "slash", 1 }, { "slam", 1 }, { "slice", 1 },
      { "smash", 1 }, { "sting", 1 }, { "strike", 1 }, { "bashes", 1 }, { "bites", 1 }, { "backstabs", 1 },
      { "claws", 1 }, { "crushes", 1 }, { "gores", 1 }, { "hits", 1 }, { "kicks", 1 }, { "mauls", 1 },
      { "punches", 1 }, { "pierces", 1 }, { "rends", 1 }, { "shoots", 1 }, { "slashes", 1 }, { "slams", 1 },
      { "slices", 1 }, { "smashes", 1 }, { "stings", 1 }, { "strikes", 1 }
    };

    private static Dictionary<string, string> HitAdditionalMap = new Dictionary<string, string>()
    {
      { "frenzies", "frenzies on" }, { "frenzy", "frenzy on" }
    };

    public static void Process(string line)
    {
      try
      {
        int index;
        if (line.Length >= 40 && line.IndexOf(" damage", ACTION_PART_INDEX + 13, StringComparison.Ordinal) > -1)
        {
          ProcessLine pline = new ProcessLine() { Line = line, ActionPart = line.Substring(ACTION_PART_INDEX) };
          pline.TimeString = pline.Line.Substring(1, 24);
          pline.CurrentTime = DateUtil.ParseDate(pline.TimeString);

          DamageRecord record = ParseDamage(pline.ActionPart);
          if (record != null)
          {
            DamageProcessedEvent e = new DamageProcessedEvent() { Record = record, ProcessLine = pline };
            EventsDamageProcessed(record, e);
          }
        }
        else if (line.Length >= 51 && (index = line.IndexOf(" healed ", ACTION_PART_INDEX, 24, StringComparison.Ordinal)) > -1 && char.IsUpper(line[index + 8]))
        {
          // WARNING -- this is only a subset of all heal lines
          ProcessLine pline = new ProcessLine() { Line = line, ActionPart = line.Substring(ACTION_PART_INDEX) };
          pline.OptionalIndex = index - ACTION_PART_INDEX;
          pline.TimeString = pline.Line.Substring(1, 24);
          pline.CurrentTime = DateUtil.ParseDate(pline.TimeString);
          HandleHealed(pline);
        }
        else if (line.Length < 102 && (index = line.IndexOf(" has been slain by", ACTION_PART_INDEX, StringComparison.Ordinal)) > -1)
        {
          ProcessLine pline = new ProcessLine() { Line = line, ActionPart = line.Substring(ACTION_PART_INDEX) };
          pline.OptionalIndex = index - ACTION_PART_INDEX;
          pline.TimeString = pline.Line.Substring(1, 24);
          pline.CurrentTime = DateUtil.ParseDate(pline.TimeString);
          HandleSlain(pline);
        }
        else
        {
          ProcessLine pline= new ProcessLine() { Line = line, ActionPart = line.Substring(ACTION_PART_INDEX) };

          // check other things
          if (!CheckForPlayers(pline))
          {
            CheckForPetLeader(pline);
          }
        }
      }
      catch (Exception e)
      {
        LOG.Error(e);
      }

      EventsLineProcessed(line, line);
    }

    private static void HandleSlain(ProcessLine pline)
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

    private static void HandleHealed(ProcessLine pline)
    {
      string healed = null;
      string healer = pline.ActionPart.Substring(0, pline.OptionalIndex);

      int forword = pline.ActionPart.IndexOf(" for ", pline.OptionalIndex + 8, StringComparison.Ordinal);
      if (forword > -1)
      {
        healed = pline.ActionPart.Substring(pline.OptionalIndex + 8, forword - pline.OptionalIndex - 8);
      }

      bool foundHealer = DataManager.Instance.CheckNameForPlayer(healer);
      bool foundHealed = DataManager.Instance.CheckNameForPlayer(healed) || DataManager.Instance.CheckNameForPet(healed);

      if (foundHealer && !foundHealed && Helpers.IsPossiblePlayerName(healed, healed.Length))
      {
        DataManager.Instance.UpdateUnverifiedPetOrPlayer(healed, true);
      }
      else if (!foundHealer && foundHealed && Helpers.IsPossiblePlayerName(healer, healer.Length))
      {
        DataManager.Instance.UpdateVerifiedPlayers(healer);
      }
    }

    private static bool CheckForPetLeader(ProcessLine pline)
    {
      bool found = false;
      if (pline.ActionPart.Length >= 28 && pline.ActionPart.Length < 55)
      {
        int index = pline.ActionPart.IndexOf(" says, 'My leader is ", StringComparison.Ordinal);
        if (index > -1)
        {
          string pet = pline.ActionPart.Substring(0, index);
          if (!DataManager.Instance.CheckNameForPlayer(pet)) // thanks idiots for this
          {
            int period = pline.ActionPart.IndexOf(".", index + 24, StringComparison.Ordinal);
            if (period > -1)
            {
              string owner = pline.ActionPart.Substring(index + 21, period - index - 21);
              DataManager.Instance.UpdateVerifiedPlayers(owner);
              DataManager.Instance.UpdateVerifiedPets(pet);
              DataManager.Instance.UpdatePetToPlayer(pet, owner);
            }
          }

          found = true;
        }
      }
      return found;
    }

    private static bool CheckForPlayers(ProcessLine pline)
    {
      bool found = false;
      if (pline.ActionPart.Length < 35)
      {
        int index = -1;
        if (pline.ActionPart.Length > 10 && pline.ActionPart.Length < 25 && (index = pline.ActionPart.IndexOf(" shrinks.", StringComparison.Ordinal)) > -1
          && Helpers.IsPossiblePlayerName(pline.ActionPart, index))
        {
          string test = pline.ActionPart.Substring(0, index);
          DataManager.Instance.UpdateUnverifiedPetOrPlayer(test);
          found = true;
        }
        else if ((index = pline.ActionPart.IndexOf(" tells the guild, ", StringComparison.Ordinal)) > -1)
        {
          int firstSpace = pline.ActionPart.IndexOf(" ", StringComparison.Ordinal);
          if (firstSpace > -1 && firstSpace == index)
          {
            string name = pline.ActionPart.Substring(0, index);
            DataManager.Instance.UpdateVerifiedPlayers(name);
          }
          found = true; // found chat, not that it had to work
        }
        else if (pline.ActionPart.StartsWith("Targeted (Player)", StringComparison.Ordinal))
        {
          DataManager.Instance.UpdateVerifiedPlayers(pline.ActionPart.Substring(19));
          found = true;
        }
      }
      return found;
    }

    private static DamageRecord ParseDamage(string part)
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
        int firstSpace = part.IndexOf(" ", StringComparison.Ordinal);
        if (firstSpace > 0)
        {
          // check if name has a possessive
          if (firstSpace >= 2 && part.Substring(firstSpace - 2, 2) == "`s")
          {
            if (Helpers.IsPossiblePlayerName(part, firstSpace - 2))
            {
              int len;
              if (Helpers.IsPetOrMount(part, firstSpace + 1, out len))
              {
                string petType = part.Substring(firstSpace + 1, len);
                string owner = part.Substring(0, firstSpace - 2);

                int sizeSoFar = firstSpace + 1 + len + 1;
                if (part.Length > sizeSoFar)
                {
                  string player = part.Substring(0, sizeSoFar - 1);
                  int secondSpace = part.IndexOf(" ", sizeSoFar, StringComparison.Ordinal);
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
          else if (Helpers.IsPossiblePlayerName(part, firstSpace))
          {
            int sizeSoFar = firstSpace + 1;
            int secondSpace = part.IndexOf(" ", sizeSoFar, StringComparison.Ordinal);
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
            int hasTakenIndex = part.IndexOf("has taken ", firstSpace + 1, StringComparison.Ordinal);
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
              int forIndex = part.IndexOf(" for ", afterAction, StringComparison.Ordinal);
              if (forIndex > -1)
              {
                defender = part.Substring(afterAction, forIndex - afterAction);
                int posessiveIndex = defender.IndexOf("`s ", StringComparison.Ordinal);
                if (posessiveIndex > -1)
                {
                  int len;
                  if (Helpers.IsPetOrMount(defender, posessiveIndex + 3, out len))
                  {
                    if (Helpers.IsPossiblePlayerName(defender, posessiveIndex))
                    {
                      defenderOwner = defender.Substring(0, posessiveIndex);
                      defenderPetType = defender.Substring(posessiveIndex + 3, len);
                    }
                  }
                }

                int dmgStart = afterAction + defender.Length + 5;
                if (part.Length > dmgStart)
                {
                  int afterDmg = part.IndexOf(" ", dmgStart, StringComparison.Ordinal);
                  if (afterDmg > -1)
                  {
                    damage = Helpers.ParseLong(part.Substring(dmgStart, afterDmg - dmgStart));
                    if (damage != long.MaxValue)
                    {
                      int points;
                      if ((points = part.IndexOf(" points ", afterDmg, StringComparison.Ordinal)) > -1)
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
              int afterDmg = part.IndexOf(" ", dmgStart, StringComparison.Ordinal);
              if (afterDmg > -1)
              {
                damage = Helpers.ParseLong(part.Substring(dmgStart, afterDmg - dmgStart));
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
                      int byIndex = part.IndexOf("by ", afterDmg + 12, StringComparison.Ordinal);
                      if (byIndex > -1)
                      {
                        int endIndex = part.IndexOf(".", byIndex + 3, StringComparison.Ordinal);
                        if (endIndex > -1)
                        {
                          string player = part.Substring(byIndex + 3, endIndex - byIndex - 3);
                          if (Helpers.IsPossiblePlayerName(player, player.Length))
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
              DefenderOwner = defenderOwner
            };

            if (part[part.Length - 1] == ')')
            {
              // using 4 here since the shortest modifier should at least be 3 even in the future. probably.
              int firstParen = part.LastIndexOf('(', part.Length - 4);
              if (firstParen > -1)
              {
                record.Modifiers = part.Substring(firstParen + 1, part.Length - 1 - firstParen - 1);
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
  }
}
