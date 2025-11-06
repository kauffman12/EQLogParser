using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace EQLogParser
{
  internal partial class TriggerProcessor : ILogProcessor
  {
    public const string CharacterCode = "{c}";
    public const string CounterCode = "{counter}";
    public const string RepeatedCode = "{repeated}";
    public const string LogTimeCode = "{logtime}";
    public const string NullCode = "{null}";
    public const string TimerWarnTimeCode = "{timer-warn-time-value}";
    public readonly TriggerLogStore TriggerLog;
    public readonly string CurrentCharacterId;
    public readonly string CurrentProcessorName;
    private static readonly Regex TokenRegex = MatchesTokenRegex();

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private readonly string _currentPlayer;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RepeatedData>> _counterTimes = [];
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RepeatedData>> _repeatedTextTimes = [];
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RepeatedData>> _repeatedTimerTimes = [];
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RepeatedData>> _repeatedSpeakTimes = [];
    private readonly ConcurrentDictionary<string, bool> _requiredOverlays = [];
    private readonly ConcurrentDictionary<string, List<TimerData>> _timerLists = [];
    private readonly BlockingCollection<TriggerLogItem> _triggerLogCollection = [];
    private readonly BlockingCollection<LineData> _chatCollection = [];
    private readonly BlockingCollection<Speak> _speakCollection = [];
    private readonly Dictionary<string, TriggerWrapper> _activeTriggersById = [];
    private readonly SemaphoreSlim _activeTriggerSemaphore = new(1, 1);
    private readonly object _repeatedLock = new();
    private IReadOnlyDictionary<string, string> _lexicon;
    private List<TrustedPlayer> _trustedPlayers;
    private volatile string _activeColor;
    private volatile string _fontColor;
    private volatile bool _isDisposed;
    private volatile bool _ready;
    private volatile int _voiceRate;
    private volatile int _playerVolume;
    private Task _triggerLogTask;
    private Task _chatTask;
    private Task _mainTask;
    private Task _speakTask;
    private long _activityLastTicks;
    private LineData _previous;
    private bool _isTesting;

    internal TriggerProcessor(string id, string name, string playerName, string voice, int voiceRate,
      int playerVolume, string activeColor, string fontColor)
    {
      CurrentCharacterId = id;
      CurrentProcessorName = name;
      TriggerLog = new TriggerLogStore(name);
      _currentPlayer = playerName;
      _activeColor = activeColor;
      _fontColor = fontColor;
      _voiceRate = voiceRate;
      _playerVolume = playerVolume;
      AudioManager.Instance.Add(CurrentCharacterId, voice);
      TriggerStateManager.Instance.LexiconUpdateEvent += LexiconUpdateEvent;
      TriggerStateManager.Instance.TrustedPlayersUpdateEvent += TrustedPlayersUpdateEvent;
    }

    internal long GetActivityLastTicks() => Interlocked.Read(ref _activityLastTicks);
    internal List<string> GetRequiredOverlayIds() => [.. _requiredOverlays.Keys];
    internal void SetActiveColor(string color) => _activeColor = color;
    internal void SetFontColor(string color) => _fontColor = color;
    internal void SetPlayerVolume(int volume) => _playerVolume = volume;
    internal void SetVoice(string voice) => AudioManager.Instance.SetVoice(CurrentCharacterId, voice);
    internal void SetVoiceRate(int rate) => _voiceRate = rate;
    internal void SetTesting(bool testing) => _isTesting = testing;

    internal async Task StartAsync()
    {
      await GetActiveTriggersAsync();
      _lexicon = TriggerUtil.ToLexiconDictionary(await TriggerStateManager.Instance.GetLexicon());
      _trustedPlayers = [.. await TriggerStateManager.Instance.GetTrustedPlayers()];
    }

    internal async Task<List<string>> GetEnabledTriggersAsync()
    {
      await _activeTriggerSemaphore.WaitAsync().ConfigureAwait(false);

      try
      {
        return [.. _activeTriggersById.Keys];
      }
      finally
      {
        _activeTriggerSemaphore.Release();
      }
    }

    internal async Task StopTriggersAsync()
    {
      if (_isDisposed) return;
      await _activeTriggerSemaphore.WaitAsync();

      try
      {
        AudioManager.Instance.Stop(CurrentCharacterId);

        foreach (var kv in _timerLists)
        {
          if (_activeTriggersById.TryGetValue(kv.Key, out var wrapper))
          {
            await CleanupTimersAsync(kv.Value, wrapper);
          }
        }
      }
      finally
      {
        _activeTriggerSemaphore.Release();
      }
    }

    internal async Task UpdateActiveTriggers()
    {
      await GetActiveTriggersAsync();
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
            if (_isDisposed) break;
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
            if (_isDisposed) break;
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
            if (_isDisposed) break;
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
          while (!_isDisposed)
          {
            if (collection.TryTake(out var data, 250))
            {
              try
              {
                DoProcessAsync(data.Line, data.Ts).GetAwaiter().GetResult();
              }
              catch
              {
                // ignore
              }
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
    private void LexiconUpdateEvent(List<LexiconItem> update) => _lexicon = TriggerUtil.ToLexiconDictionary(update);
    private void TrustedPlayersUpdateEvent(List<TrustedPlayer> update) => _trustedPlayers = update?.ToList();
    private List<TimerData> GetTimerList(TriggerWrapper w) => _timerLists.GetOrAdd(w.Id, _ => new List<TimerData>(2));

    private async Task DoProcessAsync(string line, double dateTime)
    {
      var localTime = FastTime.Now();
      // ignore anything older than 120 seconds in case a log file is replaced/reloaded but allow for bad lag
      if (!_isTesting && localTime.Seconds - dateTime > 120)
      {
        return;
      }

      Interlocked.Exchange(ref _activityLastTicks, localTime.Ticks);
      var lineData = new LineData { Action = line[27..], BeginTime = dateTime };

      if (_isDisposed) return;
      await _activeTriggerSemaphore.WaitAsync().ConfigureAwait(false);

      try
      {
        var beginTicks = DateTime.UtcNow.Ticks;
        foreach (var wrapper in _activeTriggersById.Values)
        {
          if (CheckLine(wrapper, lineData, out var matches, out var dynamicDuration, out var swTime) &&
              CheckPreviousLine(wrapper, _previous, out var previousMatches, out var previousSwTime))
          {
            swTime += previousSwTime;
            await HandleTriggerAsync(wrapper, lineData, matches, previousMatches, dynamicDuration, swTime, beginTicks);
          }
        }

        foreach (var kv in _timerLists)
        {
          if (kv.Value.Count > 0 && _activeTriggersById.TryGetValue(kv.Key, out var wrapper))
          {
            await CheckTimersAsync(wrapper, kv.Value, lineData);
          }
        }

        // check overlays that need to close
        TriggerOverlayManager.Instance.CheckLine(lineData.Action);
      }
      finally
      {
        _previous = lineData;
        _activeTriggerSemaphore.Release();
      }

      // check only if line might be a quickshare or stop commands
      if (lineData.Action.Contains("{EQLP", StringComparison.OrdinalIgnoreCase) || lineData.Action.Contains("{GINA", StringComparison.OrdinalIgnoreCase))
      {
        if (!_isDisposed && !_chatCollection.IsCompleted)
        {
          try
          {
            _chatCollection.Add(lineData);
          }
          catch (InvalidOperationException)
          {
            // ignore
          }
        }
      }
    }

    private static bool CheckLine(TriggerWrapper wrapper, LineData lineData, out Dictionary<string, string> matches, out double dynamicDuration, out long swTime)
    {
      var found = false;
      dynamicDuration = double.NaN;
      swTime = 0;
      matches = null;

      if (wrapper.IsDisabled)
      {
        swTime = 0;
        return false;
      }

      long ts0;
      if (wrapper.Regex != null)
      {
        ts0 = Stopwatch.GetTimestamp();
        var success = false;
        try
        {
          if (!string.IsNullOrEmpty(wrapper.StartText))
          {
            if (lineData.Action.StartsWith(wrapper.StartText, StringComparison.OrdinalIgnoreCase))
            {
              success = TextUtils.SnapshotMatches(wrapper.Regex.Matches(lineData.Action), out matches);
            }
          }
          else if (!string.IsNullOrEmpty(wrapper.ContainsText))
          {
            if (lineData.Action.Contains(wrapper.ContainsText, StringComparison.OrdinalIgnoreCase))
            {
              success = TextUtils.SnapshotMatches(wrapper.Regex.Matches(lineData.Action), out matches);
            }
          }
          else
          {
            success = TextUtils.SnapshotMatches(wrapper.Regex.Matches(lineData.Action), out matches);
          }
        }
        catch (RegexMatchTimeoutException)
        {
          Log.Warn($"Disabling {wrapper.Name} with slow Regex: {wrapper.TriggerData?.Pattern}");
          wrapper.IsDisabled = true;
          return false;
        }

        found = success && TriggerUtil.CheckOptions(wrapper.RegexNOptions, matches, out dynamicDuration);

        // no need to count failures the user doesn't see
        if (found) swTime = Stopwatch.GetTimestamp() - ts0;
      }
      else if (!string.IsNullOrEmpty(wrapper.ModifiedPattern))
      {
        ts0 = Stopwatch.GetTimestamp();
        found = lineData.Action.Contains(wrapper.ModifiedPattern, StringComparison.OrdinalIgnoreCase);
        // no need to count failures the user doesn't see
        if (found) swTime = Stopwatch.GetTimestamp() - ts0;
      }

      return found;
    }

    private static bool CheckPreviousLine(TriggerWrapper wrapper, LineData lineData, out Dictionary<string, string> matches, out long swTime)
    {
      var found = true;
      swTime = 0;
      matches = null;


      long ts0;
      if (wrapper.PreviousRegex != null)
      {
        if (string.IsNullOrEmpty(lineData?.Action))
        {
          return false;
        }

        ts0 = Stopwatch.GetTimestamp();
        var success = false;

        try
        {
          if (!string.IsNullOrEmpty(wrapper.PreviousStartText))
          {
            if (lineData.Action.StartsWith(wrapper.PreviousStartText, StringComparison.OrdinalIgnoreCase))
            {
              success = TextUtils.SnapshotMatches(wrapper.PreviousRegex.Matches(lineData.Action), out matches);
            }
          }
          else if (!string.IsNullOrEmpty(wrapper.PreviousContainsText))
          {
            if (lineData.Action.Contains(wrapper.PreviousContainsText, StringComparison.OrdinalIgnoreCase))
            {
              success = TextUtils.SnapshotMatches(wrapper.PreviousRegex.Matches(lineData.Action), out matches);
            }
          }
          else
          {
            success = TextUtils.SnapshotMatches(wrapper.PreviousRegex.Matches(lineData.Action), out matches);
          }
        }
        catch (RegexMatchTimeoutException)
        {
          Log.Warn($"Disabling {wrapper.Name} with slow Regex: {wrapper.TriggerData?.PreviousPattern}");
          wrapper.IsDisabled = true;
          return false;
        }

        found = success && TriggerUtil.CheckOptions(wrapper.PreviousRegexNOptions, matches, out _);
        if (found) swTime = Stopwatch.GetTimestamp() - ts0;
      }
      else if (!string.IsNullOrEmpty(wrapper.ModifiedPreviousPattern))
      {
        if (string.IsNullOrEmpty(lineData?.Action))
        {
          return false;
        }

        ts0 = Stopwatch.GetTimestamp();
        found = lineData.Action.Contains(wrapper.ModifiedPreviousPattern, StringComparison.OrdinalIgnoreCase);
        if (found) swTime = Stopwatch.GetTimestamp() - ts0;
      }

      return found;
    }

    private async Task CheckTimersAsync(TriggerWrapper wrapper, List<TimerData> timerList, LineData lineData)
    {
      // Collect side effects to run after releasing lock(timerList)
      List<Speak> speaksToAdd = null;
      List<TriggerLogItem> logsToAdd = null;
      List<TimerData> timersToStopUi = null;
      List<Trigger> overlayTriggers = null;
      List<string> overlayTexts = null;

      lock (timerList)
      {
        List<TimerData> toRemove = null;
        foreach (var timerData in timerList)
        {
          var endEarly = CheckEndEarly(timerData.EndEarlyRegex, timerData.EndEarlyRegexNOptions, timerData.EndEarlyPattern,
            lineData.Action, out var earlyMatches);

          if (!endEarly)
          {
            endEarly = CheckEndEarly(timerData.EndEarlyRegex2, timerData.EndEarlyRegex2NOptions, timerData.EndEarlyPattern2, lineData.Action, out earlyMatches);
          }

          // Repeated threshold option
          if (!endEarly && wrapper.TriggerData != null && wrapper.TriggerData.EndEarlyRepeatedCount > 0 && (wrapper.HasCounterTimer || wrapper.HasRepeatedTimer))
          {
            var stopCount = wrapper.TriggerData.EndEarlyRepeatedCount;

            if ((GetRepeatedCount(_repeatedTimerTimes, wrapper, timerData.DisplayName) >= stopCount) ||
                (GetRepeatedCount(_counterTimes, wrapper, "trigger-count") >= stopCount))
            {
              endEarly = true;
              RemoveRepeatedTimes(_repeatedTimerTimes, wrapper, timerData.DisplayName);
              RemoveRepeatedTimes(_counterTimes, wrapper, "trigger-count");
            }
          }

          if (!endEarly)
          {
            continue;
          }

          // --- Defer: Speak enqueue ---
          var tts = TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.EndEarlySoundToPlay, wrapper.ModifiedEndEarlySpeak, out var isSound);

          // ✅ FALLBACK: if EndEarly is blank, use normal End sound/speak instead
          if (string.IsNullOrEmpty(tts))
          {
            // re-call the helper to determine proper isSound flag for the fallback
            tts = TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.EndSoundToPlay, wrapper.ModifiedEndSpeak, out isSound);
          }

          if (!string.IsNullOrEmpty(tts) && !tts.Equals(NullCode, StringComparison.OrdinalIgnoreCase))
          {
            speaksToAdd ??= new List<Speak>(2);
            speaksToAdd.Add(new Speak
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

          // --- Defer: Overlay text add ---
          var displayTemplate = string.IsNullOrEmpty(wrapper.ModifiedEndEarlyDisplay) ? wrapper.ModifiedEndDisplay : wrapper.ModifiedEndEarlyDisplay;

          if (!string.IsNullOrEmpty(displayTemplate) && !displayTemplate.Equals(NullCode, StringComparison.OrdinalIgnoreCase))
          {
            // It’s safe/cheap to compute the final string here; we only defer the external AddText call
            var updatedDisplayText = ProcessDisplayText(displayTemplate, lineData.Action, earlyMatches, timerData.OriginalMatches, timerData.PreviousMatches);
            if (!string.IsNullOrEmpty(updatedDisplayText))
            {
              if (overlayTriggers == null)
              {
                overlayTriggers = new List<Trigger>(2);
                overlayTexts = new List<string>(2);
              }

              overlayTriggers.Add(wrapper.TriggerData);
              overlayTexts.Add(updatedDisplayText);
            }
          }

          // --- Defer: Log add ---
          logsToAdd ??= new List<TriggerLogItem>(2);
          logsToAdd.Add(new TriggerLogItem(lineData, wrapper, "Timer End Early", 0));

          // --- Remove/cancel timer *under the lock*, but DO NOT call UpdateTimer(...) here ---
          toRemove ??= new List<TimerData>(2);
          toRemove.Add(timerData);
          CleanupTimerData(timerData);
          timersToStopUi ??= new List<TimerData>(2);
          timersToStopUi.Add(timerData);
        }

        // Remove timers from the list after the loop (still under lock)
        if (toRemove != null)
        {
          for (var i = 0; i < toRemove.Count; i++)
          {
            timerList.Remove(toRemove[i]);
          }
        }
      }

      // --- Perform side effects AFTER releasing the lock ---

      // Stop visual timers
      if (timersToStopUi != null)
      {
        for (var i = 0; i < timersToStopUi.Count; i++)
        {
          await TriggerOverlayManager.Instance.UpdateTimerAsync(wrapper.TriggerData, timersToStopUi[i], TriggerOverlayManager.TimerStateChange.Stop);
        }
      }

      // Add overlay texts
      if (overlayTexts != null)
      {
        for (var i = 0; i < overlayTexts.Count; i++)
        {
          await TriggerOverlayManager.Instance.AddTextAsync(overlayTriggers[i], overlayTexts[i], _fontColor);
        }
      }

      // Enqueue speaks
      if (speaksToAdd != null && !_isDisposed && !_speakCollection.IsCompleted)
      {
        for (var i = 0; i < speaksToAdd.Count; i++)
        {
          try
          {
            _speakCollection.Add(speaksToAdd[i]);
          }
          catch (InvalidOperationException)
          {
            // ignore
          }
        }
      }

      // Add logs
      if (logsToAdd != null && !_isDisposed && !_triggerLogCollection.IsCompleted)
      {
        for (var i = 0; i < logsToAdd.Count; i++)
        {
          try
          {
            _triggerLogCollection.Add(logsToAdd[i]);
          }
          catch (InvalidOperationException)
          {
            // ignore
          }
        }
      }
    }

    private async Task HandleTriggerAsync(TriggerWrapper wrapper, LineData lineData, Dictionary<string, string> matches,
      Dictionary<string, string> previousMatches, double dynamicDuration, long swTime, long beginTicks, int loopCount = 0)
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

        if (wrapper.TriggerData.TimerType > 0 && (wrapper.TriggerData.DurationSeconds > 0 ||
             (wrapper.TriggerData.TimerType is 1 or 3 && !double.IsNaN(dynamicDuration) && dynamicDuration > 0)))
        {
          await StartTimerAsync(wrapper, altTimerName, beginTicks, dynamicDuration, lineData, matches, previousMatches, loopCount);
        }
      }

      var tts = TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.SoundToPlay, wrapper.ModifiedSpeak, out var isSound);
      if (!string.IsNullOrEmpty(tts) && !tts.Equals(NullCode, StringComparison.OrdinalIgnoreCase) && !_isDisposed && !_speakCollection.IsCompleted)
      {
        try
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
        catch (InvalidOperationException)
        {
          // ignore
        }
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

        await TriggerOverlayManager.Instance.AddTextAsync(wrapper.TriggerData, updatedDisplayText, _fontColor);
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
          url ??= "Not Set";
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
        try
        {
          _triggerLogCollection.Add(new(lineData, wrapper, "Initial Trigger", swTime * 1_000_000.0 / Stopwatch.Frequency));
        }
        catch (InvalidOperationException)
        {
          // ignore
        }
      }
    }

    private async Task StartTimerAsync(TriggerWrapper wrapper, string displayName, long beginTicks, double dynamicDuration, LineData lineData,
      Dictionary<string, string> matches, Dictionary<string, string> previousMatches, int loopCount = 0)
    {
      var trigger = wrapper.TriggerData;
      var timerList = GetTimerList(wrapper);

      switch (trigger.TriggerAgainOption)
      {
        // Restart Timer Option so clear out everything
        case 1:
          {
            await CleanupTimersAsync(timerList, wrapper);
          }
          break;
        // Restart Timer only if it is already running
        case 2:
          {
            TimerData removed = null;
            lock (timerList)
            {
              if (FindTimerDataByDisplayName(timerList, displayName) is var dataIndex and > -1)
              {
                removed = timerList[dataIndex];
                CleanupTimerData(removed);
                // remove from list
                timerList.RemoveAt(dataIndex);
              }
            }

            if (removed != null)
            {
              await TriggerOverlayManager.Instance.UpdateTimerAsync(trigger, removed, TriggerOverlayManager.TimerStateChange.Stop);
            }
          }
          break;
        // Do nothing if any exist
        case 3:
          {
            lock (timerList)
            {
              if (timerList.Count != 0)
              {
                return;
              }
            }
          }
          break;
        // Do nothing only if a timer with this name is already running
        case 4:
          {
            lock (timerList)
            {
              if (FindTimerDataByDisplayName(timerList, displayName) > -1)
              {
                return;
              }
            }
          }
          break;
      }

      var newTimerData = new TimerData
      {
        ActiveColor = _activeColor ?? trigger.ActiveColor,
        BeginTicks = beginTicks,
        CancelSource = new CancellationTokenSource(),
        CharacterId = CurrentCharacterId,
        DisplayName = displayName,
        FontColor = _fontColor ?? trigger.FontColor,
        Key = wrapper.Id + "-" + displayName,
        OriginalMatches = matches,
        PreviousMatches = previousMatches,
        TimerOverlayIds = new ReadOnlyCollection<string>(trigger.SelectedOverlays),
        TimerIcon = wrapper.TimerIcon,
        TimerType = trigger.TimerType,
        TimesToLoopCount = loopCount,
        TriggerId = wrapper.Id,
        TriggerAgainOption = trigger.TriggerAgainOption,
      };

      newTimerData.DurationSeconds = trigger.DurationSeconds;
      if (wrapper.TriggerData.TimerType is 1 or 3 && !double.IsNaN(dynamicDuration) && dynamicDuration > 0)
      {
        newTimerData.DurationSeconds = dynamicDuration;
      }

      newTimerData.EndTicks = beginTicks + (long)(TimeSpan.TicksPerSecond * newTimerData.DurationSeconds);
      newTimerData.DurationTicks = newTimerData.EndTicks - beginTicks;
      newTimerData.ResetTicks = trigger.ResetDurationSeconds > 0 ? beginTicks + (long)(TimeSpan.TicksPerSecond * trigger.ResetDurationSeconds) : 0;
      newTimerData.ResetDurationTicks = newTimerData.ResetTicks - beginTicks;

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

      // save line data if repeating timer
      if (wrapper.TriggerData.TimerType == 4)
      {
        newTimerData.RepeatingTimerLineData = lineData;
      }

      if (trigger.WarningSeconds > 0 && newTimerData.DurationSeconds - trigger.WarningSeconds is var diff and > 0)
      {
        newTimerData.WarningSource = new CancellationTokenSource();
        var data = newTimerData;
        var warningToken = data.WarningSource.Token;

        _ = Task.Run(async () =>
        {
          try
          {
            await Task.Delay((int)diff * 1000, warningToken);
          }
          catch (OperationCanceledException)
          {
            return;
          }

          if (_isDisposed) return;

          lock (timerList)
          {
            if (data.Warned)
            {
              return;
            }
          }

          var tts = TriggerUtil.GetFromDecodedSoundOrText(trigger.WarningSoundToPlay, wrapper.ModifiedWarningSpeak, out var isSound);
          if (!string.IsNullOrEmpty(tts) && !tts.Equals(NullCode, StringComparison.OrdinalIgnoreCase) && !_isDisposed && !_speakCollection.IsCompleted)
          {
            try
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
            catch (InvalidOperationException)
            {
              // ignore
            }
          }

          if (ProcessDisplayText(wrapper.ModifiedWarningDisplay, lineData.Action, matches, null, previousMatches) is { } updatedDisplayText)
          {
            await TriggerOverlayManager.Instance.AddTextAsync(trigger, updatedDisplayText, _fontColor);
          }

          if (!_isDisposed && !_triggerLogCollection.IsCompleted)
          {
            try
            {
              _triggerLogCollection.Add(new(lineData, wrapper, "Timer Warning", 0));
            }
            catch (InvalidOperationException)
            {
              // ignore
            }
          }
        });
      }

      if (!string.IsNullOrEmpty(wrapper.ModifiedEndEarlyPattern))
      {
        var endEarlyPattern = ProcessMatchesText(wrapper.ModifiedEndEarlyPattern, matches);
        endEarlyPattern = ProcessMatchesText(endEarlyPattern, previousMatches);
        endEarlyPattern = UpdatePattern(trigger.EndUseRegex, endEarlyPattern, out var numberOptions2);

        if (trigger.EndUseRegex)
        {
          newTimerData.EndEarlyRegex = new Regex(endEarlyPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
            RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));
          newTimerData.EndEarlyRegexNOptions = numberOptions2;
        }
        else
        {
          newTimerData.EndEarlyPattern = endEarlyPattern;
        }
      }

      if (!string.IsNullOrEmpty(wrapper.ModifiedEndEarlyPattern2))
      {
        var endEarlyPattern2 = ProcessMatchesText(wrapper.ModifiedEndEarlyPattern2, matches);
        endEarlyPattern2 = ProcessMatchesText(endEarlyPattern2, previousMatches);
        endEarlyPattern2 = UpdatePattern(trigger.EndUseRegex2, endEarlyPattern2, out var numberOptions3);

        if (trigger.EndUseRegex2)
        {
          newTimerData.EndEarlyRegex2 = new Regex(endEarlyPattern2, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
            RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));
          newTimerData.EndEarlyRegex2NOptions = numberOptions3;
        }
        else
        {
          newTimerData.EndEarlyPattern2 = endEarlyPattern2;
        }
      }

      lock (timerList)
      {
        timerList.Add(newTimerData);
      }

      // true for add
      await TriggerOverlayManager.Instance.UpdateTimerAsync(trigger, newTimerData, TriggerOverlayManager.TimerStateChange.Start);

      var data2 = newTimerData;
      var token = data2.CancelSource.Token;

      _ = Task.Run(async () =>
      {
        try
        {
          await Task.Delay((int)(data2.DurationSeconds * 1000), token);
        }
        catch (OperationCanceledException)
        {
          return;
        }

        if (_isDisposed) return;

        bool proceed;
        lock (timerList)
        {
          proceed = !data2.Canceled;
          CleanupTimerData(data2);
          timerList.Remove(data2);
        }

        if (proceed)
        {
          // stop timer
          await TriggerOverlayManager.Instance.UpdateTimerAsync(trigger, data2, TriggerOverlayManager.TimerStateChange.Stop);

          var tts = TriggerUtil.GetFromDecodedSoundOrText(trigger.EndSoundToPlay, wrapper.ModifiedEndSpeak, out var isSound);
          if (!string.IsNullOrEmpty(tts) && !tts.Equals(NullCode, StringComparison.OrdinalIgnoreCase) && !_isDisposed && !_speakCollection.IsCompleted)
          {
            try
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
            catch (InvalidOperationException)
            {
              // ignore
            }
          }

          if (ProcessDisplayText(wrapper.ModifiedEndDisplay, lineData.Action, matches, data2.OriginalMatches, data2.PreviousMatches) is { } updatedDisplayText)
          {
            await TriggerOverlayManager.Instance.AddTextAsync(trigger, updatedDisplayText, _fontColor);
          }

          if (!_isDisposed && !_triggerLogCollection.IsCompleted)
          {
            try
            {
              _triggerLogCollection.Add(new(lineData, wrapper, "Timer End", 0));
            }
            catch (InvalidOperationException)
            {
              // ignore
            }
          }

          // repeating
          if (wrapper.TriggerData.TimerType == 4 && wrapper.TriggerData.TimesToLoop > data2.TimesToLoopCount)
          {
            if (_isDisposed) return;

            await _activeTriggerSemaphore.WaitAsync().ConfigureAwait(false);

            try
            {
              if (!_activeTriggersById.ContainsKey(wrapper.Id)) return;
              // repeat 
              await HandleTriggerAsync(wrapper, data2.RepeatingTimerLineData, data2.OriginalMatches, data2.PreviousMatches, dynamicDuration,
                0, DateTime.UtcNow.Ticks, data2.TimesToLoopCount + 1);
              await CheckTimersAsync(wrapper, timerList, lineData);
            }
            finally
            {
              _activeTriggerSemaphore.Release();
            }
          }
        }
      });
    }

    private void HandleSpeech(Speak speak)
    {
      if (!_ready) return;

      if (!string.IsNullOrEmpty(speak.TtsOrSound))
      {
        var data = speak.Wrapper.TriggerData;
        if (speak.IsSound)
        {
          var theFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "sounds", speak.TtsOrSound);
          AudioManager.Instance.SpeakFileAsync(CurrentCharacterId, theFile, _playerVolume, data.Volume);
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
            tts = TextUtils.ReplaceWholeWords(tts, lexicon);

            if (!string.IsNullOrEmpty(tts))
            {
              tts = ReplaceBadCharsRegex().Replace(tts, string.Empty);
              // trigger voice rate uses 0 for system setting. 1 for default rate, etc
              var rate = data.VoiceRate > 0 ? data.VoiceRate - 1 : _voiceRate;
              AudioManager.Instance.SpeakTtsAsync(CurrentCharacterId, tts, data.Priority, rate, _playerVolume, data.Volume);
            }
          }
        }
      }
    }

    private void HandleChat(LineData lineData)
    {
      if (!_ready) return;

      // look for quick shares after triggers have been processed
      var chatType = ChatLineParser.ParseChatType(lineData.Action);
      if (chatType != null)
      {
        // Look for Stop
        TriggerUtil.CheckForStop(chatType, lineData.Action);

        // Look for Quick Share entries
        TriggerUtil.CheckQuickShare(chatType, lineData.Action, lineData.BeginTime, true, CurrentCharacterId, CurrentProcessorName, _trustedPlayers);
        GinaUtil.CheckGina(_trustedPlayers, chatType, lineData.Action, lineData.BeginTime, CurrentCharacterId, CurrentProcessorName);
      }
    }

    private async Task GetActiveTriggersAsync()
    {
      _ready = false;

      var requiredOverlayIds = new HashSet<string>(StringComparer.Ordinal);
      var activeTriggersById = new Dictionary<string, TriggerWrapper>();
      var enabledTriggers = await TriggerStateManager.Instance.GetEnabledTriggers(CurrentCharacterId);
      long triggerCount = 0;

      foreach (var enabled in enabledTriggers)
      {
        var trigger = enabled.Trigger;
        if (trigger.Pattern is { } pattern && !string.IsNullOrEmpty(pattern))
        {
          try
          {
            triggerCount++;

            pattern = UpdatePattern(trigger.UseRegex, pattern, out var numberOptions);
            pattern = PreProcessCodes(pattern, trigger);
            pattern = UpdateTimePattern(trigger.UseRegex, pattern);
            var modifiedDisplay = PreProcessCodes(trigger.TextToDisplay, trigger);
            var modifiedSpeak = PreProcessCodes(trigger.TextToSpeak, trigger);
            var timerName = string.IsNullOrEmpty(trigger.AltTimerName) ? enabled.Name : trigger.AltTimerName;
            var modifiedTimerName = PreProcessCodes(timerName, trigger);
            var modifiedSendToChat = PreProcessCodes(trigger.TextToSendToChat, trigger);

            var wrapper = new TriggerWrapper
            {
              Id = enabled.Id,
              Name = enabled.Name,
              TriggerData = trigger,
              ModifiedSpeak = modifiedSpeak,
              ModifiedWarningSpeak = PreProcessCodes(trigger.WarningTextToSpeak, trigger),
              ModifiedEndSpeak = PreProcessCodes(trigger.EndTextToSpeak, trigger),
              ModifiedEndEarlySpeak = PreProcessCodes(trigger.EndEarlyTextToSpeak, trigger),
              ModifiedDisplay = modifiedDisplay,
              ModifiedShare = PreProcessCodes(trigger.TextToShare, trigger),
              ModifiedSendToChat = modifiedSendToChat,
              ModifiedWarningDisplay = PreProcessCodes(trigger.WarningTextToDisplay, trigger),
              ModifiedEndDisplay = PreProcessCodes(trigger.EndTextToDisplay, trigger),
              ModifiedEndEarlyDisplay = PreProcessCodes(trigger.EndEarlyTextToDisplay, trigger),
              ModifiedTimerName = string.IsNullOrEmpty(modifiedTimerName) ? "" : modifiedTimerName,
              ModifiedEndEarlyPattern = PreProcessCodes(trigger.EndEarlyPattern, trigger),
              ModifiedEndEarlyPattern2 = PreProcessCodes(trigger.EndEarlyPattern2, trigger),
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
              previousPattern = PreProcessCodes(previousPattern, trigger);
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
                requiredOverlayIds.Add(overlayId);
              }
            }

            // keep track of everything enabled by Id for quick lookup
            activeTriggersById[wrapper.Id] = wrapper;
          }
          catch (Exception)
          {
            // Log.Debug("Bad Trigger?", ex);
          }
        }
      }

      if (triggerCount > 750 && CurrentProcessorName?.Contains("Trigger Tester") == false)
      {
        Log.Warn($"Over {triggerCount} triggers active for one character. To improve performance consider turning off old triggers.");
      }

      await SetActiveTriggersAsync(activeTriggersById, requiredOverlayIds);
      _ready = true;
    }

    private static bool CheckEndEarly(Regex endEarlyRegex, List<NumberOptions> options, string endEarlyPattern,
      string action, out Dictionary<string, string> earlyMatches)
    {
      earlyMatches = null;
      var endEarly = false;

      if (endEarlyRegex != null)
      {
        try
        {
          if (TextUtils.SnapshotMatches(endEarlyRegex.Matches(action), out earlyMatches) && TriggerUtil.CheckOptions(options, earlyMatches, out _))
          {
            endEarly = true;
          }
        }
        catch (RegexMatchTimeoutException)
        {
          // ignore
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

    private static string ProcessDisplayText(string text, string action, Dictionary<string, string> matches,
      Dictionary<string, string> originalMatches, Dictionary<string, string> previousMatches)
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

    private static string ProcessMatchesText(string text, Dictionary<string, string> matches)
    {
      if (matches == null || string.IsNullOrEmpty(text)) return text;

      var matchCollection = TokenRegex.Matches(text);
      if (matchCollection.Count == 0) return text;

      var lastIndex = 0;
      var sb = new StringBuilder(text.Length);
      foreach (Match m in matchCollection)
      {
        sb.Append(text, lastIndex, m.Index - lastIndex);
        lastIndex = m.Index + m.Length;

        var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value;
        var modifier = m.Groups[2].Success ? m.Groups[2].Value :
                       (m.Groups[4].Success ? m.Groups[4].Value : null);

        if (!matches.TryGetValue(name, out var value))
        {
          sb.Append(m.Value);
          continue;
        }

        if (!string.IsNullOrEmpty(modifier))
        {
          switch (modifier.ToLowerInvariant())
          {
            case "number":
              if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
                value = num.ToString("N0", CultureInfo.CurrentCulture);
              break;
            case "upper":
              value = value.ToUpper(CultureInfo.CurrentCulture);
              break;
            case "lower":
              value = value.ToLower(CultureInfo.CurrentCulture);
              break;
            case "capitalize":
              value = TextUtils.ToUpper(value, CultureInfo.CurrentCulture);
              break;
          }
        }

        sb.Append(value);
      }

      if (lastIndex < text.Length)
      {
        sb.Append(text, lastIndex, text.Length - lastIndex);
      }

      return sb.ToString();
    }


    private static string ProcessTts(string tts, string action, Dictionary<string, string> matches, Dictionary<string, string> previous, Dictionary<string, string> original)
    {
      tts = ProcessMatchesText(tts, original);
      tts = ProcessMatchesText(tts, matches);
      tts = ProcessMatchesText(tts, previous);
      tts = ProcessLineCode(tts, action);
      return tts;
    }

    private long GetRepeatedCount(ConcurrentDictionary<string, ConcurrentDictionary<string, RepeatedData>> times, TriggerWrapper wrapper, string displayValue)
    {
      if (!string.IsNullOrEmpty(wrapper.Id))
      {
        lock (_repeatedLock)
        {
          if (times.TryGetValue(wrapper.Id, out var displayTimes))
          {
            if (displayTimes.TryGetValue(displayValue, out var repeatedData))
            {
              return repeatedData.Count;
            }
          }
        }
      }
      return -1;
    }

    private void RemoveRepeatedTimes(ConcurrentDictionary<string, ConcurrentDictionary<string, RepeatedData>> times, TriggerWrapper wrapper, string displayValue)
    {
      if (!string.IsNullOrEmpty(wrapper.Id) && !string.IsNullOrEmpty(displayValue))
      {
        lock (_repeatedLock)
        {
          if (times.TryGetValue(wrapper.Id, out var displayTimes))
          {
            displayTimes.TryRemove(displayValue, out _);
          }
        }
      }
    }

    private long UpdateRepeatedTimes(ConcurrentDictionary<string, ConcurrentDictionary<string, RepeatedData>> times, TriggerWrapper wrapper,
      string displayValue, long beginTicks)
    {
      long repeatedCount = -1;

      if (!string.IsNullOrEmpty(wrapper.Id) && !string.IsNullOrEmpty(displayValue) && wrapper.TriggerData?.RepeatedResetTime >= 0)
      {
        lock (_repeatedLock)
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
            displayTimes = new ConcurrentDictionary<string, RepeatedData>();
            displayTimes[displayValue] = new RepeatedData { Count = 1, CountTicks = beginTicks };
            times[wrapper.Id] = displayTimes;
            repeatedCount = 1;
          }
        }
      }

      return repeatedCount;
    }

    private static string UpdatePattern(bool useRegex, string pattern, out List<NumberOptions> numberOptions)
    {
      numberOptions = [];

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

    private string PreProcessCodes(string text, Trigger trigger)
    {
      if (string.IsNullOrEmpty(text))
      {
        return text;
      }

      // character name
      text = text.Replace(CharacterCode, _currentPlayer ?? string.Empty, StringComparison.OrdinalIgnoreCase);

      // warning time
      text = text.Replace(TimerWarnTimeCode, $"{trigger.WarningSeconds}", StringComparison.OrdinalIgnoreCase);
      return text;
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

    private void HandleLog(LineData lineData, TriggerWrapper wrapper, string type, double eval = 0)
    {
      // update log
      var log = new TriggerLogEntry
      {
        BeginTime = FastTime.Now().Seconds,
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
    private static void CleanupTimerData(TimerData timerData)
    {
      timerData.CancelSource?.Cancel();
      timerData.CancelSource?.Dispose();
      timerData.CancelSource = null;
      timerData.Canceled = true;
      timerData.WarningSource?.Cancel();
      timerData.WarningSource?.Dispose();
      timerData.WarningSource = null;
      timerData.Warned = true;
    }

    // make sure each call is from within lock of TimerList
    private static async Task CleanupTimersAsync(List<TimerData> timerList, TriggerWrapper wrapper)
    {
      if (timerList.Count == 0)
      {
        return;
      }

      TimerData[] toRemove;

      lock (timerList)
      {
        foreach (var timerData in timerList)
        {
          CleanupTimerData(timerData);
        }

        toRemove = [.. timerList];
        timerList.Clear();
      }

      // stop timer
      foreach (var timerData in toRemove)
      {
        await TriggerOverlayManager.Instance.UpdateTimerAsync(wrapper.TriggerData, timerData, TriggerOverlayManager.TimerStateChange.Stop);
      }
    }

    private async Task SetActiveTriggersAsync(Dictionary<string, TriggerWrapper> activeTriggersById, HashSet<string> requiredOverlayIds)
    {
      if (_isDisposed) return;
      await _activeTriggerSemaphore.WaitAsync().ConfigureAwait(false);

      try
      {
        foreach (var old in _activeTriggersById.Values)
        {
          // if timerlist exists and the trigger is no longer active then cleanup
          if (old.Id != null && _timerLists.TryGetValue(old.Id, out var timerList) && !activeTriggersById.ContainsKey(old.Id))
          {
            await CleanupTimersAsync(timerList, old);
            _timerLists.TryRemove(old.Id, out _);

            // purge repeated counters for this trigger id
            _counterTimes.TryRemove(old.Id, out _);
            _repeatedTextTimes.TryRemove(old.Id, out _);
            _repeatedTimerTimes.TryRemove(old.Id, out _);
            _repeatedSpeakTimes.TryRemove(old.Id, out _);
          }
        }
      }
      finally
      {
        _activeTriggersById.Clear();

        foreach (var kv in activeTriggersById)
        {
          _activeTriggersById[kv.Key] = kv.Value;
        }

        _requiredOverlays.Clear();
        foreach (var id in requiredOverlayIds)
        {
          _requiredOverlays[id] = true;
        }

        _activeTriggerSemaphore.Release();
      }
    }

    // avoid LINQ for performance
    private static int FindTimerDataByDisplayName(List<TimerData> list, string name)
    {
      for (var i = 0; i < list.Count; i++)
      {
        if (string.Equals(list[i].DisplayName, name, StringComparison.OrdinalIgnoreCase))
        {
          return i;
        }
      }

      return -1;
    }

    public void Dispose()
    {
      if (!_isDisposed)
      {
        StopTriggersAsync().GetAwaiter().GetResult();
        _isDisposed = true;
        _ready = false;

        TriggerStateManager.Instance.LexiconUpdateEvent -= LexiconUpdateEvent;
        TriggerStateManager.Instance.TrustedPlayersUpdateEvent -= TrustedPlayersUpdateEvent;
        _triggerLogCollection.CompleteAdding();
        _chatCollection.CompleteAdding();
        _speakCollection.CompleteAdding();

        try
        {
          foreach (var task in new Task[] { _speakTask, _chatTask, _triggerLogTask, _mainTask })
          {
            task?.Wait();
          }
        }
        finally
        {
          _triggerLogCollection.Dispose();
          _chatCollection.Dispose();
          _speakCollection.Dispose();
        }
      }
    }

    private readonly record struct TriggerLogItem(LineData LineData, TriggerWrapper Wrapper, string Type, double Eval);

    private class Speak
    {
      public bool IsPrimary { get; init; }
      public TriggerWrapper Wrapper { get; init; }
      public string TtsOrSound { get; init; }
      public bool IsSound { get; init; }
      public string Action { get; init; }
      public Dictionary<string, string> Matches { get; init; }
      public Dictionary<string, string> Previous { get; init; }
      public Dictionary<string, string> Original { get; init; }
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
      public string ModifiedEndEarlyPattern { get; init; }
      public string ModifiedEndEarlyPattern2 { get; init; }
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
    }

    [GeneratedRegex(@"{(s\d?)}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReplaceStringRegex();
    [GeneratedRegex(@"{(n\d?)(<=|>=|>|<|=|==)?(\d+)?}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReplaceNumberRegex();
    [GeneratedRegex(@"{(ts)}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReplaceTsRegex();
    [GeneratedRegex(@"[^a-zA-Z0-9 .,!?;:'""-()]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReplaceBadCharsRegex();
    [GeneratedRegex(@"\$\{([a-zA-Z0-9_]+)(?:\.([a-zA-Z0-9_]+))?\}|\{([a-zA-Z0-9_]+)(?:\.([a-zA-Z0-9_]+))?\}", RegexOptions.Compiled)]
    private static partial Regex MatchesTokenRegex();
  }
}
