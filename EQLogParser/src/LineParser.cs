using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EQLogParser
{
  class LineParser
  {
    public static NpcDamageManager NpcDamageManagerInstance;
    public static ConcurrentDictionary<string, string> AttackerReplacement = new ConcurrentDictionary<string, string>();
    public static ConcurrentDictionary<string, string> PetToPlayers = new ConcurrentDictionary<string, string>();

    private static Regex CheckEye = new Regex(@"^Eye of (\w+)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static Regex CheckLeader = new Regex(@"^(\w+) says, 'My leader is (\w+)\.'", RegexOptions.Singleline | RegexOptions.Compiled);
    private static Regex CheckPetOrMount = new Regex(@"^(\w+)`s (pet|Mount|ward|warder)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static Regex CheckDirectDamage = new Regex(@"^(?:(\w+)(?:`s (pet|ward|warder))?) (?:" + string.Join("|", DataTypes.DAMAGE_LIST) + @")[es]{0,2} (.+) for (\d+) points of (?:non-melee )?damage\.", RegexOptions.Singleline | RegexOptions.Compiled);
    private static Regex CheckDoTDamage = new Regex(@"^(.+) has taken (\d+) damage from (.+) by (\w+)\.", RegexOptions.Singleline | RegexOptions.Compiled);

    private delegate DamageRecord ParseDamageFunc(string line);
    private static ParseDamageFunc[] DamageFuncs = { ParseDoTDamage, ParseDirectDamage };
    public static ConcurrentDictionary<string, bool> VerifiedPlayers = new ConcurrentDictionary<string, bool>(
      new List<KeyValuePair<string, bool>>
    {
      new KeyValuePair<string, bool>("himself", true),
      new KeyValuePair<string, bool>("you", true),
      new KeyValuePair<string, bool>("YOU", true)
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

    public static int KeepForProcessingState(string line)
    {
      return KeepKeyWords.FindIndex(word => line.Contains(word));
    }

    public static void CheckForSlain(string line)
    {
      // Regex was really slow for this
      int index = line.IndexOf(" has been slain by");
      if (index > 0)
      {
        string sub = line.Substring(0, index);
        if (!VerifiedPlayers.ContainsKey(sub))
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
        string player = matches[0].Groups[2].Value;

        VerifiedPlayers.TryAdd(pet, true);
        if (PetToPlayers.TryAdd(pet, player))
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

      if (player != null)
      {
        if (!VerifiedPlayers.ContainsKey(player))
        {
          if (VerifiedPlayers.TryAdd(player, true) && NpcDamageManagerInstance.CheckForPlayer(player))
          {
            needRemove = true;
          }

          name = player;
          found = true;
        }
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
          // replace player pets or You with the player name
          if (AttackerReplacement.ContainsKey(record.Attacker))
          {
            record.Attacker = AttackerReplacement[record.Attacker];
          }

          if (VerifiedPlayers.ContainsKey(record.Defender) || CheckEye.IsMatch(record.Defender))
          {
            record = null;
          }
          else
          {
            MatchCollection matches = CheckPetOrMount.Matches(record.Defender);
            if (matches.Count > 0 && matches[0].Groups.Count == 3)
            {
              string player = matches[0].Groups[1].Value;
              if (VerifiedPlayers.TryAdd(player, true) && NpcDamageManagerInstance.CheckForPlayer(player))
              {
                name = player;
              }

              record = null;
            }
          }

          break;
        }
      }

      return record;
    }

    private static DamageRecord ParseDirectDamage(string line)
    {
      DamageRecord record = null;

      MatchCollection matches = CheckDirectDamage.Matches(line);
      if (matches.Count > 0 && matches[0].Groups.Count == 5)
      {
        int damage = 0;
        Int32.TryParse(matches[0].Groups[4].Value, out damage);
        record = new DamageRecord() { Type = "DD", Attacker = matches[0].Groups[1].Value, Defender = matches[0].Groups[3].Value, Damage = damage, IsPet = matches[0].Groups[2].Value != null };
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
        record = new DamageRecord() { Type = "DoT", Attacker = matches[0].Groups[4].Value, Defender = matches[0].Groups[1].Value, Damage = damage };
      }

      return record;
    }
  }
}
