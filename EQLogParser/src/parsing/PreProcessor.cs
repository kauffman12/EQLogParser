using System;

namespace EQLogParser
{
  class PreProcessor
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private const int MIN_LINE_LENGTH = 33;
    private const int ACTION_PART_INDEX = 27;

    internal static bool NeedProcessing(string line)
    {
      bool needProcessing = line.Length > MIN_LINE_LENGTH;

      try
      {
        int index;
        if (needProcessing && line.Length >= 40 && (index = line.IndexOf(" ", ACTION_PART_INDEX, StringComparison.Ordinal)) > -1 &&
          (line.IndexOf("say", index + 1, 3, StringComparison.Ordinal) > -1 || line.IndexOf("tell", 4, index + 1, StringComparison.Ordinal) > -1))
        {
          // ignore tells but check for some chat related things
          ProcessLine pline = new ProcessLine() { Line = line, ActionPart = line.Substring(ACTION_PART_INDEX) };

          // check other things
          if (!CheckForPlayersOrNPCs(pline))
          {
            CheckForPetLeader(pline);
          }

          needProcessing = false;
        }
      }
      catch (Exception e)
      {
        LOG.Error(e);
      }

      return needProcessing;
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
        //else if (pline.ActionPart.Length > 17 && pline.ActionPart[10] == 'N' && pline.ActionPart[11] == 'P') // NPC + Pet..
        //{
        //  DataManager.Instance.UpdateDefinitelyNotAPlayer(pline.ActionPart.Substring(16));
        //  found = true;
        //}
      }
      else if (pline.ActionPart.Length > 10 && pline.ActionPart.Length < 25 && (index = pline.ActionPart.IndexOf(" shrinks.", StringComparison.Ordinal)) > -1
        && Helpers.IsPossiblePlayerName(pline.ActionPart, index))
      {
        string test = pline.ActionPart.Substring(0, index);
        DataManager.Instance.UpdateUnVerifiedPetOrPlayer(test);
        found = true;
      }
      else
      {
        if ((index = pline.ActionPart.IndexOf(" tells the guild, ", StringComparison.Ordinal)) > -1)
        {
          int firstSpace = pline.ActionPart.IndexOf(" ", StringComparison.Ordinal);
          if (firstSpace > -1 && firstSpace == index)
          {
            string name = pline.ActionPart.Substring(0, index);
            DataManager.Instance.UpdateVerifiedPlayers(name);
          }

          found = true; // found chat, not that it had to work
        }
      }
      return found;
    }
  }
}
