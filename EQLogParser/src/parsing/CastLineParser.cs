using log4net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace EQLogParser
{
  static class CastLineParser
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly char[] OldSpellChars = { '<', '>' };

    private static readonly Dictionary<string, string> SpecialCastCodes = new()
    {
      { "Glyph of Ultimate Power", "G" }, { "Glyph of Destruction", "G" }, { "Glyph of Dragon", "D" },
      { "Intensity of the Resolute", "7" }, { "Staunch Recovery", "6" }, { "Glyph of Arcane Secrets", "S" }
    };

    private static readonly Dictionary<string, bool> PetSpells = new()
    {
      { "Fortify Companion", true }, { "Zeal of the Elements", true }, { "Frenzied Burnout", true }, { "Frenzy of the Dead", true }
    };

    public static bool Process(LineData lineData)
    {
      try
      {
        var split = lineData.Split;
        if (split.Length > 1 && !split[0].Contains(".") && !split.Last().EndsWith(")") && !CheckLandsOnMessages(split, lineData.BeginTime))
        {
          string player = null;
          string spellName = null;
          var isSpell = false;
          var isInterrupted = false;
          var isYou = false;

          // [Sat Mar 14 19:57:48 2020] You activate Venon's Vindication.
          // [Mon Mar 02 19:46:09 2020] You begin casting Shield of Destiny Rk. II.
          // [Sat Mar 14 19:45:40 2020] You begin singing Agilmente's Aria of Eagles.
          // [Tue Dec 25 11:38:42 2018] You begin casting Focus of Arcanum VI.
          // [Sun Dec 02 16:33:37 2018] You begin singing Vainglorious Shout VI.
          // [Mon Mar 02 19:44:27 2020] Stabborz activates Conditioned Reflexes Rk. II.
          // [Mon Mar 02 19:47:43 2020] Sancus begins casting Burnout XIV Rk. II.
          // [Mon Mar 02 19:33:49 2020] Iggokk begins singing Shauri's Sonorous Clouding III.
          // [Tue Dec 25 09:58:20 2018] Sylfvia begins to cast a spell. <Syllable of Mending Rk. II>
          // [Tue Dec 25 14:19:57 2018] Sonozen begins to sing a song. <Lyre Leap>
          // [Thu Apr 18 01:38:10 2019] Incogitable's Dizzying Wheel Rk. II spell is interrupted.
          // [Thu Apr 18 01:38:00 2019] Your Stormjolt Vortex Rk. III spell is interrupted.
          // [Sun Mar 01 22:34:58 2020] You have entered The Eastern Wastes.
          if (split[0] == "You")
          {
            isYou = true;
            player = ConfigUtil.PlayerName;
            if (split[1] == "activate" && split.Length > 2)
            {
              spellName = TextUtils.ParseSpellOrNpc(split.ToArray(), 2);
            }
            else if (split[1] == "begin" && split.Length > 3)
            {
              if (split[2] == "casting")
              {
                spellName = TextUtils.ParseSpellOrNpc(split.ToArray(), 3);
                isSpell = true;
              }
              else if (split[2] == "singing")
              {
                spellName = TextUtils.ParseSpellOrNpc(split.ToArray(), 3);
              }
            }
          }
          else if (split[1] == "activates")
          {
            player = split[0];
            spellName = TextUtils.ParseSpellOrNpc(split.ToArray(), 2);
          }
          else if (split.Length > 3 && Array.FindIndex(split, 1, split.Length - 1, s => s == "begins") is var bIndex and > -1 && (bIndex + 2) < split.Length)
          {
            if (split[bIndex + 1] == "casting")
            {
              player = string.Join(" ", split.ToArray(), 0, bIndex);
              spellName = TextUtils.ParseSpellOrNpc(split.ToArray(), bIndex + 2);
              isSpell = true;
            }
            else if (split[bIndex + 1] == "singing")
            {
              player = string.Join(" ", split.ToArray(), 0, bIndex);
              spellName = TextUtils.ParseSpellOrNpc(split.ToArray(), bIndex + 2);
            }
            else if (split.Length > 5 && split[2] == "to" && split[4] == "a")
            {
              if (split[3] == "cast" && split[5] == "spell.")
              {
                player = split[0];
                spellName = ParseOldSpellName(split, 6);
                isSpell = true;
              }
              else if (split[3] == "sing" && split[5] == "song.")
              {
                player = split[0];
                spellName = ParseOldSpellName(split, 6);
              }
            }
          }
          else if (split.Length > 4 && split[^1] == "interrupted." && split[^2] == "is" && split[^3] == "spell")
          {
            isInterrupted = true;
            spellName = string.Join(" ", split.ToArray(), 1, split.Length - 4);

            if (split[0] == "Your")
            {
              player = ConfigUtil.PlayerName;
            }
            else if (split[0].Length > 3 && split[0][^1] == 's' && split[0][^2] == '\'')
            {
              player = split[0][..^2];
            }
          }

          if (!string.IsNullOrEmpty(player) && !string.IsNullOrEmpty(spellName))
          {
            var currentTime = lineData.BeginTime;

            if (!isInterrupted)
            {
              string specialKey = null;

              if (isSpell)
              {
                // For some reason Glyphs don't show up for current player so this special case should limit the checks
                // and allow glyph to work
                if (CheckForSpecial(SpecialCastCodes, spellName, player, currentTime) is { } found && isYou)
                {
                  specialKey = found;
                }
              }

              var spellData = DataManager.Instance.GetSpellByName(spellName);
              DataManager.Instance.AddSpellCast(new SpellCast
              {
                Caster = player,
                Spell = string.Intern(spellName),
                SpellData = spellData,
                BeginTime = currentTime
              },
              currentTime, specialKey);
            }
            else
            {
              DataManager.Instance.HandleSpellInterrupt(player, spellName, currentTime);
            }

            return true;
          }
        }
      }
      catch (Exception e)
      {
        Log.Error(e);
      }

      return false;
    }

    private static bool CheckLandsOnMessages(string[] split, double beginTime)
    {
      // LandsOnYou messages also require DataIndex of zero
      var player = ConfigUtil.PlayerName;

      // old logs sometimes had received messages on the same line as a heal
      // [Sun Aug 04 23:39:56 2019] You are generously healed. You healed Kizant for 35830 (500745) hit points by Staunch Recovery.
      for (var i = 0; i < split.Length; i++)
      {
        if (split[i].EndsWith("."))
        {
          // if its a spell
          var lastIndex = split.Length - 1;
          if (lastIndex != i && split[i].Equals("Rk."))
          {
            return false;
          }

          if (i < lastIndex)
          {
            split = split.Take(i + 1).ToArray();
          }
        }
      }

      var searchResult = DataManager.Instance.GetLandsOnYou(split);
      if (searchResult.SpellData.Count == 0 || searchResult.DataIndex != 0)
      {
        // WearOff messages can only apply to use so DataIndex has to also be zero meaning that every word was matched
        searchResult = DataManager.Instance.GetWearOff(split);
        if (searchResult.SpellData.Count > 0 && searchResult.DataIndex == 0)
        {
          if (!string.IsNullOrEmpty(player))
          {
            var newSpell = new ReceivedSpell { Receiver = player, BeginTime = beginTime, IsWearOff = true };
            if (searchResult.SpellData.Count == 1)
            {
              newSpell.SpellData = searchResult.SpellData.First();
            }
            else
            {
              newSpell.Ambiguity.AddRange(searchResult.SpellData);
            }

            DataManager.Instance.AddReceivedSpell(newSpell, beginTime);
          }
          return true;
        }

        searchResult = DataManager.Instance.GetLandsOnOther(split, out player);
        if (searchResult.SpellData.Count == 1 && !string.IsNullOrEmpty(player))
        {
          if (searchResult.SpellData[0].Target == (int)SpellTarget.PET && !PlayerManager.Instance.IsVerifiedPet(player) &&
          PlayerManager.IsPossiblePlayerName(player) && !PlayerManager.Instance.IsVerifiedPlayer(player))
          {
            foreach (var spell in PetSpells.Keys)
            {
              if (searchResult.SpellData[0].Name.StartsWith(spell))
              {
                PlayerManager.Instance.AddVerifiedPet(player);
              }
            }
          }
          else if (searchResult.SpellData[0].Target == (int)SpellTarget.PET2 && !PlayerManager.Instance.IsVerifiedPet(player) &&
            PlayerManager.IsPossiblePlayerName(player) && !PlayerManager.Instance.IsVerifiedPlayer(player))
          {
            PlayerManager.Instance.AddVerifiedPet(player);
          }
        }
      }

      if (searchResult.SpellData.Count > 0 && !string.IsNullOrEmpty(player))
      {
        var newSpell = new ReceivedSpell { Receiver = player, BeginTime = beginTime };
        if (searchResult.SpellData.Count == 1)
        {
          newSpell.SpellData = searchResult.SpellData.First();
        }
        else
        {
          newSpell.Ambiguity.AddRange(searchResult.SpellData);
        }

        DataManager.Instance.AddReceivedSpell(newSpell, beginTime);
        return true;
      }

      // ZONE EVENT - moved here to keep it in the same thread as lands on message parsing
      if (split[1] == "have" && split[2] == "entered")
      {
        var zone = string.Join(" ", split.ToArray(), 3, split.Length - 3).TrimEnd('.');
        DataManager.Instance.AddMiscRecord(new ZoneRecord { Zone = zone }, beginTime);
        if (!zone.StartsWith("an area", StringComparison.OrdinalIgnoreCase))
        {
          DataManager.Instance.ZoneChanged();
          return true;
        }
      }

      return false;
    }

    private static string CheckForSpecial(Dictionary<string, string> codes, string spellName, string player, double currentTime)
    {
      string found = null;
      if (codes.Keys.FirstOrDefault(special => !string.IsNullOrEmpty(spellName) && spellName.Contains(special)) is { } key && !string.IsNullOrEmpty(key))
      {
        DataManager.Instance.AddSpecial(new SpecialSpell { Code = codes[key], Player = player, BeginTime = currentTime });
        found = key;
      }
      return found;
    }

    private static string ParseOldSpellName(string[] split, int spellIndex)
    {
      return string.Join(" ", split.ToArray(), spellIndex, split.Length - spellIndex).Trim(OldSpellChars);
    }
  }
}
