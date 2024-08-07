﻿using log4net;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
    internal static AudioManager Instance => Lazy.Value;
    private readonly ConcurrentDictionary<string, PlayerAudio> _playerAudios = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly AudioSessionControl _session;

    private AudioManager()
    {
      var silentWav = new byte[]
      {
        0x52, 0x49, 0x46, 0x46, 0x24, 0x08, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45, 0x66, 0x6D, 0x74, 0x20,
        0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x44, 0xAC, 0x00, 0x00, 0x88, 0x58, 0x01, 0x00,
        0x02, 0x00, 0x10, 0x00, 0x64, 0x61, 0x74, 0x61, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00
      };

      // play something to register the audio session with windows
      var memStream = new MemoryStream(silentWav);
      var reader = new WaveFileReader(memStream);
      var waveOut = CreateWaveOut();
      waveOut.Init(reader);

      waveOut.PlaybackStopped += (_, _) =>
      {
        memStream.DisposeAsync();
        reader.DisposeAsync();
        waveOut.Dispose();
      };

      waveOut.Play();
      waveOut.Stop();

      var deviceEnumerator = new MMDeviceEnumerator();
      var defaultDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
      var sessionManager = defaultDevice.AudioSessionManager;
      for (var i = 0; i < sessionManager.Sessions.Count; i++)
      {
        if (sessionManager.Sessions[i].GetProcessID == Process.GetCurrentProcess().Id)
        {
          _session = sessionManager.Sessions[i];
        }
      }
    }

    internal int GetVolume()
    {
      if (_session?.SimpleAudioVolume?.Volume != null)
      {
        return (int)(_session.SimpleAudioVolume.Volume * 100);
      }

      var waveOut = CreateWaveOut();
      var volume = (int)waveOut.Volume * 100;
      waveOut.Dispose();
      return volume;
    }

    internal void SetVolume(int volume)
    {
      if (_session?.SimpleAudioVolume?.Volume != null)
      {
        var update = volume / 100.0f;
        _session.SimpleAudioVolume.Volume = update;
      }
    }

    internal void Add(string id)
    {
      var audio = new PlayerAudio();
      _playerAudios.TryAdd(id, audio);
      ProcessAsync(audio);
    }

    internal void Stop(string id, bool remove = false)
    {
      if (_playerAudios.TryGetValue(id, out var playerAudio))
      {
        lock (playerAudio)
        {
          playerAudio.WaveOut.Stop();
          playerAudio.CurrentEvent = null;
          playerAudio.Events.Clear();

          if (remove)
          {
            playerAudio.CancellationTokenSource.Cancel();
            playerAudio.WaveOut?.Dispose();
            _playerAudios.TryRemove(id, out _);
          }
        }
      }
    }

    internal async void TestSpeakFileAsync(string filePath)
    {
      var reader = new AudioFileReader(filePath);
      if (!string.IsNullOrEmpty(filePath) && await ReadFileToByteArrayAsync(reader) is { Length: > 0 } data)
      {
        PlayAudioData(data, reader.WaveFormat);
      }
    }

    internal async void TestSpeakTtsAsync(string tts, string voice = null, int rate = 1, int volume = 4)
    {
      if (!string.IsNullOrEmpty(tts) && CreateSpeechSynthesizer() is var synth && synth != null)
      {
        synth.Voice = GetVoice(voice);
        synth.Options.SpeakingRate = GetSpeakingRate(rate);

        if (await SynthesizeTextToByteArrayAsync(synth, tts) is { Length: > 0 } data)
        {
          var waveFormat = new WaveFormat(16000, 16, 1);
          PlayAudioData(data, waveFormat, volume);
        }
      }
    }

    internal async void SpeakFileAsync(string id, string filePath, Trigger trigger)
    {
      if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(filePath))
      {
        try
        {
          var reader = new AudioFileReader(filePath);
          if (await ReadFileToByteArrayAsync(reader) is { Length: > 0 } data)
          {
            SpeakAsync(id, data, reader.WaveFormat, trigger.Priority, trigger.Volume, reader.TotalTime.TotalSeconds);
          }
        }
        catch (Exception ex)
        {
          Log.Debug($"Error while playing wav file: {filePath}", ex);
        }
      }
    }

    internal async void SpeakTtsAsync(string id, SpeechSynthesizer synth, string tts, Trigger trigger)
    {
      if (!string.IsNullOrEmpty(id) && synth != null && !string.IsNullOrEmpty(tts))
      {
        byte[] data = null;
        await _semaphore.WaitAsync();
        try
        {
          data = await SynthesizeTextToByteArrayAsync(synth, tts);
        }
        catch (Exception ex)
        {
          Log.Debug("Error synthesizing text.", ex);
        }
        finally
        {
          _semaphore.Release();
        }

        if (data is { Length: > 0 })
        {
          var waveFormat = new WaveFormat(16000, 16, 1);
          SpeakAsync(id, data, waveFormat, trigger.Priority, trigger.Volume);
        }
      }
    }

    private static WaveOutEvent CreateWaveOut()
    {
      return new WaveOutEvent()
      {
        DesiredLatency = 50,
        NumberOfBuffers = 4
      };
    }

    private static async Task<byte[]> ReadFileToByteArrayAsync(AudioFileReader reader)
    {
      try
      {
        var memStream = new MemoryStream();
        await reader.CopyToAsync(memStream);
        return memStream.ToArray();
      }
      catch (Exception ex)
      {
        Log.Debug($"Error reading file to byte array: {reader.FileName}", ex);
        return null;
      }
    }

    private static async Task<byte[]> SynthesizeTextToByteArrayAsync(SpeechSynthesizer synth, string tts)
    {
      try
      {
        var stream = await synth.SynthesizeTextToStreamAsync(tts);
        var memStream = new MemoryStream();
        await stream.AsStream().CopyToAsync(memStream);
        return memStream.ToArray();
      }
      catch (Exception ex)
      {
        Log.Debug("Error synthesizing text to byte array.", ex);
        return null;
      }
    }

    private void SpeakAsync(string id, byte[] audioData, WaveFormat waveFormat, long priority = 5, int volume = 4, double seconds = -1)
    {
      if (_playerAudios.TryGetValue(id, out var playerAudio))
      {
        lock (playerAudio)
        {
          playerAudio.Events = playerAudio.Events.Where(pa => pa.Priority <= priority).ToList();
          playerAudio.Events.Add(new PlaybackEvent { AudioData = audioData, WaveFormat = waveFormat, Priority = priority, Volume = volume, Seconds = seconds });
          if (playerAudio.CurrentEvent != null && playerAudio.WaveOut.PlaybackState != PlaybackState.Stopped && playerAudio.CurrentEvent.Priority > priority)
          {
            playerAudio.WaveOut.Stop();
            playerAudio.CurrentEvent = null;
          }
        }
      }
    }

    internal static double GetSpeakingRate(int oldRate) => 1.0 + (oldRate / 10.0 * 1.6);

    internal static VoiceInformation GetVoice(string name)
    {
      return SpeechSynthesizer.AllVoices.FirstOrDefault(voice => voice.DisplayName == name || name?.StartsWith(voice.DisplayName) == true)
             ?? SpeechSynthesizer.DefaultVoice;
    }

    private static void PlayAudioData(byte[] data, WaveFormat waveFormat, int volume = 4)
    {
      var stream = new RawSourceWaveStream(data, 0, data.Length, waveFormat);
      var waveOut = CreateWaveOut();

      waveOut.PlaybackStopped += (_, _) =>
      {
        stream.DisposeAsync();
        waveOut.Dispose();
      };

      var provider = new VolumeSampleProvider(stream.ToSampleProvider())
      {
        Volume = ConvertVolume(waveOut.Volume, volume)
      };

      waveOut.Init(provider);
      waveOut.Play();
    }

    private static float ConvertVolume(float current, int increase)
    {
      var newValue = current *
                     increase switch
                     {
                       0 => 1.8f,
                       1 => 1.6f,
                       2 => 1.4f,
                       3 => 1.2f,
                       5 => 0.8f,
                       6 => 0.6f,
                       7 => 0.4f,
                       8 => 0.2f,
                       _ => 1.0f
                     };

      return newValue switch
      {
        < 0 => 0.0f,
        > 1.0f => 1.0f,
        _ => newValue
      };
    }

    private static void ProcessAsync(PlayerAudio audio)
    {
      var cancellationTokenSource = new CancellationTokenSource();
      audio.CancellationTokenSource = cancellationTokenSource;

      Task.Run(() =>
      {
        RawSourceWaveStream stream = null;

        try
        {
          while (!cancellationTokenSource.Token.IsCancellationRequested)
          {
            lock (audio)
            {
              if (audio.WaveOut.PlaybackState == PlaybackState.Playing &&
                  audio.CurrentEvent?.Seconds is > -1 and < 1.0 && audio.Events.Count > 0)
              {
                audio.WaveOut.Stop();
              }

              // skip through short sound files if there's audio pending
              if (audio.WaveOut.PlaybackState == PlaybackState.Stopped)
              {
                if (stream != null)
                {
                  stream.DisposeAsync();
                  stream = null;
                }

                if (audio.Events.Count > 0)
                {
                  audio.CurrentEvent = audio.Events[0];
                  audio.Events.RemoveAt(0);

                  stream = new RawSourceWaveStream(audio.CurrentEvent.AudioData, 0, audio.CurrentEvent.AudioData.Length, audio.CurrentEvent.WaveFormat);
                  var provider = new VolumeSampleProvider(stream.ToSampleProvider())
                  {
                    Volume = ConvertVolume(audio.WaveOut.Volume, audio.CurrentEvent.Volume)
                  };

                  audio.WaveOut.Init(provider);
                  audio.WaveOut.Play();
                }
              }
            }

            Thread.Sleep(40);
          }
        }
        catch (OperationCanceledException)
        {
          // ignore cancel event
        }
        catch (Exception ex)
        {
          Log.Debug("Error during playback.", ex);
        }
        finally
        {
          stream?.DisposeAsync();
          cancellationTokenSource.Dispose();
        }
      }, cancellationTokenSource.Token);
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
        return null;
      }
    }

    private class PlayerAudio
    {
      internal WaveOutEvent WaveOut { get; } = new();
      internal List<PlaybackEvent> Events { get; set; } = [];
      internal PlaybackEvent CurrentEvent { get; set; }
      internal CancellationTokenSource CancellationTokenSource { get; set; }
    }

    private class PlaybackEvent
    {
      internal long Priority { get; init; } = -1;
      internal int Volume { get; init; } = 4;
      internal byte[] AudioData { get; init; }
      internal WaveFormat WaveFormat { get; init; }
      internal double Seconds { get; init; }
    }
  }
}