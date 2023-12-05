using System;
using System.Collections.Generic;

namespace EQLogParser
{
  class ChatFilter
  {
    private readonly string _player;
    private readonly string _keyword;
    private readonly string _to;
    private readonly string _from;
    private readonly double _startDate;
    private readonly double _endDate;
    private readonly Dictionary<string, byte> _validChannels;

    internal ChatFilter(string player, List<string> channels = null, double startDate = 0,
      double endDate = 0, string to = null, string from = null, string keyword = null)
    {
      if (player.Length > 0)
      {
        var index = player.IndexOf(".", StringComparison.Ordinal);
        _player = (index > -1) ? player[..index] : player;
      }

      if (channels != null)
      {
        _validChannels = new Dictionary<string, byte>();
        channels.ForEach(chan => _validChannels[chan] = 1);
      }

      _startDate = startDate;
      _endDate = endDate;
      _keyword = keyword;
      _from = from;
      _to = to;
    }

    internal bool DuringYear(DateTime year)
    {
      var begin = DateUtil.ToDouble(year);
      var end = DateUtil.ToDouble(year.AddYears(1));
      return (_startDate == 0 || (_startDate < end)) && (_endDate == 0 || (_endDate >= begin));
    }

    internal bool DuringMonth(DateTime month)
    {
      var begin = DateUtil.ToDouble(month);
      var end = DateUtil.ToDouble(month.AddMonths(1));
      return (_startDate == 0 || (_startDate < end)) && (_endDate == 0 || (_endDate >= begin));
    }

    internal bool DuringDay(DateTime day)
    {
      var begin = DateUtil.ToDouble(day);
      var end = DateUtil.ToDouble(day.AddDays(1));
      return (_startDate == 0 || (_startDate < end)) && (_endDate == 0 || (_endDate >= begin));
    }

    internal bool PassFilter(ChatType chatType)
    {
      var passed = false;

      if (_validChannels == null || (chatType.Channel != null && _validChannels.ContainsKey(chatType.Channel)))
      {
        var receiver = chatType.Receiver;
        var sender = chatType.Sender;
        var receiverIsTo = receiver != null && _to != null && receiver.IndexOf(_to, StringComparison.OrdinalIgnoreCase) > -1;
        var senderIsFrom = sender != null && _from != null && sender.IndexOf(_from, StringComparison.OrdinalIgnoreCase) > -1;
        var receiverIsFrom = receiver != null && _from != null && receiver.IndexOf(_from, StringComparison.OrdinalIgnoreCase) > -1;
        var senderIsTo = sender != null && _to != null && sender.IndexOf(_to, StringComparison.OrdinalIgnoreCase) > -1;

        if (((_to == null || receiverIsTo) && (_from == null || senderIsFrom)) || (senderIsTo && receiverIsFrom) || (_to == _from && ((sender == _player && receiverIsTo) || (receiver == _player && senderIsFrom))))
        {
          if (chatType.SenderIsYou || (!PlayerManager.Instance.IsVerifiedPet(chatType.Sender) && IsPossiblePlayerNameWithServer(chatType.Sender)))
          {
            if (_keyword != null)
            {
              var afterSender = chatType.TextStart >= 0 ? chatType.TextStart : 0;
              var foundIndex = chatType.Text.IndexOf(_keyword, afterSender, StringComparison.OrdinalIgnoreCase);
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
