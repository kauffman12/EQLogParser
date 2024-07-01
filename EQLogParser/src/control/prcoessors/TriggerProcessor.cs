﻿using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Windows.Media.SpeechSynthesis;

namespace EQLogParser
{
  internal partial class TriggerProcessor : ILogProcessor
  {
    public readonly ObservableCollection<AlertEntry> AlertLog = [];
    public readonly string CurrentCharacterId;
    public readonly string CurrentProcessorName;
    public long ActivityLastTicks;
    private const long SixtyHours = 10 * 6 * 60 * 60 * 1000;
    private readonly string _currentPlayer;
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private readonly object _collectionLock = new();
    private readonly object _activeTriggerLock = new();
    private readonly object _voiceLock = new();
    private readonly List<TimerData> _activeTimers = [];
    private readonly Dictionary<string, Dictionary<string, RepeatedData>> _repeatedTextTimes = [];
    private readonly Dictionary<string, Dictionary<string, RepeatedData>> _repeatedTimerTimes = [];
    private readonly Action<string, Trigger, string> _addTextEvent;
    private readonly Action<Trigger, List<TimerData>> _addTimerEvent;
    private readonly SpeechSynthesizer _synth;
    private readonly BlockingCollection<Speak> _speakCollection = [];
    private readonly BlockingCollection<LineData> _chatCollection = [];
    private readonly BlockingCollection<string> _triggerTimeCollection = [];
    private readonly string _activeColor;
    private readonly string _fontColor;
    private readonly CancellationTokenSource _cts = new();
    private List<TriggerWrapper> _activeTriggers;
    private List<LexiconItem> _lexicon;
    private Task _speakTask;
    private Task _chatTask;
    private Task _triggerTimeTask;
    private Task _processTask;
    private bool _isTesting;
    private bool _ready;
    private bool _isDisposed;

    internal TriggerProcessor(string id, string name, string playerName, string voice, int voiceRate,
      string activeColor, string fontColor, Action<string, Trigger, string> addTextEvent, Action<Trigger, List<TimerData>> addTimerEvent)
    {
      CurrentCharacterId = id;
      CurrentProcessorName = name;
      _currentPlayer = playerName;
      _activeColor = activeColor;
      _fontColor = fontColor;
      _addTextEvent = addTextEvent;
      _addTimerEvent = addTimerEvent;
      _synth = AudioManager.CreateSpeechSynthesizer();
      AudioManager.Instance.Add(CurrentCharacterId);

      BindingOperations.EnableCollectionSynchronization(AlertLog, _collectionLock);
      GetActiveTriggers();
      SetVoice(voice);
      SetVoiceRate(voiceRate);
      _lexicon = TriggerStateManager.Instance.GetLexicon().ToList();
      TriggerStateManager.Instance.LexiconUpdateEvent += LexiconUpdateEvent;
    }

