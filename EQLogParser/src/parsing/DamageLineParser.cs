using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace EQLogParser
{
  class DamageLineParser
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    public static event EventHandler<DamageProcessedEvent> EventsDamageProcessed;
    public static event EventHandler<ResistProcessedEvent> EventsResistProcessed;
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
      { "slices", 1 }, { "smashes", 1 }, { "stings", 1 }, { "strikes", 1 }, { "learn", 1 }, { "learns", 1 },
      { "sweep", 1 }, { "sweeps", 1 }
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

          DamageRecord record = ParseDamage(pline);
          if (record != null)
          {
            DamageProcessedEvent e = new DamageProcessedEvent() { Record = record, TimeString = pline.TimeString, BeginTime = pline.CurrentTime };
            EventsDamageProcessed(record, e);
          }
        }
        else if (line.Length < 102 && (index = line.IndexOf(" slain ", ACTION_PART_INDEX, StringComparison.Ordinal)) > -1)
        {
          ProcessLine pline = new ProcessLine() { Line = line, ActionPart = line.Substring(ACTION_PART_INDEX) };
          pline.OptionalIndex = index - ACTION_PART_INDEX;
          pline.TimeString = pline.Line.Substring(1, 24);
          pline.CurrentTime = DateUtil.ParseDate(pline.TimeString);
          HandleSlain(pline);
        }
        else if (line.Length >= 40 && line.Length < 110 && (index = line.IndexOf(" resisted your ", ACTION_PART_INDEX, StringComparison.Ordinal)) > -1)
        {
          ProcessLine pline = new ProcessLine() { Line = line, ActionPart = line.Substring(ACTION_PART_INDEX) };
          pline.OptionalIndex = index - ACTION_PART_INDEX;
          pline.TimeString = pline.Line.Substring(1, 24);
          pline.CurrentTime = DateUtil.ParseDate(pline.TimeString);
          HandleResist(pline);
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
      string test = null;
      if (pline.ActionPart.Length > 16 && pline.ActionPart.StartsWith("You have slain ") && pline.ActionPart[pline.ActionPart.Length-1] == '!')
      {
        test = pline.ActionPart.Substring(15, pline.ActionPart.Length - 15 - 1);
      }
      else if (pline.OptionalIndex > 9)
      {
        test = pline.ActionPart.Substring(0, pline.OptionalIndex - 9);
      }

      // Gotcharms has been slain by an animated mephit!
      if (test != null && test.Length > 0)
      {
        if (DataManager.Instance.CheckNameForPlayer(test) || DataManager.Instance.CheckNameForPet(test))
        {
          int byIndex = pline.ActionPart.IndexOf(" by ");
          if (byIndex > -1)
          {
            DataManager.Instance.AddPlayerDeath(test, pline.ActionPart.Substring(byIndex + 4), pline.CurrentTime);
          }
        }
        else if (!DataManager.Instance.RemoveActiveNonPlayer(test) && char.IsUpper(test[0]))
        {
          DataManager.Instance.RemoveActiveNonPlayer(char.ToLower(test[0]) + test.Substring(1));
        }
      }
    }

    private static void HandleResist(ProcessLine pline)
    {
      // [Mon Feb 11 20:00:28 2019] An inferno flare resisted your Frostreave Strike III!
      string defender = pline.ActionPart.Substring(0, pline.OptionalIndex);
      string spell = pline.ActionPart.Substring(pline.OptionalIndex + 15, pline.ActionPart.Length - pline.OptionalIndex - 15 - 1);

      ResistRecord record = new ResistRecord() { Spell = spell };
      ResistProcessedEvent e = new ResistProcessedEvent() { Record = record, BeginTime = pline.CurrentTime };
      EventsResistProcessed(defender, e);
    }

    private static DamageRecord ParseDamage(ProcessLine pline)
    {
      DamageRecord record = null;

      record = ParseAllDamage(pline);
      if (record != null)
      {
        // Needed to replace 'You' and 'you', etc
        bool replaced;
        record.Attacker = DataManager.Instance.ReplacePlayer(record.Attacker, out replaced);

        bool isDefenderPet, isAttackerPet;
        CheckDamageRecordForPet(record, replaced, out isDefenderPet, out isAttackerPet);

        bool isDefenderPlayer, isAttackerPlayer;
        CheckDamageRecordForPlayer(record, replaced, out isDefenderPlayer, out isAttackerPlayer);

        if (isDefenderPlayer || isDefenderPet)
        {
          if (record.Attacker != record.Defender && !(isAttackerPlayer || isAttackerPet))
          {
            DataManager.Instance.UpdateProbablyNotAPlayer(record.Attacker);
          }

          record = null;
        }
        else if (CheckEye.IsMatch(record.Defender) || record.Defender.EndsWith("chest") || record.Defender.EndsWith("satchel"))
        {
          record = null;
        }

        if (record != null && record.Attacker != record.Defender)
        {
          if (DataManager.Instance.IsProbablyNotAPlayer(record.Attacker))
          {
            DataManager.Instance.UpdateUnVerifiedPetOrPlayer(record.Defender);
            record = null;
          }

          // if updating this fails then it's definitely a player or pet
          if (record != null && !DataManager.Instance.UpdateProbablyNotAPlayer(record.Defender))
          {
            record = null;
          }
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
        if (record.AttackerOwner != "")
        {
          DataManager.Instance.UpdateVerifiedPets(record.Attacker);
          isAttackerPet = true;
        }
        else
        {
          isAttackerPet = DataManager.Instance.CheckNameForPet(record.Attacker);

          if (isAttackerPet)
          {
            record.AttackerOwner = Labels.UNASSIGNED_PET_OWNER;
          }
        }
      }

      if (record.DefenderOwner != "")
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
        if (record.AttackerOwner != "")
        {
          DataManager.Instance.UpdateVerifiedPlayers(record.AttackerOwner);
          isAttackerPlayer = true;
        }

        if (record.DefenderOwner != "")
        {
          DataManager.Instance.UpdateVerifiedPlayers(record.DefenderOwner);
        }
      }

      isDefenderPlayer = record.DefenderOwner == "" && DataManager.Instance.CheckNameForPlayer(record.Defender);
    }

    private static DamageRecord ParseAllDamage(ProcessLine pline)
    {
      DamageRecord record = null;
      string part = pline.ActionPart;

      try
      {
        bool found = false;
        string type = "";
        string attacker = "";
        string attackerOwner = "";
        string defender = "";
        string defenderOwner = "";
        int afterAction = -1;
        uint damage = 0;
        string action = "";
        string spell = "";

        // find first space and see if we have a name at the beginning
        int firstSpace = part.IndexOf(" ", StringComparison.Ordinal);
        if (firstSpace > 0)
        {
          // check if name has a possessive
          if (firstSpace >= 2 && part[firstSpace - 2] == '`' && part[firstSpace - 1] == 's')
          {
            string owner = part.Substring(0, firstSpace - 2);
            if (DataManager.Instance.CheckNameForPlayer(owner) || Helpers.IsPossiblePlayerName(owner))
            {
              int len;
              if (IsPetOrMount(part, firstSpace + 1, out len))
              {
                //string petType = part.Substring(firstSpace + 1, len);

                int sizeSoFar = firstSpace + 1 + len + 1;
                if (part.Length > sizeSoFar)
                {
                  int secondSpace = part.IndexOf(" ", sizeSoFar, StringComparison.Ordinal);
                  if (secondSpace > -1)
                  {
                    string testAction = part.Substring(sizeSoFar, secondSpace - sizeSoFar);

                    if ((type = CheckType(testAction)) != "")
                    {
                      action = "DD";
                      afterAction = sizeSoFar + type.Length + 1;
                      attackerOwner = owner;
                      attacker = part.Substring(0, sizeSoFar - 1);
                    }
                    else
                    {
                      if (testAction == "has" && part.Substring(sizeSoFar + 3, 7) == " taken ")
                      {
                        action = "DoT";
                        type = Labels.DOT_NAME;
                        afterAction = sizeSoFar + "has taken".Length + 1;
                        defenderOwner = owner;
                        defender = part.Substring(0, sizeSoFar - 1);
                      }
                    }
                  }
                }
              }
            }
          }
          else
          {
            string player = part.Substring(0, firstSpace);
            if (DataManager.Instance.CheckNameForPlayer(player) || Helpers.IsPossiblePlayerName(part, firstSpace))
            {
              int sizeSoFar = firstSpace + 1;
              int secondSpace = part.IndexOf(" ", sizeSoFar, StringComparison.Ordinal);
              if (secondSpace > -1)
              {
                string testAction = part.Substring(sizeSoFar, secondSpace - sizeSoFar);

                if ((type = CheckType(testAction)) != "")
                {
                  action = "DD";
                  afterAction = sizeSoFar + type.Length + 1;
                  attacker = player;
                }
                else
                {
                  if (testAction == "has" && part.IndexOf(" taken ", sizeSoFar + 3, 7, StringComparison.Ordinal) > -1)
                  {
                    action = "DoT";
                    type = Labels.DOT_NAME;
                    afterAction = sizeSoFar + "has taken".Length + 1;
                    defender = player;
                  }
                }
              }
            }
          }

          // === TODO == 
          // Your body and mind are wracked by elemental madness!  You have taken 5800 points of damage.
          //

          if (action == "")
          {
            // check if it's an NPC if it's a DoT and they're the defender
            // ALSO true for Damage Shields and BANE
            int hasTakenIndex = part.IndexOf("has taken ", firstSpace + 1, StringComparison.Ordinal);
            if (hasTakenIndex > -1)
            {
              // [Fri Feb 08 19:58:38 2019] Ladenfir has taken 81527 damage from Magnificent Presence by Unfettered Emerald Excellence.
              // [Fri Feb 08 21:00:14 2019] a wave sentinel has taken an extra 6250000 points of non-melee damage from Abazzagorath's Shackles of Tunare II spell.
              int extraIndex = part.IndexOf("an extra ", hasTakenIndex + 10, StringComparison.Ordinal);
              if (extraIndex == -1)
              {
                action = "DoT";
                defender = part.Substring(0, hasTakenIndex - 1);
                type = Labels.DOT_NAME;
                afterAction = hasTakenIndex + 10;
              }
              else
              {
                action = "Bane";
                defender = part.Substring(0, hasTakenIndex - 1);
                type = Labels.BANE_NAME;
                afterAction = extraIndex + 9;
              }
            }
            else // Maybe it's a Damage Shield
            {
              // [Sat Feb 09 21:12:43 2019] A lava protector is[are] pierced by Incogitable's thorns for 124 points of non-melee damage.
              // [Sat Feb 09 21:32:22 2019] A molten spirit is[are] pierced by YOUR thorns for 182 points of non-melee damage.
              int byIndex = part.IndexOf(" by ");
              if (byIndex > -1)
              {
                int isIndex = part.IndexOf(" is ", 0, byIndex, StringComparison.Ordinal);
                if (isIndex > -1 || (isIndex = part.IndexOf(" are ", 0, byIndex, StringComparison.Ordinal)) > -1)
                {
                  defender = part.Substring(0, isIndex);
                  action = "DS";
                  type = Labels.DS_NAME;
                  afterAction = byIndex + 4;

                  // The following DD/DoT code doesn't really help parse damage shields so continue here... need to clean this up eventually
                  if (part.Length > afterAction + 25)
                  {
                    // check for YOUR
                    int afterAttacker = -1;
                    if (part[afterAction] == 'Y' && part[afterAction + 1] == 'O' && part[afterAction + 2] == 'U' && part[afterAction + 3] == 'R' && part[afterAction + 4] == ' ')
                    {
                      attacker = "YOUR";
                      afterAttacker = afterAction + 5;
                    }
                    else
                    {
                      int test = part.IndexOf("'s", afterAction, 25, StringComparison.Ordinal);
                      if (test > -1)
                      {
                        attacker = part.Substring(afterAction, test - afterAction);
                        afterAttacker = test + 3;
                      }
                    }

                    if (attacker != "" && afterAttacker > -1)
                    {
                      int forIndex = part.IndexOf(" for ", afterAttacker, StringComparison.Ordinal);
                      if (forIndex > -1)
                      {
                        int pointIndex = part.IndexOf(" point", forIndex + 3, StringComparison.Ordinal);
                        if (pointIndex > -1)
                        {
                          damage = StatsUtil.ParseUInt(part.Substring(forIndex + 5, pointIndex - forIndex - 5));
                          found = damage != uint.MaxValue;
                        }
                      }
                    }
                  }
                }
              }
            }
          }

          if (!found && type != "" && action != "" && part.Length > afterAction)
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
                  if (IsPetOrMount(defender, posessiveIndex + 3, out len))
                  {
                    if (Helpers.IsPossiblePlayerName(defender, posessiveIndex))
                    {
                      defenderOwner = defender.Substring(0, posessiveIndex);
                      //defenderPetType = defender.Substring(posessiveIndex + 3, len);
                    }
                  }
                }

                int dmgStart = afterAction + defender.Length + 5;
                if (part.Length > dmgStart)
                {
                  int afterDmg = part.IndexOf(" ", dmgStart, StringComparison.Ordinal);
                  if (afterDmg > -1)
                  {
                    damage = StatsUtil.ParseUInt(part.Substring(dmgStart, afterDmg - dmgStart));
                    if (damage != uint.MaxValue)
                    {
                      // can be point or points
                      int point;
                      if ((point = part.IndexOf(" point", afterDmg, StringComparison.Ordinal)) > -1)
                      {
                        found = true;
                        if (part.IndexOf("of damage.", point + 7, StringComparison.Ordinal) == -1)
                        {
                          type = Labels.DD_NAME;

                          int byIndex = part.IndexOf(" by ", point + 18, StringComparison.Ordinal);
                          if (byIndex > -1)
                          {
                            int endIndex = part.LastIndexOf(".", StringComparison.Ordinal);
                            if (endIndex > -1 && endIndex > byIndex)
                            {
                              spell = part.Substring(byIndex + 4, endIndex - byIndex - 4);
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            else if (action == "DoT" || action == "Bane")
            {
              // @"^(.+) has taken (\d+) damage from (.+) by (\w+)\."
              // Kizant`s pet has taken
              int dmgStart = afterAction;
              int afterDmg = part.IndexOf(" ", dmgStart, StringComparison.Ordinal);
              if (afterDmg > -1)
              {
                damage = StatsUtil.ParseUInt(part.Substring(dmgStart, afterDmg - dmgStart));
                if (damage != uint.MaxValue)
                {
                  int fromIndex = part.IndexOf(" from ", afterDmg, StringComparison.Ordinal);
                  if (fromIndex > -1)
                  {
                    if (part.IndexOf(" your ", fromIndex + 5, 6, StringComparison.Ordinal) > 1)
                    {
                      int periodIndex = part.LastIndexOf('.');
                      if (periodIndex > -1)
                      {
                        // if this is needed for Bane then remember to account for the word " spell" appearing at the end
                        // but don't set spell for now or it will be counted as a DoT and we dont use it anyway
                        if (action == "DoT")
                        {
                          spell = part.Substring(fromIndex + 11, periodIndex - fromIndex - 11);
                        }
                      }

                      attacker = "your";
                      found = true;
                    }
                    else if (action == "DoT")
                    {
                      // Horizon of Destiny has taken 30812 damage from Strangulate Rk. III by Kimb.
                      // Warm Heart Flickers has taken 55896 damage from your Strangulate Rk. III.
                      int byIndex = part.IndexOf("by ", fromIndex, StringComparison.Ordinal);
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
                            spell = part.Substring(afterDmg + 13, byIndex - afterDmg - 14);
                            found = true;
                          }
                        }
                      }
                    }
                    else if (action == "Bane")
                    {
                      int endIndex = part.IndexOf("'s", fromIndex, StringComparison.Ordinal);
                      if (endIndex > -1)
                      {
                        string player = part.Substring(fromIndex + 6, endIndex - fromIndex - 6);
                        if (Helpers.IsPossiblePlayerName(player, player.Length))
                        {
                          // damage parsed above
                          attacker = player;
                          found = true;
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
              Attacker = string.Intern(FixName(attacker)),
              Defender = string.Intern(FixName(defender)),
              Type = string.Intern(char.ToUpper(type[0]) + type.Substring(1)),
              Total = damage,
              AttackerOwner = string.Intern(attackerOwner),
              DefenderOwner = string.Intern(defenderOwner),
              ModifiersMask = -1
            };

            // set sub type if spell is available
            record.SubType = spell == "" ? record.Type : string.Intern(spell);

            if (part[part.Length - 1] == ')')
            {
              // using 4 here since the shortest modifier should at least be 3 even in the future. probably.
              int firstParen = part.LastIndexOf('(', part.Length - 4);
              if (firstParen > -1)
              {
                record.ModifiersMask = LineModifiersParser.Parse(part.Substring(firstParen + 1, part.Length - 1 - firstParen - 1));
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

    private static string CheckType(string testAction)
    {
      string type = "";
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
      }

      return type;
    }
  }
}
