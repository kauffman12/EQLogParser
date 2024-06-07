using System;

namespace EQLogParser
{
  internal class PreLineParser
  {
    // Process things that can easily identify a player
    private PreLineParser()
    {

    }

    internal static bool NeedProcessing(LineData lineData)
    {
      var found = false;
      var action = lineData.Action;

      if (action.Length > 10)
      {
        if (action.Length > 20 && action.StartsWith("Targeted (Player)", StringComparison.OrdinalIgnoreCase))
        {
          PlayerManager.Instance.AddVerifiedPlayer(action[19..], lineData.BeginTime);
          found = true; // ignore anything that starts with Targeted
        }
        else if (action.Length > 45 && action.StartsWith("You successfully loaded your ", StringComparison.OrdinalIgnoreCase))
        {
          if (action.IndexOf(" ", 29, StringComparison.Ordinal) is var end and > -1)
          {
            var className = action.Substring(29, end - 29);
            PlayerManager.Instance.SetPlayerClass(ConfigUtil.PlayerName, className, "Class chosen from persona change.");
            found = true;
          }
        }
        else if (action.EndsWith(" joined the raid.", StringComparison.OrdinalIgnoreCase) && !action.StartsWith("You have", StringComparison.OrdinalIgnoreCase))
        {
          if (PlayerManager.IsPossiblePlayerName(action, action.Length - 17))
          {
            var test = action[..^17];
            PlayerManager.Instance.AddVerifiedPlayer(test, lineData.BeginTime);
            found = true;
          }
        }
        else if (action.EndsWith(" has joined the group.", StringComparison.OrdinalIgnoreCase))
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
        else if (action.EndsWith(" has left the raid.", StringComparison.OrdinalIgnoreCase))
        {
          var test = action[..^19];
          if (PlayerManager.IsPossiblePlayerName(test))
          {
            PlayerManager.Instance.AddVerifiedPlayer(test, lineData.BeginTime);
            found = true;
          }
        }
        else if (action.EndsWith(" has left the group.", StringComparison.OrdinalIgnoreCase))
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
        else if (action.EndsWith(" is now the leader of your raid.", StringComparison.OrdinalIgnoreCase))
        {
          var test = action[..^32];
          if (PlayerManager.IsPossiblePlayerName(test))
          {
            PlayerManager.Instance.AddVerifiedPlayer(test, lineData.BeginTime);
            found = true;
          }
        }
        else if (action.StartsWith("Glug, glug, glug...  ", StringComparison.OrdinalIgnoreCase))
        {
          var end = PlayerManager.FindPossiblePlayerName(action, out var isCrossServer, 21, -1, ' ');
          if (end != -1 && !isCrossServer && action.AsSpan()[end..].StartsWith(" takes a drink ", StringComparison.OrdinalIgnoreCase))
          {
            PlayerManager.Instance.AddVerifiedPlayer(action[21..end], lineData.BeginTime);
            found = true;
          }
        }
      }

      return !found;
    }
  }
}
