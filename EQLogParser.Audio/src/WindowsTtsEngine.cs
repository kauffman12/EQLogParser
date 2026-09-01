using log4net;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace EQLogParser.Audio
{
  /*
   * The built in Windows voices. Two APIs are behind this engine: Windows.Media.SpeechSynthesis (the modern
   * voice pack) and System.Speech for the older SAPI voices, which surface in the picker with a "(Legacy) " prefix.
   * A per player synthesizer is kept because creating one per callout is expensive, which means the object created
   * on the UI thread is used from whatever thread asks for audio; that pairing predates this class and Windows
   * speech objects tolerate it.
   */
  internal sealed class WindowsTtsEngine : ITtsEngine
  {
    internal const string EngineName = "Windows";
    private const string LegacyPrefix = "(Legacy) ";

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    private readonly List<VoiceInformation> _validVoices = [];
    private readonly Dictionary<string, PlayerSynths> _players = [];
    private readonly object _lock = new();

    public string Name => EngineName;

    /*
     * Windows exposes voices that fail the moment they are asked to speak (a language pack that is only half
     * installed), so each candidate is proven by synthesizing a word into a stream. Expensive, which is why it
     * runs once and only for this engine.
     */
    public async Task LoadVoicesAsync()
    {
      if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) || _validVoices.Count > 0)
      {
        return;
      }

      SpeechSynthesizer synth = null;
      IReadOnlyList<VoiceInformation> voices;

      try
      {
        synth = new SpeechSynthesizer();
        voices = SpeechSynthesizer.AllVoices; // this can also throw on some machines
      }
      catch (Exception)
      {
        synth?.Dispose();
        return;
      }

      try
      {
        foreach (var voice in voices)
        {
          if (await IsVoicePlayableAsync(synth, voice).ConfigureAwait(false))
          {
            // prefer default first
            if (SpeechSynthesizer.DefaultVoice?.Id == voice?.Id)
            {
              _validVoices.Insert(0, voice);
            }
            else
            {
              _validVoices.Add(voice);
            }
          }
        }
      }
      catch (Exception ex)
      {
        Log.Error("Unable to enumerate Windows voices.", ex);
      }
      finally
      {
        synth.Dispose();
      }
    }

    public List<string> GetVoices()
    {
      var list = new List<string>();

      try
      {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
          foreach (var voice in _validVoices)
          {
            if (voice?.DisplayName is string name)
            {
              list.Add(name);
            }
          }
        }

        using var sapi = new System.Speech.Synthesis.SpeechSynthesizer();
#pragma warning disable CA1304 // If culture info is used then not all voices are returned
        foreach (var voice in sapi.GetInstalledVoices())
        {
          if (voice is not null && voice.VoiceInfo is System.Speech.Synthesis.VoiceInfo info && !string.IsNullOrEmpty(info.Name))
          {
            list.Add(LegacyPrefix + info.Name);
          }
        }
#pragma warning restore CA1304 // Specify CultureInfo
      }
      catch (Exception ex)
      {
        Log.Error("Unable to read Voices from Windows SpeechSynthesizer.", ex);
      }

      return list;
    }

    public string GetDefaultVoice() =>
      OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) && GetVoiceInfo(null) is VoiceInformation { } voiceInfo
        ? voiceInfo.DisplayName
        : string.Empty;

    public string GetVoice(string playerId)
    {
      lock (_lock)
      {
        if (playerId != null && _players.TryGetValue(playerId, out var player) && !string.IsNullOrEmpty(player.Voice))
        {
          return player.Voice;
        }
      }

      return GetDefaultVoice();
    }

    public void SetVoice(string playerId, string voice)
    {
      if (string.IsNullOrEmpty(playerId))
      {
        return;
      }

      PlayerSynths old;
      lock (_lock)
      {
        if (!_players.TryGetValue(playerId, out var player))
        {
          player = new PlayerSynths();
          _players[playerId] = player;
        }

        old = new PlayerSynths { Synth = player.Synth, SapiSynth = player.SapiSynth };

        if (IsLegacyVoice(voice))
        {
          player.SapiSynth = CreateSapiSpeechSynthesizer(voice);
          player.Synth = null;
        }
        else if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
          player.Synth = CreateSpeechSynthesizer(voice);
          player.SapiSynth = null;
        }

        player.Voice = voice;
      }

      DisposeSynths(old);
    }

    public void RemoveVoice(string playerId)
    {
      PlayerSynths player = null;

      lock (_lock)
      {
        if (playerId != null)
        {
          _players.Remove(playerId, out player);
        }
      }

      DisposeSynths(player);
    }

    public async Task<(byte[] pcm, int sampleRate)> SynthesizeForPlayerAsync(string playerId, string text)
    {
      SpeechSynthesizer synth;
      System.Speech.Synthesis.SpeechSynthesizer sapiSynth;

      lock (_lock)
      {
        if (!_players.TryGetValue(playerId ?? string.Empty, out var player))
        {
          return (null, 0);
        }

        synth = player.Synth;
        sapiSynth = player.SapiSynth;
      }

      if (synth != null)
      {
        return await SynthesizeTextToByteArrayAsync(text, synth).ConfigureAwait(false);
      }

      if (sapiSynth != null)
      {
        return await SynthesizeTextToByteArrayAsync(text, sapiSynth).ConfigureAwait(false);
      }

      return (null, 0);
    }

    public async Task<(byte[] pcm, int sampleRate)> SynthesizeVoiceAsync(string voice, string text)
    {
      // preview and export speak voices no player owns, so they get a throwaway synthesizer
      if (IsLegacyVoice(voice))
      {
        if (CreateSapiSpeechSynthesizer(voice) is not { } sapiSynth)
        {
          return (null, 0);
        }

        try
        {
          return await SynthesizeTextToByteArrayAsync(text, sapiSynth).ConfigureAwait(false);
        }
        finally
        {
          DisposeSapi(sapiSynth);
        }
      }

      if (CreateSpeechSynthesizer(voice) is not { } synth)
      {
        return (null, 0);
      }

      try
      {
        return await SynthesizeTextToByteArrayAsync(text, synth).ConfigureAwait(false);
      }
      finally
      {
        DisposeWinRt(synth);
      }
    }

    public void Dispose()
    {
      List<PlayerSynths> players;

      lock (_lock)
      {
        players = [.. _players.Values];
        _players.Clear();
      }

      foreach (var player in players)
      {
        DisposeSynths(player);
      }

      _validVoices.Clear();
    }

    private static void DisposeSynths(PlayerSynths player)
    {
      if (player is null)
      {
        return;
      }

      DisposeWinRt(player.Synth);
      DisposeSapi(player.SapiSynth);
    }

    private static void DisposeWinRt(SpeechSynthesizer synth)
    {
      try
      {
        synth?.Dispose();
      }
      catch (Exception)
      {
        // ignore dispose errors
      }
    }

    private static void DisposeSapi(System.Speech.Synthesis.SpeechSynthesizer synth)
    {
      try
      {
        synth?.Dispose();
      }
      catch (Exception)
      {
        // ignore dispose errors
      }
    }

    private SpeechSynthesizer CreateSpeechSynthesizer(string voice)
    {
      SpeechSynthesizer synth = null;

      try
      {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
          synth = new SpeechSynthesizer();
          if (GetVoiceInfo(voice) is { } voiceInfo)
          {
            synth.Voice = voiceInfo;
          }
        }
      }
      catch (Exception)
      {
        // not supported
      }

      return synth;
    }

    private static System.Speech.Synthesis.SpeechSynthesizer CreateSapiSpeechSynthesizer(string voice)
    {
      System.Speech.Synthesis.SpeechSynthesizer synth = null;

      try
      {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
          synth = new System.Speech.Synthesis.SpeechSynthesizer();
          if (GetSapiVoiceInfo(voice) is { } voiceInfo)
          {
            synth.SelectVoice(voiceInfo.Name);
          }
        }
      }
      catch (Exception)
      {
        // not supported
      }

      return synth;
    }

    private VoiceInformation GetVoiceInfo(string name)
    {
      if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) || _validVoices.Count == 0) return null;
      if (name == null) return _validVoices[0];

      foreach (var voice in _validVoices)
      {
        if (voice.DisplayName == name || name.StartsWith(voice.DisplayName, StringComparison.OrdinalIgnoreCase))
        {
          return voice;
        }
      }

      return _validVoices[0];
    }

    private static System.Speech.Synthesis.VoiceInfo GetSapiVoiceInfo(string name)
    {
      System.Speech.Synthesis.VoiceInfo voiceInfo = null;

      try
      {
        using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
        voiceInfo = synth.Voice;
        if (!string.IsNullOrEmpty(name))
        {
          // do not pass null for culture
#pragma warning disable CA1304 // Specify CultureInfo
          foreach (var voice in synth.GetInstalledVoices())
          {
            if (!string.IsNullOrEmpty(name) && name.Contains(voice.VoiceInfo.Name, StringComparison.OrdinalIgnoreCase))
            {
              voiceInfo = voice.VoiceInfo;
              break;
            }
          }
#pragma warning restore CA1304 // Specify CultureInfo
        }
      }
      catch (Exception)
      {
        // not supported
      }

      return voiceInfo;
    }

    private static bool IsLegacyVoice(string voice) =>
      !string.IsNullOrEmpty(voice) && voice.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase);

    private static async Task<(byte[] pcm, int sampleRate)> SynthesizeTextToByteArrayAsync(string tts, SpeechSynthesizer synth)
    {
      if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
      {
        return (null, 0);
      }

      SpeechSynthesisStream stream = null;

      try
      {
        stream = await synth.SynthesizeTextToStreamAsync(tts).AsTask().ConfigureAwait(false);
        using var reader = new WaveFileReader(stream.AsStream());
        return await ReadPcmAsync(reader).ConfigureAwait(false);
      }
      catch (Exception ex)
      {
        Log.Debug("Error synthesizing text to byte array.", ex);
      }
      finally
      {
        try
        {
          stream?.Dispose();
        }
        catch (Exception)
        {
          // ignore dispose errors
        }
      }

      return (null, 0);
    }

    private static async Task<(byte[] pcm, int sampleRate)> SynthesizeTextToByteArrayAsync(string tts, System.Speech.Synthesis.SpeechSynthesizer synth)
    {
      try
      {
        using var mem = new MemoryStream();
        synth.SetOutputToWaveStream(mem);
        synth.Speak(tts);
        synth.SetOutputToNull(); // release reference to mem
        mem.Position = 0;
        using var reader = new WaveFileReader(mem);
        return await ReadPcmAsync(reader).ConfigureAwait(false);
      }
      catch (Exception ex)
      {
        Log.Debug("Error synthesizing text to byte array.", ex);
      }

      return (null, 0);
    }

    private static async Task<(byte[] pcm, int sampleRate)> ReadPcmAsync(WaveFileReader reader)
    {
      using var pcm = WaveFormatConversionStream.CreatePcmStream(reader);
      using var ms = pcm.Length > 0 ? new MemoryStream((int)pcm.Length) : new MemoryStream();
      await pcm.CopyToAsync(ms).ConfigureAwait(false);
      var data = ms.ToArray();
      var sample = pcm.WaveFormat.SampleRate;
      return (data, sample);
    }

    [DebuggerNonUserCode]
    private static async Task<bool> IsVoicePlayableAsync(SpeechSynthesizer synth, VoiceInformation voice)
    {
      if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
      {
        return false;
      }

      try
      {
        synth.Voice = voice;
        using IRandomAccessStream stream = await synth.SynthesizeTextToStreamAsync("test").AsTask().ConfigureAwait(false);
        return true;
      }
      catch (FileNotFoundException)
      {
        return false;
      }
      catch (COMException)
      {
        return false;
      }
      catch (InvalidOperationException)
      {
        return false;
      }
    }

    private sealed class PlayerSynths
    {
      internal SpeechSynthesizer Synth { get; set; }
      internal System.Speech.Synthesis.SpeechSynthesizer SapiSynth { get; set; }
      internal string Voice { get; set; }
    }
  }
}
