using System;
using System.Collections.Generic;

namespace EQLogParser
{
  class ChatLineParser
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private const int MIN_LINE_LENGTH = 33;
    private const int ACTION_PART_INDEX = 27;

    private static List<string> YouCriteria = new List<string>
    {
      "You say,", "You told ", "You tell ", "You say to ", "You shout,", "You say out of", "You auction,"
    };

    private static List<string> OtherCriteria = new List<string>
    {
      " says,", " tells ", " shouts,", "says out of", " auctions,"
    };

    internal static ChatType ParseChatType(string line)
    {
      ChatType chatType = null;

      try
      {
        int index = YouCriteria.FindIndex(criteria => line.IndexOf(criteria, ACTION_PART_INDEX, StringComparison.Ordinal) > -1);

        if (index < 0)
        {
          int criteriaIndex = -1;
          for (int i=0; i<OtherCriteria.Count; i++)
          {
            criteriaIndex = line.IndexOf(OtherCriteria[i], ACTION_PART_INDEX, StringComparison.Ordinal);
            if (criteriaIndex > -1)
            {
              index = i;
              break;
            }
          }

          if (index > -1 && criteriaIndex > -1)
          {
            int start, end;
            chatType = new ChatType { SenderIsYou = false, Sender = line.Substring(ACTION_PART_INDEX, criteriaIndex - ACTION_PART_INDEX), Line = line };

            switch(index)
            {
              case 0:
                chatType.Channel = ChatChannels.SAY;
                break;
              case 1:
                start = criteriaIndex + 7;
                if (line.IndexOf("you, ", start, 5, StringComparison.Ordinal) > -1)
                {
                  chatType.Channel = ChatChannels.TELL;
                  chatType.ReceiverIsYou = true;
                  chatType.Receiver = "You";
                }
                else if (line.IndexOf("the guild", start, 9, StringComparison.Ordinal) > -1)
                {
                  chatType.Channel = ChatChannels.GUILD;
                }
                else if (line.IndexOf("the group", start, 9, StringComparison.Ordinal) > -1)
                {
                  chatType.Channel = ChatChannels.GROUP;
                }
                else if (line.IndexOf("the raid", start, 8, StringComparison.Ordinal) > -1)
                {
                  chatType.Channel = ChatChannels.RAID;
                }
                else if (line.IndexOf("the fellowship", start, 14, StringComparison.Ordinal) > -1)
                {
                  chatType.Channel = ChatChannels.FELLOWSHIP;
                }
                else if ((end = line.IndexOf(":", start + 1, StringComparison.Ordinal)) > -1)
                {
                  chatType.Channel = line.Substring(start, end - start);
                  chatType.Channel = char.ToUpper(chatType.Channel[0]) + chatType.Channel.Substring(1);
                }
                break;
              case 2:
                chatType.Channel = ChatChannels.SHOUT;
                break;
              case 3:
                chatType.Channel = ChatChannels.OOC;
                break;
              case 4:
                chatType.Channel = ChatChannels.AUCTION;
                break;
            }

            ProcessLine pline = new ProcessLine { Line = line, ActionPart = line.Substring(ACTION_PART_INDEX) };
            if (!CheckForPetLeader(pline))
            {
              CheckGuildTells(pline);
            }
          }
        }
        else
        {
          int start, end;
          chatType = new ChatType { SenderIsYou = true, Sender = "You", Line = line };
          switch(index)
          {
            case 0:
              chatType.Channel = ChatChannels.SAY;
              break;
            case 1:
              chatType.Channel = ChatChannels.TELL;

              start = ACTION_PART_INDEX + 9;
              if ((end = line.IndexOf(",", start, StringComparison.Ordinal)) > -1)
              {
                chatType.Receiver = line.Substring(start, end - start);
              }
              break;
            case 2:
              start = ACTION_PART_INDEX + 9;

              if (line.IndexOf("your party", start, 10, StringComparison.Ordinal) > -1)
              {
                chatType.Channel = ChatChannels.GROUP;
              }
              else if (line.IndexOf("your raid", start, 9, StringComparison.Ordinal) > -1)
              {
                chatType.Channel = ChatChannels.RAID;
              }
              else
              {
                if ((end = line.IndexOf(":", start, StringComparison.Ordinal)) > -1)
                {
                  chatType.Channel = line.Substring(start, end - start);
                  chatType.Channel = char.ToUpper(chatType.Channel[0]) + chatType.Channel.Substring(1);
                }
              }
              break;
            case 3:
              start = ACTION_PART_INDEX + 11;
              if (line.IndexOf("your guild", start, 10, StringComparison.Ordinal) > -1)
              {
                chatType.Channel = ChatChannels.GUILD;
              }
              else if (line.IndexOf("your fellowship", start, 15, StringComparison.Ordinal) > -1)
              {
                chatType.Channel = ChatChannels.FELLOWSHIP;
              }
              break;
            case 4:
              chatType.Channel = ChatChannels.SHOUT;
              break;
            case 5:
              chatType.Channel = ChatChannels.OOC; 
              break;
            case 6:
              chatType.Channel = ChatChannels.AUCTION;
              break;
          }

        }
      }
      catch (Exception ex)
      {
        LOG.Debug(ex);
      }

      return chatType;
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
