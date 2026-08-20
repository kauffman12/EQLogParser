using System;

namespace EQLogParser
{
  internal class PreLineParser
  {
    // Process things that can easily identify a player
    private PreLineParser()
    {

    }

    internal static bool NeedProcessing(LineData lineData, Action<string, double> addVerifiedPlayer, Func<string, int, bool> isPossiblePlayerName, Action<string> addMerc)
    {
      var found = false;
      var action = lineData.Action;

      if (action.Length > 10)
      {
        if (action.Length > 20 && action.StartsWith("Targeted (Player)", StringComparison.OrdinalIgnoreCase))
        {
          addVerifiedPlayer(action[19..], lineData.BeginTime);
          found = true; // ignore anything that starts with Targeted
        }
        else if (action.EndsWith(" joined the raid.", StringComparison.OrdinalIgnoreCase) && !action.StartsWith("You have", StringComparison.OrdinalIgnoreCase))
        {
          if (isPossiblePlayerName(action, action.Length - 17))
          {
            var test = action[..^17];
            addVerifiedPlayer(test, lineData.BeginTime);
            found = true;
          }
        }
        else if (action.EndsWith(" has joined the group.", StringComparison.OrdinalIgnoreCase))
        {
          var test = action[..^22];
          if (isPossiblePlayerName(test, -1))
          {
            addVerifiedPlayer(test, lineData.BeginTime);
          }
          else
          {
            addMerc(test);
          }

          found = true;
        }
        else if (action.EndsWith(" has left the raid.", StringComparison.OrdinalIgnoreCase))
        {
          var test = action[..^19];
          if (isPossiblePlayerName(test, -1))
          {
            addVerifiedPlayer(test, lineData.BeginTime);
            found = true;
          }
        }
        else if (action.EndsWith(" has left the group.", StringComparison.OrdinalIgnoreCase))
        {
          var test = action[..^20];
          if (isPossiblePlayerName(test, -1))
          {
            addVerifiedPlayer(test, lineData.BeginTime);
          }
          else
          {
            addMerc(test);
          }

          found = true;
        }
        else if (action.EndsWith(" is now the leader of your raid.", StringComparison.OrdinalIgnoreCase))
        {
          var test = action[..^32];
          if (isPossiblePlayerName(test, -1))
          {
            addVerifiedPlayer(test, lineData.BeginTime);
            found = true;
          }
        }
        else if (action.StartsWith("Glug, glug, glug...  ", StringComparison.OrdinalIgnoreCase))
        {
          var end = FindPossiblePlayerName(action, out var isCrossServer, 21, -1, ' ');
          if (end != -1 && !isCrossServer && action.AsSpan()[end..].StartsWith(" takes a drink ", StringComparison.OrdinalIgnoreCase))
          {
            addVerifiedPlayer(action[21..end], lineData.BeginTime);
            found = true;
          }
        }
      }

      return !found;
    }

    internal static int FindPossiblePlayerName(string action, out bool isCrossServer, int startIndex, int stopIndex, char stopChar)
    {
      isCrossServer = false;

      if (action is null)
      {
        return -1;
      }

      var stop = stopIndex > -1 ? stopIndex : action.Length;
      if (startIndex > stop || (stop - startIndex) < 3)
      {
        return -1;
      }

      var dotCount = 0;

      for (var i = startIndex; i < stop; i++)
      {
        if (action[i] == stopChar)
        {
          return i;
        }

        if (i > startIndex && action[i] == '.')
        {
          isCrossServer = true;
          if (++dotCount > 1)
          {
            return -1;
          }
        }
        else if (!char.IsLetter(action, i))
        {
          return -1;
        }
      }

      return -1;
    }
  }
}
