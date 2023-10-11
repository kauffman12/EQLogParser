using log4net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace EQLogParser
{
  static class MiscLineParser
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly List<string> Currency = new() { "Platinum", "Gold", "Silver", "Copper" };
    private static readonly Dictionary<char, uint> Rates = new() { { 'p', 1000 }, { 'g', 100 }, { 's', 10 }, { 'c', 1 } };
    private static readonly char[] LootedFromTrim = { '-', '.' };
    private static readonly Dictionary<string, byte> StruckByTypes = new()
    {
      { "afflicted", 1 }, { "angered", 1 }, { "assaulted", 1 }, { "beset", 1 }, { "bound", 1 }, { "burned", 1 }, { "consumed", 1 }, { "cursed", 1 },
      { "crushed", 1 }, { "cut", 1 }, { "drained", 1 }, { "engulfed", 1 }, { "enveloped", 1 }, { "chilled", 1 }, { "frozen", 1 }, { "hit", 1 },
      { "immolated", 1 }, { "impaled", 1 }, { "pierced", 1 }, { "pummeled", 1 }, { "rent", 1 }, { "seared", 1 }, { "shaken", 1 }, { "slashed", 1 },
      { "sliced", 1 }, { "stabbed", 1 }, { "surrounded", 1 }, { "struck", 1 }, { "stunned", 1 }, { "targeted", 1 }, { "withered", 1 }
    };

    public static bool Process(LineData lineData)
    {
      var handled = false;

      try
      {
        var split = lineData.Split;
        if (split is { Length: >= 2 })
        {
          // [Sun Mar 01 22:20:36 2020] A shaded torch has been awakened by Drogbaa.
          // [Sun Mar 01 20:35:55 2020] The master looter, Qulas, looted 32426 platinum from the corpse.
          // [Sun Mar 01 23:51:02 2020] You receive 129 platinum, 2 gold and 1 copper as your split (with a lucky bonus).
          // [Sun Feb 02 22:43:51 2020] You receive 28 platinum, 7 gold, 2 silver and 5 copper as your split.
          // [Sun Feb 02 23:31:23 2020] You receive 57 platinum as your split.
          // [Tue Jul 25 11:34:22 2023] You receive 112 platinum, 5 gold and 5 silver from the corpse
          // [Fri Feb 07 22:01:20 2020] --Kizant has looted a Lesser Engraved Velium Rune from Velden Dragonbane's corpse.--
          // [Sat Feb 08 01:20:26 2020] --Proximoe has looted a Velium Infused Spider Silk from a restless devourer's corpse.--
          // [Sat Feb 08 21:21:36 2020] --You have looted a Cold-Forged Cudgel from Queen Dracnia's corpse.--
          // [Mon Apr 27 22:32:04 2020] Restless Tijoely resisted your Stormjolt Vortex Effect!
          // [Mon Apr 27 20:51:22 2020] Kazint's Scorching Beam Rk. III spell has been reflected by a shadow reflection.
          // [Sun Mar 28 19:42:46 2021] A Draconic Lava Chain Feet Ornament was given to Aldryn.
          // [Mon Apr 05 19:42:24 2021] Hacket won the need roll on 1 item(s): Restless Velium Tainted Pelt with a roll of 996.
          // [Thu Jan 27 22:33:54 2022] **A Magic Die is rolled by Kizant. It could have been any number from 1 to 1000, but this time it turned up a 11.
          // [Thu Jan 27 22:34:03 2022] **A Magic Die is rolled by Incogitable. It could have been any number from 1 to 1000, but this time it turned up a 405.

          string looter = null;
          var awakenedIndex = -1;
          var lootedIndex = -1;
          var masterLootIndex = -1;
          var receiveIndex = -1;
          var resistedIndex = -1;
          var isIndex = -1;
          var itemsIndex = -1;

          for (var i = 0; i < split.Length && !handled; i++)
          {
            if (i == 0 && split[0].StartsWith("--", StringComparison.OrdinalIgnoreCase))
            {
              looter = split[0] == "--You" ? ConfigUtil.PlayerName : split[0].TrimStart('-');
            }
            // [Thu Jan 27 16:32:01 2022] [1 Warrior] Spasiba(Gnome)  ZONE: The Bazaar(bazaar)
            // [Thu Jan 27 16:32:01 2022] [120 Shadowblade (Rogue)] Bloodydagger(Iksar) < Realm of Insanity> ZONE: Realm of Insanity Village III, 200 Terminus Heights, Palatial Guild Hall
            // [Wed Jan 26 22:41:48 2022] [65 Overlord (Warrior)] Jenfo (Halfling)
            else if (i == 0 && split[0].StartsWith("[", StringComparison.Ordinal) && split[0].Length > 1 && split.Length > 4)
            {
              var level = split[0][1..];
              if (int.TryParse(level, out _))
              {
                string player = null;
                string className = null;
                if (split[1].EndsWith("]") && split[1].Length > 2)
                {
                  className = DataManager.Instance.GetClassFromTitle(split[1][..^1]);
                  player = split[2];
                }
                else if (split[2].EndsWith("]") && split[2].Length > 2)
                {
                  className = DataManager.Instance.GetClassFromTitle(split[1] + " " + split[2][..^1]);
                  player = split[3];
                }
                else if (split[3].EndsWith("]") && split[3].Length > 2)
                {
                  className = DataManager.Instance.GetClassFromTitle(split[1] + " " + split[2] + " " + split[3][..^1]);
                  player = split[4];
                }

                if (!string.IsNullOrEmpty(className) && !string.IsNullOrEmpty(player))
                {
                  PlayerManager.Instance.AddVerifiedPlayer(player, lineData.BeginTime);
                  PlayerManager.Instance.SetPlayerClass(player, className, "Class chosen from /who list.");
                  handled = true;
                }
              }
            }
            else
            {
              switch (split[i])
              {
                case "**A":
                  if (i == 0 && split.Length == 25 && split[1] == "Magic" && split[2] == "Die" && split[4] == "rolled" &&
                    split[12] == "number" && split[6].Length > 2 && split[16].Length > 1 && split[24].Length > 1)
                  {
                    var player = split[6][..^1];
                    var to = split[16][..^1];
                    var rolled = split[24][..^1];
                    if (int.TryParse(split[14], out var fromNumber) && int.TryParse(to, out var toNumber) && int.TryParse(rolled, out var rolledNumber))
                    {
                      DataManager.Instance.AddRandomRecord(new RandomRecord { Player = player, Rolled = rolledNumber, To = toNumber, From = fromNumber },
                        lineData.BeginTime);
                      handled = true;
                    }
                  }
                  break;
                case "awakened":
                  awakenedIndex = i;
                  break;
                case "is":
                  isIndex = i;
                  break;
                case "looted":
                  lootedIndex = i;
                  break;
                case "resisted":
                  resistedIndex = i;
                  break;
                case "item(s):":
                  if (split.Length > 9 && split[1] == "won" && split[4] == "roll")
                  {
                    itemsIndex = i;
                  }
                  break;
                case "looter,":
                  masterLootIndex = (i == 2 && split[1] == "master" && split[0] == "The") ? i + 1 : -1;
                  break;
                case "receive":
                  receiveIndex = (i == 1 && split[0] == "You") ? i : -1;
                  break;
                case "with":
                  if (itemsIndex > -1 && split.Length > i + 2 && split[i + 2] == "roll")
                  {
                    looter = split[0].Equals("you", StringComparison.OrdinalIgnoreCase) ? ConfigUtil.PlayerName : split[0];
                    var item = string.Join(" ", split, itemsIndex + 1, i - itemsIndex - 1);
                    PlayerManager.Instance.AddVerifiedPlayer(looter, lineData.BeginTime);
                    var record = new LootRecord { Item = item, Player = looter, Quantity = 0, IsCurrency = false, Npc = "Won Roll (Not Looted)" };
                    DataManager.Instance.AddLootRecord(record, lineData.BeginTime);
                    handled = true;
                  }
                  break;
                case "reflected":
                  if (split.Length > 6 && i >= 6 && i + 2 < split.Length && split[0].StartsWith(ConfigUtil.PlayerName, StringComparison.Ordinal)
                    && split[i - 1] == "been" && split[i - 2] == "has" && split[i - 3] == "spell" && split[i + 1] == "by")
                  {
                    // var spell = string.Join(" ", split, 1, i - 4);
                    var npc = string.Join(" ", split, i + 2, split.Length - i - 2).TrimEnd('.');
                    npc = TextUtils.ToUpper(npc);
                    DataManager.Instance.UpdateNpcSpellReflectStats(npc);
                    handled = true;
                  }
                  break;
                case "your":
                  if (resistedIndex > 0 && resistedIndex + 1 == i && split.Length > i + 1 && split[^1].EndsWith("!", StringComparison.Ordinal))
                  {
                    var npc = string.Join(" ", split, 0, resistedIndex);
                    npc = TextUtils.ToUpper(npc);
                    var spell = string.Join(" ", split, i + 1, split.Length - i - 1).TrimEnd('!');
                    DataManager.Instance.AddResistRecord(new ResistRecord { Defender = npc, Spell = spell }, lineData.BeginTime);
                    handled = true;
                  }
                  break;
                case "by":
                  if (awakenedIndex > -1 && awakenedIndex == (i - 1) && split.Length > 5 && split[i - 2] == "been" && split[i - 3] == "has")
                  {
                    var awakened = string.Join(" ", split, 0, i - 3);
                    awakened = TextUtils.ToUpper(awakened);
                    var breaker = string.Join(" ", split, i + 1, split.Length - i - 1).TrimEnd('.');
                    breaker = TextUtils.ToUpper(breaker);
                    DataManager.Instance.AddMiscRecord(new MezBreakRecord { Breaker = breaker, Awakened = awakened }, lineData.BeginTime);
                    handled = true;
                  }
                  else if (isIndex > 0 && StruckByTypes.ContainsKey(split[i - 1]))
                  {
                    // ignore common lines like: is struck by
                    return false;
                  }
                  break;
                case "from":
                  if (masterLootIndex > -1 && lootedIndex > masterLootIndex && split.Length > lootedIndex + 1 && split.Length > 3)
                  {
                    var name = split[3].TrimEnd(',');
                    // if master looter is empty then it was the current player who looted
                    if (string.IsNullOrEmpty(name))
                    {
                      name = ConfigUtil.PlayerName;
                    }

                    if (ParseCurrency(split, lootedIndex + 1, i, out var item, out var count))
                    {
                      PlayerManager.Instance.AddVerifiedPlayer(name, lineData.BeginTime);
                      var record = new LootRecord { Item = item, Player = name, Quantity = count, IsCurrency = true };
                      DataManager.Instance.AddLootRecord(record, lineData.BeginTime);
                      handled = true;
                    }
                  }
                  else if (!string.IsNullOrEmpty(looter) && lootedIndex == 2 && split.Length > 4)
                  {
                    // covers "a" or "an"
                    var count = split[3][0] == 'a' ? 1 : StatsUtil.ParseUInt(split[3]);
                    var item = string.Join(" ", split, 4, i - 4);
                    var npc = string.Join(" ", split, i + 1, split.Length - i - 1).TrimEnd(LootedFromTrim).Replace("'s corpse", "");
                    npc = TextUtils.ToUpper(npc);

                    if (count > 0 && count != ushort.MaxValue)
                    {
                      PlayerManager.Instance.AddVerifiedPlayer(looter, lineData.BeginTime);
                      var record = new LootRecord { Item = item, Player = looter, Quantity = count, IsCurrency = false, Npc = npc };
                      DataManager.Instance.AddLootRecord(record, lineData.BeginTime);
                      handled = true;
                    }
                  }
                  else if (receiveIndex > -1 && i > receiveIndex && string.IsNullOrEmpty(looter))
                  {
                    if (ParseCurrency(split, 2, i, out var item, out var count))
                    {
                      var record = new LootRecord { Item = item, Player = ConfigUtil.PlayerName, Quantity = count, IsCurrency = true };
                      DataManager.Instance.AddLootRecord(record, lineData.BeginTime);
                      handled = true;
                    }
                  }
                  break;
                case "given":
                  if (split[i - 1] == "was" && split.Length == (i + 3) && split[i + 1] == "to")
                  {
                    var player = split[i + 2];
                    if (player.Length > 3)
                    {
                      looter = player[..^1];
                      looter = looter.Equals("you", StringComparison.OrdinalIgnoreCase) ? ConfigUtil.PlayerName : looter;
                      PlayerManager.Instance.AddVerifiedPlayer(looter, lineData.BeginTime);
                      var item = string.Join(" ", split, 1, i - 2);
                      var record = new LootRecord { Item = item, Player = looter, Quantity = 0, IsCurrency = false, Npc = "Given (Not Looted)" };
                      DataManager.Instance.AddLootRecord(record, lineData.BeginTime);
                      handled = true;
                    }
                  }
                  break;
                case "split.":
                case "split":
                  if (receiveIndex > -1 && split[i - 1] == "your" && split[i - 2] == "as")
                  {
                    if (ParseCurrency(split, 2, i - 2, out var item, out var count))
                    {
                      var record = new LootRecord { Item = item, Player = ConfigUtil.PlayerName, Quantity = count, IsCurrency = true };
                      DataManager.Instance.AddLootRecord(record, lineData.BeginTime);
                      handled = true;
                    }
                  }
                  break;
              }
            }
          }

          // old eqemu looted items
          // [Fri Jan 14 21:05:46 2022] --Triplex has looted a Muramite Sleeve Armor.--
          if (!handled)
          {
            if (!string.IsNullOrEmpty(looter) && lootedIndex == 2 && split.Length > 4)
            {
              var item = string.Join(" ", split, 4, split.Length - 4);
              if (item.Length > 3 && item.EndsWith(".--"))
              {
                // covers "a" or "an"
                var count = split[3][0] == 'a' ? 1 : StatsUtil.ParseUInt(split[3]); item = item[..^3];
                if (count > 0 && count != ushort.MaxValue)
                {
                  PlayerManager.Instance.AddVerifiedPlayer(looter, lineData.BeginTime);
                  var record = new LootRecord { Item = item, Player = looter, Quantity = count, IsCurrency = false, Npc = "" };
                  DataManager.Instance.AddLootRecord(record, lineData.BeginTime);
                  handled = true;
                }
              }
            }
          }
        }
      }
      catch (ArgumentNullException ne)
      {
        Log.Error(ne);
      }
      catch (NullReferenceException nr)
      {
        Log.Error(nr);
      }
      catch (ArgumentOutOfRangeException aor)
      {
        Log.Error(aor);
      }
      catch (ArgumentException ae)
      {
        Log.Error(ae);
      }

      return handled;
    }

    private static bool ParseCurrency(string[] pieces, int startIndex, int toIndex, out string item, out uint count)
    {
      var parsed = true;
      item = null;
      count = 0;

      var tmp = new List<string>();
      for (var i = startIndex; i < toIndex; i += 2)
      {
        if (pieces[i] == "and")
        {
          i -= 1;
          continue;
        }

        if (StatsUtil.ParseUInt(pieces[i]) is uint value && Currency.FirstOrDefault(curr => pieces[i + 1].StartsWith(curr, StringComparison.OrdinalIgnoreCase)) is { } type)
        {
          tmp.Add(pieces[i] + " " + type);
          count += value * Rates[pieces[i + 1][0]];
        }
        else
        {
          parsed = false;
          break;
        }
      }

      if (parsed && tmp.Count > 0)
      {
        item = string.Join(", ", tmp);
      }

      return parsed;
    }
  }
}
