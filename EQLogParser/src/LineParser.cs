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
    private static Regex CheckDirectDamage = new Regex(@"^(?:(\w+)(?:`s (pet|ward|warder))?) (?:" + string.Join("|", DataTypes.DAMAGE_LIST) + @")[es]{0,2} (.+) for (\d+) points of (?:non-melee )?damage\.", RegexOptions.Singleline | RegexOptions.Compiled);
    private static Regex CheckDoTDamage = new Regex(@"^(.+) has taken (\d+) damage from (.+) by (\w+)\.", RegexOptions.Singleline | RegexOptions.Compiled);

    private delegate DamageRecord ParseDamageFunc(string line);
    private static ParseDamageFunc[] DamageFuncs = { ParseDoTDamage, ParseDirectDamage };

    public static ConcurrentDictionary<string, bool> VerifiedPets = new ConcurrentDictionary<string, bool>();

    public static ConcurrentDictionary<string, bool> VerifiedPlayers = new ConcurrentDictionary<string, bool>(
      new List<KeyValuePair<string, bool>>
    {
      new KeyValuePair<string, bool>("himself", true), new KeyValuePair<string, bool>("you", true), new KeyValuePair<string, bool>("YOU", true)
    });

    public static ConcurrentDictionary<string, bool> HitMap = new ConcurrentDictionary<string, bool>(
      new List<KeyValuePair<string, bool>>
    {
      new KeyValuePair<string, bool>("bash", true), new KeyValuePair<string, bool>("bit", true), new KeyValuePair<string, bool>("backstab", true),
      new KeyValuePair<string, bool>("claw", true), new KeyValuePair<string, bool>("crush", true), new KeyValuePair<string, bool>("frienzies", true),
      new KeyValuePair<string, bool>("frenzies on", true), new KeyValuePair<string, bool>("frenzy", true), new KeyValuePair<string, bool>("frenzy on", true),
      new KeyValuePair<string, bool>("gore", true), new KeyValuePair<string, bool>("hit", true), new KeyValuePair<string, bool>("kick", true),
      new KeyValuePair<string, bool>("maul", true), new KeyValuePair<string, bool>("punch", true), new KeyValuePair<string, bool>("pierce", true),
      new KeyValuePair<string, bool>("rend", true), new KeyValuePair<string, bool>("shoot", true), new KeyValuePair<string, bool>("slash", true),
      new KeyValuePair<string, bool>("slam", true), new KeyValuePair<string, bool>("slice", true), new KeyValuePair<string, bool>("smash", true),
      new KeyValuePair<string, bool>("sting", true), new KeyValuePair<string, bool>("strike", true),
      new KeyValuePair<string, bool>("bashes", true), new KeyValuePair<string, bool>("bites", true), new KeyValuePair<string, bool>("backstabs", true),
      new KeyValuePair<string, bool>("claws", true), new KeyValuePair<string, bool>("crushes", true), new KeyValuePair<string, bool>("gores", true),
      new KeyValuePair<string, bool>("hits", true), new KeyValuePair<string, bool>("kicks", true), new KeyValuePair<string, bool>("mauls", true),
      new KeyValuePair<string, bool>("punches", true), new KeyValuePair<string, bool>("pierces", true), new KeyValuePair<string, bool>("rends", true),
      new KeyValuePair<string, bool>("shoots", true), new KeyValuePair<string, bool>("slashes", true), new KeyValuePair<string, bool>("slams", true),
      new KeyValuePair<string, bool>("slices", true), new KeyValuePair<string, bool>("smashes", true), new KeyValuePair<string, bool>("stings", true),
      new KeyValuePair<string, bool>("strikes", true)
    });

    private static List<string> KeepKeyWords = new List<string>
    {
      "damage",
      "has been slain",
      "shrinks.",
      " tells the guild, ",
      " Targeted (Play",
      "My leader is"
    };

    public static ProcessLine KeepForProcessingState(string line)
    {
      ProcessLine pline = new ProcessLine() { Line = line, State = -1 };

      if (line.Length > MIN_LINE_LENGTH)
      {
        pline.State = KeepKeyWords.FindIndex(word => line.Contains(word));
        if (pline.State >= 0)
        {
          pline.TimeString = pline.Line.Substring(1, 24);
          pline.ActionPart = pline.Line.Substring(27); // work with everything in lower case

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

      return pline;
    }

    public static void CheckForSlain(string line)
    {
      // Regex was really slow for this
      int index = line.IndexOf(" has been slain by");
      if (index > 0)
      {
        string sub = line.Substring(0, index);
        if (!VerifiedPlayers.ContainsKey(sub) && !VerifiedPets.ContainsKey(sub))
        {
          NpcDamageManagerInstance.Slain(sub);
        }
      }
    }

    public static bool CheckForPetLeader(string line, out string name)
    {
      bool found = false;
      name = "";

      MatchCollection matches = CheckLeader.Matches(line);
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

    public static bool CheckForPlayers(string line, out string name, out bool needRemove)
    {
      bool found = false;
      needRemove = false;
      name = "";

      string player = null;
      if (line.StartsWith("Targeted (Player): "))
      {
        player = line.Substring(19);
      }
      else
      {
        int index = line.IndexOf(' ');
        if (index > 0)
        {
          string action = line.Substring(index);
          if (action == " shrinks." || action.StartsWith(" tells the guild,"))
          {
            player = line.Substring(0, index);
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

    public static DamageRecord ParseDamage(string line, out string name)
    {
      DamageRecord record = null;
      name = "";

      foreach (ParseDamageFunc func in DamageFuncs)
      {
        record = func(line);
        if (record != null)
        {
          // replaces You and you and maybe more in the future
          string replaced;
          if (AttackerReplacement.TryGetValue(record.Attacker, out replaced))
          {
            record.Attacker = replaced;
          }

          if (record.AttackerPet != "")
          {
            VerifiedPets.TryAdd(record.AttackerPet, true);
          }

          if (record.AttackerOwner != "")
          {
            VerifiedPlayers.TryAdd(record.AttackerOwner, true);
          }

          if (record.DefenderPet != "" && VerifiedPets.TryAdd(record.DefenderPet, true) && NpcDamageManagerInstance.CheckForPlayer(record.DefenderPet))
          {
            name = record.DefenderPet;
            record = null;
          }
          else if (record.DefenderOwner != "" && VerifiedPlayers.TryAdd(record.DefenderOwner, true) && NpcDamageManagerInstance.CheckForPlayer(record.DefenderOwner))
          {
            name = record.DefenderOwner;
            record = null;
          }

          if (VerifiedPlayers.ContainsKey(record.Defender) || VerifiedPets.ContainsKey(record.Defender) || CheckEye.IsMatch(record.Defender))
          {
            record = null;
          }

          break;
        }
      }

      return record;
    }

    private static DamageRecord ParseDoTDamage(string line)
    {
      DamageRecord record = null;

      MatchCollection matches = CheckDoTDamage.Matches(line);
      if (matches.Count > 0 && matches[0].Groups.Count == 5)
      {
        int damage = 0;
        Int32.TryParse(matches[0].Groups[2].Value, out damage);
        record = new DamageRecord()
        {
          Type = "DoT",
          Attacker = matches[0].Groups[4].Value,
          Defender = matches[0].Groups[1].Value,
          Damage = damage,
          AttackerPet = "",
          AttackerOwner = "",
          DefenderPet = "",
          DefenderOwner = "",
          Action = "hit"
        };
      }

      return record;
    }

    private static DamageRecord ParseDirectDamage(string part)
    {
      DamageRecord record = null;

      try
      {
        string action = null;
        string attacker = "";
        string attackerOwner = "";
        string attackerPet = "";
        string defender = "";
        string defenderPet = "";
        string defenderOwner = "";
        int afterAction = -1;
        long damage = 0;

        // find first space and see if we have a name in the first  second
        int firstSpace = part.IndexOf(" ");
        if (firstSpace > 3)
        {
          // check if name has a possessive
          if (part.Substring(firstSpace - 2, 2) == "`s")
          {
            if (IsPossiblePlayerName(part, firstSpace - 2))
            {
              int len;
              if (IsPetOrMount(part, firstSpace + 1, out len))
              {
                attackerPet = part.Substring(firstSpace + 1, len);
                attackerOwner = part.Substring(0, firstSpace - 2);

                int sizeSoFar = firstSpace + 1 + len + 1;
                if (part.Length > sizeSoFar)
                {
                  attacker = part.Substring(0, sizeSoFar - 1);
                  int secondSpace = part.IndexOf(" ", sizeSoFar);
                  if (secondSpace > -1)
                  {
                    string testAction = part.Substring(firstSpace + 1 + len + 1, secondSpace - sizeSoFar);
                    if (HitMap.ContainsKey(testAction))
                    {
                      action = testAction;
                      afterAction = firstSpace + 1 + len + 1 + action.Length + 1;
                    }
                  }
                }
              }
            }
          }
          else
          {
            if (IsPossiblePlayerName(part, firstSpace))
            {
              int secondSpace = part.IndexOf(" ", firstSpace + 1);
              if (secondSpace > -1)
              {
                string testAction = part.Substring(firstSpace + 1, secondSpace - firstSpace - 1);
                if (HitMap.ContainsKey(testAction))
                {
                  action = testAction;
                  attacker = part.Substring(0, firstSpace);
                  afterAction = firstSpace + 1 + action.Length + 1;
                }
              }
            }
          }

          if (action != null && part.Length > afterAction)
          {
            int forIndex = part.IndexOf("for", afterAction);
            if (forIndex > -1)
            {
              defender = part.Substring(afterAction, forIndex - afterAction - 1);
              int posessiveIndex = defender.IndexOf("`s ");
              if (posessiveIndex > -1)
              {
                int len;
                if (IsPetOrMount(defender, posessiveIndex + 3, out len))
                {
                  if (IsPossiblePlayerName(defender, posessiveIndex))
                  {
                    defenderOwner = defender.Substring(0, posessiveIndex);
                    defenderPet = defender;
                  }
                }
              }
              int dmgStart = afterAction + defender.Length + 5;
              if (part.Length > dmgStart)
              {
                int afterDmg = part.IndexOf(" ", dmgStart);
                if (afterDmg > -1)
                {
                  if (long.TryParse(part.Substring(dmgStart, afterDmg - dmgStart), out damage))
                  {
                    if (part.IndexOf(" points", afterDmg) > -1)
                    {
                      record = new DamageRecord()
                      {
                        Attacker = attacker,
                        Defender = defender,
                        Type = "DD",
                        Action = action,
                        Damage = damage,
                        AttackerPet = attackerPet,
                        AttackerOwner = attackerOwner,
                        DefenderPet = defenderPet,
                        DefenderOwner = defenderOwner
                      };
                    }
                  }
                }
              }
            }
          }
        }
      }
      catch (Exception e)
      {
        Console.WriteLine(e.Message);
      }
      return record;
    }

    private static bool IsPetOrMount(string part, int start, out int len)
    {
      bool found = false;
      len = -1;

      int end = 2;
      if (part.Length >= (start + ++end) && part.Substring(start, 3) == "pet" ||
        part.Length >= (start + ++end) && part.Substring(start, 4) == "ward" ||
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
      bool found = true;
      for (int i = 0; i < stop; i++)
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
