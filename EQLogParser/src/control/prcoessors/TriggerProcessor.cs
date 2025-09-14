using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace EQLogParser
{
  internal partial class TriggerProcessor : ILogProcessor
  {
    public const string CounterCode = "{counter}";
    public const string RepeatedCode = "{repeated}";
    public const string LogTimeCode = "{logtime}";
    public const string NullCode = "{null}";
    public readonly TriggerLogStore TriggerLog;
    public readonly string CurrentCharacterId;
    public readonly string CurrentProcessorName;

    private const long SixtyHours = 10 * 6 * 60 * 60 * 1000;
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private readonly string _currentPlayer;
    private readonly Dictionary<string, Dictionary<string, RepeatedData>> _counterTimes = [];
    private readonly Dictionary<string, Dictionary<string, RepeatedData>> _repeatedTextTimes = [];
    private readonly Dictionary<string, Dictionary<string, RepeatedData>> _repeatedTimerTimes = [];
    private readonly Dictionary<string, Dictionary<string, RepeatedData>> _repeatedSpeakTimes = [];
    private readonly BlockingCollection<TriggerLogItem> _triggerLogCollection = [];
    private readonly BlockingCollection<LineData> _chatCollection = [];
    private readonly BlockingCollection<Speak> _speakCollection = [];
    private readonly SemaphoreSlim _activeTriggerSemaphore = new(1, 1);
    private readonly ConcurrentDictionary<string, bool> _activeTimerLists = [];
    private readonly ConcurrentDictionary<string, bool> _enabledTriggers = [];
    private readonly ConcurrentDictionary<string, bool> _requiredOverlays = [];
    private volatile string _activeColor;
    private volatile string _fontColor;
    private volatile bool _isDisposed;
    private volatile bool _ready;
    private volatile List<LexiconItem> _lexicon;
    private volatile List<TrustedPlayer> _trustedPlayers;
    private volatile int _voiceRate;
    private volatile int _customVolume;
    private Task _triggerLogTask;
    private Task _chatTask;
    private Task _mainTask;
    private Task _speakTask;
    private long _activityLastTicks;
    private List<TriggerWrapper> _activeTriggers = [];
    private LineData _previous;
    private bool _isTesting;

    internal TriggerProcessor(string id, string name, string playerName, string voice, int voiceRate,
      int customVolume, string activeColor, string fontColor)
    {
      CurrentCharacterId = id;
      CurrentProcessorName = name;
      TriggerLog = new TriggerLogStore(name);
      _currentPlayer = playerName;
      _activeColor = activeColor;
      _fontColor = fontColor;
      _voiceRate = voiceRate;
      _customVolume = customVolume;
      AudioManager.Instance.Add(CurrentCharacterId, voice);
      TriggerStateManager.Instance.LexiconUpdateEvent += LexiconUpdateEvent;
      TriggerStateManager.Instance.TrustedPlayersUpdateEvent += TrustedPlayersUpdateEvent;
    }

    internal long GetActivityLastTicks() => Interlocked.Read(ref _activityLastTicks);
    internal List<string> GetRequiredOverlayIds() => [.. _requiredOverlays.Keys];
    internal List<string> GetEnabledTriggers() => [.. _enabledTriggers.Keys];
    internal void SetActiveColor(string color) => _activeColor = color;
    internal void SetFontColor(string color) => _fontColor = color;
    internal void SetCustomVolume(int volume) => _customVolume = volume;
    internal void SetVoice(string voice) => AudioManager.Instance.SetVoice(CurrentCharacterId, voice);
    internal void SetVoiceRate(int rate) => _voiceRate = rate;
    internal void SetTesting(bool testing) => _isTesting = testing;

    internal async Task StartAsync()
    {
      await GetActiveTriggersAsync();
      _lexicon = [.. await TriggerStateManager.Instance.GetLexicon()];
      _trustedPlayers = [.. await TriggerStateManager.Instance.GetTrustedPlayers()];
    }

    internal async Task StopTriggersAsync()
    {
      if (_isDisposed) return;
      await _activeTriggerSemaphore.WaitAsync();

      try
      {
        AudioManager.Instance.Stop(CurrentCharacterId);

        foreach (var wrapper in _activeTriggers)
        {
          CleanupWrapper(wrapper);
        }
      }
      finally
      {
        if (!_isDisposed) _activeTriggerSemaphore.Release();
      }
    }

    internal async Task UpdateActiveTriggers()
    {
      await GetActiveTriggersAsync();
      AudioManager.Instance.Stop(CurrentCharacterId);
    }

    public void LinkTo(BlockingCollection<LogReaderItem> collection)
    {
      // delay start until log is ready
      AudioManager.Instance.Start(CurrentCharacterId);

      _chatTask = Task.Run(() =>
      {
        try
        {
          foreach (var data in _chatCollection.GetConsumingEnumerable())
          {
            if (_isDisposed) continue;
            try
            {
              HandleChat(data);
            }
            catch (Exception)
            {
              // ignore
            }
          }
        }
        catch (Exception)
        {
          // ignore (should only be cancel requests)
        }
      });

      _triggerLogTask = Task.Run(() =>
      {
        try
        {
          foreach (var data in _triggerLogCollection.GetConsumingEnumerable())
          {
            if (_isDisposed) continue;
            try
            {
              HandleLog(data.LineData, data.Wrapper, data.Type, data.Eval);
            }
            catch (Exception)
            {
              // ignore
            }
          }
        }
        catch (Exception)
        {
          // ignore (should only be cancel requests)
        }
      });

      _speakTask = Task.Factory.StartNew(() =>
      {
        try
        {
          foreach (var data in _speakCollection.GetConsumingEnumerable())
          {
            if (_isDisposed) continue;
            try
            {
              HandleSpeech(data);
            }
            catch (Exception)
            {
              // ignore
            }
          }
        }
        catch (Exception)
        {
          // ignore (should only be cancel requests)
        }
        finally
        {
          AudioManager.Instance.Stop(CurrentCharacterId, true);
        }
      }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

      _mainTask = Task.Factory.StartNew(() =>
      {
        try
        {
          foreach (var data in collection.GetConsumingEnumerable())
          {
            if (_isDisposed) continue;
            try
            {
              DoProcessAsync(data.Line, data.Ts).ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
              // ignore
            }
          }
        }
        catch (Exception)
        {
          // ignore (should only be cancel requests)
        }
        finally
        {
          collection?.Dispose();
        }
      }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static string ProcessLineCode(string text, string line) => !string.IsNullOrEmpty(text) ?
      text.Replace("{l}", line, StringComparison.OrdinalIgnoreCase) : text;
    private string ProcessCharacterCode(string text) => !string.IsNullOrEmpty(text) ?
      text.Replace("{c}", _currentPlayer ?? string.Empty, StringComparison.OrdinalIgnoreCase) : text;
    private void LexiconUpdateEvent(List<LexiconItem> update) => _lexicon = update;
    private void TrustedPlayersUpdateEvent(List<TrustedPlayer> update) => _trustedPlayers = update;

    private async Task DoProcessAsync(string line, double dateTime)
    {
      var beginTime = DateTime.Now;
      // ignore anything older than 120 seconds in case a log file is replaced/reloaded but allow for bad lag
      if (!_isTesting && DateUtil.ToDouble(beginTime) - dateTime > 120)
      {
        return;
      }

      Interlocked.Exchange(ref _activityLastTicks, beginTime.Ticks);
      var lineData = new LineData { Action = line[27..], BeginTime = dateTime };

      if (_isDisposed) return;
      await _activeTriggerSemaphore.WaitAsync();

      try
      {
        foreach (var wrapper in _activeTriggers)
        {
          if (CheckLine(wrapper, lineData, out var matches, out var timing, out var beginTicks) &&
              CheckPreviousLine(wrapper, _previous, out var previousMatches, out var previousTiming))
          {
            timing += previousTiming;
            HandleTrigger(wrapper, lineData, matches, previousMatches, timing, beginTicks);
          }

          CheckTimers(wrapper, lineData);
        }
      }
      finally
      {
        if (!_isDisposed) _activeTriggerSemaphore.Release();
      }

      _previous = lineData;

      if (!_isDisposed && !_chatCollection.IsCompleted)
      {
        _chatCollection.Add(lineData);
      }
    }

    private static bool CheckLine(TriggerWrapper wrapper, LineData lineData, out MatchCollection matches, out long timing, out long beginTicks)
    {
      beginTicks = DateTime.UtcNow.Ticks;
      var found = false;
      matches = null;

      if (wrapper.IsDisabled)
      {
        timing = 0;
        return false;
      }

      var dynamicDuration = double.NaN;
      if (wrapper.Regex != null)
      {
        try
        {
          if (!string.IsNullOrEmpty(wrapper.StartText))
          {
            if (lineData.Action.StartsWith(wrapper.StartText, StringComparison.OrdinalIgnoreCase))
            {
              matches = wrapper.Regex.Matches(lineData.Action);
            }
          }
          else if (!string.IsNullOrEmpty(wrapper.ContainsText))
          {
            if (lineData.Action.Contains(wrapper.ContainsText, StringComparison.OrdinalIgnoreCase))
            {
              matches = wrapper.Regex.Matches(lineData.Action);
            }
          }
          else
          {
            matches = wrapper.Regex.Matches(lineData.Action);
          }
        }
        catch (RegexMatchTimeoutException)
        {
          Log.Warn($"Disabling {wrapper.Name} with slow Regex: {wrapper.TriggerData?.Pattern}");
          wrapper.IsDisabled = true;
          timing = DateTime.UtcNow.Ticks - beginTicks;
          return false;
        }

        found = matches?.Count > 0 && TriggerUtil.CheckOptions(wrapper.RegexNOptions, matches, out dynamicDuration);
        if (!double.IsNaN(dynamicDuration) && wrapper.TriggerData.TimerType is 1 or 3) // countdown or progress
        {
          wrapper.ModifiedDurationSeconds = dynamicDuration;
        }
      }
      else if (!string.IsNullOrEmpty(wrapper.ModifiedPattern))
      {
        found = lineData.Action.Contains(wrapper.ModifiedPattern, StringComparison.OrdinalIgnoreCase);
      }

      var nowTicks = DateTime.UtcNow.Ticks;
      timing = nowTicks - beginTicks;
      beginTicks = nowTicks;
      return found;
    }

    private static bool CheckPreviousLine(TriggerWrapper wrapper, LineData lineData, out MatchCollection matches, out long timing)
    {
      timing = 0;
      matches = null;
      var found = true;

      if (!string.IsNullOrEmpty(lineData?.Action))
      {
        var beginTicks = DateTime.UtcNow.Ticks;
        if (wrapper.PreviousRegex != null)
        {
          try
          {
            if (!string.IsNullOrEmpty(wrapper.PreviousStartText))
            {
              if (lineData.Action.StartsWith(wrapper.PreviousStartText, StringComparison.OrdinalIgnoreCase))
              {
                matches = wrapper.PreviousRegex.Matches(lineData.Action);
              }
            }
            else if (!string.IsNullOrEmpty(wrapper.PreviousContainsText))
            {
              if (lineData.Action.Contains(wrapper.PreviousContainsText, StringComparison.OrdinalIgnoreCase))
              {
                matches = wrapper.PreviousRegex.Matches(lineData.Action);
              }
            }
            else
            {
              matches = wrapper.PreviousRegex.Matches(lineData.Action);
            }
          }
          catch (RegexMatchTimeoutException)
          {
            Log.Warn($"Disabling {wrapper.Name} with slow Regex: {wrapper.TriggerData?.PreviousPattern}");
            wrapper.IsDisabled = true;
            timing = DateTime.UtcNow.Ticks - beginTicks;
            return false;
          }

          found = matches?.Count > 0 && TriggerUtil.CheckOptions(wrapper.PreviousRegexNOptions, matches, out _);
        }
        else if (!string.IsNullOrEmpty(wrapper.ModifiedPreviousPattern))
        {
          found = lineData.Action.Contains(wrapper.ModifiedPreviousPattern, StringComparison.OrdinalIgnoreCase);
        }

        timing = DateTime.UtcNow.Ticks - beginTicks;
      }

      return found;
    }

    private void CheckTimers(TriggerWrapper wrapper, LineData lineData)
    {
      lock (wrapper.TimerList)
      {
        if (wrapper.TimerList.Count == 0) return;

        List<TimerData> cleanup = null;
        foreach (var timerData in wrapper.TimerList.ToArray())
        {
          var endEarly = CheckEndEarly(timerData.EndEarlyRegex, timerData.EndEarlyRegexNOptions, timerData.EndEarlyPattern,
            lineData.Action, out var earlyMatches);

          // try 2nd
          if (!endEarly)
          {
            endEarly = CheckEndEarly(timerData.EndEarlyRegex2, timerData.EndEarlyRegex2NOptions, timerData.EndEarlyPattern2, lineData.Action, out earlyMatches);
          }

          // check repeated threshold
          if (!endEarly && wrapper.TriggerData?.EndEarlyRepeatedCount > 0 && (wrapper.HasCounterTimer || wrapper.HasRepeatedTimer))
          {
            var stopCount = wrapper.TriggerData.EndEarlyRepeatedCount;
            endEarly = (GetRepeatedCount(_repeatedTimerTimes, wrapper, timerData.DisplayName) >= stopCount) ||
              (GetRepeatedCount(_counterTimes, wrapper, "trigger-count") >= stopCount);

            if (endEarly)
            {
              RemoveRepeatedTimes(_repeatedTimerTimes, wrapper, timerData.DisplayName);
              RemoveRepeatedTimes(_counterTimes, wrapper, "trigger-count");
            }
          }

          if (endEarly)
          {
            var tts = TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.EndEarlySoundToPlay, wrapper.ModifiedEndEarlySpeak, out var isSound);
            tts = string.IsNullOrEmpty(tts) ? TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.EndSoundToPlay, wrapper.ModifiedEndSpeak, out isSound) : tts;
            var displayText = string.IsNullOrEmpty(wrapper.ModifiedEndEarlyDisplay) ? wrapper.ModifiedEndDisplay : wrapper.ModifiedEndEarlyDisplay;

            if (!string.IsNullOrEmpty(tts) && !tts.Equals(NullCode, StringComparison.OrdinalIgnoreCase) && !_isDisposed && !_speakCollection.IsCompleted)
            {
              _speakCollection.Add(new Speak
              {
                Wrapper = wrapper,
                TtsOrSound = tts,
                IsSound = isSound,
                Matches = earlyMatches,
                Previous = timerData.PreviousMatches,
                Original = timerData.OriginalMatches,
                Action = lineData.Action
              });
            }

            if (ProcessDisplayText(displayText, lineData.Action, earlyMatches, timerData.OriginalMatches,
              timerData.PreviousMatches) is { } updatedDisplayText)
            {
              TriggerOverlayManager.Instance.AddText(wrapper.TriggerData, updatedDisplayText, _fontColor);
            }

            if (!_isDisposed && !_triggerLogCollection.IsCompleted)
            {
              _triggerLogCollection.Add(new(lineData, wrapper, "Timer End Early", 0));
            }

            // lazy initialize a list as it most often won't be needed
            if (cleanup == null)
            {
              cleanup = [];
            }

            cleanup.Add(timerData);
          }
        }

        // cleanup timers after we've finished iterating over the list
        cleanup?.ForEach(timerData => CleanupTimer(wrapper, timerData));
      }
    }

    private void HandleTrigger(TriggerWrapper wrapper, LineData lineData, MatchCollection matches,
      MatchCollection previousMatches, long timing, long beginTicks, int loopCount = 0)
    {
      if (!_ready) return;

      if (loopCount == 0 && wrapper.TriggerData.LockoutTime > 0)
      {
        if (wrapper.LockedOutTicks > 0 && beginTicks <= wrapper.LockedOutTicks)
        {
          // during lockout do nothing
          return;
        }

        // update lockout time
        wrapper.LockedOutTicks = beginTicks + (wrapper.TriggerData.LockoutTime * TimeSpan.TicksPerSecond);
      }

      // no need to constantly updated the DB. 6 hour check
      var updatedTime = beginTicks / TimeSpan.TicksPerMillisecond;

      if (updatedTime - wrapper.TriggerData.LastTriggered > SixtyHours)
      {
        // queue update to last trigger time
        TriggerStateManager.Instance.UpdateLastTriggered(wrapper.Id, updatedTime);
        wrapper.TriggerData.LastTriggered = updatedTime;
      }

      // GINA {counter} that is based on trigger firing regardless of whether the
      // speak, text, or timer names are unique
      var counterCount = -1L;
      if (wrapper.HasCounterSpeak || wrapper.HasCounterText || wrapper.HasCounterTimer)
      {
        counterCount = UpdateRepeatedTimes(_counterTimes, wrapper, "trigger-count", beginTicks);
      }

      if (ProcessMatchesText(wrapper.ModifiedTimerName, matches) is { } altTimerName)
      {
        altTimerName = ProcessMatchesText(altTimerName, previousMatches);
        altTimerName = ProcessLineCode(altTimerName, lineData.Action);
        if (wrapper.HasRepeatedTimer)
        {
          UpdateRepeatedTimes(_repeatedTimerTimes, wrapper, altTimerName, beginTicks);
        }

        if (wrapper.TriggerData.TimerType > 0 && wrapper.TriggerData.DurationSeconds > 0)
        {
          StartTimer(wrapper, altTimerName, beginTicks, lineData, matches, previousMatches, loopCount);
        }
      }

      var tts = TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.SoundToPlay, wrapper.ModifiedSpeak, out var isSound);
      if (!string.IsNullOrEmpty(tts) && !tts.Equals(NullCode, StringComparison.OrdinalIgnoreCase) && !_isDisposed && !_speakCollection.IsCompleted)
      {
        _speakCollection.Add(new Speak
        {
          IsPrimary = true,
          Wrapper = wrapper,
          TtsOrSound = tts,
          IsSound = isSound,
          Matches = matches,
          Previous = previousMatches,
          Action = lineData.Action,
          CounterCount = counterCount,
          BeginTicks = beginTicks,
          BeginTime = lineData.BeginTime
        });
      }

      if (ProcessDisplayText(wrapper.ModifiedDisplay, lineData.Action, matches, null, previousMatches) is { } updatedDisplayText)
      {
        if (wrapper.HasRepeatedText)
        {
          var repeatedCount = UpdateRepeatedTimes(_repeatedTextTimes, wrapper, updatedDisplayText, beginTicks);
          updatedDisplayText = updatedDisplayText.Replace(RepeatedCode, repeatedCount.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        }

        if (wrapper.HasCounterText)
        {
          updatedDisplayText = updatedDisplayText.Replace(CounterCode, counterCount.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        }

        if (wrapper.HasLogTimeText)
        {
          updatedDisplayText = updatedDisplayText.Replace(LogTimeCode, DateUtil.FormatSimpleHms(lineData.BeginTime), StringComparison.OrdinalIgnoreCase);
        }

        TriggerOverlayManager.Instance.AddText(wrapper.TriggerData, updatedDisplayText, _fontColor);
      }

      if (ProcessDisplayText(wrapper.ModifiedShare, lineData.Action, matches, null, previousMatches) is { } updatedShareText)
      {
        UiUtil.SetClipboardText(updatedShareText);
      }

      if (ProcessDisplayText(wrapper.ModifiedSendToChat, lineData.Action, matches, null, previousMatches) is { } updatedSendToChatText)
      {
        var url = wrapper.TriggerData.ChatWebhook;
        if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
          url = url ?? "Not Set";
          Log.Warn($"Can not send to chat, invalid webhook: {url}");
        }
        else
        {
          if (wrapper.HasLogTimeSendToChat)
          {
            updatedSendToChatText = updatedSendToChatText.Replace(LogTimeCode, DateUtil.FormatSimpleHms(lineData.BeginTime), StringComparison.OrdinalIgnoreCase);
          }
          _ = MainActions.SendDiscordMessage(updatedSendToChatText, wrapper.TriggerData.ChatWebhook);
        }
      }

      if (!_isDisposed && !_triggerLogCollection.IsCompleted)
      {
        _triggerLogCollection.Add(new(lineData, wrapper, "Initial Trigger", timing / 10));
      }
    }

    private void StartTimer(TriggerWrapper wrapper, string displayName, long beginTicks, LineData lineData,
      MatchCollection matches, MatchCollection previousMatches, int loopCount = 0)
    {
      var trigger = wrapper.TriggerData;
      switch (trigger.TriggerAgainOption)
      {
        // Restart Timer Option so clear out everything
        case 1:
          {
            CleanupWrapper(wrapper);
          }
          break;
        // Restart Timer only if it is already running
        case 2:
          {
            lock (wrapper.TimerList)
            {
              if (wrapper.TimerList.FirstOrDefault(data => displayName.Equals(data?.DisplayName, StringComparison.OrdinalIgnoreCase))
                is { } timerData)
              {
                CleanupTimer(wrapper, timerData);
              }
            }
          }
          break;
        // Do nothing if any exist
        case 3:
          {
            lock (wrapper.TimerList)
            {
              if (wrapper.TimerList.Count != 0)
              {
                return;
              }
            }
          }
          break;
        // Do nothing only if a timer with this name is already running
        case 4:
          {
            lock (wrapper.TimerList)
            {
              if (wrapper.TimerList.FirstOrDefault(data => displayName.Equals(data?.DisplayName, StringComparison.OrdinalIgnoreCase)) is not null)
              {
                return;
              }
            }
          }
          break;
      }

      TimerData newTimerData = null;
      if (trigger.WarningSeconds > 0 && wrapper.ModifiedDurationSeconds - trigger.WarningSeconds is var diff and > 0)
      {
        newTimerData = new TimerData { CharacterId = CurrentCharacterId, DisplayName = displayName, WarningSource = new CancellationTokenSource() };

        var data = newTimerData;
        Task.Delay((int)diff * 1000).ContinueWith(_ =>
        {
          var proceed = false;
          lock (wrapper.TimerList)
          {
            proceed = !data.Warned;
          }

          if (proceed)
          {
            var tts = TriggerUtil.GetFromDecodedSoundOrText(trigger.WarningSoundToPlay, wrapper.ModifiedWarningSpeak, out var isSound);
            if (!string.IsNullOrEmpty(tts) && !tts.Equals(NullCode, StringComparison.OrdinalIgnoreCase) && !_isDisposed && !_speakCollection.IsCompleted)
            {
              _speakCollection.Add(new Speak
              {
                Wrapper = wrapper,
                TtsOrSound = tts,
                IsSound = isSound,
                Matches = matches,
                Previous = previousMatches,
                Action = lineData.Action,
              });
            }

            if (ProcessDisplayText(wrapper.ModifiedWarningDisplay, lineData.Action, matches, null, previousMatches) is { } updatedDisplayText)
            {
              TriggerOverlayManager.Instance.AddText(trigger, updatedDisplayText, _fontColor);
            }

            if (!_isDisposed && !_triggerLogCollection.IsCompleted)
            {
              _triggerLogCollection.Add(new(lineData, wrapper, "Timer Warning", 0));
            }
          }
        }, data.WarningSource.Token);
      }

      newTimerData ??= new TimerData { CharacterId = CurrentCharacterId, DisplayName = displayName };

      if (wrapper.HasRepeatedTimer)
      {
        newTimerData.RepeatedCount = GetRepeatedCount(_repeatedTimerTimes, wrapper, displayName);
      }

      if (wrapper.HasCounterTimer)
      {
        newTimerData.CounterCount = GetRepeatedCount(_counterTimes, wrapper, "trigger-count");
      }

      if (wrapper.HasLogTimeTimer)
      {
        newTimerData.LogTime = DateUtil.FormatSimpleHms(lineData.BeginTime);
      }

      newTimerData.BeginTicks = beginTicks;
      newTimerData.EndTicks = beginTicks + (long)(TimeSpan.TicksPerSecond * wrapper.ModifiedDurationSeconds);
      newTimerData.DurationTicks = newTimerData.EndTicks - beginTicks;
      newTimerData.ResetTicks = trigger.ResetDurationSeconds > 0 ?
        beginTicks + (long)(TimeSpan.TicksPerSecond * trigger.ResetDurationSeconds) : 0;
      newTimerData.ResetDurationTicks = newTimerData.ResetTicks - beginTicks;
      newTimerData.TimerOverlayIds = new ReadOnlyCollection<string>(trigger.SelectedOverlays);
      newTimerData.TriggerAgainOption = trigger.TriggerAgainOption;
      newTimerData.TimerType = trigger.TimerType;
      newTimerData.OriginalMatches = matches;
      newTimerData.PreviousMatches = previousMatches;
      newTimerData.ActiveColor = _activeColor ?? trigger.ActiveColor;
      newTimerData.FontColor = _fontColor ?? trigger.FontColor;
      newTimerData.TriggerId = wrapper.Id;
      newTimerData.Key = wrapper.Id + "-" + displayName;
      newTimerData.CancelSource = new CancellationTokenSource();
      newTimerData.TimesToLoopCount = loopCount;
      newTimerData.TimerIcon = wrapper.TimerIcon;

      // save line data if repeating timer
      if (wrapper.TriggerData.TimerType == 4)
      {
        newTimerData.RepeatingTimerLineData = lineData;
      }

      if (!string.IsNullOrEmpty(trigger.EndEarlyPattern))
      {
        var endEarlyPattern = ProcessMatchesText(trigger.EndEarlyPattern, matches);
        endEarlyPattern = ProcessMatchesText(endEarlyPattern, previousMatches);
        endEarlyPattern = UpdatePattern(trigger.EndUseRegex, endEarlyPattern, out var numberOptions2);

        if (trigger.EndUseRegex)
        {
          newTimerData.EndEarlyRegex = new Regex(endEarlyPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
          newTimerData.EndEarlyRegexNOptions = numberOptions2;
        }
        else
        {
          newTimerData.EndEarlyPattern = endEarlyPattern;
        }
      }

      if (!string.IsNullOrEmpty(trigger.EndEarlyPattern2))
      {
        var endEarlyPattern2 = ProcessMatchesText(trigger.EndEarlyPattern2, matches);
        endEarlyPattern2 = ProcessMatchesText(endEarlyPattern2, previousMatches);
        endEarlyPattern2 = UpdatePattern(trigger.EndUseRegex2, endEarlyPattern2, out var numberOptions3);

        if (trigger.EndUseRegex2)
        {
          newTimerData.EndEarlyRegex2 = new Regex(endEarlyPattern2, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
          newTimerData.EndEarlyRegex2NOptions = numberOptions3;
        }
        else
        {
          newTimerData.EndEarlyPattern2 = endEarlyPattern2;
        }
      }

      lock (wrapper.TimerList)
      {
        wrapper.TimerList.Add(newTimerData);
        // true for add
        TriggerOverlayManager.Instance.UpdateTimer(trigger, newTimerData, TriggerOverlayManager.TimerStateChange.Start);
        // save for later
        _activeTimerLists[wrapper.Id] = true;
      }

      var data2 = newTimerData;
      Task.Delay((int)(wrapper.ModifiedDurationSeconds * 1000)).ContinueWith(_ =>
      {
        var proceed = false;
        lock (wrapper.TimerList)
        {
          proceed = !data2.Canceled;
          CleanupTimer(wrapper, data2);
        }

        if (proceed)
        {
          var tts = TriggerUtil.GetFromDecodedSoundOrText(trigger.EndSoundToPlay, wrapper.ModifiedEndSpeak, out var isSound);
          if (!string.IsNullOrEmpty(tts) && !tts.Equals(NullCode, StringComparison.OrdinalIgnoreCase) && !_isDisposed && !_speakCollection.IsCompleted)
          {
            _speakCollection.Add(new Speak
            {
              Wrapper = wrapper,
              TtsOrSound = tts,
              IsSound = isSound,
              Matches = matches,
              Previous = data2.PreviousMatches,
              Original = data2.OriginalMatches,
              Action = lineData.Action
            });
          }

          if (ProcessDisplayText(wrapper.ModifiedEndDisplay, lineData.Action, matches, data2.OriginalMatches, data2.PreviousMatches) is { } updatedDisplayText)
          {
            TriggerOverlayManager.Instance.AddText(trigger, updatedDisplayText, _fontColor);
          }

          if (!_isDisposed && !_triggerLogCollection.IsCompleted)
          {
            _triggerLogCollection.Add(new(lineData, wrapper, "Timer End", 0));
          }

          // repeating
          if (wrapper.TriggerData.TimerType == 4 && wrapper.TriggerData.TimesToLoop > data2.TimesToLoopCount)
          {
            // repeat normal process
            if (CheckLine(wrapper, data2.RepeatingTimerLineData, out var matchAgain, out var timing, out var beginTicks))
            {
              HandleTrigger(wrapper, data2.RepeatingTimerLineData, matchAgain, null, timing, beginTicks, data2.TimesToLoopCount + 1);
            }

            CheckTimers(wrapper, lineData);
          }
        }
      }, data2.CancelSource.Token);
    }

    private void HandleSpeech(Speak speak)
    {
      if (!_ready) return;

      if (!string.IsNullOrEmpty(speak.TtsOrSound))
      {
        if (speak.IsSound)
        {
          var theFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "sounds", speak.TtsOrSound);
          AudioManager.Instance.SpeakFileAsync(CurrentCharacterId, theFile, _customVolume, speak.Wrapper.TriggerData);
        }
        else
        {
          var lexicon = _lexicon;
          var tts = ProcessTts(speak.TtsOrSound, speak.Action, speak.Matches, speak.Previous, speak.Original);

          if (speak.IsPrimary)
          {
            if (speak.Wrapper.HasRepeatedSpeak && speak.BeginTicks > 0)
            {
              var repeatedCount = UpdateRepeatedTimes(_repeatedSpeakTimes, speak.Wrapper, tts, speak.BeginTicks);
              tts = tts.Replace(RepeatedCode, repeatedCount.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
            }

            if (speak.Wrapper.HasCounterSpeak && speak.CounterCount > 0)
            {
              tts = tts.Replace(CounterCode, speak.CounterCount.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
            }

            if (speak.Wrapper.HasLogTimeSpeak && speak.BeginTime > 0)
            {
              tts = tts.Replace(LogTimeCode, DateUtil.FormatSimpleHms(speak.BeginTime), StringComparison.OrdinalIgnoreCase);
            }
          }

          if (!string.IsNullOrEmpty(tts) && lexicon != null)
          {
            foreach (var item in CollectionsMarshal.AsSpan(lexicon))
            {
              if (item != null && !string.IsNullOrEmpty(item.Replace) && !string.IsNullOrEmpty(item.With))
              {
                tts = tts.Replace(item.Replace, item.With, StringComparison.OrdinalIgnoreCase);
              }
            }

            if (!string.IsNullOrEmpty(tts))
            {
              tts = ReplaceBadCharsRegex().Replace(tts, string.Empty);
              AudioManager.Instance.SpeakTtsAsync(CurrentCharacterId, tts, _voiceRate, _customVolume, speak.Wrapper.TriggerData);
            }
          }
        }
      }
    }

    private void HandleChat(LineData lineData)
    {
      if (!_ready) return;

      // check overlays that need to close
      // this prob only happens outside of active raid time so can be
      // delayed a bit
      TriggerOverlayManager.Instance.CheckLine(lineData.Action);

      // look for quick shares after triggers have been processed
      var chatType = ChatLineParser.ParseChatType(lineData.Action);
      if (chatType != null)
      {
        // Look for Quick Share entries
        TriggerUtil.CheckQuickShare(_trustedPlayers, chatType, lineData.Action, lineData.BeginTime, CurrentCharacterId, CurrentProcessorName);
        GinaUtil.CheckGina(_trustedPlayers, chatType, lineData.Action, lineData.BeginTime, CurrentCharacterId, CurrentProcessorName);
      }
    }

    private async Task GetActiveTriggersAsync()
    {
      _ready = false;
      _requiredOverlays.Clear();
      _enabledTriggers.Clear();

      var activeTriggers = new List<TriggerWrapper>();
      var enabledTriggers = await TriggerStateManager.Instance.GetEnabledTriggers(CurrentCharacterId);
      long triggerCount = 0;
      foreach (var enabled in enabledTriggers.OrderByDescending(enabled => enabled.Trigger.LastTriggered))
      {
        var trigger = enabled.Trigger;
        if (trigger.Pattern is { } pattern && !string.IsNullOrEmpty(pattern))
        {
          try
          {
            // keep track of everything enabled
            _enabledTriggers[enabled.Id] = true;

            triggerCount++;

            pattern = UpdatePattern(trigger.UseRegex, pattern, out var numberOptions);
            pattern = UpdateTimePattern(trigger.UseRegex, pattern);
            var modifiedDisplay = ProcessCharacterCode(trigger.TextToDisplay);
            var modifiedSpeak = ProcessCharacterCode(trigger.TextToSpeak);
            var timerName = string.IsNullOrEmpty(trigger.AltTimerName) ? enabled.Name : trigger.AltTimerName;
            var modifiedTimerName = ProcessCharacterCode(timerName);
            var modifiedSendToChat = ProcessCharacterCode(trigger.TextToSendToChat);

            var wrapper = new TriggerWrapper
            {
              Id = enabled.Id,
              Name = enabled.Name,
              TriggerData = trigger,
              ModifiedSpeak = modifiedSpeak,
              ModifiedWarningSpeak = ProcessCharacterCode(trigger.WarningTextToSpeak),
              ModifiedEndSpeak = ProcessCharacterCode(trigger.EndTextToSpeak),
              ModifiedEndEarlySpeak = ProcessCharacterCode(trigger.EndEarlyTextToSpeak),
              ModifiedDisplay = modifiedDisplay,
              ModifiedShare = ProcessCharacterCode(trigger.TextToShare),
              ModifiedSendToChat = modifiedSendToChat,
              ModifiedWarningDisplay = ProcessCharacterCode(trigger.WarningTextToDisplay),
              ModifiedEndDisplay = ProcessCharacterCode(trigger.EndTextToDisplay),
              ModifiedEndEarlyDisplay = ProcessCharacterCode(trigger.EndEarlyTextToDisplay),
              ModifiedTimerName = string.IsNullOrEmpty(modifiedTimerName) ? "" : modifiedTimerName,
              ModifiedDurationSeconds = trigger.DurationSeconds,
              ModifiedPattern = !trigger.UseRegex ? pattern : null,
              HasCounterSpeak = modifiedSpeak?.Contains(CounterCode, StringComparison.OrdinalIgnoreCase) == true,
              HasCounterText = modifiedDisplay?.Contains(CounterCode, StringComparison.OrdinalIgnoreCase) == true,
              HasCounterTimer = modifiedTimerName?.Contains(CounterCode, StringComparison.OrdinalIgnoreCase) == true,
              HasRepeatedSpeak = modifiedSpeak?.Contains(RepeatedCode, StringComparison.OrdinalIgnoreCase) == true,
              HasRepeatedText = modifiedDisplay?.Contains(RepeatedCode, StringComparison.OrdinalIgnoreCase) == true,
              HasRepeatedTimer = modifiedTimerName?.Contains(RepeatedCode, StringComparison.OrdinalIgnoreCase) == true,
              HasLogTimeSpeak = modifiedSpeak?.Contains(LogTimeCode, StringComparison.OrdinalIgnoreCase) == true,
              HasLogTimeText = modifiedDisplay?.Contains(LogTimeCode, StringComparison.OrdinalIgnoreCase) == true,
              HasLogTimeTimer = modifiedTimerName?.Contains(LogTimeCode, StringComparison.OrdinalIgnoreCase) == true,
              HasLogTimeSendToChat = modifiedSendToChat?.Contains(LogTimeCode, StringComparison.OrdinalIgnoreCase) == true,
              TimerIcon = UiElementUtil.CreateBitmap(trigger.IconSource)
            };

            // temp
            if (wrapper.TriggerData.EnableTimer && wrapper.TriggerData.TimerType == 0)
            {
              wrapper.TriggerData.TimerType = 1;
            }

            // main pattern
            if (trigger.UseRegex)
            {
              wrapper.Regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));
              // ReSharper disable once ReturnValueOfPureMethodIsNotUsed
              wrapper.Regex.Match(""); // warm up the regex
              wrapper.RegexNOptions = numberOptions;

              // save some start text to search for before trying the regex
              if (!string.IsNullOrEmpty(pattern) && pattern.Length > 3)
              {
                if (pattern[0] == '^')
                {
                  var startText = TextUtils.GetSearchableTextFromStart(pattern, 1);
                  if (!string.IsNullOrEmpty(startText))
                  {
                    wrapper.StartText = startText;
                  }
                }
                else
                {
                  var containsText = TextUtils.GetSearchableTextFromStart(pattern, 0);
                  if (!string.IsNullOrEmpty(containsText) && containsText.Length > 2)
                  {
                    wrapper.ContainsText = containsText;
                  }
                }
              }
            }

            // previous line
            if (trigger.PreviousPattern is { } previousPattern && !string.IsNullOrEmpty(previousPattern))
            {
              previousPattern = UpdatePattern(trigger.PreviousUseRegex, previousPattern, out var previousNumberOptions);
              previousPattern = UpdateTimePattern(trigger.PreviousUseRegex, previousPattern);

              if (trigger.PreviousUseRegex)
              {
                wrapper.PreviousRegex = new Regex(previousPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));
                // ReSharper disable once ReturnValueOfPureMethodIsNotUsed
                wrapper.PreviousRegex.Match(""); // warm up the regex
                wrapper.PreviousRegexNOptions = previousNumberOptions;

                // save some start text to search for before trying the regex
                if (!string.IsNullOrEmpty(previousPattern) && previousPattern.Length > 3)
                {
                  if (previousPattern[0] == '^')
                  {
                    var startText = TextUtils.GetSearchableTextFromStart(previousPattern, 1);
                    if (!string.IsNullOrEmpty(startText))
                    {
                      wrapper.PreviousStartText = startText;
                    }
                  }
                  else
                  {
                    var containsText = TextUtils.GetSearchableTextFromStart(previousPattern, 0);
                    if (!string.IsNullOrEmpty(containsText) && containsText.Length > 2)
                    {
                      wrapper.PreviousContainsText = containsText;
                    }
                  }
                }
              }
              else
              {
                wrapper.ModifiedPreviousPattern = previousPattern;
              }
            }

            foreach (var overlayId in trigger.SelectedOverlays)
            {
              if (!string.IsNullOrEmpty(overlayId))
              {
                _requiredOverlays[overlayId] = true;
              }
            }

            activeTriggers.Add(wrapper);
          }
          catch (Exception)
          {
            // Log.Debug("Bad Trigger?", ex);
          }
        }
      }

      if (triggerCount > 500 && CurrentProcessorName?.Contains("Trigger Tester") == false)
      {
        Log.Warn($"Over {triggerCount} triggers active for one character. To improve performance consider turning off old triggers.");
      }

      await SetActiveTriggersAsync(activeTriggers);
      _ready = true;
    }

    private static bool CheckEndEarly(Regex endEarlyRegex, List<NumberOptions> options, string endEarlyPattern,
      string action, out MatchCollection earlyMatches)
    {
      earlyMatches = null;
      var endEarly = false;

      if (endEarlyRegex != null)
      {
        earlyMatches = endEarlyRegex.Matches(action);
        if (earlyMatches is { Count: > 0 } && TriggerUtil.CheckOptions(options, earlyMatches, out _))
        {
          endEarly = true;
        }
      }
      else if (!string.IsNullOrEmpty(endEarlyPattern))
      {
        if (action.Contains(endEarlyPattern, StringComparison.OrdinalIgnoreCase))
        {
          endEarly = true;
        }
      }

      return endEarly;
    }

    private static string ProcessDisplayText(string text, string action, MatchCollection matches,
      MatchCollection originalMatches, MatchCollection previousMatches)
    {
      if (!string.IsNullOrEmpty(text) && !text.Equals(NullCode, StringComparison.OrdinalIgnoreCase))
      {
        text = ProcessMatchesText(text, originalMatches);
        text = ProcessMatchesText(text, matches);
        text = ProcessMatchesText(text, previousMatches);
        text = ProcessLineCode(text, action);
        return text;
      }
      return null;
    }

    private static string ProcessMatchesText(string text, MatchCollection matches)
    {
      if (matches == null || string.IsNullOrEmpty(text))
      {
        return text;
      }

      foreach (var match in matches.Cast<Match>())
      {
        for (var i = 1; i < match.Groups.Count; i++)
        {
          if (!string.IsNullOrEmpty(match.Groups[i].Name))
          {
            text = text.Replace("${" + match.Groups[i].Name + "}", match.Groups[i].Value, StringComparison.OrdinalIgnoreCase);
            text = text.Replace("{" + match.Groups[i].Name + "}", match.Groups[i].Value, StringComparison.OrdinalIgnoreCase);
          }
        }
      }

      return text;
    }

    private static string ProcessTts(string tts, string action, MatchCollection matches, MatchCollection previous, MatchCollection original)
    {
      tts = ProcessMatchesText(tts, original);
      tts = ProcessMatchesText(tts, matches);
      tts = ProcessMatchesText(tts, previous);
      tts = ProcessLineCode(tts, action);
      return tts;
    }

    private static long GetRepeatedCount(IReadOnlyDictionary<string, Dictionary<string, RepeatedData>> times, TriggerWrapper wrapper, string displayValue)
    {
      if (!string.IsNullOrEmpty(wrapper.Id) && times.TryGetValue(wrapper.Id, out var displayTimes))
      {
        if (displayTimes.TryGetValue(displayValue, out var repeatedData))
        {
          return repeatedData.Count;
        }
      }
      return -1;
    }

    private static void RemoveRepeatedTimes(IDictionary<string, Dictionary<string, RepeatedData>> times, TriggerWrapper wrapper,
      string displayValue)
    {
      if (!string.IsNullOrEmpty(wrapper.Id) && !string.IsNullOrEmpty(displayValue))
      {
        if (times.TryGetValue(wrapper.Id, out var displayTimes))
        {
          displayTimes.Remove(displayValue);
        }
      }
    }

    private static long UpdateRepeatedTimes(IDictionary<string, Dictionary<string, RepeatedData>> times, TriggerWrapper wrapper,
      string displayValue, long beginTicks)
    {
      long repeatedCount = -1;

      if (!string.IsNullOrEmpty(wrapper.Id) && !string.IsNullOrEmpty(displayValue) && wrapper.TriggerData?.RepeatedResetTime >= 0)
      {
        if (times.TryGetValue(wrapper.Id, out var displayTimes))
        {
          if (displayTimes.TryGetValue(displayValue, out var repeatedData))
          {
            var diff = (beginTicks - repeatedData.CountTicks) / TimeSpan.TicksPerSecond;
            if (diff > wrapper.TriggerData.RepeatedResetTime)
            {
              repeatedData.Count = 1;
              repeatedData.CountTicks = beginTicks;
            }
            else
            {
              repeatedData.Count++;
            }

            repeatedCount = repeatedData.Count;
          }
          else
          {
            displayTimes[displayValue] = new RepeatedData { Count = 1, CountTicks = beginTicks };
            repeatedCount = 1;
          }
        }
        else
        {
          displayTimes = new Dictionary<string, RepeatedData>
          {
            { displayValue, new RepeatedData { Count = 1, CountTicks = beginTicks } }
          };

          times[wrapper.Id] = displayTimes;
          repeatedCount = 1;
        }
      }

      return repeatedCount;
    }

    private string UpdatePattern(bool useRegex, string pattern, out List<NumberOptions> numberOptions)
    {
      numberOptions = [];
      pattern = ProcessCharacterCode(pattern);

      if (useRegex)
      {
        if (ReplaceStringRegex().Matches(pattern) is { Count: > 0 } matches)
        {
          foreach (var match in matches.Cast<Match>())
          {
            if (match.Groups.Count == 2)
            {
              pattern = pattern.Replace(match.Value, "(?<" + match.Groups[1].Value + ">.+)");
            }
          }
        }

        if (ReplaceNumberRegex().Matches(pattern) is { Count: > 0 } matches2)
        {
          foreach (var match in matches2.Cast<Match>())
          {
            if (match.Groups.Count == 4)
            {
              pattern = pattern.Replace(match.Value, "(?<" + match.Groups[1].Value + @">\d+)");
              if (!string.IsNullOrEmpty(match.Groups[2].Value) && !string.IsNullOrEmpty(match.Groups[3].Value) &&
                uint.TryParse(match.Groups[3].Value, out var value))
              {
                numberOptions.Add(new NumberOptions { Key = match.Groups[1].Value, Op = match.Groups[2].Value, Value = value });
              }
            }
          }
        }
      }

      return pattern;
    }

    private static string UpdateTimePattern(bool useRegex, string pattern)
    {
      if (useRegex)
      {
        if (ReplaceTsRegex().Matches(pattern) is { Count: > 0 } matches2)
        {
          foreach (var match in matches2.Cast<Match>())
          {
            if (match.Groups.Count == 2)
            {
              // This regex pattern matches time in the formats hh:mm:ss, mm:ss, or ss
              var timePattern = @"(?<" + match.Groups[1].Value + @">(?:\d+[:]?){1,3})";
              pattern = pattern.Replace(match.Value, timePattern);
            }
          }
        }
      }

      return pattern;
    }

    private void HandleLog(LineData lineData, TriggerWrapper wrapper, string type, long eval = 0)
    {
      // update log
      var log = new TriggerLogEntry
      {
        BeginTime = DateUtil.ToDouble(DateTime.Now),
        LogTime = lineData?.BeginTime ?? double.NaN,
        Line = lineData?.Action ?? "",
        Name = wrapper.Name,
        Type = type,
        Eval = eval,
        NodeId = wrapper.Id,
        Priority = wrapper.TriggerData.Priority,
        CharacterId = CurrentCharacterId
      };

      TriggerLog.Add(log);
    }

    // make sure each call is from within lock of TimerList
    private void CleanupTimer(TriggerWrapper wrapper, TimerData timerData)
    {
      timerData.CancelSource?.Cancel();
      timerData.CancelSource?.Dispose();
      timerData.CancelSource = null;
      timerData.Canceled = true;
      timerData.WarningSource?.Cancel();
      timerData.WarningSource?.Dispose();
      timerData.WarningSource = null;
      timerData.Warned = true;
      wrapper.TimerList.Remove(timerData);

      // stop timer
      TriggerOverlayManager.Instance.UpdateTimer(wrapper.TriggerData, timerData, TriggerOverlayManager.TimerStateChange.Stop);
      if (wrapper.TimerList.Count == 0)
      {
        _activeTimerLists.TryRemove(wrapper.Id, out _);
      }
    }

    private void CleanupWrapper(TriggerWrapper wrapper)
    {
      lock (wrapper.TimerList)
      {
        foreach (var timerData in wrapper.TimerList.ToArray())
        {
          CleanupTimer(wrapper, timerData);
        }
      }
    }

    private async Task SetActiveTriggersAsync(List<TriggerWrapper> newActive = null)
    {
      // cleanup on process exit
      List<TriggerWrapper> cleanup = null;

      if (_isDisposed) return;
      await _activeTriggerSemaphore.WaitAsync();

      try
      {
        cleanup = [.. _activeTriggers];
        _activeTriggers = newActive ?? [];

        foreach (var old in cleanup)
        {
          if (_activeTimerLists.ContainsKey(old.Id))
          {
            if (_activeTriggers.Find(wrapper => wrapper.Id == old.Id) is TriggerWrapper { } wrapper)
            {
              // old lists can still change during this process (end early case)
              lock (old.TimerList)
              {
                wrapper.TimerList.AddRange(old.TimerList);
                old.TimerList.Clear();
              }
            }
          }
        }
      }
      finally
      {
        if (!_isDisposed) _activeTriggerSemaphore.Release();
      }

      cleanup?.ForEach(CleanupWrapper);
    }

    public async void Dispose()
    {
      if (!_isDisposed)
      {
        _isDisposed = true;
        _ready = false;

        // cleanup on process exit
        await SetActiveTriggersAsync();

        TriggerStateManager.Instance.LexiconUpdateEvent -= LexiconUpdateEvent;
        TriggerStateManager.Instance.TrustedPlayersUpdateEvent -= TrustedPlayersUpdateEvent;
        _triggerLogCollection.CompleteAdding();
        _chatCollection.CompleteAdding();
        _speakCollection.CompleteAdding();

        try
        {
          foreach (var task in new Task[] { _speakTask, _chatTask, _triggerLogTask, _mainTask })
          {
            if (task != null)
            {
              task.Wait();
            }
          }
        }
        finally
        {
          _triggerLogCollection.Dispose();
          _chatCollection.Dispose();
          _speakCollection.Dispose();
          _activeTriggerSemaphore.Dispose();
        }
      }
    }

    private readonly record struct TriggerLogItem(LineData LineData, TriggerWrapper Wrapper, string Type, long Eval);

    private class Speak
    {
      public bool IsPrimary { get; init; }
      public TriggerWrapper Wrapper { get; init; }
      public string TtsOrSound { get; init; }
      public bool IsSound { get; init; }
      public string Action { get; init; }
      public MatchCollection Matches { get; init; }
      public MatchCollection Previous { get; init; }
      public MatchCollection Original { get; init; }
      public long CounterCount { get; init; }
      public double BeginTime { get; init; }
      public long BeginTicks { get; init; }
    }

    private class RepeatedData
    {
      public long Count { get; set; }
      public long CountTicks { get; set; }
    }

    private class TriggerWrapper
    {
      public string Id { get; init; }
      public string Name { get; init; }
      public string ModifiedPattern { get; init; }
      public string ModifiedSpeak { get; init; }
      public string ModifiedEndSpeak { get; init; }
      public string ModifiedEndEarlySpeak { get; init; }
      public string ModifiedWarningSpeak { get; init; }
      public string ModifiedDisplay { get; init; }
      public string ModifiedShare { get; init; }
      public string ModifiedSendToChat { get; init; }
      public string ModifiedEndDisplay { get; init; }
      public string ModifiedEndEarlyDisplay { get; init; }
      public string ModifiedWarningDisplay { get; init; }
      public string ModifiedTimerName { get; init; }
      public bool HasCounterTimer { get; init; }
      public bool HasCounterText { get; init; }
      public bool HasCounterSpeak { get; init; }
      public bool HasRepeatedTimer { get; init; }
      public bool HasRepeatedText { get; init; }
      public bool HasRepeatedSpeak { get; init; }
      public bool HasLogTimeTimer { get; init; }
      public bool HasLogTimeText { get; init; }
      public bool HasLogTimeSpeak { get; init; }
      public bool HasLogTimeSendToChat { get; init; }
      public BitmapImage TimerIcon { get; init; }
      public Trigger TriggerData { get; init; }
      // only the main thread modifies these values
      public string ModifiedPreviousPattern { get; set; }
      public double ModifiedDurationSeconds { get; set; }
      public Regex Regex { get; set; }
      public Regex PreviousRegex { get; set; }
      public List<NumberOptions> RegexNOptions { get; set; }
      public List<NumberOptions> PreviousRegexNOptions { get; set; }
      public bool IsDisabled { get; set; }
      public string ContainsText { get; set; }
      public string PreviousContainsText { get; set; }
      public string StartText { get; set; }
      public string PreviousStartText { get; set; }
      public double LockedOutTicks { get; set; }
      // need to synchronize when working with timer list or even timer data
      public List<TimerData> TimerList { get; } = [];
    }

    [GeneratedRegex(@"{(s\d?)}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReplaceStringRegex();
    [GeneratedRegex(@"{(n\d?)(<=|>=|>|<|=|==)?(\d+)?}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReplaceNumberRegex();
    [GeneratedRegex(@"{(ts)}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReplaceTsRegex();
    [GeneratedRegex(@"[^a-zA-Z0-9 .,!?;:'""-()]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReplaceBadCharsRegex();
  }
}
