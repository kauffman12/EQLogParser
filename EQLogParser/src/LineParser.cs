using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EQLogParser
{
  class LineParser
  {
    public static NpcDamageManager NpcDamageManagerInstance;
    public static ConcurrentDictionary<string, string> AttackerReplacement = new ConcurrentDictionary<string, string>();
    public static ConcurrentDictionary<string, string> PetToPlayers = new ConcurrentDictionary<string, string>();

    // counting this thing is really slow
    private static int DateCount = 0;
    private static ConcurrentDictionary<string, DateTime> DateTimeCache = new ConcurrentDictionary<string, DateTime>();

    private const int MIN_LINE_LENGTH = 33;
    private static Regex CheckEye = new Regex(@"^Eye of (\w+)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static Regex CheckLeader = new Regex(@"^(\w+) says, 'My leader is (\w+)\.'", RegexOptions.Singleline | RegexOptions.Compiled);
    private static Regex CheckDoTDamage = new Regex(@"^(.+) has taken (\d+) damage from (.+) by (\w+)\.", RegexOptions.Singleline | RegexOptions.Compiled);

    private delegate DamageRecord ParseDamageFunc(string line);

    public static ConcurrentDictionary<string, bool> GeneratedPets = new ConcurrentDictionary<string, bool>();
    public static ConcurrentDictionary<string, bool> VerifiedPets = new ConcurrentDictionary<string, bool>();
    public static ConcurrentDictionary<string, bool> NotNPC = new ConcurrentDictionary<string, bool>();

    public static ConcurrentDictionary<string, bool> VerifiedPlayers = new ConcurrentDictionary<string, bool>(
      new List<KeyValuePair<string, bool>>
    {
      new KeyValuePair<string, bool>("himself", true), new KeyValuePair<string, bool>("you", true), new KeyValuePair<string, bool>("YOU", true)
    });

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

    // still need to work on this
    public static int PreCheck(ProcessLine pline)
    {
      int result = -1;

      int index;

      if (pline.ActionPart.Contains(" damage")) // damage. for DD
      {
        return 0;
      }

      if ((index = pline.ActionPart.IndexOf(" has been slain by")) > -1)
      {
        pline.OptionalIndex = index;
        return 1;
      }

      if (pline.ActionPart.Length > 10 && pline.ActionPart.Length < 25 && (index = pline.ActionPart.IndexOf(" shrinks.")) > -1)
      {
        pline.OptionalIndex = index;
        return 2;
      }

      if (pline.ActionPart.Contains(" tells the guild, "))
      {
        return 3;
      }

      if (pline.ActionPart.StartsWith("Targeted(Play"))
      {
        return 4;
      }

      if (pline.ActionPart.Length <= 55 && pline.Line.Contains("My leader is"))
      {
        return 5;
      }

      return result;
    }

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
        Console.WriteLine(e.StackTrace);
      }

      return pline;
    }

    public static bool CheckForShrink(ProcessLine pline, out string name)
    {
      bool needUpdate = false;
      name = "";

      if (IsPossiblePlayerName(pline.ActionPart, pline.OptionalIndex))
      {
        string test = pline.ActionPart.Substring(0, pline.OptionalIndex);
        if (!VerifiedPlayers.ContainsKey(test) && !VerifiedPets.ContainsKey(test))
        {
          NotNPC.TryAdd(test, true);
          needUpdate = NpcDamageManagerInstance.CheckForPlayer(test);
          if (needUpdate)
          {
            name = test;
          }
        }
      }
      return needUpdate;
    }

    public static void CheckForSlain(ProcessLine pline)
    {
      string sub = pline.ActionPart.Substring(0, pline.OptionalIndex);
      if (!VerifiedPlayers.ContainsKey(sub) && !VerifiedPets.ContainsKey(sub))
      {
        NpcDamageManagerInstance.Slain(sub);
      }
    }

    public static bool CheckForPetLeader(ProcessLine pline, out string name)
    {
      bool found = false;
      name = "";

      MatchCollection matches = CheckLeader.Matches(pline.ActionPart);
      if (matches.Count > 0 && matches[0].Groups.Count == 3)
      {
        string pet = matches[0].Groups[1].Value;
        string owner = matches[0].Groups[2].Value;

        VerifiedPlayers.TryAdd(owner, true);
        VerifiedPets.TryAdd(pet, true);

        if (PetToPlayers.TryAdd(pet, owner))
        {
          name = pet;
        }

        found = true;
      }

      return found;
    }

    public static bool CheckForPlayers(ProcessLine pline, out string name, out bool needRemove)
    {
      bool found = false;
      needRemove = false;
      name = "";

      string player = null;
      if (pline.ActionPart.StartsWith("Targeted (Player): "))
      {
        player = pline.ActionPart.Substring(19);
      }
      else
      {
        int index = pline.ActionPart.IndexOf(' ');
        if (index > 0)
        {
          string action = pline.ActionPart.Substring(index);
          if (action.StartsWith(" tells the guild,"))
          {
            player = pline.ActionPart.Substring(0, index);
          }
        }
      }

      if (player != null && VerifiedPlayers.TryAdd(player, true))
      {
        if (NpcDamageManagerInstance.CheckForPlayer(player))
        {
          needRemove = true;
        }

        name = player;
        found = true;
      }

      return found;
    }

    public static DamageRecord ParseDamage(string line, out List<string> newPlayers, out List<string> newPets, out bool needRemoved)
    {
      DamageRecord record = null;
      needRemoved = false;
      newPlayers = new List<string>();
      newPets = new List<string>();

      record = ParseAllDamage(line);
      if (record != null)
      {
        // replaces You and you and maybe more in the future
        string replaced;
        bool failure = false;

        // handle Attackers as a set
        if (AttackerReplacement.TryGetValue(record.Attacker, out replaced))
        {
          record.Attacker = replaced;
        }
        else
        {
          if ((record.AttackerPetType != "" && VerifiedPets.TryAdd(record.Attacker, true)) ||
            GeneratedPets.ContainsKey(record.Attacker) && VerifiedPets.TryAdd(record.Attacker, true))
          {
            newPets.Add(record.Attacker);
            needRemoved = needRemoved || NpcDamageManagerInstance.CheckForPlayer(record.Attacker);
          }

          if (record.AttackerOwner != "" && VerifiedPlayers.TryAdd(record.AttackerOwner, true))
          {
            newPlayers.Add(record.AttackerOwner);
            needRemoved = needRemoved || NpcDamageManagerInstance.CheckForPlayer(record.AttackerOwner);
          }
        }

        if ((record.DefenderPetType != "" && VerifiedPets.TryAdd(record.Defender, true)) ||
          GeneratedPets.ContainsKey(record.Defender) && VerifiedPets.TryAdd(record.Defender, true))
        {
          newPets.Add(record.Defender);
          needRemoved = needRemoved || NpcDamageManagerInstance.CheckForPlayer(record.Defender);
          failure = true;
        }
        else if (record.DefenderOwner != "" && VerifiedPlayers.TryAdd(record.DefenderOwner, true))
        {
          newPlayers.Add(record.DefenderOwner);
          needRemoved = needRemoved || NpcDamageManagerInstance.CheckForPlayer(record.DefenderOwner);
          failure = true;
        }

        if (!failure && (VerifiedPlayers.ContainsKey(record.Defender) || VerifiedPets.ContainsKey(record.Defender)
          || NotNPC.ContainsKey(record.Defender) || GeneratedPets.ContainsKey(record.Defender) || CheckEye.IsMatch(record.Defender)))
        {
          failure = true;
        }

        if (failure)
        {
          record = null;
        }
      }

      return record;
    }

    private static DamageRecord ParseAllDamage(string part)
    {
      DamageRecord record = null;

      try
      {
        bool found = false;
        string action = "";
        string attacker = "";
        string attackerOwner = "";
        string attackerPetType = "";
        string defender = "";
        string defenderPetType = "";
        string defenderOwner = "";
        int afterAction = -1;
        long damage = 0;
        string type = "";

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
                        action = HitAdditionalMap[testAction];
                      }
                      else
                      {
                        action = testAction;
                      }

                      type = "DD";
                      afterAction = sizeSoFar + action.Length + 1;
                      attackerPetType = petType;
                      attackerOwner = owner;
                      attacker = player;
                    }
                    else
                    {
                      if (testAction == "has" && part.Substring(sizeSoFar + 3, 7) == " taken ")
                      {
                        type = "DoT";
                        action = "has taken";
                        afterAction = sizeSoFar + action.Length + 1;
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
                  action = HitAdditionalMap[testAction];
                }
                else
                {
                  action = testAction;
                }

                type = "DD";
                afterAction = sizeSoFar + action.Length + 1;
                attacker = player;
              }
              else
              {
                if (testAction == "has" && part.Substring(sizeSoFar + 3, 7) == " taken ")
                {
                  type = "DoT";
                  action = "has taken";
                  afterAction = sizeSoFar + action.Length + 1;
                  defender = player;
                }
              }
            }
          }

          if (type == "")
          {
            // only check if it's an NPC if it's a DoT and they're the defender
            int hasTakenIndex = part.IndexOf("has taken ", firstSpace + 1);
            if (hasTakenIndex > -1)
            {
              type = "DoT";
              defender = part.Substring(0, hasTakenIndex - 1);
              action = "has taken";
              afterAction = hasTakenIndex + 10;
            }
          }

          if (action != "" && type != "" && part.Length > afterAction)
          {
            if (type == "DD")
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
                      if (part.IndexOf(" points ", afterDmg) > -1)
                      {
                        found = true;
                      }
                    }
                  }
                }
              }
            }
            else if (type == "DoT")
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
                          type = "DoT";
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
              Attacker = attacker,
              Defender = defender,
              Type = type,
              Action = action,
              Damage = damage,
              AttackerPetType = attackerPetType,
              AttackerOwner = attackerOwner,
              DefenderPetType = defenderPetType,
              DefenderOwner = defenderOwner
            };
          }
        }
      }
      catch (Exception e)
      {
        Console.WriteLine(e.StackTrace);
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