    public void LinkTo(BlockingCollection<Tuple<string, double, bool>> collection)
    {
      _speakTask = Task.Run(() =>
      {
        try
        {
          // ReSharper disable once InconsistentlySynchronizedField
          foreach (var data in _speakCollection.GetConsumingEnumerable(_cts.Token))
          {
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
      });

      _chatTask = Task.Run(() =>
      {
        try
        {
          foreach (var data in _chatCollection.GetConsumingEnumerable(_cts.Token))
          {
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

      _triggerTimeTask = Task.Run(() =>
      {
        try
        {
          foreach (var data in _triggerTimeCollection.GetConsumingEnumerable(_cts.Token))
          {
            try
            {
              var beginTicks = DateTime.UtcNow.Ticks;
              var updatedTime = beginTicks / TimeSpan.TicksPerMillisecond;
              TriggerStateManager.Instance.UpdateLastTriggered(data, updatedTime);
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

      _processTask = Task.Run(() =>
      {
        try
        {
          foreach (var data in collection.GetConsumingEnumerable(_cts.Token))
          {
            try
            {
              DoProcess(data.Item1, data.Item2);
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

        // cleanup on process exit
        lock (_activeTriggerLock)
        {
          _activeTriggers.ToList().ForEach(CleanupWrapper);
        }
      });
    }

    internal List<TimerData> GetActiveTimers()
    {
      lock (_activeTimers) return [.. _activeTimers];
    }

    internal void SetVoice(string voice)
    {
      if (_synth != null && !string.IsNullOrEmpty(voice))
      {
        lock (_voiceLock)
        {
          if (_synth.Voice?.DisplayName != voice)
          {
            _synth.Voice = AudioManager.GetVoice(voice);
          }
        }
      }
    }

    internal void SetVoiceRate(int rate)
    {
      if (_synth != null)
      {
        lock (_voiceLock)
        {
          _synth.Options.SpeakingRate = AudioManager.GetSpeakingRate(rate);
        }
      }
    }

    internal void SetTesting(bool testing)
    {
      _isTesting = testing;
    }

    internal void UpdateActiveTriggers()
    {
      lock (_activeTriggerLock)
      {
        _ready = false;
        _activeTriggers.ToList().ForEach(CleanupWrapper);
        GetActiveTriggers();
      }

      lock (_voiceLock)
      {
        AudioManager.Instance.Stop(CurrentCharacterId);
      }
    }

    private static string ModLine(string text, string line) => !string.IsNullOrEmpty(text) ?
      text.Replace("{l}", line, StringComparison.OrdinalIgnoreCase) : text;
    private static string ModCounter(string text) => !string.IsNullOrEmpty(text) ?
      text.Replace("{counter}", "{repeated}", StringComparison.OrdinalIgnoreCase) : text;
    private string ModPlayer(string text) => !string.IsNullOrEmpty(text) ?
      text.Replace("{c}", _currentPlayer ?? string.Empty, StringComparison.OrdinalIgnoreCase) : text;

    private void LexiconUpdateEvent(List<LexiconItem> update)
    {
      lock (_voiceLock)
      {
        _lexicon = update;
      }
    }

    private void DoProcess(string line, double dateTime)
    {
      // ignore anything older than 120 seconds in case a log file is replaced/reloaded but allow for bad lag
      if (!_isTesting && DateUtil.ToDouble(DateTime.Now) - dateTime > 120)
      {
        return;
      }

      ActivityLastTicks = DateTime.UtcNow.Ticks;
      var lineData = new LineData { Action = line[27..], BeginTime = dateTime };
      lock (_activeTriggerLock)
      {
        foreach (var wrapper in CollectionsMarshal.AsSpan(_activeTriggers))
        {
          if (CheckLine(wrapper, lineData, out var matches, out var timing))
          {
            HandleTrigger(wrapper, lineData, matches, timing);
          }

          CheckTimers(wrapper, lineData);
        }
      }

      // await Task.WhenAll(processingTasks);
      if (!_chatCollection.IsCompleted)
      {
        _chatCollection.Add(lineData);
      }
    }

    private static bool CheckLine(TriggerWrapper wrapper, LineData lineData, out MatchCollection matches, out long timing)
    {
      var beginTicks = DateTime.UtcNow.Ticks;
      var found = false;
      matches = null;

      lock (wrapper)
      {
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
      }

      timing = DateTime.UtcNow.Ticks - beginTicks;
      return found;
    }

    private void CheckTimers(TriggerWrapper wrapper, LineData lineData)
    {
      lock (wrapper)
      {
        foreach (var timerData in CollectionsMarshal.AsSpan(wrapper.TimerList))
        {
          var endEarly = CheckEndEarly(timerData.EndEarlyRegex, timerData.EndEarlyRegexNOptions, timerData.EndEarlyPattern,
            lineData.Action, out var earlyMatches);

          // try 2nd
          if (!endEarly)
          {
            endEarly = CheckEndEarly(timerData.EndEarlyRegex2, timerData.EndEarlyRegex2NOptions, timerData.EndEarlyPattern2, lineData.Action, out earlyMatches);
          }

          if (endEarly)
          {
            Speak speak = null;
            var tts = TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.EndEarlySoundToPlay, wrapper.ModifiedEndEarlySpeak, out var isSound);
            tts = string.IsNullOrEmpty(tts) ? TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.EndSoundToPlay, wrapper.ModifiedEndSpeak, out isSound) : tts;
            var displayText = string.IsNullOrEmpty(wrapper.ModifiedEndEarlyDisplay) ? wrapper.ModifiedEndDisplay : wrapper.ModifiedEndEarlyDisplay;

            if (!string.IsNullOrEmpty(tts))
            {
              speak = new Speak
              {
                Wrapper = wrapper,
                TtsOrSound = tts,
                IsSound = isSound,
                Matches = earlyMatches,
                OriginalMatches = timerData.OriginalMatches,
                Action = lineData.Action
              };
            }

            if (ProcessDisplayText(displayText, lineData.Action, earlyMatches, timerData.OriginalMatches) is { } updatedDisplayText)
            {
              _addTextEvent(updatedDisplayText, wrapper.TriggerData, _fontColor);
            }

            AddEntry(lineData, wrapper, "Timer End Early");

            if (speak != null && !_speakCollection.IsCompleted)
            {
              _speakCollection.Add(speak);
            }

            CleanupTimer(wrapper, timerData);
          }
        }
      }
    }

    private void HandleTrigger(TriggerWrapper wrapper, LineData lineData, MatchCollection matches, long timing, int loopCount = 0)
    {
      lock (wrapper)
      {
        var beginTicks = DateTime.UtcNow.Ticks;
        if (loopCount == 0 && wrapper.TriggerData.LockoutTime > 0)
        {
          if (wrapper.LockedOutTicks > 0 && beginTicks <= wrapper.LockedOutTicks)
          {
            // during lockout do nothing
            Log.Warn("LOCKOUT");
            return;
          }

          // update lockout time
          wrapper.LockedOutTicks = beginTicks + (wrapper.TriggerData.LockoutTime * TimeSpan.TicksPerMillisecond);
        }

        // no need to constantly updated the DB. 6 hour check
        var updatedTime = beginTicks / TimeSpan.TicksPerMillisecond;
        if (updatedTime - wrapper.TriggerData.LastTriggered > SixtyHours)
        {
          if (!_triggerTimeCollection.IsCompleted)
          {
            _triggerTimeCollection.Add(wrapper.Id);
          }
          wrapper.TriggerData.LastTriggered = updatedTime;
        }

        if (ProcessMatchesText(wrapper.ModifiedTimerName, matches) is { } displayName)
        {
          displayName = ModLine(displayName, lineData.Action);
          if (wrapper.HasRepeatedTimer)
          {
            UpdateRepeatedTimes(_repeatedTimerTimes, wrapper, displayName, beginTicks);
          }

          if (wrapper.TriggerData.TimerType > 0 && wrapper.TriggerData.DurationSeconds > 0)
          {
            StartTimer(wrapper, displayName, beginTicks, lineData, matches, loopCount);
          }
        }

        Speak speak = null;
        var tts = TriggerUtil.GetFromDecodedSoundOrText(wrapper.TriggerData.SoundToPlay, wrapper.ModifiedSpeak, out var isSound);
        if (!string.IsNullOrEmpty(tts))
        {
          speak = new Speak
          {
            Wrapper = wrapper,
            TtsOrSound = tts,
            IsSound = isSound,
            Matches = matches,
            Action = lineData.Action
          };
        }

        if (ProcessDisplayText(wrapper.ModifiedDisplay, lineData.Action, matches, null) is { } updatedDisplayText)
        {
          if (wrapper.HasRepeatedText)
          {
            var currentCount = UpdateRepeatedTimes(_repeatedTextTimes, wrapper, updatedDisplayText, beginTicks);
            updatedDisplayText = updatedDisplayText.Replace("{repeated}", currentCount.ToString(), StringComparison.OrdinalIgnoreCase);
          }

          _addTextEvent(updatedDisplayText, wrapper.TriggerData, _fontColor);
        }

        if (ProcessDisplayText(wrapper.ModifiedShare, lineData.Action, matches, null) is { } updatedShareText)
        {
          UiUtil.InvokeAsync(() => Clipboard.SetText(updatedShareText));
        }

        AddEntry(lineData, wrapper, "Trigger", timing / 10);

        if (speak != null)
        {
          if (!_speakCollection.IsCompleted)
          {
            _speakCollection.Add(speak);
          }
        }
      }
    }

    private void StartTimer(TriggerWrapper wrapper, string displayName, long beginTicks, LineData lineData,
      MatchCollection matches, int loopCount = 0)
    {
      var trigger = wrapper.TriggerData;
      switch (trigger.TriggerAgainOption)
      {
        // Restart Timer Option so clear out everything
        case 1:
          CleanupWrapper(wrapper);
          break;
        // Restart Timer only if it is already running
        case 2:
          {
            if (wrapper.TimerList.FirstOrDefault(data => displayName.Equals(data?.DisplayName, StringComparison.OrdinalIgnoreCase))
                is { } timerData)
            {
              CleanupTimer(wrapper, timerData);
            }

            break;
          }
        // Do nothing if any exist
        case 3 when wrapper.TimerList.Count != 0:
          {
            return;
          }
        // Do nothing only if a timer with this name is already running
        case 4 when wrapper.TimerList.FirstOrDefault(data => displayName.Equals(data?.DisplayName, StringComparison.OrdinalIgnoreCase))
          is not null:
          {
            return;
          }
      }

      TimerData newTimerData = null;
      if (trigger.WarningSeconds > 0 && wrapper.ModifiedDurationSeconds - trigger.WarningSeconds is var diff and > 0)
      {
        newTimerData = new TimerData { DisplayName = displayName, WarningSource = new CancellationTokenSource() };

        var data = newTimerData;
        Task.Delay((int)diff * 1000).ContinueWith(_ =>
        {
          var proceed = false;
          lock (wrapper)
          {
            if (data.WarningSource != null)
            {
              proceed = !data.WarningSource.Token.IsCancellationRequested;
              data.WarningSource.Dispose();
              data.WarningSource = null;
            }

            if (proceed)
            {
              var speak = TriggerUtil.GetFromDecodedSoundOrText(trigger.WarningSoundToPlay, wrapper.ModifiedWarningSpeak, out var isSound);
              if (!_speakCollection.IsCompleted)
              {
                _speakCollection.Add(new Speak
                {
                  Wrapper = wrapper,
                  TtsOrSound = speak,
                  IsSound = isSound,
                  Matches = matches,
                  Action = lineData.Action
                });
              }

              if (ProcessDisplayText(wrapper.ModifiedWarningDisplay, lineData.Action, matches, null) is { } updatedDisplayText)
              {
                _addTextEvent(updatedDisplayText, trigger, _fontColor);
              }

              AddEntry(lineData, wrapper, "Timer Warning");
            }
          }
        }, newTimerData.WarningSource.Token);
      }

      newTimerData ??= new TimerData { DisplayName = displayName };

      if (wrapper.HasRepeatedTimer)
      {
        newTimerData.RepeatedCount = GetRepeatedCount(_repeatedTimerTimes, wrapper, displayName);
      }

      newTimerData.BeginTicks = beginTicks;
      newTimerData.EndTicks = beginTicks + (long)(TimeSpan.TicksPerMillisecond * wrapper.ModifiedDurationSeconds * 1000);
      newTimerData.DurationTicks = newTimerData.EndTicks - beginTicks;
      newTimerData.ResetTicks = trigger.ResetDurationSeconds > 0 ?
        beginTicks + (long)(TimeSpan.TicksPerSecond * trigger.ResetDurationSeconds) : 0;
      newTimerData.ResetDurationTicks = newTimerData.ResetTicks - beginTicks;
      newTimerData.TimerOverlayIds = wrapper.TimerOverlayIds;
      newTimerData.TriggerAgainOption = trigger.TriggerAgainOption;
      newTimerData.TimerType = trigger.TimerType;
      newTimerData.OriginalMatches = matches;
      newTimerData.ActiveColor = _activeColor ?? trigger.ActiveColor;
      newTimerData.FontColor = _fontColor ?? trigger.FontColor;
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
        endEarlyPattern = UpdatePattern(trigger.EndUseRegex, endEarlyPattern, out var numberOptions2);

        if (trigger.EndUseRegex)
        {
          newTimerData.EndEarlyRegex = new Regex(endEarlyPattern, RegexOptions.IgnoreCase);
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
        endEarlyPattern2 = UpdatePattern(trigger.EndUseRegex2, endEarlyPattern2, out var numberOptions3);

        if (trigger.EndUseRegex2)
        {
          newTimerData.EndEarlyRegex2 = new Regex(endEarlyPattern2, RegexOptions.IgnoreCase);
          newTimerData.EndEarlyRegex2NOptions = numberOptions3;
        }
        else
        {
          newTimerData.EndEarlyPattern2 = endEarlyPattern2;
        }
      }

      wrapper.TimerList.Add(newTimerData);
      var needEvent = wrapper.TimerList.Count == 1;

      var data2 = newTimerData;
      Task.Delay((int)(wrapper.ModifiedDurationSeconds * 1000)).ContinueWith(_ =>
      {
        var proceed = false;
        lock (wrapper)
        {
          if (data2.CancelSource != null)
          {
            proceed = !data2.CancelSource.Token.IsCancellationRequested;
          }

          if (proceed)
          {
            var speak = TriggerUtil.GetFromDecodedSoundOrText(trigger.EndSoundToPlay, wrapper.ModifiedEndSpeak, out var isSound);

            if (!_speakCollection.IsCompleted)
            {
              _speakCollection.Add(new Speak
              {
                Wrapper = wrapper,
                TtsOrSound = speak,
                IsSound = isSound,
                Matches = matches,
                OriginalMatches = data2.OriginalMatches,
                Action = lineData.Action
              });
            }

            if (ProcessDisplayText(wrapper.ModifiedEndDisplay, lineData.Action, matches, data2.OriginalMatches) is { } updatedDisplayText)
            {
              _addTextEvent(updatedDisplayText, trigger, _fontColor);
            }

            AddEntry(lineData, wrapper, "Timer End");
            CleanupTimer(wrapper, data2);

            // repeating
            if (_ready && wrapper.TriggerData.TimerType == 4 && wrapper.TriggerData.TimesToLoop > data2.TimesToLoopCount)
            {
              // repeat normal process
              if (CheckLine(wrapper, data2.RepeatingTimerLineData, out var matchAgain, out var timing))
              {
                HandleTrigger(wrapper, data2.RepeatingTimerLineData, matchAgain, timing, data2.TimesToLoopCount + 1);
              }

              CheckTimers(wrapper, lineData);
            }
          }
        }
      }, newTimerData.CancelSource.Token);

      lock (_activeTimers)
      {
        _activeTimers.Add(newTimerData);
      }

      if (needEvent)
      {
        _addTimerEvent(trigger, GetActiveTimers());
      }
    }

    private void HandleSpeech(Speak speak)
    {
      lock (_activeTriggerLock) if (!_ready) return;

      if (!string.IsNullOrEmpty(speak.TtsOrSound))
      {
        if (speak.IsSound)
        {
          var theFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "sounds", speak.TtsOrSound);

          // locking here just to handle the Stop case better on trigger reset
          // we don't start playing a sound at the same time we try to stop them all
          lock (_voiceLock)
          {
            AudioManager.Instance.SpeakFileAsync(CurrentCharacterId, theFile, speak.Wrapper.TriggerData.Priority);
          }
        }
        else
        {
          if (_synth != null)
          {
            var tts = ProcessMatchesText(speak.TtsOrSound, speak.OriginalMatches);
            tts = ProcessMatchesText(tts, speak.Matches);
            tts = ModLine(tts, speak.Action);

            if (!string.IsNullOrEmpty(tts))
            {
              foreach (var item in CollectionsMarshal.AsSpan(_lexicon))
              {
                if (item != null && !string.IsNullOrEmpty(item.Replace) && !string.IsNullOrEmpty(item.With))
                {
                  tts = tts.Replace(item.Replace, item.With, StringComparison.OrdinalIgnoreCase);
                }
              }

              if (!string.IsNullOrEmpty(tts))
              {
                tts = ReplaceBadCharsRegex().Replace(tts, string.Empty);
                lock (_voiceLock)
                {
                  AudioManager.Instance.SpeakTtsAsync(CurrentCharacterId, _synth, tts, speak.Wrapper.TriggerData.Priority);
                }
              }
            }
          }
        }
      }
    }

    private void HandleChat(LineData lineData)
    {
      // look for quick shares after triggers have been processed
      var chatType = ChatLineParser.ParseChatType(lineData.Action);
      if (chatType != null && TriggerStateManager.Instance.IsActive())
      {
        // Look for Quick Share entries
        TriggerUtil.CheckQuickShare(chatType, lineData.Action, lineData.BeginTime, CurrentCharacterId, CurrentProcessorName);
        GinaUtil.CheckGina(chatType, lineData.Action, lineData.BeginTime, CurrentCharacterId, CurrentProcessorName);
      }
    }

    private void GetActiveTriggers()
    {
      var activeTriggers = new List<TriggerWrapper>();
      var enabledTriggers = TriggerStateManager.Instance.GetEnabledTriggers(CurrentCharacterId);
      var timerOverlayCache = new Dictionary<string, bool>();
      long triggerCount = 0;
      foreach (var enabled in enabledTriggers.OrderByDescending(enabled => enabled.Trigger.LastTriggered))
      {
        var trigger = enabled.Trigger;
        if (trigger.Pattern is { } pattern && !string.IsNullOrEmpty(pattern))
        {
          try
          {
            triggerCount++;
            var wrapper = new TriggerWrapper
            {
              Id = enabled.Id,
              Name = enabled.Name,
              TriggerData = trigger,
              ModifiedSpeak = ModPlayer(trigger.TextToSpeak),
              ModifiedWarningSpeak = ModPlayer(trigger.WarningTextToSpeak),
              ModifiedEndSpeak = ModPlayer(trigger.EndTextToSpeak),
              ModifiedEndEarlySpeak = ModPlayer(trigger.EndEarlyTextToSpeak),
              ModifiedDisplay = ModPlayer(trigger.TextToDisplay),
              ModifiedShare = ModPlayer(trigger.TextToShare),
              ModifiedWarningDisplay = ModPlayer(trigger.WarningTextToDisplay),
              ModifiedEndDisplay = ModPlayer(trigger.EndTextToDisplay),
              ModifiedEndEarlyDisplay = ModPlayer(trigger.EndEarlyTextToDisplay),
              ModifiedTimerName = ModPlayer(string.IsNullOrEmpty(trigger.AltTimerName) ? enabled.Name : trigger.AltTimerName),
              ModifiedDurationSeconds = trigger.DurationSeconds,
              TimerIcon = UiElementUtil.CreateBitmap(trigger.IconSource)
            };

            // replace GINA counted with repeated
            wrapper.ModifiedDisplay = ModCounter(wrapper.ModifiedDisplay);
            wrapper.ModifiedTimerName = ModCounter(wrapper.ModifiedTimerName);

            // get overlays for timers
            wrapper.TriggerData.SelectedOverlays?.ForEach(overlayId =>
            {
              if (timerOverlayCache.TryGetValue(overlayId, out var isTimer))
              {
                if (isTimer)
                {
                  wrapper.TimerOverlayIds.Add(overlayId);
                }
              }
              else
              {
                if (TriggerStateManager.Instance.GetOverlayById(overlayId) is { } overlay && overlay.OverlayData.IsTimerOverlay)
                {
                  wrapper.TimerOverlayIds.Add(overlayId);
                  timerOverlayCache[overlayId] = true;
                }
                else
                {
                  timerOverlayCache[overlayId] = false;
                }
              }
            });

            wrapper.ModifiedTimerName = string.IsNullOrEmpty(wrapper.ModifiedTimerName) ? "" : wrapper.ModifiedTimerName;
            wrapper.HasRepeatedText = wrapper.ModifiedDisplay?.Contains("{repeated}", StringComparison.OrdinalIgnoreCase) == true;
            wrapper.HasRepeatedTimer = wrapper.ModifiedTimerName?.Contains("{repeated}", StringComparison.OrdinalIgnoreCase) == true;
            pattern = UpdatePattern(trigger.UseRegex, pattern, out var numberOptions);
            pattern = UpdateTimePattern(trigger.UseRegex, pattern);

            // temp
            if (wrapper.TriggerData.EnableTimer && wrapper.TriggerData.TimerType == 0)
            {
              wrapper.TriggerData.TimerType = 1;
            }

            if (trigger.UseRegex)
            {
              wrapper.Regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));
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
            else
            {
              wrapper.ModifiedPattern = pattern;
            }

            activeTriggers.Add(wrapper);
          }
          catch (Exception ex)
          {
            Log.Debug("Bad Trigger?", ex);
          }
        }

        _ready = true;
      }

      if (triggerCount > 400 && CurrentProcessorName?.Contains("Trigger Tester") == false)
      {
        Log.Warn($"Over {triggerCount} triggers active for one character. To improve performance consider turning off old triggers.");
      }

      _activeTriggers = activeTriggers;
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

    private static string ProcessDisplayText(string text, string line, MatchCollection matches, MatchCollection originalMatches)
    {
      if (!string.IsNullOrEmpty(text))
      {
        text = ProcessMatchesText(text, originalMatches);
        text = ProcessMatchesText(text, matches);
        text = ModLine(text, line);
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

    private static long UpdateRepeatedTimes(IDictionary<string, Dictionary<string, RepeatedData>> times, TriggerWrapper wrapper,
      string displayValue, long beginTicks)
    {
      long currentCount = -1;

      if (!string.IsNullOrEmpty(wrapper.Id) && wrapper.TriggerData?.RepeatedResetTime >= 0)
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

            currentCount = repeatedData.Count;
          }
          else
          {
            displayTimes[displayValue] = new RepeatedData { Count = 1, CountTicks = beginTicks };
            currentCount = 1;
          }
        }
        else
        {
          displayTimes = new Dictionary<string, RepeatedData>
          {
            { displayValue, new RepeatedData { Count = 1, CountTicks = beginTicks } }
          };

          times[wrapper.Id] = displayTimes;
          currentCount = 1;
        }
      }

      return currentCount;
    }

    private string UpdatePattern(bool useRegex, string pattern, out List<NumberOptions> numberOptions)
    {
      numberOptions = [];
      pattern = ModPlayer(pattern);

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

    private void AddEntry(LineData lineData, TriggerWrapper wrapper, string type, long eval = 0)
    {
      // update log
      var log = new AlertEntry
      {
        EventTime = DateUtil.ToDouble(DateTime.Now),
        LogTime = lineData?.BeginTime ?? double.NaN,
        Line = lineData?.Action ?? "",
        Name = wrapper.Name,
        Type = type,
        Eval = eval,
        NodeId = wrapper.Id,
        Priority = wrapper.TriggerData.Priority,
        CharacterId = CurrentCharacterId
      };

      lock (_collectionLock)
      {
        AlertLog.Insert(0, log);
        if (AlertLog.Count > 5000)
        {
          AlertLog.RemoveAt(AlertLog.Count - 1);
        }
      }
    }

    private void CleanupTimer(TriggerWrapper wrapper, TimerData timerData)
    {
      timerData.CancelSource?.Cancel();
      timerData.CancelSource?.Dispose();
      timerData.WarningSource?.Cancel();
      timerData.WarningSource?.Dispose();
      timerData.CancelSource = null;
      timerData.WarningSource = null;
      wrapper.TimerList.Remove(timerData);

      lock (_activeTimers)
      {
        _activeTimers.Remove(timerData);
      }
    }

    private void CleanupWrapper(TriggerWrapper wrapper)
    {
      lock (wrapper)
      {
        // need an ew list because CleanupTimer will update TimerList
        wrapper.TimerList.ToList().ForEach(timerData => CleanupTimer(wrapper, timerData));
      }
    }

    public void Dispose()
    {
      if (!_isDisposed)
      {
        _isDisposed = true;
        _ready = false;
        AudioManager.Instance.Stop(CurrentCharacterId, true);
        TriggerStateManager.Instance.LexiconUpdateEvent -= LexiconUpdateEvent;
        _speakCollection.CompleteAdding();
        _chatCollection.CompleteAdding();
        _triggerTimeCollection.CompleteAdding();
        _cts?.Cancel();
        _speakTask?.Wait();
        _chatTask?.Wait();
        _triggerTimeTask?.Wait();
        _processTask?.Wait();
        _speakTask?.Dispose();
        _chatTask?.Dispose();
        _triggerTimeTask?.Dispose();
        _processTask?.Dispose();
        _speakCollection?.Dispose();
        _chatCollection?.Dispose();
        _triggerTimeCollection?.Dispose();
        _cts?.Dispose();

        lock (_voiceLock)
        {
          _synth?.Dispose();
        }
      }
    }

    private class Speak
    {
      public TriggerWrapper Wrapper { get; init; }
      public string TtsOrSound { get; init; }
      public bool IsSound { get; init; }
      public MatchCollection Matches { get; init; }
      public MatchCollection OriginalMatches { get; init; }
      public string Action { get; init; }
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
      public List<TimerData> TimerList { get; } = [];
      public string ModifiedPattern { get; set; }
      public string ModifiedSpeak { get; init; }
      public string ModifiedEndSpeak { get; init; }
      public string ModifiedEndEarlySpeak { get; init; }
      public string ModifiedWarningSpeak { get; init; }
      public string ModifiedDisplay { get; set; }
      public string ModifiedShare { get; init; }
      public string ModifiedEndDisplay { get; init; }
      public string ModifiedEndEarlyDisplay { get; init; }
      public string ModifiedWarningDisplay { get; init; }
      public string ModifiedTimerName { get; set; }
      public double ModifiedDurationSeconds { get; set; }
      public List<string> TimerOverlayIds { get; } = [];
      public Regex Regex { get; set; }
      public List<NumberOptions> RegexNOptions { get; set; }
      public Trigger TriggerData { get; init; }
      public bool HasRepeatedTimer { get; set; }
      public bool HasRepeatedText { get; set; }
      public bool IsDisabled { get; set; }
      public string ContainsText { get; set; }
      public string StartText { get; set; }
      public BitmapImage TimerIcon { get; set; }
      public double LockedOutTicks { get; set; }
    }

    [GeneratedRegex(@"{(s\d?)}", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex ReplaceStringRegex();
    [GeneratedRegex(@"{(n\d?)(<=|>=|>|<|=|==)?(\d+)?}", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex ReplaceNumberRegex();
    [GeneratedRegex(@"{(ts)}", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex ReplaceTsRegex();
    [GeneratedRegex(@"[^a-zA-Z0-9 .,!?;:'""-()]", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex ReplaceBadCharsRegex();
  }
}
