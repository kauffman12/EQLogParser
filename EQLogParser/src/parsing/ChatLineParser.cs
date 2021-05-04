using System;
using System.Collections.Generic;
using System.Globalization;

namespace EQLogParser
{
  class ChatLineParser
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private static readonly DateUtil DateUtil = new DateUtil();

    private static readonly List<string> YouCriteria = new List<string>
    {
      "You say,", "You told ", "You tell ", "You say to ", "You shout,", "You say out of", "You auction,"
    };

    private static readonly List<string> OtherCriteria = new List<string>
    {
      " says,", " tells ", " shouts,", " says out of", " auctions,", " told you,"
    };

    internal static ChatType Process(LineData lineData)
    {
      var chatType = ParseChatType(lineData.Line);
      if (chatType != null && chatType.SenderIsYou == false && chatType.Sender != null)
      {
        if (chatType.Channel == ChatChannels.GUILD || chatType.Channel == ChatChannels.RAID || chatType.Channel == ChatChannels.FELLOWSHIP)
        {
          PlayerManager.Instance.AddVerifiedPlayer(chatType.Sender, DateUtil.ParseLogDate(lineData.Line, out _));
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
        int max = Math.Min(16, line.Length - LineParsing.ACTIONINDEX);
        int index = YouCriteria.FindIndex(criteria => line.IndexOf(criteria, LineParsing.ACTIONINDEX, max, StringComparison.Ordinal) > -1);

        if (line.Contains("out of character"))
        {
          if (true)
          {

          }
        }

        if (index < 0)
        {
          int criteriaIndex = -1;
          for (int i = 0; i < OtherCriteria.Count; i++)
          {
            int lastIndex = line.IndexOf("'", LineParsing.ACTIONINDEX, StringComparison.Ordinal);
            if (lastIndex > -1)
            {
              count = lastIndex - LineParsing.ACTIONINDEX;
              if (count > 0)
              {
                criteriaIndex = line.IndexOf(OtherCriteria[i], LineParsing.ACTIONINDEX, count, StringComparison.Ordinal);
                if (criteriaIndex > -1)
                {
                  index = i;
                  break;
                }
              }
            }
          }

          if (index < 0)
          {
            index = line.IndexOf(" ", LineParsing.ACTIONINDEX, StringComparison.Ordinal);
            if (index > -1 && index + 5 < line.Length)
            {
              if (line[index + 1] == '-' && line[index + 2] == '>')
              {
                int lastIndex = line.IndexOf(":", index + 4, StringComparison.Ordinal);
                if (lastIndex > -1)
                {
                  string sender = line.Substring(LineParsing.ACTIONINDEX, index - LineParsing.ACTIONINDEX);
                  string receiver = line.Substring(index + 4, lastIndex - index - 4);
                  chatType = new ChatType { Channel = ChatChannels.TELL, Sender = sender, Receiver = receiver, Line = line, AfterSenderIndex = lastIndex };

                  if (ConfigUtil.PlayerName == sender)
                  {
                    chatType.SenderIsYou = true;
                  }
                }
              }
            }
          }
          else if (index > -1 && criteriaIndex > -1)
          {
            int start, end;
            int senderLen = criteriaIndex - LineParsing.ACTIONINDEX;
            chatType = new ChatType { SenderIsYou = false, Sender = line.Substring(LineParsing.ACTIONINDEX, senderLen), AfterSenderIndex = criteriaIndex, Line = line };

            switch (index)
            {
              case 0:
                chatType.Channel = ChatChannels.SAY;
                break;
              case 1:
                start = criteriaIndex + 7;
                if (line.IndexOf("you, ", start, 5, StringComparison.Ordinal) > -1)
                {
                  chatType.Channel = ChatChannels.TELL;
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
                // check if it's an old cross server tell and not an NPC
                if (line.IndexOf(" told you,", criteriaIndex, 10, StringComparison.Ordinal) > -1 && chatType.Sender.IndexOf(".", StringComparison.Ordinal) > -1)
                {
                  chatType.Channel = ChatChannels.TELL;
                  chatType.Receiver = "You";
                }
                break;
            }
          }
        }
        else
        {
          int start, end;
          chatType = new ChatType { SenderIsYou = true, Sender = "You", AfterSenderIndex = LineParsing.ACTIONINDEX + 4, Line = line };
          switch (index)
          {
            case 0:
              chatType.Channel = ChatChannels.SAY;
              break;
            case 1:
              chatType.Channel = ChatChannels.TELL;

              start = LineParsing.ACTIONINDEX + 9;
              if ((end = line.IndexOf(",", start, StringComparison.Ordinal)) > -1)
              {
                chatType.Receiver = line.Substring(start, end - start);
              }
              break;
            case 2:
              start = LineParsing.ACTIONINDEX + 9;

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
              start = LineParsing.ACTIONINDEX + 11;
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
#pragma warning disable CA1031 // Do not catch general exception types
      catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
      {
        if (ex is ArgumentNullException || ex is NullReferenceException || ex is ArgumentOutOfRangeException || ex is ArgumentException)
        {
          LOG.Error(ex);
        }
      }

      return chatType;
    }
  }
  public static class ChatChannels
  {
    public const string AUCTION = "Auction";
    public const string SAY = "Say";
    public const string GUILD = "Guild";
    public const string FELLOWSHIP = "Fellowship";
    public const string TELL = "Tell";
    public const string SHOUT = "Shout";
    public const string GROUP = "Group";
    public const string RAID = "Raid";
    public const string OOC = "OOC";
  }
}
