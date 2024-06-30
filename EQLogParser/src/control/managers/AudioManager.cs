using log4net;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.SpeechSynthesis;

namespace EQLogParser
{
  internal class AudioManager
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly Lazy<AudioManager> Lazy = new(() => new AudioManager());
    internal static AudioManager Instance => Lazy.Value; // instance
    private readonly List<PlaybackEvent> _playBackEvents;
    private readonly object _lockObject = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private AudioManager()
    {
      _playBackEvents = Enumerable.Range(0, 20).Select(_ => new PlaybackEvent()).ToList();
    }

    internal async Task SpeakAsync(string filePath, long priority = 5)
    {
      if (!string.IsNullOrEmpty(filePath))
      {
        try
        {
          var reader = new WaveFileReader(filePath);
          var memStream = new MemoryStream();
          await reader.CopyToAsync(memStream);
          memStream.Position = 0;
          var audio = new RawSourceWaveStream(memStream, reader.WaveFormat);
          await SpeakAsync(audio, priority);
        }
        catch (Exception ex)
        {
          Log.Debug($"Error while playing wav file: {filePath}", ex);
        }
      }
    }

    internal async Task SpeakAsync(SpeechSynthesizer synth, string tts, long priority = 5)
    {
      if (synth != null && !string.IsNullOrEmpty(tts))
      {
        byte[] data = null;
        await _semaphore.WaitAsync();
        try
        {
          // synthesize and make sure we're done with the TTS API before releasing
          var stream = await synth.SynthesizeTextToStreamAsync(tts);
          var memStream = new MemoryStream();
          await stream.AsStream().CopyToAsync(memStream);
          memStream.Position = 0;
          data = memStream.ToArray();
          await memStream.DisposeAsync();
          stream.Dispose();
        }
        catch (Exception ex)
        {
          Log.Debug("Error synthesizing text.", ex);
        }
        finally
        {
          _semaphore.Release();
        }

        if (data != null)
        {
          var memStream = new MemoryStream(data);
          var audio = new RawSourceWaveStream(memStream, new WaveFormat(16000, 16, 1));
          await SpeakAsync(audio, priority);
        }
      }
    }

    private Task SpeakAsync(RawSourceWaveStream audioStream, long priority = 5)
    {
      lock (_lockObject)
      {
        // stop any playbacks of lower priority
        foreach (var item in _playBackEvents)
        {
          if (priority < item.Priority && item.WaveOut.PlaybackState != PlaybackState.Stopped)
          {
            item.WaveOut.Stop();
            item.Priority = -1;
          }
        }

        if (_playBackEvents.FirstOrDefault(item => item.WaveOut.PlaybackState == PlaybackState.Stopped) is { } playback)
        {
          playback.Priority = priority;
          playback.WaveOut.PlaybackStopped += PlaybackStopped;
          playback.WaveOut.Init(audioStream);
          playback.WaveOut.Play();
        }
      }

      return Task.CompletedTask;

      async void PlaybackStopped(object sender, StoppedEventArgs e)
      {
        await audioStream.DisposeAsync();
        if (sender is WaveOutEvent waveOut)
        {
          waveOut.PlaybackStopped -= PlaybackStopped;
        }
      }
    }

    internal void Stop()
    {
      lock (_lockObject)
      {
        foreach (var item in _playBackEvents)
        {
          if (item.WaveOut.PlaybackState != PlaybackState.Stopped)
          {
            item.WaveOut.Stop();
            item.Priority = -1;
          }
        }
      }
    }

    internal static SpeechSynthesizer CreateSpeechSynthesizer()
    {
      try
      {
        return new SpeechSynthesizer();
      }
      catch (Exception ex)
      {
        Log.Error(ex);
      }

      return null;
    }

    internal static double GetSpeakingRate(int oldRate)
    {
      // estimate but also gives a little faster
      return 1.0 + (oldRate / 10.0 * 1.6);
    }

    internal static VoiceInformation GetVoice(string name)
    {
      // old Synthesizer had different names like Microsoft David Desktop vs Microsoft David
      return SpeechSynthesizer.AllVoices.FirstOrDefault(voice => voice.DisplayName == name || name?.StartsWith(voice.DisplayName) == true)
             ?? SpeechSynthesizer.DefaultVoice;
    }

    private class PlaybackEvent
    {
      internal WaveOutEvent WaveOut { get; } = new();
      internal long Priority { get; set; } = -1;
    }
  }
}