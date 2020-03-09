using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser
{
  class CastLineParser
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly DateUtil DateUtil = new DateUtil();

    private static readonly Dictionary<string, byte> IgnoreMap = new Dictionary<string, byte>()
    {
      { "Players", 1 }, { "There", 1}, { "The", 1 }, { "Targeted", 1 }, { "Right", 1 }, { "Stand" , 1}
    };

    private static readonly Dictionary<string, string> SpecialLandsOnCodes = new Dictionary<string, string>()
    {
      { "Glyph of Destruction", "G" }, { "Glyph of Dragon", "D" }, { "Intensity of the Resolute", "7" }, { "Staunch Recovery", "6" }
    };

    private static readonly Dictionary<string, string> SpecialYouCodes = new Dictionary<string, string>()
    {
      { "Glyph of Destruction", "G" }, { "Glyph of Dragon", "D" }
    };

    private static readonly Dictionary<string, string> SpecialOtherCodes = new Dictionary<string, string>()
    {
      { "Staunch Recovery", "6" }
    };

    public static void Process(string source, string line)
    {
      bool handled = false;

      try
      {
        int index = -1;
        bool isSpell = line.Length > 44 && (index = line.IndexOf(" begin", LineParsing.ACTIONINDEX + 3, StringComparison.Ordinal)) > -1;
        bool isActivate = !isSpell && line.Length > 44 && (index = line.IndexOf(" activate", LineParsing.ACTIONINDEX + 3, StringComparison.Ordinal)) > -1;
        bool isYou = false;

        if (isSpell || isActivate)
        {
          SpellCast cast = null;
          ProcessLine pline = null;
          int firstSpace = line.IndexOf(" ", LineParsing.ACTIONINDEX, StringComparison.Ordinal);
          if (firstSpace > -1 && firstSpace == index)
          {
            if (firstSpace == (LineParsing.ACTIONINDEX + 3) && line.Substring(LineParsing.ACTIONINDEX, 3) == "You")
            {
              var test = isActivate ? line[index + 9] == ' ' ? "activate" : "" : line.Substring(index + 7, 4);
              if (test == "cast" || test == "sing" || test == "activate")
              {
                isYou = true;
                pline = new ProcessLine() { Line = line, ActionPart = line.Substring(LineParsing.ACTIONINDEX) };
                pline.OptionalData = "you" + test;
                pline.OptionalIndex = 3;
                pline.TimeString = pline.Line.Substring(1, 24);
                pline.CurrentTime = DateUtil.ParseDate(pline.TimeString);
                cast = HandleSpellCast(pline, line.Substring(LineParsing.ACTIONINDEX, index - LineParsing.ACTIONINDEX));
              }
            }
            else
            {
              // [Sun Mar 31 19:40:12 2019] Jiren begins to cast a spell. <Blood Pact Strike XXV>
              // [Thu Apr 18 01:46:06 2019] Incogitable begins casting Dizzying Wheel Rk. II.

              int spellIndex = -1;
              var test = isActivate ? line[index + 9] == 's' ? "activates" : "" :  line.Substring(index + 8, 7);
              if (test == "casting" || test == "singing")
              {
                spellIndex = firstSpace - LineParsing.ACTIONINDEX + 16;
              }
              else if (test == "activates")
              {
                spellIndex = firstSpace - LineParsing.ACTIONINDEX + 11;
              }
              else if (line.Length >= index + 22)
              {
                test = line.Substring(index + 11, 11);
                if (test == "cast a spel")
                {
                  test = "cast";
                  spellIndex = firstSpace - LineParsing.ACTIONINDEX + 26;
                }
                else if (test == "sing a song")
                {
                  test = "sing";
                  spellIndex = firstSpace - LineParsing.ACTIONINDEX + 25;
                }
              }

              if (spellIndex > -1)
              {
                pline = new ProcessLine() { Line = line, ActionPart = line.Substring(LineParsing.ACTIONINDEX) };
                pline.OptionalData = test;
                pline.OptionalIndex = spellIndex;
                pline.TimeString = pline.Line.Substring(1, 24);
                pline.CurrentTime = DateUtil.ParseDate(pline.TimeString);
                cast = HandleSpellCast(pline, line.Substring(LineParsing.ACTIONINDEX, index - LineParsing.ACTIONINDEX));
              }
            }

            if (cast != null)
            {
              if (isSpell && isYou)
              {
                // For some reason Glyphs don't show up for current player
                CheckForSpecial(SpecialYouCodes, cast.Spell, cast.Caster, pline.CurrentTime);
              }
              else if (isSpell && !isYou)
              {
                // Some spells only show up as casting
                CheckForSpecial(SpecialOtherCodes, cast.Spell, cast.Caster, pline.CurrentTime);
              }

              DataManager.Instance.AddSpellCast(cast, pline.CurrentTime);
              handled = true;
            }
          }
        }

        if (!handled && line.EndsWith(" spell is interrupted.", StringComparison.Ordinal))
        {
          //[Thu Apr 18 01:38:10 2019] Incogitable's Dizzying Wheel Rk. II spell is interrupted.
          //[Thu Apr 18 01:38:00 2019] Your Stormjolt Vortex Rk. III spell is interrupted.
          ProcessLine pline = new ProcessLine() { Line = line, ActionPart = line.Substring(LineParsing.ACTIONINDEX), OptionalIndex = line.Length - 22 };
          pline.TimeString = pline.Line.Substring(1, 24);
          pline.CurrentTime = DateUtil.ParseDate(pline.TimeString);

          string player = null;
          string spell = null;
          int end = line.Length - 22;
          int len;

          if (line.IndexOf("Your", LineParsing.ACTIONINDEX, 4, StringComparison.Ordinal) > -1)
          {
            player = ConfigUtil.PlayerName;
            len = end - LineParsing.ACTIONINDEX - 5;
            if (len > 0)
            {
              spell = line.Substring(LineParsing.ACTIONINDEX + 5, len);
            }
          }
          else
          {
            int possessive = line.IndexOf("'s ", LineParsing.ACTIONINDEX, StringComparison.Ordinal);
            if (possessive > -1)
            {
              player = line.Substring(LineParsing.ACTIONINDEX, possessive - LineParsing.ACTIONINDEX);

              len = end - possessive - 3;
              if (len > 0)
              {
                spell = line.Substring(possessive + 3, len);
              }
            }
          }

          if (!string.IsNullOrEmpty(player) && !string.IsNullOrEmpty(spell))
          {
            string timeString = line.Substring(1, 24);
            double currentTime = DateUtil.ParseDate(timeString);
            DataManager.Instance.HandleSpellInterrupt(player, spell, currentTime);
            handled = true;
          }
        }
        else if (!handled) // lands on messages
        {
          int firstSpace = line.IndexOf(" ", LineParsing.ACTIONINDEX, StringComparison.Ordinal);
          if (firstSpace > -1 && line[firstSpace - 2] == '\'' && line[firstSpace - 1] == 's')
          {
            ProcessLine pline = new ProcessLine() { Line = line, ActionPart = line.Substring(LineParsing.ACTIONINDEX) };
            pline.OptionalIndex = firstSpace + 1 - LineParsing.ACTIONINDEX;
            pline.TimeString = pline.Line.Substring(1, 24);
            pline.CurrentTime = DateUtil.ParseDate(pline.TimeString);
            HandlePosessiveLandsOnOther(pline);
            handled = true;
          }
          else if (firstSpace > -1)
          {
            string player = line.Substring(LineParsing.ACTIONINDEX, firstSpace - LineParsing.ACTIONINDEX);
            if (!IgnoreMap.ContainsKey(player))
            {
              if (line.Length > firstSpace + 4)
              {
                ProcessLine pline = new ProcessLine() { Line = line, ActionPart = line.Substring(LineParsing.ACTIONINDEX) };
                pline.OptionalIndex = firstSpace + 1 - LineParsing.ACTIONINDEX;
                pline.OptionalData = player;
                pline.TimeString = pline.Line.Substring(1, 24);
                pline.CurrentTime = DateUtil.ParseDate(pline.TimeString);
                HandleOtherLandsOnCases(pline);
                handled = true;
              }
            }
          }
        }
      }
      catch (ArgumentNullException ne)
      {
        LOG.Error(ne);
      }
      catch (NullReferenceException nr)
      {
        LOG.Error(nr);
      }
      catch (ArgumentOutOfRangeException aor)
      {
        LOG.Error(aor);
      }
      catch (ArgumentException ae)
      {
        LOG.Error(ae);
      }

      if (!handled)
      {
        DataManager.Instance.AddUnhandledLine(source, line);
      }
    }

    private static void HandleOtherLandsOnCases(ProcessLine pline)
    {
      string player = pline.OptionalData;

      string landsOnMessage = pline.ActionPart;
      // some abilities like staunch show a lands on message followed by a heal. so search based on first sentence
      if (!string.IsNullOrEmpty(landsOnMessage) && landsOnMessage.IndexOf('.', pline.OptionalIndex) is int period && period > -1)
      {
        landsOnMessage = landsOnMessage.Substring(0, period + 1);
      }

      string matchOn = landsOnMessage.Substring(pline.OptionalIndex);

      SpellData result = DataManager.Instance.GetNonPosessiveLandsOnOther(matchOn, out _);
      if (result == null)
      {
        matchOn = landsOnMessage;

        result = DataManager.Instance.GetLandsOnYou(matchOn, out _);
        if (result != null)
        {
          player = ConfigUtil.PlayerName;
        }
      }

      if (result != null)
      {
        var newSpell = new ReceivedSpell() { Receiver = string.Intern(player), SpellData = result };
        DataManager.Instance.AddReceivedSpell(newSpell, pline.CurrentTime);
        CheckForSpecial(SpecialLandsOnCodes, result.Name, newSpell.Receiver, pline.CurrentTime);
      }
    }

    private static void HandlePosessiveLandsOnOther(ProcessLine pline)
    {
      string matchOn = pline.ActionPart.Substring(pline.OptionalIndex);
      SpellData result = DataManager.Instance.GetPosessiveLandsOnOther(matchOn, out _);
      if (result != null)
      {
        var newSpell = new ReceivedSpell() { Receiver = string.Intern(pline.ActionPart.Substring(0, pline.OptionalIndex - 3)), SpellData = result };
        DataManager.Instance.AddReceivedSpell(newSpell, pline.CurrentTime);
        CheckForSpecial(SpecialLandsOnCodes, result.Name, newSpell.Receiver, pline.CurrentTime);
      }
    }

    private static SpellCast HandleSpellCast(ProcessLine pline, string caster)
    {
      SpellCast cast = null;

      switch (pline.OptionalData)
      {
        case "activates":
        case "casting":
        case "singing":
        case "cast":
        case "sing":
          if (pline.ActionPart.Length > pline.OptionalIndex)
          {
            cast = new SpellCast()
            {
              Caster = string.Intern(caster),
              Spell = string.Intern(pline.ActionPart.Substring(pline.OptionalIndex, pline.ActionPart.Length - pline.OptionalIndex - 1))
            };
          }
          break;
        case "youcast":
        case "yousing":
          if (pline.ActionPart.Length > pline.OptionalIndex + 15)
          {
            cast = new SpellCast()
            {
              Caster = ConfigUtil.PlayerName,
              Spell = string.Intern(pline.ActionPart.Substring(pline.OptionalIndex + 15, pline.ActionPart.Length - pline.OptionalIndex - 15 - 1))
            };
          }
          break;
        case "youactivate":
          if (pline.ActionPart.Length > pline.OptionalIndex + 10)
          {
            cast = new SpellCast()
            {
              Caster = ConfigUtil.PlayerName,
              Spell = string.Intern(pline.ActionPart.Substring(pline.OptionalIndex + 10, pline.ActionPart.Length - pline.OptionalIndex - 10 - 1))
            };
          }
          break;
      }

      return cast;
    }

    private static void CheckForSpecial(Dictionary<string, string> codes, string spellName, string player, double currentTime)
    {
      if (codes.Keys.FirstOrDefault(special => !string.IsNullOrEmpty(spellName) && spellName.Contains(special)) is string key && !string.IsNullOrEmpty(key))
      {
        DataManager.Instance.AddSpecial(new SpecialSpell() { Code = codes[key], Player = player, BeginTime = currentTime });
      }
    }
  }
}
