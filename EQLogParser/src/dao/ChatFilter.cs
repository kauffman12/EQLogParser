using System;
using System.Collections.Generic;

namespace EQLogParser
{
  class ChatFilter
  {
    private readonly string Player;
    private readonly string Keyword;
    private readonly string To;
    private readonly string From;
    private readonly double StartDate;
    private readonly double EndDate;
    private readonly Dictionary<string, byte> ValidChannels;
    private readonly DateUtil DateUtil = new();

    internal ChatFilter(string player, List<string> channels = null, double startDate = 0,
      double endDate = 0, string to = null, string from = null, string keyword = null)
    {
      if (player.Length > 0)
      {
        var index = player.IndexOf(".", StringComparison.Ordinal);
        Player = (index > -1) ? player[..index] : player;
      }

      if (channels != null)
      {
        ValidChannels = new Dictionary<string, byte>();
        channels.ForEach(chan => ValidChannels[chan] = 1);
      }

      StartDate = startDate;
      EndDate = endDate;
      Keyword = keyword;
      From = from;
      To = to;
    }

    internal bool DuringYear(DateTime year)
    {
      var begin = DateUtil.ToDouble(year);
      var end = DateUtil.ToDouble(year.AddYears(1));
      return (StartDate == 0 || (StartDate < end)) && (EndDate == 0 || (EndDate >= begin));
    }

    internal bool DuringMonth(DateTime month)
    {
      var begin = DateUtil.ToDouble(month);
      var end = DateUtil.ToDouble(month.AddMonths(1));
      return (StartDate == 0 || (StartDate < end)) && (EndDate == 0 || (EndDate >= begin));
    }

    internal bool DuringDay(DateTime day)
    {
      var begin = DateUtil.ToDouble(day);
      var end = DateUtil.ToDouble(day.AddDays(1));
      return (StartDate == 0 || (StartDate < end)) && (EndDate == 0 || (EndDate >= begin));
    }

    internal bool PassFilter(ChatType chatType)
    {
      var passed = false;

      if (ValidChannels == null || (chatType.Channel != null && ValidChannels.ContainsKey(chatType.Channel)))
      {
        var receiver = chatType.Receiver;
        var sender = chatType.Sender;
        var receiverIsTo = receiver != null && To != null && receiver.IndexOf(To, StringComparison.OrdinalIgnoreCase) > -1;
        var senderIsFrom = sender != null && From != null && sender.IndexOf(From, StringComparison.OrdinalIgnoreCase) > -1;
        var receiverIsFrom = receiver != null && From != null && receiver.IndexOf(From, StringComparison.OrdinalIgnoreCase) > -1;
        var senderIsTo = sender != null && To != null && sender.IndexOf(To, StringComparison.OrdinalIgnoreCase) > -1;

        if (((To == null || receiverIsTo) && (From == null || senderIsFrom)) || (senderIsTo && receiverIsFrom) || (To == From && ((sender == Player && receiverIsTo) || (receiver == Player && senderIsFrom))))
        {
          if (chatType.SenderIsYou || (!PlayerManager.Instance.IsVerifiedPet(chatType.Sender) && IsPossiblePlayerNameWithServer(chatType.Sender)))
          {
            if (Keyword != null)
            {
              var afterSender = chatType.TextStart >= 0 ? chatType.TextStart : 0;
              var foundIndex = chatType.Text.IndexOf(Keyword, afterSender, StringComparison.OrdinalIgnoreCase);
              if (foundIndex > -1)
              {
                passed = true;
                chatType.KeywordStart = foundIndex;
              }
            }
            else
            {
              passed = true;
            }
          }
        }
      }

      return passed;
    }

    internal static bool IsPossiblePlayerNameWithServer(string part, int stop = -1)
    {
      if (stop == -1)
      {
        stop = part.Length;
      }

      var found = stop >= 3;
      for (var i = 0; found && i < stop; i++)
      {
        if (!char.IsLetter(part, i) && part[i] != '.')
        {
          found = false;
          break;
        }
      }

      return found;
    }
  }
}
