using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  class CastLineParser
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly char[] OldSpellChars = new char[] { '<', '>' };

    private static readonly Dictionary<string, string> SpecialLandsOnCodes = new Dictionary<string, string>()
    {
      { "Glyph of Ultimate Power", "G" }, { "Glyph of Destruction", "G" }, { "Glyph of Dragon", "D" }, { "Intensity of the Resolute", "7" }, { "Staunch Recovery", "6" }
    };

    private static readonly Dictionary<string, string> SpecialYouCodes = new Dictionary<string, string>()
    {
      { "Glyph of Ultimate Power", "G" }, { "Glyph of Destruction", "G" }, { "Glyph of Dragon", "D" }
    };

    public static void Process(LineData lineData)
    {
      try
      {
        List<string> sList = new List<string>(lineData.Action.Split(' '));

        if (sList.Count > 1 && !sList[0].Contains(".") && !sList.Last().EndsWith(")") && !CheckLandsOnMessages(sList, lineData.BeginTime))
        {
          string player = null;
          string spellName = null;
          bool isYou = false;
          bool isSpell = false;
          bool isInterrupted = false;

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
          if (sList[0] == "You")
          {
            player = ConfigUtil.PlayerName;
            isYou = true;

            if (sList[1] == "activate")
            {
              spellName = ParseNewSpellName(sList, 2);
            }

            if (sList[1] == "begin")
            {
              if (sList[2] == "casting")
              {
                spellName = ParseNewSpellName(sList, 3);
                isSpell = true;
              }
              else if (sList[2] == "singing")
              {
                spellName = ParseNewSpellName(sList, 3);
              }
            }
          }
          else if (sList[1] == "activates")
          {
            player = sList[0];
            spellName = ParseNewSpellName(sList, 2);
          }
          else if (sList.FindIndex(1, sList.Count - 1, s => s == "begins") is int bIndex && bIndex > -1)
          {
            if (sList[bIndex + 1] == "casting")
            {
              player = string.Join(" ", sList.ToArray(), 0, bIndex);
              spellName = ParseNewSpellName(sList, bIndex + 2);
              isSpell = true;
            }
            else if (sList[bIndex + 1] == "singing")
            {
              player = string.Join(" ", sList.ToArray(), 0, bIndex);
              spellName = ParseNewSpellName(sList, bIndex + 2);
            }
            else if (sList.Count > 5 && sList[2] == "to" && sList[4] == "a")
            {
              if (sList[3] == "cast" && sList[5] == "spell.")
              {
                player = sList[0];
                spellName = ParseOldSpellName(sList, 6);
                isSpell = true;
              }
              else if (sList[3] == "sing" && sList[5] == "song.")
              {
                player = sList[0];
                spellName = ParseOldSpellName(sList, 6);
              }
            }
          }
          else if (sList.Count > 4 && sList[sList.Count - 1] == "interrupted." && sList[sList.Count - 2] == "is" && sList[sList.Count - 3] == "spell")
          {
            isInterrupted = true;
            spellName = string.Join(" ", sList.ToArray(), 1, sList.Count - 4);

            if (sList[0] == "Your")
            {
              player = ConfigUtil.PlayerName;
            }
            else if (sList[0].Length > 3 && sList[0][sList[0].Length - 1] == 's' && sList[0][sList[0].Length - 2] == '\'')
            {
              player = sList[0].Substring(0, sList[0].Length - 2);
            }
          }

          if (!string.IsNullOrEmpty(player) && !string.IsNullOrEmpty(spellName))
          {
            double currentTime = lineData.BeginTime;

            if (!isInterrupted)
            {
              if (isSpell && isYou)
              {
                // For some reason Glyphs don't show up for current player
                CheckForSpecial(SpecialYouCodes, spellName, player, currentTime);
              }

              var spellData = DataManager.Instance.GetSpellByName(spellName);
              DataManager.Instance.AddSpellCast(new SpellCast { Caster = player, Spell = string.Intern(spellName), SpellData = spellData, BeginTime = currentTime }, currentTime);
            }
            else
            {
              DataManager.Instance.HandleSpellInterrupt(player, spellName, currentTime);
            }
          }
        }
      }
      catch (Exception e)
      {
        LOG.Error(e);
      }
    }

    private static bool CheckLandsOnMessages(List<string> sList, double beginTime)
    {
      // LandsOnYou messages also require DataIndex of zero
      string player = ConfigUtil.PlayerName;

      // old logs sometimes had received messages on the same line as a heal
      // [Sun Aug 04 23:39:56 2019] You are generously healed. You healed Kizant for 35830 (500745) hit points by Staunch Recovery.
      for (int i = 0; i < sList.Count; i++)
      {
        if (sList[i].EndsWith("."))
        {
          // if its a spell
          int lastIndex = (sList.Count - 1);
          if (lastIndex != i && sList[i].Equals("Rk."))
          {
            return false;
          }
          else if (i < lastIndex)
          {
            sList = sList.Take(i + 1).ToList();
          }
        }
      }

      var searchResult = DataManager.Instance.GetLandsOnYou(sList);
      if (searchResult.SpellData.Count == 0 || searchResult.DataIndex != 0)
      {
        // WearOff messages can only apply to use so DataIndex has to also be zero meaing that every word was matched
        searchResult = DataManager.Instance.GetWearOff(sList);
        if (searchResult.SpellData.Count > 0 && searchResult.DataIndex == 0)
        {
          return true;
        }

        searchResult = DataManager.Instance.GetLandsOnOther(sList, out player);
        if (searchResult.SpellData.Count == 1 && !string.IsNullOrEmpty(player) && searchResult.SpellData[0].Target == (int)SpellTarget.PET
          && PlayerManager.IsPossiblePlayerName(player))
        {
          // let the add verified API be used to force change to a pet so make this check here
          if (!PlayerManager.Instance.IsVerifiedPlayer(player))
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
          CheckForSpecial(SpecialLandsOnCodes, newSpell.SpellData.Name, newSpell.Receiver, beginTime);
        }
        else
        {
          newSpell.Ambiguity.AddRange(searchResult.SpellData);
        }

        DataManager.Instance.AddReceivedSpell(newSpell, beginTime);
        return true;
      }

      // ZONE EVENT - moved here to keep it in the same thread as lands on message parsing
      if (sList[1] == "have" && sList[2] == "entered")
      {
        string zone = string.Join(" ", sList.ToArray(), 3, sList.Count - 3).TrimEnd('.');
        DataManager.Instance.AddMiscRecord(new ZoneRecord { Zone = zone }, beginTime);
        if (!zone.StartsWith("an area", StringComparison.OrdinalIgnoreCase))
        {
          DataManager.Instance.ZoneChanged();
          return true;
        }
      }

      return false;
    }

    private static void CheckForSpecial(Dictionary<string, string> codes, string spellName, string player, double currentTime)
    {
      if (codes.Keys.FirstOrDefault(special => !string.IsNullOrEmpty(spellName) && spellName.Contains(special)) is string key && !string.IsNullOrEmpty(key))
      {
        DataManager.Instance.AddSpecial(new SpecialSpell() { Code = codes[key], Player = player, BeginTime = currentTime });
      }
    }

    private static string ParseNewSpellName(List<string> split, int spellIndex)
    {
      return string.Join(" ", split.ToArray(), spellIndex, split.Count - spellIndex).Trim('.');
    }

    private static string ParseOldSpellName(List<string> split, int spellIndex)
    {
      return string.Join(" ", split.ToArray(), spellIndex, split.Count - spellIndex).Trim(OldSpellChars);
    }
  }
}
