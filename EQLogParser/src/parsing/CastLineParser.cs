using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace EQLogParser
{
  class CastLineParser
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    public static event EventHandler<string> EventsLineProcessed;

    private static readonly DateUtil DateUtil = new DateUtil();

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

    public static void Process(string source, string line)
    {
      bool handled = false;

      try
      {
        int index = -1;
        if (line.Length > 44 && (index = line.IndexOf(" begin", Parsing.ACTIONINDEX + 3, StringComparison.Ordinal)) > -1)
        {
          SpellCast cast = null;
          ProcessLine pline = null;
          int firstSpace = line.IndexOf(" ", Parsing.ACTIONINDEX, StringComparison.Ordinal);
          if (firstSpace > -1 && firstSpace == index)
          {
            if (firstSpace == (Parsing.ACTIONINDEX + 3) && line.Substring(Parsing.ACTIONINDEX, 3) == "You")
            {
              var test = line.Substring(index + 7, 4);
              if (test == "cast" || test == "sing")
              {
                pline = new ProcessLine() { Line = line, ActionPart = line.Substring(Parsing.ACTIONINDEX) };
                pline.OptionalData = "you" + test;
                pline.OptionalIndex = 3;
                pline.TimeString = pline.Line.Substring(1, 24);
                pline.CurrentTime = DateUtil.ParseDate(pline.TimeString, out double precise);
                cast = HandleSpellCast(pline, line.Substring(Parsing.ACTIONINDEX, index - Parsing.ACTIONINDEX));
              }
            }
            else
            {
              // [Sun Mar 31 19:40:12 2019] Jiren begins to cast a spell. <Blood Pact Strike XXV>
              // [Thu Apr 18 01:46:06 2019] Incogitable begins casting Dizzying Wheel Rk. II.

              int spellIndex = -1;
              var test = line.Substring(index + 8, 7);
              if (test == "casting" || test == "singing")
              {
                spellIndex = firstSpace - Parsing.ACTIONINDEX + 16;
              }
              else
              {
                test = line.Substring(index + 11, 4);
                if (test == "cast")
                {
                  spellIndex = firstSpace - Parsing.ACTIONINDEX + 26;
                }
                else if (test == "sing")
                {
                  spellIndex = firstSpace - Parsing.ACTIONINDEX + 25;
                }
              }

              if (spellIndex > -1)
              {
                pline = new ProcessLine() { Line = line, ActionPart = line.Substring(Parsing.ACTIONINDEX) };
                pline.OptionalData = test;
                pline.OptionalIndex = spellIndex;
                pline.TimeString = pline.Line.Substring(1, 24);
                pline.CurrentTime = DateUtil.ParseDate(pline.TimeString, out double precise);
                cast = HandleSpellCast(pline, line.Substring(Parsing.ACTIONINDEX, index - Parsing.ACTIONINDEX));
              }
            }

            if (cast != null)
            {
              DataManager.Instance.AddSpellCast(cast, pline.CurrentTime);
              handled = true;
            }
          }
        }

        if (!handled && line.EndsWith(" spell is interrupted.", StringComparison.Ordinal))
        {
          //[Thu Apr 18 01:38:10 2019] Incogitable's Dizzying Wheel Rk. II spell is interrupted.
          //[Thu Apr 18 01:38:00 2019] Your Stormjolt Vortex Rk. III spell is interrupted.
          ProcessLine pline = new ProcessLine() { Line = line, ActionPart = line.Substring(Parsing.ACTIONINDEX), OptionalIndex = line.Length - 22 };
          pline.TimeString = pline.Line.Substring(1, 24);
          pline.CurrentTime = DateUtil.ParseDate(pline.TimeString, out _);

          string player = null;
          string spell = null;
          int end = line.Length - 22;
          int len;

          if (line.IndexOf("Your", Parsing.ACTIONINDEX, 4, StringComparison.Ordinal) > -1)
          {
            player = "You";
            len = end - Parsing.ACTIONINDEX - 5;
            if (len > 0)
            {
              spell = line.Substring(Parsing.ACTIONINDEX + 5, len);
            }
          }
          else
          {
            int possessive = line.IndexOf("'s ", Parsing.ACTIONINDEX, StringComparison.Ordinal);
            if (possessive > -1)
            {
              player = line.Substring(Parsing.ACTIONINDEX, possessive - Parsing.ACTIONINDEX);

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
            double currentTime = DateUtil.ParseDate(timeString, out _);
            DataManager.Instance.HandleSpellInterrupt(player, spell, currentTime);
            handled = true;
          }
        }
        else if (!handled) // lands on messages
        {
          int firstSpace = line.IndexOf(" ", Parsing.ACTIONINDEX, StringComparison.Ordinal);
          if (firstSpace > -1 && line[firstSpace - 2] == '\'' && line[firstSpace - 1] == 's')
          {
            ProcessLine pline = new ProcessLine() { Line = line, ActionPart = line.Substring(Parsing.ACTIONINDEX) };
            pline.OptionalIndex = firstSpace + 1 - Parsing.ACTIONINDEX;
            pline.TimeString = pline.Line.Substring(1, 24);
            pline.CurrentTime = DateUtil.ParseDate(pline.TimeString, out double precise);
            HandlePosessiveLandsOnOther(pline);
            handled = true;
          }
          else if (firstSpace > -1)
          {
            string player = line.Substring(Parsing.ACTIONINDEX, firstSpace - Parsing.ACTIONINDEX);
            if (!IgnoreMap.ContainsKey(player))
            {
              if (line.Length > firstSpace + 4)
              {
                ProcessLine pline = new ProcessLine() { Line = line, ActionPart = line.Substring(Parsing.ACTIONINDEX) };
                pline.OptionalIndex = firstSpace + 1 - Parsing.ACTIONINDEX;
                pline.OptionalData = player;
                pline.TimeString = pline.Line.Substring(1, 24);
                pline.CurrentTime = DateUtil.ParseDate(pline.TimeString, out double precise);
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

      EventsLineProcessed(line, line);
    }

    private static void HandleOtherLandsOnCases(ProcessLine pline)
    {
      string player = pline.OptionalData;
      string matchOn = pline.ActionPart.Substring(pline.OptionalIndex);
      SpellData result = DataManager.Instance.GetNonPosessiveLandsOnOther(matchOn, out _);
      if (result == null)
      {
        matchOn = pline.ActionPart;
        result = DataManager.Instance.GetLandsOnYou(matchOn, out _);
        if (result != null)
        {
          player = "You";
        }
      }

      if (result != null)
      {
        var newSpell = new ReceivedSpell() { Receiver = string.Intern(player), SpellData = result };
        DataManager.Instance.AddReceivedSpell(newSpell, pline.CurrentTime);
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
      }
    }

    private static SpellCast HandleSpellCast(ProcessLine pline, string caster)
    {
      SpellCast cast = null;

      switch (pline.OptionalData)
      {
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
              Caster = string.Intern(caster),
              Spell = string.Intern(pline.ActionPart.Substring(pline.OptionalIndex + 15, pline.ActionPart.Length - pline.OptionalIndex - 15 - 1))
            };
          }
          break;
      }

      return cast;
    }
  }
}
