using System;

namespace EQLogParser
{
  class PreLineParser
  {
    private PreLineParser()
    {

    }

    internal static bool NeedProcessing(LineData lineData)
    {
      bool found = false;
      string action = lineData.Action;

      if (action.Length > 10)
      {
        if (action.Length > 20 && action.StartsWith("Targeted (Player)"))
        {
          PlayerManager.Instance.AddVerifiedPlayer(action.Substring(19), lineData.BeginTime);
          found = true; // ignore anything that starts with Targeted
        }
        else if (action.EndsWith(" shrinks.") && PlayerManager.Instance.IsPossiblePlayerName(action, action.Length - 9))
        {
          string test = action.Substring(0, action.Length - 9);
          PlayerManager.Instance.AddPetOrPlayerAction(test);
          found = true;
        }
        else if (action.EndsWith(" joined the raid.") && !action.StartsWith("You have"))
        {
          if (PlayerManager.Instance.IsPossiblePlayerName(action, action.Length - 17))
          {
            string test = action.Substring(0, action.Length - 17);
            PlayerManager.Instance.AddVerifiedPlayer(test, lineData.BeginTime);
            found = true;
          }
        }
        else if (action.EndsWith(" has joined the group."))
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
        else if (action.EndsWith(" has left the raid."))
        {
          string test = action.Substring(0, action.Length - 19);
          if (PlayerManager.Instance.IsPossiblePlayerName(test))
          {
            PlayerManager.Instance.AddVerifiedPlayer(test, lineData.BeginTime);
            found = true;
          }
        }
        else if (action.EndsWith(" has left the group."))
        {
          string test = action.Substring(0, action.Length - 20);
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
        else if (action.EndsWith(" is now the leader of your raid.", StringComparison.Ordinal))
        {
          string test = action.Substring(0, action.Length - 32);
          if (PlayerManager.Instance.IsPossiblePlayerName(test))
          {
            PlayerManager.Instance.AddVerifiedPlayer(test, lineData.BeginTime);
            found = true;
          }
        }
      }

      return !found;
    }
  }
}
