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
    private readonly Dictionary<string, byte> ValidChannels = null;
    private readonly DateUtil DateUtil = new DateUtil();

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

    internal bool PassFilter(ChatType chatType)
    {
      bool passed = false;

      if (ValidChannels == null || (chatType.Channel != null && ValidChannels.ContainsKey(chatType.Channel)))
      {
        if ((StartDate == 0 || ParseDate(chatType.Line) >= StartDate) && (EndDate == 0 || ParseDate(chatType.Line) < EndDate))
        {
          if (To == null || (chatType.Receiver != null && chatType.Receiver.IndexOf(To, StringComparison.OrdinalIgnoreCase) > -1))
          {
            if (From == null || (chatType.Sender != null && chatType.Sender.IndexOf(From, StringComparison.OrdinalIgnoreCase) > -1))
            {
              if (!DataManager.Instance.CheckNameForPet(chatType.Sender) && Helpers.IsPossiblePlayerName(chatType.Sender))
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
      }

      return passed;
    }

    private double ParseDate(string line)
    {
      string timeString = line.Substring(1, 24);
      double parsed = DateUtil.ParseDate(timeString, out _);
      return double.IsNaN(parsed) ? 0 : parsed;
    }
  }
}
