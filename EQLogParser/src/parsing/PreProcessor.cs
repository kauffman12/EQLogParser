using System;
using System.Collections.Generic;

namespace EQLogParser
{
  class PreProcessor
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private const int MIN_LINE_LENGTH = 33;
    private const int ACTION_PART_INDEX = 27;
    private const int MAX_NAME_CHECK = 24;

    private static List<string> YouCriteria = new List<string>
    {
      "You say,", "You told ", "You tell ", "You say to", "You shout,"
    };

    private static List<string> OtherCriteria = new List<string>
    {
      " tells ", " says,", " shouts "
    };

    internal static bool IsChat(string line)
    {
      bool found = false;

      try
      {
        int max = Math.Min(line.Length - ACTION_PART_INDEX, MAX_NAME_CHECK);
        found = YouCriteria.FindIndex(criteria => line.IndexOf(criteria, ACTION_PART_INDEX, max, StringComparison.Ordinal) > -1) > -1;

        if (!found)
        {
          int firstSpace = line.IndexOf(" ", ACTION_PART_INDEX, StringComparison.Ordinal);
          if (firstSpace > -1)
          {
            max = Math.Min(line.Length - firstSpace, MAX_NAME_CHECK);
            found = OtherCriteria.FindIndex(criteria => line.IndexOf(criteria, firstSpace, max, StringComparison.Ordinal) > -1) > -1;

            if (found)
            {
              ProcessLine pline = new ProcessLine { Line = line, ActionPart = line.Substring(ACTION_PART_INDEX) };
              if (!CheckForPetLeader(pline))
              {
                CheckGuildTells(pline);
              }
            }
          }
        }
      }
      catch (Exception)
      {
        // LOG.Debug(ex);
      }

      return found;
    }

    internal static bool IsValid(string line)
    {
      bool valid = false;
      if (line != null && line.Length > MIN_LINE_LENGTH)
      {
        ProcessLine pline = new ProcessLine { Line = line, ActionPart = line.Substring(ACTION_PART_INDEX) };
        valid = !CheckForPlayersOrNPCs(pline);
      }
      return valid;
    }

    private static bool CheckForPetLeader(ProcessLine pline)
    {
      bool found = false;
      if (pline.ActionPart.Length >= 28 && pline.ActionPart.Length < 55)
      {
        int index = pline.ActionPart.IndexOf(" says, 'My leader is ", StringComparison.Ordinal);
        if (index > -1)
        {
          string pet = pline.ActionPart.Substring(0, index);
          if (!DataManager.Instance.CheckNameForPlayer(pet)) // thanks idiots for this
          {
            int period = pline.ActionPart.IndexOf(".", index + 24, StringComparison.Ordinal);
            if (period > -1)
            {
              string owner = pline.ActionPart.Substring(index + 21, period - index - 21);
              DataManager.Instance.UpdateVerifiedPlayers(owner);
              DataManager.Instance.UpdateVerifiedPets(pet);
              DataManager.Instance.UpdatePetToPlayer(pet, owner);
            }
          }

          found = true;
        }
      }
      return found;
    }

    private static bool CheckForPlayersOrNPCs(ProcessLine pline)
    {
      bool found = false;

      int index = -1;
      if (pline.ActionPart.StartsWith("Targeted (", StringComparison.Ordinal))
      {
        if (pline.ActionPart.Length > 20 && pline.ActionPart[10] == 'P' && pline.ActionPart[11] == 'l') // Player
        {
          DataManager.Instance.UpdateVerifiedPlayers(pline.ActionPart.Substring(19));
          found = true;
        }
      }
      else if (pline.ActionPart.Length > 10 && pline.ActionPart.Length < 25 && (index = pline.ActionPart.IndexOf(" shrinks.", StringComparison.Ordinal)) > -1
        && Helpers.IsPossiblePlayerName(pline.ActionPart, index))
      {
        string test = pline.ActionPart.Substring(0, index);
        DataManager.Instance.UpdateUnVerifiedPetOrPlayer(test);
        found = true;
      }

      return found;
    }

    private static void CheckGuildTells(ProcessLine pline)
    {
      int index;
      if ((index = pline.ActionPart.IndexOf(" tells the guild, ", StringComparison.Ordinal)) > -1)
      {
        int firstSpace = pline.ActionPart.IndexOf(" ", StringComparison.Ordinal);
        if (firstSpace > -1 && firstSpace == index)
        {
          string name = pline.ActionPart.Substring(0, index);
          DataManager.Instance.UpdateVerifiedPlayers(name);
        }
      }
    }
  }
}
