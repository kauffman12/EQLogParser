using System;
using System.Globalization;

namespace EQLogParser
{
  class PreLineParser
  {
    private const int MIN_LINE_LENGTH = 33;

    internal static bool NeedProcessing(string line)
    {
      bool valid = false;
      if (line != null && line.Length > MIN_LINE_LENGTH)
      {
        ProcessLine pline = new ProcessLine { Line = line, ActionPart = line.Substring(Parsing.ACTIONINDEX) };
        valid = !(CheckForPlayersOrNPCs(pline) || CheckForPetLeader(pline) || CheckForJunk(pline));
      }

      return valid;
    }

    private static bool CheckForPetLeader(ProcessLine pline)
    {
      bool found = false;
      if (pline.ActionPart.Length >= 28 && pline.ActionPart.Length < 75)
      {
        int index = pline.ActionPart.IndexOf(" says, 'My leader is ", StringComparison.Ordinal);
        if (index > -1)
        {
          string pet = pline.ActionPart.Substring(0, index);
          if (!PlayerManager.Instance.IsVerifiedPlayer(pet)) // thanks idiots for this
          {
            int period = pline.ActionPart.IndexOf(".", index + 24, StringComparison.Ordinal);
            if (period > -1)
            {
              string owner = pline.ActionPart.Substring(index + 21, period - index - 21);

              if (!Helpers.IsPossiblePlayerName(pet) && (pet.StartsWith("A ", StringComparison.Ordinal) || pet.StartsWith("An ", StringComparison.Ordinal)))
              {
                pet = pet.ToLower(CultureInfo.CurrentCulture);
              }

              PlayerManager.Instance.AddVerifiedPlayer(owner);
              PlayerManager.Instance.AddVerifiedPet(pet);
              PlayerManager.Instance.AddPetToPlayer(pet, owner);
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

      int index;
      if (pline.ActionPart.StartsWith("Targeted (", StringComparison.Ordinal))
      {
        if (pline.ActionPart.Length > 20 && pline.ActionPart[10] == 'P' && pline.ActionPart[11] == 'l') // Player
        {
          PlayerManager.Instance.AddVerifiedPlayer(pline.ActionPart.Substring(19));
        }

        found = true; // ignore anything that starts with Targeted
      }
      else if (pline.ActionPart.Length > 10 && pline.ActionPart.Length < 27 && (index = pline.ActionPart.IndexOf(" shrinks.", StringComparison.Ordinal)) > -1
        && (index + 9) == pline.ActionPart.Length && Helpers.IsPossiblePlayerName(pline.ActionPart, index))
      {
        string test = pline.ActionPart.Substring(0, index);
        PlayerManager.Instance.AddPetOrPlayerAction(test);
        found = true;
      }
      else if (pline.ActionPart.Length > 10 && pline.ActionPart.Length < 35 && (index = pline.ActionPart.IndexOf(" joined the raid.", StringComparison.Ordinal)) > -1
        && (index + 17) == pline.ActionPart.Length && Helpers.IsPossiblePlayerName(pline.ActionPart, index))
      {
        string test = pline.ActionPart.Substring(0, index);
        PlayerManager.Instance.AddVerifiedPlayer(test);
        found = true;
      }

      return found;
    }

    private static bool CheckForJunk(ProcessLine pline)
    {
      return pline.ActionPart.StartsWith("Right click on", StringComparison.OrdinalIgnoreCase) ||
        pline.ActionPart.StartsWith("Stand close to and right click on", StringComparison.OrdinalIgnoreCase) ||
        pline.ActionPart.StartsWith("GUILD MOTD", StringComparison.OrdinalIgnoreCase) ||
        pline.ActionPart.StartsWith("Beginning to memorize ", StringComparison.OrdinalIgnoreCase);
    }
  }
}
