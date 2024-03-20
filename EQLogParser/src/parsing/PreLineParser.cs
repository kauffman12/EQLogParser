using System;

namespace EQLogParser
{
  internal class PreLineParser
  {
    private PreLineParser()
    {

    }

    internal static bool NeedProcessing(LineData lineData)
    {
      var found = false;
      var action = lineData.Action;

      if (action.Length > 10)
      {
        if (action.Length > 20 && action.StartsWith("Targeted (Player)"))
        {
          PlayerManager.Instance.AddVerifiedPlayer(action[19..], lineData.BeginTime);
          found = true; // ignore anything that starts with Targeted
        }
        else if (action.EndsWith(" shrinks.") && PlayerManager.IsPossiblePlayerName(action, action.Length - 9))
        {
          var test = action[..^9];
          PlayerManager.Instance.AddPetOrPlayerAction(test);
          found = true;
        }
        else if (action.EndsWith(" joined the raid.") && !action.StartsWith("You have"))
        {
          if (PlayerManager.IsPossiblePlayerName(action, action.Length - 17))
          {
            var test = action[..^17];
            PlayerManager.Instance.AddVerifiedPlayer(test, lineData.BeginTime);
            found = true;
          }
        }
        else if (action.EndsWith(" has joined the group."))
        {
          var test = action[..^22];
          if (PlayerManager.IsPossiblePlayerName(test))
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
          var test = action[..^19];
          if (PlayerManager.IsPossiblePlayerName(test))
          {
            PlayerManager.Instance.AddVerifiedPlayer(test, lineData.BeginTime);
            found = true;
          }
        }
        else if (action.EndsWith(" has left the group."))
        {
          var test = action[..^20];
          if (PlayerManager.IsPossiblePlayerName(test))
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
          var test = action[..^32];
          if (PlayerManager.IsPossiblePlayerName(test))
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
