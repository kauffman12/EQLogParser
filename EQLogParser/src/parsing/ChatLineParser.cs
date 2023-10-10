namespace EQLogParser
{
  static class ChatLineParser
  {
    internal static ChatType ParseChatType(string action, double beginTime = double.NaN)
    {
      if (!string.IsNullOrEmpty(action))
      {
        return action.StartsWith("You ") ? CheckYouCriteria(action) : CheckOtherCriteria(action, beginTime);
      }

      return null;
    }

    internal static ChatType CheckYouCriteria(string action)
    {
      ChatType chatType = null;
      var you = "You";

      if (action.Length > 7 && action.IndexOf("say", 4, 3) == 4)
      {
        // "You say,"
        if (action.Length > 9 && action[7] == ',')
        {
          chatType = new ChatType { Channel = ChatChannels.Say, SenderIsYou = true, Sender = you, TextStart = 9 };
        }
        // "You say to "
        else if (action.Length > 23 && action.IndexOf(" to your ", 7, 9) == 7)
        {
          if (action.IndexOf("guild, ", 16, 7) == 16)
          {
            chatType = new ChatType
            {
              Channel = ChatChannels.Guild,
              SenderIsYou = true,
              Sender = you,
              TextStart = 23
            };
          }
          else if (action.Length > 27 && action.IndexOf("fellowship, ", 16, 12) == 16)
          {
            chatType = new ChatType
            {
              Channel = ChatChannels.Fellowship,
              SenderIsYou = true,
              Sender = you,
              TextStart = 28
            };
          }
        }
        // "You say out of"
        else if (action.Length > 14 && action.IndexOf(" out of character, ", 7, 19) == 7)
        {
          chatType = new ChatType { Channel = ChatChannels.Ooc, SenderIsYou = true, Sender = you, TextStart = 26 };
        }
      }
      else if (action.Length > 10 && action[3] == ' ')
      {
        // "You told "
        if (action.IndexOf("told ", 4, 5) == 4)
        {
          // start at 11 since names have to be at least a few characters
          if (action.IndexOf(",", 11) is int end && end > -1)
          {
            chatType = new ChatType
            {
              Channel = ChatChannels.Tell,
              SenderIsYou = true,
              Sender = you,
              TextStart = end + 2,
              Receiver = action.Substring(9, end - 9)
            };
          }
        }
        // "You tell "
        else if (action.IndexOf("tell ", 4, 5) == 4)
        {
          if (action.Length > 20 && action.IndexOf("your ", 9, 5) == 9)
          {
            if (action.IndexOf("party, ", 14, 7) == 14)
            {
              chatType = new ChatType { Channel = ChatChannels.Group, SenderIsYou = true, Sender = you, TextStart = 21 };
            }
            else if (action.IndexOf("raid, ", 14, 6) == 14)
            {
              chatType = new ChatType { Channel = ChatChannels.Raid, SenderIsYou = true, Sender = you, TextStart = 20 };
            }
          }
          else if (action.IndexOf(":", 11) is int end && end > -1)
          {
            if (action.Length > end + 3 && (action[end + 2] == ',' || action[end + 3] == ','))
            {
              chatType = new ChatType
              {
                SenderIsYou = true,
                Sender = you,
                TextStart = end + 3,
                Channel = action.Substring(9, end - 9).ToLower()
              };
            }
          }
        }
        // "You shout,"
        else if (action.IndexOf("shout, ", 4, 7) == 4)
        {
          chatType = new ChatType { Channel = ChatChannels.Shout, SenderIsYou = true, Sender = you, TextStart = 10 };
        }
        // "You auction,"
        else if (action.Length > 12 && action.IndexOf("auction, ", 4, 9) == 4)
        {
          chatType = new ChatType { Channel = ChatChannels.Auction, SenderIsYou = true, Sender = you, TextStart = 12 };
        }
      }

      return chatType;
    }

    internal static ChatType CheckOtherCriteria(string action, double beginTime = double.NaN)
    {
      ChatType chatType = null;
      var you = "You";

      // check if line starts with what looks like a player name followed by a space
      // ignore NPC like names entirely
      var end1 = PlayerManager.FindPossiblePlayerName(action, out var isCrossServer, 0, -1, ' ');

      if (end1 > -1 && action.Length > (end1 + 5))
      {
        // "Kant -> Kazint:
        if (action.IndexOf("-> ", end1 + 1, 3) == (end1 + 1))
        {
          var end2 = PlayerManager.FindPossiblePlayerName(action, out var _, end1 + 4, -1, ':');
          if (end2 > -1)
          {
            chatType = new ChatType
            {
              Channel = ChatChannels.Tell,
              TextStart = end1 + 1,
              Sender = action.Substring(0, end1)
            };
            chatType.SenderIsYou = you == chatType.Sender;
            chatType.Receiver = action.Substring(end1 + 4, end2 - (end1 + 4));
          }
        }
        else if (action.IndexOf("says", end1 + 1, 4) == (end1 + 1))
        {
          // Kizant says, 
          if (action.Length > (end1 + 7) && (action[end1 + 5] == ',' || action[end1 + 6] == ' '))
          {
            chatType = new ChatType
            {
              Channel = ChatChannels.Say,
              SenderIsYou = false,
              TextStart = end1 + 7,
              Sender = action.Substring(0, end1)
            };

            if (!double.IsNaN(beginTime))
            {
              CheckPetLeader(action, end1, beginTime, chatType.Sender);
            }
          }
          // Kizant says out of character,
          else if (action.Length > (end1 + 24) && action.IndexOf(" out of character, ", end1 + 5, 19) == (end1 + 5))
          {
            chatType = new ChatType
            {
              Channel = ChatChannels.Ooc,
              SenderIsYou = false,
              TextStart = end1 + 24,
              Sender = action.Substring(0, end1)
            };
          }
          else if (!double.IsNaN(beginTime))
          {
            // EQEMU has pet leader say without the comma
            CheckPetLeader(action, end1, beginTime);
          }
        }
        else if (action.Length > (end1 + 9) && action.IndexOf("tells ", end1 + 1, 6) == (end1 + 1))
        {
          // Kizant tells you,
          if (action.Length > (end1 + 12) && action.IndexOf("you, ", end1 + 7, 5) == (end1 + 7))
          {
            chatType = new ChatType
            {
              Channel = ChatChannels.Tell,
              SenderIsYou = false,
              Receiver = you,
              TextStart = end1 + 12,
              Sender = action.Substring(0, end1)
            };
          }
          else if (action.Length > (end1 + 17) && action.IndexOf("the ", end1 + 7, 4) == end1 + 7)
          {
            // Kizant tells the raid,
            if (action.IndexOf("raid, ", end1 + 11, 6) == end1 + 11)
            {
              chatType = new ChatType
              {
                Channel = ChatChannels.Raid,
                SenderIsYou = false,
                TextStart = end1 + 17,
                Sender = action.Substring(0, end1)
              };
            }
            // Kizant tells the group,
            else if (action.Length > (end1 + 18) && action.IndexOf("group, ", end1 + 11, 7) == end1 + 11)
            {
              chatType = new ChatType
              {
                Channel = ChatChannels.Group,
                SenderIsYou = false,
                TextStart = end1 + 18,
                Sender = action.Substring(0, end1)
              };
            }
            // Kizant tells the guild,
            else if (action.Length > (end1 + 18) && action.IndexOf("guild, ", end1 + 11, 7) == end1 + 11)
            {
              chatType = new ChatType
              {
                Channel = ChatChannels.Guild,
                SenderIsYou = false,
                TextStart = end1 + 18,
                Sender = action.Substring(0, end1)
              };
            }
            // Kizant tells the fellowship,
            else if (action.Length > (end1 + 23) && action.IndexOf("fellowship, ", end1 + 11, 12) == end1 + 11)
            {
              chatType = new ChatType
              {
                Channel = ChatChannels.Fellowship,
                SenderIsYou = false,
                TextStart = end1 + 23,
                Sender = action.Substring(0, end1)
              };
            }
          }
          // Kizant tells General:1,
          else if (action.IndexOf(":", end1 + 7) is int end2 && end2 > -1)
          {
            if (action.Length > end2 + 3 && (action[end2 + 2] == ',' || action[end2 + 3] == ','))
            {
              chatType = new ChatType
              {
                SenderIsYou = false,
                Sender = action.Substring(0, end1),
                TextStart = end2 + 3,
                Channel = action.Substring(end1 + 7, end2 - (end1 + 7)).ToLower()
              };
            }
          }
        }
        // Kizant told you,
        else if (action.Length > (end1 + 11) && action.IndexOf("told you, ", end1 + 1, 10) == (end1 + 1))
        {
          chatType = new ChatType
          {
            Channel = ChatChannels.Tell,
            SenderIsYou = false,
            Receiver = you,
            TextStart = end1 + 11,
            Sender = action.Substring(0, end1)
          };
        }
        // Kizant shouts,
        else if (action.Length > (end1 + 9) && action.IndexOf("shouts, ", end1 + 1, 8) == (end1 + 1))
        {
          chatType = new ChatType
          {
            Channel = ChatChannels.Shout,
            SenderIsYou = false,
            TextStart = end1 + 9,
            Sender = action.Substring(0, end1)
          };
        }
        // Kizant auctions,
        else if (action.Length > (end1 + 11) && action.IndexOf("auctions, ", end1 + 1, 10) == (end1 + 1))
        {
          chatType = new ChatType
          {
            Channel = ChatChannels.Auction,
            SenderIsYou = false,
            TextStart = end1 + 11,
            Sender = action.Substring(0, end1)
          };
        }

        if (chatType != null && isCrossServer && chatType.Sender.Contains("."))
        {
          chatType.Sender = chatType.Sender.Split('.')[1];
        }
      }

      return chatType;
    }

    private static void CheckPetLeader(string action, int petEnd, double beginTime, string pet = null)
    {
      if (!double.IsNaN(beginTime) && action.Length > (petEnd + 15))
      {
        var petLeaderIndex = action.IndexOf("'My leader is ", petEnd + 6);
        if (petLeaderIndex > -1)
        {
          if (string.IsNullOrEmpty(pet))
          {
            pet = action.Substring(0, petEnd);
          }

          if (!PlayerManager.Instance.IsVerifiedPlayer(pet)) // thanks idiots for this
          {
            var period = action.IndexOf(".", petLeaderIndex + 16);
            if (period > -1)
            {
              var owner = action.Substring(petLeaderIndex + 14, period - (petLeaderIndex + 14));
              PlayerManager.Instance.AddVerifiedPlayer(owner, beginTime);
              PlayerManager.Instance.AddVerifiedPet(pet);
              PlayerManager.Instance.AddPetToPlayer(pet, owner);
            }
          }
        }
      }
    }
  }

  internal static class ChatChannels
  {
    public const string Auction = "auction";
    public const string Say = "say";
    public const string Guild = "guild";
    public const string Fellowship = "fellowship";
    public const string Tell = "tell";
    public const string Shout = "shout";
    public const string Group = "group";
    public const string Raid = "raid";
    public const string Ooc = "ooc";
  }
}
