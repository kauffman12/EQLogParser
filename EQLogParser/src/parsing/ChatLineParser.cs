using System;

namespace EQLogParser
{
  public static class ChatChannels
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

  public class ChatType
  {
    public const string You = "You";

    public ChatType()
    {
    }

    public ChatType(string channel, string sender, int start, string receiver = null)
    {
      Channel = channel;
      Sender = sender;
      TextStart = start;
      SenderIsYou = sender == You;
      Receiver = receiver;
    }

    public string Channel { get; set; }
    public string Sender { get; set; }
    public string Receiver { get; set; }
    public bool SenderIsYou { get; set; }
    public string Text { get; set; }
    public int TextStart { get; set; }
    public int KeywordStart { get; set; }
    public double BeginTime { get; set; }
  }

  public static class ChatLineParser
  {
    public static ChatType ParseChatType(string action)
    {
      if (!string.IsNullOrEmpty(action))
      {
        var span = action.AsSpan();
        return span.StartsWith("You ") ? CheckYouCriteria(span) : CheckOtherCriteria(span);
      }

      return null;
    }

    public static ChatType CheckOtherCriteria(ReadOnlySpan<char> span)
    {
      if (MatchAnyPlayer(span, out var sender) is var end && end == -1)
      {
        return null;
      }

      span = span[end..];
      if (span.StartsWith(" -> "))
      {
        span = span[" -> ".Length..];
        if (MatchAnyPlayer(span, out var receiver) is var end2 and > -1)
        {
          // "Test -> Test: hello"
          return new ChatType(ChatChannels.Tell, sender, 27 + end + end2 + " -> ".Length, receiver);
        }
      }
      else if (StartsWith(span, " auctions, ") is var start and > -1)
      {
        // Test auctions, 'hello'
        return new ChatType(ChatChannels.Auction, sender, 27 + end + " auctions, ".Length + start);
      }
      else if (span.StartsWith(" says"))
      {
        span = span[" says".Length..];
        if (StartsWith(span, ", ") is var start2 and > -1)
        {
          // Test says, 'hello'
          return new ChatType(ChatChannels.Say, sender, 27 + end + " says, ".Length + start2);
        }

        if (StartsWith(span, " out of character, ") is var start3 and > -1)
        {
          // Test says out of character, 'hello'
          return new ChatType(ChatChannels.Ooc, sender, 27 + end + " says out of character, ".Length + start3);
        }

        // EMU support
        if (span.StartsWith(" 'My leader is"))
        {
          // Test says 'My leader is Test'
          return new ChatType(ChatChannels.Say, sender, 27 + end + " says '".Length);
        }
      }
      else if (span.StartsWith(" tells "))
      {
        span = span[" tells ".Length..];
        if (span.StartsWith("the "))
        {
          span = span["the ".Length..];
          if (StartsWith(span, "fellowship, ") is var start4 and > -1)
          {
            // Test tells the fellowship, 'hello'
            return new ChatType(ChatChannels.Fellowship, sender, 27 + end + " tells the fellowship, ".Length + start4);
          }

          if (StartsWith(span, "group, ") is var start5 and > -1)
          {
            // Test tells the group, 'hello'
            return new ChatType(ChatChannels.Group, sender, 27 + end + " tells the group, ".Length + start5);
          }

          if (StartsWith(span, "guild, ") is var start6 and > -1)
          {
            // Test tells the guild, 'hello'
            return new ChatType(ChatChannels.Guild, sender, 27 + end + " tells the guild, ".Length + start6);
          }

          if (StartsWith(span, "raid, ") is var start7 and > -1)
          {
            // Test tells the raid, 'hello'
            return new ChatType(ChatChannels.Raid, sender, 27 + end + " tells the raid, ".Length + start7);
          }
        }
        else if (StartsWith(span, "you, ") is var start8 and > -1)
        {
          // Test tells you, 'hello'
          return new ChatType(ChatChannels.Tell, sender, 27 + end + " tells you, ".Length + start8, ChatType.You);
        }
        else if (MatchTellChannel(span, out var channel) is var start9 and > -1)
        {
          // Test tells test.test:34, 'hello'
          return new ChatType(channel, sender, 27 + end + " tells ".Length + start9);
        }
      }
      else if (StartsWith(span, " told you, ") is var start10 and > -1)
      {
        // Test.test told you, 'hello'
        return new ChatType(ChatChannels.Tell, sender, 27 + end + " told you, ".Length + start10);
      }
      else if (StartsWith(span, " shouts, ") is var start11 and > -1)
      {
        // Test shouts, 'hello'
        return new ChatType(ChatChannels.Shout, sender, 27 + end + " shouts, ".Length + start11);
      }

      return null;
    }

