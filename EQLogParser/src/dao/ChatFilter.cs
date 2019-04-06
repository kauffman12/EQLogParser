using System;
using System.Collections.Generic;

namespace EQLogParser
{
  class ChatFilter
  {
    private readonly string Keyword;
    private readonly string To;
    private readonly string From;
    private readonly double StartDate = 0;
    private readonly double EndDate = 0;
    private readonly DateUtil DateUtil = new DateUtil();
    private readonly Dictionary<string, byte> ValidChannels = null;

    internal ChatFilter(List<string> channels = null, double startDate = 0,
      double endDate = 0, string to = null, string from = null, string keyword = null)
    {
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
      bool pass = false;
      string timeString = chatType.Line.Substring(1, 24);
      if (timeString != null)
      {
        double time = DateUtil.ParseDate(timeString, out double _);
        if (!double.IsNaN(time))
        {
          double endOfDay = EndDate + 86400;
          pass = (StartDate == 0 || time >= StartDate) && (EndDate == 0 || time < endOfDay) && PassFilter(chatType);
        }
      }

      return pass;
    }

    internal bool PassFilter(ChatType chatType)
    {
      bool passed = false;

      if (ValidChannels == null || (chatType.Channel != null && ValidChannels.ContainsKey(chatType.Channel)))
      {
        if (To == null || (chatType.Receiver != null && chatType.Receiver.IndexOf(To, StringComparison.OrdinalIgnoreCase) > -1))
        {
          if (From == null || (chatType.Sender != null && chatType.Sender.IndexOf(From, StringComparison.OrdinalIgnoreCase) > -1))
          {
            if (!DataManager.Instance.CheckNameForPet(chatType.Sender) && Helpers.IsPossiblePlayerNameWithServer(chatType.Sender))
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
      }

      return passed;
    }
  }
}
