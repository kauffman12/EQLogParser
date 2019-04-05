using System;
using System.Collections.Generic;
using System.Globalization;

namespace EQLogParser
{
  class ChatLineParser
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private static readonly List<string> YouCriteria = new List<string>
    {
      "You say,", "You told ", "You tell ", "You say to ", "You shout,", "You say out of", "You auction,"
    };

    private static readonly List<string> OtherCriteria = new List<string>
    {
      " says,", " tells ", " shouts,", "says out of", " auctions,", " told you,"
    };

    internal static ChatType Process(string line)
    {
      var chatType = ParseChatType(line);
      if (chatType != null && chatType.SenderIsYou == false && chatType.Sender != null)
      {
        if (chatType.Channel == ChatChannels.GUILD || chatType.Channel == ChatChannels.RAID || chatType.Channel == ChatChannels.GROUP)
        {
          DataManager.Instance.UpdateVerifiedPlayers(chatType.Sender);
        }
      }

      return chatType;
    }

    internal static ChatType ParseChatType(string line)
    {
      ChatType chatType = null;

      try
      {
        int count;
        int max = Math.Min(16, line.Length - Parsing.ACTIONINDEX);
        int index = YouCriteria.FindIndex(criteria => line.IndexOf(criteria, Parsing.ACTIONINDEX, max, StringComparison.Ordinal) > -1);

        if (index < 0)
        {
          int criteriaIndex = -1;
          for (int i=0; i<OtherCriteria.Count; i++)
          {
            int lastIndex = line.IndexOf("'", Parsing.ACTIONINDEX, StringComparison.Ordinal);
            if (lastIndex > -1)
            {
              count = lastIndex - Parsing.ACTIONINDEX;
              if (count > 0)
              {
                criteriaIndex = line.IndexOf(OtherCriteria[i], Parsing.ACTIONINDEX, count, StringComparison.Ordinal);
                if (criteriaIndex > -1)
                {
                  index = i;
                  break;
                }
              }
            }
          }

          if (index > -1 && criteriaIndex > -1)
          {
            int start, end;
            int senderLen = criteriaIndex - Parsing.ACTIONINDEX;
            chatType = new ChatType { SenderIsYou = false, Sender = line.Substring(Parsing.ACTIONINDEX, senderLen), AfterSenderIndex = criteriaIndex, Line = line };

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
                  chatType.Channel = char.ToUpper(chatType.Channel[0], CultureInfo.CurrentCulture) + chatType.Channel.Substring(1).ToLower(CultureInfo.CurrentCulture);
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
              case 5:
                if (line.IndexOf(" told you,", criteriaIndex, 10, StringComparison.Ordinal) > -1)
                {
                  chatType.Channel = ChatChannels.TELL;
                  chatType.ReceiverIsYou = true;
                  chatType.Receiver = "You";
                }
                break;
            }
          }
        }
        else
        {
          int start, end;
          chatType = new ChatType { SenderIsYou = true, Sender = "You", AfterSenderIndex = Parsing.ACTIONINDEX + 4, Line = line };
          switch(index)
          {
            case 0:
              chatType.Channel = ChatChannels.SAY;
              break;
            case 1:
              chatType.Channel = ChatChannels.TELL;

              start = Parsing.ACTIONINDEX + 9;
              if ((end = line.IndexOf(",", start, StringComparison.Ordinal)) > -1)
              {
                chatType.Receiver = line.Substring(start, end - start);
              }
              break;
            case 2:
              start = Parsing.ACTIONINDEX + 9;

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
                  chatType.Channel = char.ToUpper(chatType.Channel[0], CultureInfo.CurrentCulture) + chatType.Channel.Substring(1).ToLower(CultureInfo.CurrentCulture);
                }
              }
              break;
            case 3:
              start = Parsing.ACTIONINDEX + 11;
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
  }
}
