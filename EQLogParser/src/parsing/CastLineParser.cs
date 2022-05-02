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

    private static readonly Dictionary<string, byte> PetCheck = new Dictionary<string, byte>()
    {
      { "body is covered in slithering runes.", 1 }, { "enters an accelerated frenzy.", 1 }, { "enters a bloodrage.", 1 }, { "wishes to show its obedience.", 1 }
    };

    public static void Process(LineData lineData)
    {
      bool handled = false;

      try
      {
        List<string> sList = new List<string>(lineData.Action.Split(' '));

        if (sList.Count > 1 && !sList[0].Contains("."))
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

            // ZONE EVENT - moved here to keep it in the same thread as lands on message parsing
            if (sList[1] == "have" && sList[2] == "entered")
            {
              string zone = string.Join(" ", sList.ToArray(), 3, sList.Count - 3).TrimEnd('.');
              DataManager.Instance.AddMiscRecord(new ZoneRecord { Zone = zone }, lineData.BeginTime);
              handled = true;

              if (!zone.StartsWith("an area", StringComparison.OrdinalIgnoreCase))
              {
                DataManager.Instance.ZoneChanged();
              }
            }
            else if (sList[1] == "begin")
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

          if (!handled && !string.IsNullOrEmpty(player) && !string.IsNullOrEmpty(spellName))
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

            handled = true;
          }

          if (!handled && lineData.Action[lineData.Action.Length - 1] != ')')
          {
            if (sList[0].Length > 3 && sList[0][sList[0].Length - 1] == 's' && sList[0][sList[0].Length - 2] == '\'')
            {
              player = string.Intern(sList[0].Substring(0, sList[0].Length - 2));
              var landsOnPosessiveMessage = string.Join(" ", sList.ToArray(), 1, sList.Count - 1);
              List<SpellData> result = DataManager.Instance.GetPosessiveLandsOnOther(player, landsOnPosessiveMessage, out _);
              if (result != null)
              {
                double currentTime = lineData.BeginTime;
                var newSpell = new ReceivedSpell { Receiver = player, BeginTime = currentTime };

                if (result.Count == 1)
                {
                  newSpell.SpellData = result.First();
                  CheckForSpecial(SpecialLandsOnCodes, newSpell.SpellData.Name, newSpell.Receiver, currentTime);
                }
                else
                {
                  newSpell.Ambiguity.AddRange(result);
                }

                // valid lands on other. check for pet receiving DPS AA
                if (PetCheck.ContainsKey(landsOnPosessiveMessage))
                {
                  PlayerManager.Instance.AddVerifiedPet(player);
                }

                DataManager.Instance.AddReceivedSpell(newSpell, currentTime);
                handled = true;
              }
            }
            else if (sList.Count > 0)
            {
              string landsOnMessage = string.Join(" ", sList.ToArray(), 1, sList.Count - 1);
              int midPeriod = -1;

              // some abilities like staunch show a lands on message followed by a heal. so search based on first sentence
              if (landsOnMessage.Length >= 2)
              {
                if ((midPeriod = landsOnMessage.LastIndexOf('.', landsOnMessage.Length - 2)) > -1)
                {
                  landsOnMessage = landsOnMessage.Substring(0, midPeriod + 1);
                }
              }

              player = sList[0];
              List<SpellData> result = DataManager.Instance.GetNonPosessiveLandsOnOther(player, landsOnMessage, out _);

              if (result == null)
              {
                result = DataManager.Instance.GetLandsOnYou(player, player + " " + landsOnMessage, out _);
                if (result != null)
                {
                  player = ConfigUtil.PlayerName;
                }
              }
              else
              {
                // valid lands on other. check for pet receiving DPS AA
                if (PetCheck.ContainsKey(landsOnMessage))
                {
                  PlayerManager.Instance.AddVerifiedPet(player);
                }
              }

              if (result != null)
              {
                double currentTime = lineData.BeginTime;
                var newSpell = new ReceivedSpell() { Receiver = string.Intern(player), BeginTime = currentTime };

                if (result.Count == 1)
                {
                  newSpell.SpellData = result.First();
                  CheckForSpecial(SpecialLandsOnCodes, newSpell.SpellData.Name, newSpell.Receiver, currentTime);
                }
                else
                {
                  newSpell.Ambiguity.AddRange(result);
                }

                DataManager.Instance.AddReceivedSpell(newSpell, currentTime);
                handled = true;
              }
            }
          }
        }
      }
      catch (Exception e)
      {
        LOG.Error(e);
      }

      DebugUtil.UnregisterLine(lineData.LineNumber, handled);
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
