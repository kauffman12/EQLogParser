using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace EQLogParser
{
  class CastLineParser
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    public static event EventHandler<string> EventsLineProcessed;

    private static DateUtil DateUtil = new DateUtil();

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

    public static void Process(string line)
    {
      try
      {
        int index = -1;
        if (line.Length > 44 && (index = line.IndexOf(" begin", Parsing.ACTION_INDEX + 3, StringComparison.Ordinal)) > -1)
        {
          SpellCast cast = null;
          ProcessLine pline = null;
          int firstSpace = line.IndexOf(" ", Parsing.ACTION_INDEX);
          if (firstSpace > -1 && firstSpace == index)
          {
            if (firstSpace == (Parsing.ACTION_INDEX + 3) && line.Substring(Parsing.ACTION_INDEX, 3) == "You")
            {
              var test = line.Substring(index + 7, 4);
              if (test == "cast" || test == "sing")
              {
                pline = new ProcessLine() { Line = line, ActionPart = line.Substring(Parsing.ACTION_INDEX) };
                pline.OptionalIndex = index - Parsing.ACTION_INDEX;
                pline.OptionalData = "you" + test;
                pline.TimeString = pline.Line.Substring(1, 24);
                pline.CurrentTime = DateUtil.ParseDate(pline.TimeString, out double precise);
                cast = HandleSpellCast(pline);
              }
            }
            else
            {
              var test = line.Substring(index + 11, 4);
              if (test == "cast" || test == "sing")
              {
                pline = new ProcessLine() { Line = line, ActionPart = line.Substring(Parsing.ACTION_INDEX) };
                pline.OptionalIndex = index - Parsing.ACTION_INDEX;
                pline.OptionalData = test;
                pline.TimeString = pline.Line.Substring(1, 24);
                pline.CurrentTime = DateUtil.ParseDate(pline.TimeString, out double precise);
                cast = HandleSpellCast(pline);
              }
            }

            if (cast != null)
            {
              DataManager.Instance.AddSpellCast(cast, pline.CurrentTime);
            }
          }
        }
        else // lands on messages
        {
          int firstSpace = line.IndexOf(" ", Parsing.ACTION_INDEX, StringComparison.Ordinal);
          if (firstSpace > -1 && line[firstSpace - 2] == '\'' && line[firstSpace - 1] == 's')
          {
            ProcessLine pline = new ProcessLine() { Line = line, ActionPart = line.Substring(Parsing.ACTION_INDEX) };
            pline.OptionalIndex = firstSpace + 1 - Parsing.ACTION_INDEX;
            pline.TimeString = pline.Line.Substring(1, 24);
            pline.CurrentTime = DateUtil.ParseDate(pline.TimeString, out double precise);
            HandlePosessiveLandsOnOther(pline);
          }
          else if (firstSpace > -1)
          {
            string player = line.Substring(Parsing.ACTION_INDEX, firstSpace - Parsing.ACTION_INDEX);
            if (!IgnoreMap.ContainsKey(player))
            {
              if (line.Length > firstSpace + 6)
              {
                ProcessLine pline = new ProcessLine() { Line = line, ActionPart = line.Substring(Parsing.ACTION_INDEX) };
                pline.OptionalIndex = firstSpace + 1 - Parsing.ACTION_INDEX;
                pline.OptionalData = player;
                pline.TimeString = pline.Line.Substring(1, 24);
                pline.CurrentTime = DateUtil.ParseDate(pline.TimeString, out double precise);
                HandleOtherLandsOnCases(pline);
              }
            }
          }
        }
      }
      catch (Exception e)
      {
        LOG.Error(e);
      }

      EventsLineProcessed(line, line);
    }

    public static void HandleOtherLandsOnCases(ProcessLine pline)
    {
      string player = pline.OptionalData;
      string matchOn = pline.ActionPart.Substring(pline.OptionalIndex);
      SpellData result = DataManager.Instance.GetNonPosessiveLandsOnOther(matchOn, out List<SpellData> output);
      if (result == null)
      {
        matchOn = pline.ActionPart;
        result = DataManager.Instance.GetLandsOnYou(matchOn, out output);
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

    public static void HandlePosessiveLandsOnOther(ProcessLine pline)
    {
      string matchOn = pline.ActionPart.Substring(pline.OptionalIndex);
      SpellData result = DataManager.Instance.GetPosessiveLandsOnOther(matchOn, out List<SpellData> output);
      if (result != null)
      {
        var newSpell = new ReceivedSpell() { Receiver = string.Intern(pline.ActionPart.Substring(0, pline.OptionalIndex - 3)), SpellData = result };
        DataManager.Instance.AddReceivedSpell(newSpell, pline.CurrentTime);
      }
    }

    public static SpellCast HandleSpellCast(ProcessLine pline)
    {
      SpellCast cast = null;
      string caster = pline.ActionPart.Substring(0, pline.OptionalIndex);

      switch (pline.OptionalData)
      {
        case "cast":
        case "sing":
          int bracketIndex = (pline.OptionalData == "cast") ? 25 : 24;
          if (pline.ActionPart.Length > pline.OptionalIndex + bracketIndex)
          {
            int finalBracket;
            int index = pline.ActionPart.IndexOf("<", pline.OptionalIndex + bracketIndex);
            if (index > -1 && (finalBracket = pline.ActionPart.IndexOf(">", pline.OptionalIndex + bracketIndex, StringComparison.Ordinal)) > -1)
            {
              cast = new SpellCast()
              {
                Caster = string.Intern(caster),
                Spell = string.Intern(pline.ActionPart.Substring(index + 1, finalBracket - index - 1))
              };
            }
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
