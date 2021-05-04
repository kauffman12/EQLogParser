using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  class CastLineParser
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private static readonly DateUtil DateUtil = new DateUtil();
    private static readonly char[] OldSpellChars = new char[] { '<', '>' };

    private static readonly Dictionary<string, string> SpecialLandsOnCodes = new Dictionary<string, string>()
    {
      { "Glyph of Destruction", "G" }, { "Glyph of Dragon", "D" }, { "Intensity of the Resolute", "7" }, { "Staunch Recovery", "6" }
    };

    private static readonly Dictionary<string, string> SpecialYouCodes = new Dictionary<string, string>()
    {
      { "Glyph of Destruction", "G" }, { "Glyph of Dragon", "D" }
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
        string[] split = lineData.Action.Split(' ');

        if (split != null && split.Length > 1 && !split[0].Contains("."))
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
          if (split[0] == "You")
          {
            player = ConfigUtil.PlayerName;
            isYou = true;

            if (split[1] == "activate")
            {
              spellName = ParseNewSpellName(split, 2);
            }

            // ZONE EVENT - moved here to keep it in the same thread as lands on message parsing
            if (split[1] == "have" && split[2] == "entered")
            {
              string zone = string.Join(" ", split, 3, split.Length - 3).TrimEnd('.');
              DataManager.Instance.AddMiscRecord(new ZoneRecord { Zone = zone }, DateUtil.ParseLogDate(lineData.Line, out _));
              handled = true;

              if (!zone.StartsWith("an area", StringComparison.OrdinalIgnoreCase))
              {
                DataManager.Instance.ZoneChanged();
              }
            }
            else if (split[1] == "begin")
            {
              if (split[2] == "casting")
              {
                spellName = ParseNewSpellName(split, 3);
                isSpell = true;
              }
              else if (split[2] == "singing")
              {
                spellName = ParseNewSpellName(split, 3);
              }
            }
          }
          else if (split[1] == "activates")
          {
            player = split[0];
            spellName = ParseNewSpellName(split, 2);
          }
          else if (split[1] == "begins")
          {
            if (split[2] == "casting")
            {
              player = split[0];
              spellName = ParseNewSpellName(split, 3);
              isSpell = true;
            }
            else if (split[2] == "singing")
            {
              player = split[0];
              spellName = ParseNewSpellName(split, 3);
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
          else if (split.Length > 4 && split[split.Length - 1] == "interrupted." && split[split.Length - 2] == "is" && split[split.Length - 3] == "spell")
          {
            isInterrupted = true;
            spellName = string.Join(" ", split, 1, split.Length - 4);

            if (split[0] == "Your")
            {
              player = ConfigUtil.PlayerName;
            }
            else if (split[0].Length > 3 && split[0][split[0].Length - 1] == 's' && split[0][split[0].Length - 2] == '\'')
            {
              player = split[0].Substring(0, split[0].Length - 2);
            }
          }

          if (!handled && !string.IsNullOrEmpty(player) && !string.IsNullOrEmpty(spellName))
          {
            double currentTime = DateUtil.ParseDate(lineData.Line.Substring(1, 24));

            if (!isInterrupted)
            {
              if (isSpell && isYou)
              {
                // For some reason Glyphs don't show up for current player
                CheckForSpecial(SpecialYouCodes, spellName, player, currentTime);
              }

              var spellData = DataManager.Instance.GetSpellByName(spellName);
              DataManager.Instance.AddSpellCast(new SpellCast() { Caster = player, Spell = string.Intern(spellName), SpellData = spellData }, currentTime);
            }
            else
            {
              DataManager.Instance.HandleSpellInterrupt(player, spellName, currentTime);
            }

            handled = true;
          }

          if (!handled && lineData.Line[lineData.Line.Length - 1] != ')')
          {
            if (split[0].Length > 3 && split[0][split[0].Length - 1] == 's' && split[0][split[0].Length - 2] == '\'')
            {
              player = string.Intern(split[0].Substring(0, split[0].Length - 2));
              var landsOnPosessiveMessage = string.Join(" ", split, 1, split.Length - 1);
              List<SpellData> result = DataManager.Instance.GetPosessiveLandsOnOther(player, landsOnPosessiveMessage, out _);
              if (result != null)
              {
                double currentTime = DateUtil.ParseDate(lineData.Line.Substring(1, 24));
                var newSpell = new ReceivedSpell() { Receiver = player };

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
            else
            {
              string landsOnMessage = string.Join(" ", split, 1, split.Length - 1);
              int midPeriod = -1;

              // some abilities like staunch show a lands on message followed by a heal. so search based on first sentence
              if ((midPeriod = landsOnMessage.LastIndexOf('.', landsOnMessage.Length - 2)) > -1)
              {
                landsOnMessage = landsOnMessage.Substring(0, midPeriod + 1);
              }

              player = split[0];
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
                double currentTime = DateUtil.ParseDate(lineData.Line.Substring(1, 24));
                var newSpell = new ReceivedSpell() { Receiver = string.Intern(player) };

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
#pragma warning disable CA1031 // Do not catch general exception types
      catch (Exception e)
#pragma warning restore CA1031 // Do not catch general exception types
      {
        if (e is ArgumentException || e is NullReferenceException || e is ArgumentOutOfRangeException || e is ArgumentException)
        {
          LOG.Error(e);
        }
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

    private static string ParseNewSpellName(string[] split, int spellIndex)
    {
      return string.Join(" ", split, spellIndex, split.Length - spellIndex).Trim('.');
    }

    private static string ParseOldSpellName(string[] split, int spellIndex)
    {
      return string.Join(" ", split, spellIndex, split.Length - spellIndex).Trim(OldSpellChars);
    }
  }
}
