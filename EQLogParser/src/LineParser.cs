using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace EQLogParser
{
  class LineParser
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private const int ACTION_PART_INDEX = 27;
    private static Regex CheckEye = new Regex(@"^Eye of (\w+)", RegexOptions.Singleline | RegexOptions.Compiled);

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

    public static ConcurrentDictionary<string, byte> IgnoreMap = new ConcurrentDictionary<string, byte>(
      new List<KeyValuePair<string, byte>>
    {
      new KeyValuePair<string, byte>("Players", 1), new KeyValuePair<string, byte>("GUILD", 1),
      new KeyValuePair<string, byte>("Autojoining", 1), new KeyValuePair<string, byte>("Welcome", 1),
      new KeyValuePair<string, byte>("There", 1), new KeyValuePair<string, byte>("The", 1),
      new KeyValuePair<string, byte>("Fellowship", 1), new KeyValuePair<string, byte>("Targeted", 1),
      new KeyValuePair<string, byte>("Right", 1), new KeyValuePair<string, byte>("Beginning", 1),
      new KeyValuePair<string, byte>("Stand", 1), new KeyValuePair<string, byte>("MESSAGE", 1)
    });

    public static ProcessLine KeepForDamageProcessing(string line)
    {
      ProcessLine pline = null;

      try
      {
        int index;
        if (line.Length >= 40 && line.IndexOf(" damage", ACTION_PART_INDEX + 13, StringComparison.Ordinal) > -1)
        {
          pline = new ProcessLine() { Line = line, State = 0, ActionPart = line.Substring(ACTION_PART_INDEX) };
        }
        else if (line.Length >= 51 && (index = line.IndexOf(" healed ", ACTION_PART_INDEX, 24, StringComparison.Ordinal)) > -1 && char.IsUpper(line[index + 8]))
        {
          // WARNING -- this is only a subset of all heal lines
          pline = new ProcessLine() { Line = line, State = 2, ActionPart = line.Substring(ACTION_PART_INDEX) };
          pline.OptionalIndex = index - ACTION_PART_INDEX;
        }
        else if (line.Length < 102 && (index = line.IndexOf(" has been slain by", ACTION_PART_INDEX, StringComparison.Ordinal)) > -1)
        {
          pline = new ProcessLine() { Line = line, State = 1, ActionPart = line.Substring(ACTION_PART_INDEX) };
          pline.OptionalIndex = index - ACTION_PART_INDEX;
        }
        else
        {
          ProcessLine preProcessOnly = new ProcessLine() { Line = line, State = -1 };
          preProcessOnly.ActionPart = line.Substring(ACTION_PART_INDEX);

          // check other things
          if (!CheckForPlayers(preProcessOnly))
          {
            CheckForPetLeader(preProcessOnly);
          }
        }

        if (pline != null && pline.State >= 0)
        {
          pline.TimeString = pline.Line.Substring(1, 24);
          pline.CurrentTime = Utils.ParseDate(pline.TimeString);
        }
      }
      catch (Exception e)
      {
        LOG.Error(e);
      }

      return pline;
    }

    public static ProcessLine KeepForSpellCastProcessing(string line)
    {
      ProcessLine pline = null;

      try
      {
        int index = -1;
        if (line.Length > 44 && (index = line.IndexOf(" begin", ACTION_PART_INDEX + 3, StringComparison.Ordinal)) > -1)
        {
          int firstSpace = line.IndexOf(" ", ACTION_PART_INDEX);
          if (firstSpace > -1 && firstSpace == index)
          {
            if (firstSpace == (ACTION_PART_INDEX + 3) && line.Substring(ACTION_PART_INDEX, 3) == "You")
            {
              var test = line.Substring(index + 7, 4);
              if (test == "cast" || test == "sing")
              {
                pline = new ProcessLine() { Line = line, State = 10, ActionPart = line.Substring(ACTION_PART_INDEX) };
                pline.OptionalIndex = index - ACTION_PART_INDEX;
                pline.OptionalData = "you" + test;
              }
            }
            else
            {
              var test = line.Substring(index + 11, 4);
              if (test == "cast" || test == "sing")
              {
                pline = new ProcessLine() { Line = line, State = 10, ActionPart = line.Substring(ACTION_PART_INDEX) };
                pline.OptionalIndex = index - ACTION_PART_INDEX;
                pline.OptionalData = test;
              }
            }
          }
        }
        else // lands on messages
        {
          if (line.Length > ACTION_PART_INDEX + 6)
          {
            if (line.IndexOf("You", ACTION_PART_INDEX, 3, StringComparison.OrdinalIgnoreCase) > -1 && line[ACTION_PART_INDEX + 3] == ' ' || 
              (line[ACTION_PART_INDEX + 3] == 'r' && line[ACTION_PART_INDEX + 4] == ' ' && Char.IsLower(line[ACTION_PART_INDEX + 5])))
            {
              pline = new ProcessLine() { Line = line, State = 11, ActionPart = line.Substring(ACTION_PART_INDEX) };
            }
            else
            {
              int firstSpace = line.IndexOf(" ", ACTION_PART_INDEX, StringComparison.Ordinal);
              if (firstSpace > -1 && line[firstSpace - 2] == '\'' && line[firstSpace - 1] == 's')
              {
                pline = new ProcessLine() { Line = line, State = 12, ActionPart = line.Substring(ACTION_PART_INDEX) };
                pline.OptionalIndex = firstSpace + 1 - ACTION_PART_INDEX;
              }
              else if (firstSpace > -1)
              {
                string player = line.Substring(ACTION_PART_INDEX, firstSpace - ACTION_PART_INDEX);
                if (!IgnoreMap.ContainsKey(player) && (Utils.IsPossiblePlayerName(player)))
                {
                  if (line.Length > firstSpace + 6)
                  {
                    pline = new ProcessLine() { Line = line, State = 13, ActionPart = line.Substring(ACTION_PART_INDEX) };
                    pline.OptionalIndex = firstSpace + 1 - ACTION_PART_INDEX;
                    pline.OptionalData = player;
                  }
                }
              }
            }
          }
        }

        if (pline != null && pline.State >= 0)
        {
          pline.TimeString = pline.Line.Substring(1, 24);
          pline.CurrentTime = Utils.ParseDate(pline.TimeString);
        }
      }
      catch (Exception e)
      {
        LOG.Error(e);
      }

      return pline;
    }

    public static void CheckForLandsOnYou(ProcessLine pline)
    {
      List<string> results = DataManager.Instance.GetLandsOnYou(pline.ActionPart);
      if (results != null && results.Count > 0)
      {
        if (results.Count == 1)
        {
          DataManager.Instance.AddReceivedSpell(new ReceivedSpell()
          {
            Receiver = "You",
            BeginTime = pline.CurrentTime,
            SpellAbbrv = results[0]
          });
        }
      }
    }

    public static void CheckForNonPosessiveLandsOnOther(ProcessLine pline)
    {
      List<string> results = DataManager.Instance.GetNonPosessiveLandsOnOther(pline.ActionPart.Substring(pline.OptionalIndex));
      if (results != null && results.Count > 0)
      {
        if (results.Count == 1)
        {
          DataManager.Instance.AddReceivedSpell(new ReceivedSpell()
          {
            Receiver = pline.OptionalData,
            BeginTime = pline.CurrentTime,
            SpellAbbrv = results[0]
          });
        }
      }
    }

    public static void CheckForPosessiveLandsOnOther(ProcessLine pline)
    {
      List<string> results = DataManager.Instance.GetPosessiveLandsOnOther(pline.ActionPart.Substring(pline.OptionalIndex));
      if (results != null && results.Count > 0)
      {
        if (results.Count == 1)
        {
          DataManager.Instance.AddReceivedSpell(new ReceivedSpell()
          {
            Receiver = pline.ActionPart.Substring(0, pline.OptionalIndex - 3),
            BeginTime = pline.CurrentTime,
            SpellAbbrv = results[0]
          });
        }
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

    public static void CheckForHeal(ProcessLine pline)
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

      if (foundHealer && !foundHealed && Utils.IsPossiblePlayerName(healed, healed.Length))
      {
        DataManager.Instance.UpdateUnverifiedPetOrPlayer(healed, true);
      }
      else if (!foundHealer && foundHealed && Utils.IsPossiblePlayerName(healer, healer.Length))
      {
        DataManager.Instance.UpdateVerifiedPlayers(healer);
      }
    }

    public static SpellCast ParseSpellCast(ProcessLine pline)
    {
      SpellCast cast = null;
      string caster = pline.ActionPart.Substring(0, pline.OptionalIndex);

      switch (pline.OptionalData)
      {
        case "cast":
        case "sing":
          int bracketIndex = (pline.OptionalData == "cast") ? 25 : 24;
          if (pline.ActionPart.Length > pline.OptionalIndex + bracketIndex)
          {
            int finalBracket;
            int index = pline.ActionPart.IndexOf("<", pline.OptionalIndex + bracketIndex);
            if (index > -1 && (finalBracket = pline.ActionPart.IndexOf(">", pline.OptionalIndex + bracketIndex, StringComparison.Ordinal)) > -1)
            {
              cast = new SpellCast() { Caster = caster, Spell = pline.ActionPart.Substring(index + 1, finalBracket - index - 1), BeginTime = pline.CurrentTime };
            }
          }
          break;
        case "youcast":
        case "yousing":
          if (pline.ActionPart.Length > pline.OptionalIndex + 15)
          {
            cast = new SpellCast()
            {
              Caster = caster,
              Spell = pline.ActionPart.Substring(pline.OptionalIndex + 15, pline.ActionPart.Length - pline.OptionalIndex - 15 - 1),
              BeginTime = pline.CurrentTime
            };
          }
          break;
      }

      if (cast != null)
      {
        cast.SpellAbbrv = Utils.AbbreviateSpellName(cast.Spell);
      }
      return cast;
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
          && Utils.IsPossiblePlayerName(pline.ActionPart, index))
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
            if (Utils.IsPossiblePlayerName(part, firstSpace - 2))
            {
              int len;
              if (Utils.IsPetOrMount(part, firstSpace + 1, out len))
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
          else if (Utils.IsPossiblePlayerName(part, firstSpace))
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
                  if (Utils.IsPetOrMount(defender, posessiveIndex + 3, out len))
                  {
                    if (Utils.IsPossiblePlayerName(defender, posessiveIndex))
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
                    damage = Utils.ParseLong(part.Substring(dmgStart, afterDmg - dmgStart));
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
                      int byIndex = part.IndexOf("by ", afterDmg + 12, StringComparison.Ordinal);
                      if (byIndex > -1)
                      {
                        int endIndex = part.IndexOf(".", byIndex + 3, StringComparison.Ordinal);
                        if (endIndex > -1)
                        {
                          string player = part.Substring(byIndex + 3, endIndex - byIndex - 3);
                          if (Utils.IsPossiblePlayerName(player, player.Length))
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
  }
}