    public static ChatType CheckYouCriteria(ReadOnlySpan<char> span)
    {
      span = span["You ".Length..];
      if (StartsWith(span, "auction, ") is var start and > -1)
      {
        // You auction, 'hello'
        return new ChatType(ChatChannels.Auction, ChatType.You, 27 + "You auction, ".Length + start);
      }

      if (span.StartsWith("say"))
      {
        span = span["say".Length..];
        if (StartsWith(span, ", ") is var start2 and > -1)
        {
          // You say, 'hello'
          return new ChatType(ChatChannels.Say, ChatType.You, 27 + "You say, ".Length + start2);
        }

        if (span.StartsWith(" to your "))
        {
          span = span[" to your ".Length..];
          if (StartsWith(span, "fellowship, ") is var start4 and > -1)
          {
            // You say to your fellowship, 'hello'
            return new ChatType(ChatChannels.Fellowship, ChatType.You, 27 + "You say to your fellowship, ".Length + start4);
          }

          if (StartsWith(span, "guild, ") is var start3 and > -1)
          {
            // You say to your guild, 'hello'
            return new ChatType(ChatChannels.Guild, ChatType.You, 27 + "You say to your guild, ".Length + start3);
          }
        }
        else if (StartsWith(span, " out of character, ") is var start5 and > -1)
        {
          // You say out of character, 'hello'
          return new ChatType(ChatChannels.Ooc, ChatType.You, 27 + "You say out of character, ".Length + start5);
        }
      }
      else if (StartsWith(span, "shout, ") is var start6 and > -1)
      {
        // You shout, 'hello'
        return new ChatType(ChatChannels.Shout, ChatType.You, 27 + "You shout, ".Length + start6);
      }
      else if (span.StartsWith("tell "))
      {
        span = span["tell ".Length..];
        if (MatchTellChannel(span, out var channel) is var start7 and > -1)
        {
          // You tell test.test:34, 'hello'
          return new ChatType(channel, ChatType.You, 27 + "You tell ".Length + start7);
        }

        if (span.StartsWith("your "))
        {
          span = span["your ".Length..];
          if (StartsWith(span, "party, ") is var start8 and > -1)
          {
            // You tell your party, 'hello'
            return new ChatType(ChatChannels.Group, ChatType.You, 27 + "You tell your party, ".Length + start8);
          }

          if (StartsWith(span, "raid, ") is var start9 and > -1)
          {
            // You tell your raid, 'hello'
            return new ChatType(ChatChannels.Raid, ChatType.You, 27 + "You tell your raid, ".Length + start9);
          }
        }
      }
      else if (span.StartsWith("told "))
      {
        span = span["told ".Length..];
        if (MatchTellPlayer(span, out var receiver) is var start9 and > -1)
        {
          // You told test.test, 'hello'
          return new ChatType(ChatChannels.Tell, ChatType.You, 27 + "You told ".Length + start9, receiver);
        }
      }

      return null;
    }

    private static int StartsWith(ReadOnlySpan<char> span, string test)
    {
      if (span.StartsWith(test) && span[test.Length..].IndexOf("'") is var found and > -1)
      {
        return span.Length > found + 1 ? found + 1 : found;
      }

      return -1;
    }

    private static int MatchAnyPlayer(ReadOnlySpan<char> span, out string receiver)
    {
      receiver = string.Empty;

      var dotIndex = -1;
      for (var i = 0; i < span.Length; i++)
      {
        if (span[i] == '.')
        {
          if (dotIndex != -1)
          {
            return -1;
          }

          dotIndex = i + 1;
          continue;
        }

        if (span[i] == ' ' || span[i] == ':')
        {
          receiver = dotIndex == -1 ? span[..i].ToString() : span[dotIndex..i].ToString();
          return i;
        }
      }

      return -1;
    }

    private static int MatchTellPlayer(ReadOnlySpan<char> span, out string receiver)
    {
      receiver = string.Empty;

      var dotIndex = -1;
      var wsIndex = -1;
      for (var i = 0; i < span.Length; i++)
      {
        if (span[i] == '.')
        {
          if (dotIndex != -1)
          {
            return -1;
          }

          dotIndex = i + 1;
          continue;
        }

        if (span[i] == '\'')
        {
          receiver = dotIndex == -1 ? span[..wsIndex].ToString() : span[dotIndex..wsIndex].ToString();
          return span.Length > i + 1 ? i + 1 : i;
        }

        if (span[i] == ':')
        {
          receiver = dotIndex == -1 ? span[..wsIndex].ToString() : span[dotIndex..wsIndex].ToString();
          return span.Length > i + 1 ? i + 1 : i;
        }

        if ((wsIndex == -1 && char.IsWhiteSpace(span[i])) || span[i] == ',')
        {
          wsIndex = i;
        }
      }

      return -1;
    }

    private static int MatchTellChannel(ReadOnlySpan<char> span, out string channel)
    {
      channel = string.Empty;
      var colonIndex = -1;
      var digitsCount = 0;

      for (var i = 0; i < span.Length; i++)
      {
        if (span[i] == ':')
        {
          colonIndex = i;
          continue;
        }

        if (colonIndex != -1 || span[i] == ',')
        {
          if (char.IsDigit(span[i]))
          {
            digitsCount++;
            if (digitsCount > 2)
            {
              return -1;
            }

            continue;
          }

          // check that there is at least some data afterwards
          if (digitsCount is 0 or 1 or 2 && span.Length > i + 2 && span.IndexOf("'") is var found and > -1)
          {
            var stop = colonIndex == -1 ? i : colonIndex;
            var arr = new char[stop];
            for (var j = 0; j < stop; j++)
            {
              arr[j] = char.ToLower(span[j]);
            }

            channel = new string(arr);
            return span.Length > found + 1 ? found + 1 : found;
          }

          return -1;
        }

        if (!char.IsLetterOrDigit(span[i]) && span[i] != '.' && span[i] != ',')
        {
          return -1;
        }
      }

      return -1;
    }
  }
}
