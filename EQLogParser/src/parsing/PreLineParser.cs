using System;

namespace EQLogParser
{
  class PreLineParser
  {
    private const int MIN_LINE_LENGTH = 30;

    private PreLineParser()
    {

    }

    internal static bool NeedProcessing(LineData lineData)
    {
      bool valid = false;
      if (lineData.Line != null && lineData.Line.Length > MIN_LINE_LENGTH)
      {
        lineData.Action = lineData.Line.Substring(LineParsing.ActionIndex);
        valid = !(CheckForPlayersOrNPCs(lineData) || CheckForPetLeader(lineData, " says, 'My leader is ")
          || CheckForPetLeader(lineData, " says 'My leader is ")); // eqemu doesn't have the comma
      }
      else if (lineData.Line != null)
      {
        DebugUtil.WriteLine(lineData.Line);
      }

      return valid;
    }

    private static bool CheckForPetLeader(LineData lineData, string search)
    {
      bool handled = false;
      string action = lineData.Action;
      if (action.Length >= 28 && action.Length < 75)
      {
        int index = action.IndexOf(search, StringComparison.Ordinal);
        if (index > -1)
        {
          string pet = action.Substring(0, index);
          if (!PlayerManager.Instance.IsVerifiedPlayer(pet)) // thanks idiots for this
          {
            int period = action.IndexOf(".", index + 23, StringComparison.Ordinal);
            if (period > -1)
            {
              string owner = action.Substring(index + search.Length, period - index - search.Length);
              PlayerManager.Instance.AddVerifiedPlayer(owner, lineData.BeginTime);
              PlayerManager.Instance.AddVerifiedPet(pet);
              PlayerManager.Instance.AddPetToPlayer(pet, owner);
            }
          }

          handled = true;
        }
      }

      return handled;
    }

    private static bool CheckForPlayersOrNPCs(LineData lineData)
    {
      bool found = false;

      string action = lineData.Action;
      if (action.Length > 10)
      {
        if (action.Length > 20 && action.StartsWith("Targeted (", StringComparison.Ordinal))
        {
          if (action[10] == 'P' && action[11] == 'l') // Player
          {
            PlayerManager.Instance.AddVerifiedPlayer(action.Substring(19), lineData.BeginTime);
          }

          found = true; // ignore anything that starts with Targeted
        }
        else if (action.EndsWith(" shrinks.", StringComparison.Ordinal) && PlayerManager.Instance.IsPossiblePlayerName(action, action.Length - 9))
        {
          string test = action.Substring(0, action.Length - 9);
          if (PlayerManager.Instance.IsPossiblePlayerName(test))
          {
            PlayerManager.Instance.AddPetOrPlayerAction(test);
            found = true;
          }
        }
        else if (action.EndsWith(" joined the raid.", StringComparison.Ordinal) && !action.StartsWith("You have", StringComparison.Ordinal))
        {
          string test = action.Substring(0, action.Length - 17);
          if (PlayerManager.Instance.IsPossiblePlayerName(test))
          {
            PlayerManager.Instance.AddVerifiedPlayer(test, lineData.BeginTime);
            found = true;
          }
        }
        else if (action.EndsWith(" has joined the group.", StringComparison.Ordinal))
        {
          string test = action.Substring(0, action.Length - 22);
          if (PlayerManager.Instance.IsPossiblePlayerName(test))
          {
            PlayerManager.Instance.AddVerifiedPlayer(test, lineData.BeginTime);
          }
          else
          {
            PlayerManager.Instance.AddMerc(test);
          }

          found = true;
        }
        else if (action.EndsWith(" has left the raid.", StringComparison.Ordinal))
        {
          string test = action.Substring(0, action.Length - 19);
          if (PlayerManager.Instance.IsPossiblePlayerName(test))
          {
            PlayerManager.Instance.AddVerifiedPlayer(test, lineData.BeginTime);
            found = true;
          }
        }
        else if (action.EndsWith(" has left the group.", StringComparison.Ordinal))
        {
          string test = action.Substring(0, action.Length - 20);
          if (PlayerManager.Instance.IsPossiblePlayerName(test))
          {
            PlayerManager.Instance.AddVerifiedPlayer(test, lineData.BeginTime);
            found = true;
          }
        }
        else if (action.EndsWith(" is now the leader of your raid.", StringComparison.Ordinal))
        {
          string test = action.Substring(0, action.Length - 32);
          if (PlayerManager.Instance.IsPossiblePlayerName(test))
          {
            PlayerManager.Instance.AddVerifiedPlayer(test, lineData.BeginTime);
            found = true;
          }
        }
        // handle junk line to avoid it being written to debug
        else if (action.StartsWith("Your Irae Faycite Shard:", StringComparison.Ordinal))
        {
          found = true;
        }
        else if (action.EndsWith("feels alive with power.", StringComparison.Ordinal))
        {
          // handle junk line to avoid it being written to debug
          found = true;
        }
        else if (action.Equals("You cannot see your target.", StringComparison.Ordinal))
        {
          // handle junk line to avoid it being written to debug
          found = true;
        }
      }

      return found;
    }
  }
}
