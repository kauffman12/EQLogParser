using System;

namespace EQLogParser
{
  class PreLineParser
  {
    private const int MIN_LINE_LENGTH = 30;

    private static readonly DateUtil DateUtil = new DateUtil();

    private PreLineParser()
    {

    }

    internal static bool NeedProcessing(string line, out string action)
    {
      action = null;
      bool valid = false;
      if (line != null && line.Length > MIN_LINE_LENGTH)
      {
        action = line.Substring(LineParsing.ActionIndex);
        valid = !(CheckForPlayersOrNPCs(line, action) || CheckForPetLeader(line, action));
      }
      else if (line != null)
      {
        DebugUtil.WriteLine(line);
      }

      return valid;
    }

    private static bool CheckForPetLeader(string line, string action)
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
              PlayerManager.Instance.AddVerifiedPlayer(owner, DateUtil.ParseLogDate(line, out _));
              PlayerManager.Instance.AddVerifiedPet(pet);
              PlayerManager.Instance.AddPetToPlayer(pet, owner);
            }
          }

          handled = true;
        }
      }

      return handled;
    }

    private static bool CheckForPlayersOrNPCs(string line, string action)
    {
      bool found = false;

      if (action.Length > 10)
      {
        if (action.Length > 20 && action.StartsWith("Targeted (", StringComparison.Ordinal))
        {
          if (action[10] == 'P' && action[11] == 'l') // Player
          {
            PlayerManager.Instance.AddVerifiedPlayer(action.Substring(19), DateUtil.ParseLogDate(line, out _));
          }

          found = true; // ignore anything that starts with Targeted
        }
        else if (action.EndsWith(" shrinks.", StringComparison.Ordinal) && PlayerManager.Instance.IsPossiblePlayerName(action, action.Length - 9))
        {
          string test = action.Substring(0, action.Length - 9);
          PlayerManager.Instance.AddPetOrPlayerAction(test);
          found = true;
        }
        else if (action.EndsWith(" joined the raid.", StringComparison.Ordinal) && !action.StartsWith("You have", StringComparison.Ordinal))
        {
          string test = action.Substring(0, action.Length - 17);
          PlayerManager.Instance.AddVerifiedPlayer(test, DateUtil.ParseLogDate(line, out _));
          found = true;
        }
        else if (action.EndsWith(" has joined the group.", StringComparison.Ordinal))
        {
          string test = action.Substring(0, action.Length - 22);
          PlayerManager.Instance.AddVerifiedPlayer(test, DateUtil.ParseLogDate(line, out _));
          found = true;
        }
        else if (action.EndsWith(" has left the raid.", StringComparison.Ordinal))
        {
          string test = action.Substring(0, action.Length - 19);
          PlayerManager.Instance.AddVerifiedPlayer(test, DateUtil.ParseLogDate(line, out _));
          found = true;
        }
        else if (action.EndsWith(" has left the group.", StringComparison.Ordinal))
        {
          string test = action.Substring(0, action.Length - 20);
          PlayerManager.Instance.AddVerifiedPlayer(test, DateUtil.ParseLogDate(line, out _));
          found = true;
        }
        else if (action.EndsWith(" is now the leader of your raid.", StringComparison.Ordinal))
        {
          string test = action.Substring(0, action.Length - 32);
          PlayerManager.Instance.AddVerifiedPlayer(test, DateUtil.ParseLogDate(line, out _));
          found = true;
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
