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
    private readonly double StartDate = 0;
    private readonly double EndDate = 0;
    private readonly Dictionary<string, byte> ValidChannels = null;
    private readonly DateUtil DateUtil = new DateUtil();

    internal ChatFilter(string player, List<string> channels = null, double startDate = 0,
      double endDate = 0, string to = null, string from = null, string keyword = null)
    {
      if (player.Length > 0)
      {
        int index = player.IndexOf(".", StringComparison.Ordinal);
        if (index > -1)
        {
          Player = player.Substring(0, index);
        }
        else
        {
          Player = player;
        }
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
      double begin = DateUtil.ToDouble(year);
      double end = DateUtil.ToDouble(year.AddYears(1));

      return (StartDate == 0 || (StartDate < end)) && (EndDate == 0 || (EndDate >= begin));
    }

    internal bool DuringMonth(DateTime month)
    {
      double begin = DateUtil.ToDouble(month);
      double end = DateUtil.ToDouble(month.AddMonths(1));

      return (StartDate == 0 || (StartDate < end)) && (EndDate == 0 || (EndDate >= begin));
    }

    internal bool DuringDay(DateTime day)
    {
      double begin = DateUtil.ToDouble(day);
      double end = DateUtil.ToDouble(day.AddDays(1));

      return (StartDate == 0 || (StartDate < end)) && (EndDate == 0 || (EndDate >= begin));
    }

    internal bool PastLiveFilter(ChatType chatType)
    {
      double endOfDay = EndDate + 86400;
      var time = DateUtil.ParseDate(chatType.Line.Substring(1, 24));
      return (StartDate == 0 || time >= StartDate) && (EndDate == 0 || time < endOfDay) && PassFilter(chatType);
    }

    internal bool PassFilter(ChatType chatType)
    {
      bool passed = false;

      if (ValidChannels == null || (chatType.Channel != null && ValidChannels.ContainsKey(chatType.Channel)))
      {
        string receiver = chatType.Receiver == "You" ? Player : chatType.Receiver;
        string sender = chatType.Sender == "You" ? Player : chatType.Sender;
        bool receiverIsTo = receiver != null && To != null && receiver.IndexOf(To, StringComparison.OrdinalIgnoreCase) > -1;
        bool senderIsFrom = sender != null && From != null && sender.IndexOf(From, StringComparison.OrdinalIgnoreCase) > -1;
        bool receiverIsFrom = receiver != null && From != null && receiver.IndexOf(From, StringComparison.OrdinalIgnoreCase) > -1;
        bool senderIsTo = sender != null && To != null && sender.IndexOf(To, StringComparison.OrdinalIgnoreCase) > -1;

        if (((To == null || receiverIsTo) && (From == null || senderIsFrom)) || (senderIsTo && receiverIsFrom) || (To == From && (sender == Player && receiverIsTo || receiver == Player && senderIsFrom)))
        {
          if (!PlayerManager.Instance.IsVerifiedPet(chatType.Sender) && IsPossiblePlayerNameWithServer(chatType.Sender))
          {
            if (Keyword != null)
            {
              int afterSender = chatType.AfterSenderIndex >= 0 ? chatType.AfterSenderIndex : 0;
              int foundIndex = chatType.Line.IndexOf(Keyword, afterSender, StringComparison.OrdinalIgnoreCase);
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

      bool found = stop < 3 ? false : true;
      for (int i = 0; found != false && i < stop; i++)
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
