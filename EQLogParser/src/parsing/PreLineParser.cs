using System;
using System.Globalization;

namespace EQLogParser
{
  class PreLineParser
  {
    private const int MIN_LINE_LENGTH = 33;

    internal static bool NeedProcessing(string line, out string action)
    {
      action = null;
      bool valid = false;
      if (line != null && line.Length > MIN_LINE_LENGTH)
      {
        action = line.Substring(LineParsing.ACTIONINDEX);
        valid = !(CheckForPlayersOrNPCs(action) || CheckForPetLeader(action));
      }
      else if (line != null)
      {
        DebugUtil.WriteLine(line);
      }

      return valid;
    }

    private static bool CheckForPetLeader(string action)
    {
      bool handled = false;
      if (action.Length >= 28 && action.Length < 75)
      {
        int index = action.IndexOf(" says, 'My leader is ", StringComparison.Ordinal);
        if (index > -1)
        {
          string pet = action.Substring(0, index);
          if (!PlayerManager.Instance.IsVerifiedPlayer(pet)) // thanks idiots for this
          {
            int period = action.IndexOf(".", index + 24, StringComparison.Ordinal);
            if (period > -1)
            {
              string owner = action.Substring(index + 21, period - index - 21);

              if (!PlayerManager.Instance.IsPossiblePlayerName(pet) && (pet.StartsWith("A ", StringComparison.Ordinal) || pet.StartsWith("An ", StringComparison.Ordinal)))
              {
                pet = pet.ToLower(CultureInfo.CurrentCulture);
              }

              PlayerManager.Instance.AddVerifiedPlayer(owner);
              PlayerManager.Instance.AddVerifiedPet(pet);
              PlayerManager.Instance.AddPetToPlayer(pet, owner);
            }
          }

          handled = true;
        }
      }

      return handled;
    }

    private static bool CheckForPlayersOrNPCs(string action)
    {
      bool found = false;

      if (action.Length > 10)
      {
        int index;
        if (action.Length > 20 && action.StartsWith("Targeted (", StringComparison.Ordinal))
        {
          if (action[10] == 'P' && action[11] == 'l') // Player
          {
            PlayerManager.Instance.AddVerifiedPlayer(action.Substring(19));
          }

          found = true; // ignore anything that starts with Targeted
        }
        else if (action.Length < 27 && (index = action.IndexOf(" shrinks.", StringComparison.Ordinal)) > -1
          && (index + 9) == action.Length && PlayerManager.Instance.IsPossiblePlayerName(action, index))
        {
          string test = action.Substring(0, index);
          PlayerManager.Instance.AddPetOrPlayerAction(test);
          found = true;
        }
        else if (action.Length < 35 && (index = action.IndexOf(" joined the raid.", StringComparison.Ordinal)) > -1
          && (index + 17) == action.Length && PlayerManager.Instance.IsPossiblePlayerName(action, index))
        {
          string test = action.Substring(0, index);
          PlayerManager.Instance.AddVerifiedPlayer(test);
          found = true;
        }
        else if (action.Length < 60 && (index = action.IndexOf(" has joined the group.", StringComparison.Ordinal)) > -1)
        {
          string test = action.Substring(0, index);
          PlayerManager.Instance.AddPetOrPlayerAction(test);
          found = true;
        }
      }

      return found;
    }
  }
}
