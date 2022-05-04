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
      " says,", " tells ", " shouts,", " says out of", " auctions,", " told you,"
    };

    internal static ChatType Process(LineData lineData, string fullLine)
    {
      var chatType = ParseChatType(lineData.Action);
      if (chatType != null)
      {
        chatType.BeginTime = lineData.BeginTime;
        chatType.Text = fullLine; // workaround for now?

        if (chatType.SenderIsYou == false && chatType.Sender != null)
        {
          if (chatType.Channel == ChatChannels.Guild || chatType.Channel == ChatChannels.Raid || chatType.Channel == ChatChannels.Fellowship)
          {
            PlayerManager.Instance.AddVerifiedPlayer(chatType.Sender, lineData.BeginTime);
          }
        }
      }

      return chatType;
    }

    internal static ChatType ParseChatType(string action)
    {
      ChatType chatType = null;

      if (!string.IsNullOrEmpty(action) && action.Length > 3)
      {
        try
        {
          int index = YouCriteria.FindIndex(criteria => action.IndexOf(criteria, StringComparison.Ordinal) > -1);

          if (index < 0)
          {
            int criteriaIndex = -1;
            for (int i = 0; i < OtherCriteria.Count; i++)
            {
              int lastIndex = action.IndexOf("'", StringComparison.Ordinal);
              if (lastIndex > -1)
              {
                criteriaIndex = action.IndexOf(OtherCriteria[i], 0, lastIndex, StringComparison.Ordinal);
                if (criteriaIndex > -1)
                {
                  index = i;
                  break;
                }
              }
            }

            if (index < 0)
            {
              index = action.IndexOf(" ", StringComparison.Ordinal);
              if (index > -1 && index + 5 < action.Length)
              {
                if (action[index + 1] == '-' && action[index + 2] == '>' && action.Length >= (index + 4))
                {
                  int lastIndex = action.IndexOf(":", index + 4, StringComparison.Ordinal);
                  if (lastIndex > -1)
                  {
                    string sender = action.Substring(index);
                    string receiver = action.Substring(index + 4, lastIndex - index - 4);
                    chatType = new ChatType { Channel = ChatChannels.Tell, Sender = sender, Receiver = receiver, AfterSenderIndex = lastIndex };

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
              chatType = new ChatType { SenderIsYou = false, Sender = action.Substring(0, criteriaIndex), AfterSenderIndex = criteriaIndex };

              switch (index)
              {
                case 0:
                  chatType.Channel = ChatChannels.Say;
                  break;
                case 1:
                  start = criteriaIndex + 7;
                  if (action.Length >= (start + 5) && action.IndexOf("you, ", start, 5, StringComparison.Ordinal) > -1)
                  {
                    chatType.Channel = ChatChannels.Tell;
                    chatType.Receiver = "You";
                  }
                  else if (action.Length >= (start + 9) && action.IndexOf("the guild", start, 9, StringComparison.Ordinal) > -1)
                  {
                    chatType.Channel = ChatChannels.Guild;
                  }
                  else if (action.Length >= (start + 9) && action.IndexOf("the group", start, 9, StringComparison.Ordinal) > -1)
                  {
                    chatType.Channel = ChatChannels.Group;
                  }
                  else if (action.Length >= (start + 8) && action.IndexOf("the raid", start, 8, StringComparison.Ordinal) > -1)
                  {
                    chatType.Channel = ChatChannels.Raid;
                  }
                  else if (action.Length >= (start + 14) && action.IndexOf("the fellowship", start, 14, StringComparison.Ordinal) > -1)
                  {
                    chatType.Channel = ChatChannels.Fellowship;
                  }
                  else if ((end = action.IndexOf(":", start + 1, StringComparison.Ordinal)) > -1)
                  {
                    chatType.Channel = action.Substring(start, end - start);
                    chatType.Channel = char.ToUpper(chatType.Channel[0], CultureInfo.CurrentCulture) + 
                      chatType.Channel.Substring(1).ToLower(CultureInfo.CurrentCulture);
                  }
                  break;
                case 2:
                  chatType.Channel = ChatChannels.Shout;
                  break;
                case 3:
                  chatType.Channel = ChatChannels.Ooc;
                  break;
                case 4:
                  chatType.Channel = ChatChannels.Auction;
                  break;
                case 5:
                  // check if it's an old cross server tell and not an NPC
                  if (action.Length >= (criteriaIndex + 10) && action.IndexOf(" told you,", criteriaIndex, 10, StringComparison.Ordinal) > -1 && 
                    chatType.Sender.IndexOf(".", StringComparison.Ordinal) > -1)
                  {
                    chatType.Channel = ChatChannels.Tell;
                    chatType.Receiver = "You";
                  }
                  break;
              }
            }
          }
          else
          {
            int start, end;
            chatType = new ChatType { SenderIsYou = true, Sender = "You", AfterSenderIndex = 4 };
            switch (index)
            {
              case 0:
                chatType.Channel = ChatChannels.Say;
                break;
              case 1:
                chatType.Channel = ChatChannels.Tell;

                start = 9;
                if ((end = action.IndexOf(",", start, StringComparison.Ordinal)) > -1)
                {
                  chatType.Receiver = action.Substring(start, end - start);
                }
                break;
              case 2:
                start = 9;

                if (action.Length >= (start + 10) && action.IndexOf("your party", start, 10, StringComparison.Ordinal) > -1)
                {
                  chatType.Channel = ChatChannels.Group;
                }
                else if (action.Length >= (start + 9) && action.IndexOf("your raid", start, 9, StringComparison.Ordinal) > -1)
                {
                  chatType.Channel = ChatChannels.Raid;
                }
                else
                {
                  if ((end = action.IndexOf(":", start, StringComparison.Ordinal)) > -1)
                  {
                    chatType.Channel = action.Substring(start, end - start);
                    chatType.Channel = char.ToUpper(chatType.Channel[0], CultureInfo.CurrentCulture) + chatType.Channel.Substring(1).ToLower(CultureInfo.CurrentCulture);
                  }
                }
                break;
              case 3:
                start = 11;
                if (action.Length >= (start + 10) && action.IndexOf("your guild", start, 10, StringComparison.Ordinal) > -1)
                {
                  chatType.Channel = ChatChannels.Guild;
                }
                else if (action.Length >= (start + 15) && action.IndexOf("your fellowship", start, 15, StringComparison.Ordinal) > -1)
                {
                  chatType.Channel = ChatChannels.Fellowship;
                }
                break;
              case 4:
                chatType.Channel = ChatChannels.Shout;
                break;
              case 5:
                chatType.Channel = ChatChannels.Ooc;
                break;
              case 6:
                chatType.Channel = ChatChannels.Auction;
                break;
            }
          }
        }
        catch (Exception ex)
        {
          LOG.Debug(ex);
        }
      }

      return chatType;
    }
  }
  internal static class ChatChannels
  {
    public const string Auction = "Auction";
    public const string Say = "Say";
    public const string Guild = "Guild";
    public const string Fellowship = "Fellowship";
    public const string Tell = "Tell";
    public const string Shout = "Shout";
    public const string Group = "Group";
    public const string Raid = "Raid";
    public const string Ooc = "OOC";
  }
}
