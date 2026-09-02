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

    // Word spoken into memory by an engine that has not spoken yet. See WarmUpVoiceAsync.
    private const string WarmUpText = "test";

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    /*
     * Whether this machine can actually speak through Windows. Null until LoadVoicesAsync has looked: the engine is
     * assumed to work until it is caught not working, because it is the last engine standing and hiding it on a
     * hunch would silence someone. Proven false where these voices do not exist at all -- Wine and Linux emulators,
     * a stripped Windows image, a speech runtime the user turned off.
     */
    private static bool? _usableVoicesProven;

    // 0 = not looked, 1 = Wine, 2 = not Wine. A pinnable export lookup, so it is asked once.
    private static int _wineState;

    /*
     * What discovery has already established about this machine: the voice ids that proved able to speak, and the
     * legacy names System.Speech reports. Both are answers about the machine rather than about an engine instance, and
     * both cost seconds to get - proving a voice is one synthesis per voice, and asking SAPI for its list means making
     * a synthesizer - so they are kept for the life of the process instead of being paid again every time the user
     * switches engines and back. A voice pack installed while running needs a restart to be usable either way; a
     * voice that is not in the proven set yet is still proved on sight, so nothing is trusted that has not spoken.
     */
    private static HashSet<string> _provenVoiceIds;
    private static List<string> _legacyVoiceNames;
    private static readonly object _discoveryLock = new();

    private readonly List<VoiceInformation> _validVoices = [];
    private readonly Dictionary<string, PlayerSynths> _players = [];
    private readonly object _lock = new();

    public string Name => EngineName;

    /*
     * Windows exposes voices that fail the moment they are asked to speak (a language pack that is only half
     * installed), so each candidate is proven by synthesizing a word into a stream. Expensive, which is why it
     * runs once and only for this engine.
     */
    internal static bool IsAvailable() => !IsRunningUnderWine() && _usableVoicesProven != false;

    /*
     * Wine answers the question before anything has to fail. wine_get_version is an ntdll export Wine added long ago
     * and real Windows has never had, so this is not a heuristic about build numbers or registry keys that a service
     * pack can move: either the export is there or it is not. That matters because the direction of the error is not
     * symmetric -- wrongly saying "this is Wine" would switch off the only engine a machine has -- and a wrong yes
     * needs Windows to grow an export it does not have. A wrong no costs nothing either: the runtime probe below is
     * still the backstop.
     *
     * Covers Wine itself and the wrappers built on it (Whisky, Bottles, CrossOver). A real Windows install in a VM on
     * Linux keeps its voices, which is right: those are ordinary Windows voice packs running on ordinary Windows.
     */
    private static bool IsRunningUnderWine()
    {
      switch (_wineState)
      {
        case 1:
          return true;
        case 2:
          return false;
      }

      var isWine = false;
      try
      {
        // System32 only: a searched-for ntdll.dll would mean anything could plant one next to the executable and make
        // this answer whatever it liked. The handle is deliberately not released; ntdll stays mapped for the life of
        // the process, and the answer is cached either way.
        if (NativeLibrary.TryLoad("ntdll.dll", typeof(WindowsTtsEngine).Assembly,
              DllImportSearchPath.System32, out var ntdll))
        {
          isWine = NativeLibrary.TryGetExport(ntdll, "wine_get_version", out _);
        }
      }
      catch (Exception ex)
      {
        // Not knowing is not the same as knowing: leave the runtime probe to decide.
        Log.Debug("Unable to check for Wine", ex);
        return false;
      }

      _wineState = isWine ? 1 : 2;
      if (isWine)
      {
        Log.Warn("Running under Wine: the Windows voices are not offered. Enable Piper or Kokoro instead.");
      }

      return isWine;
    }

    public async Task LoadVoicesAsync()
    {
      if (_validVoices.Count > 0)
      {
        return;
      }

      if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
      {
        MarkUnavailable("Windows speech needs Windows 10 or newer");
        return;
      }

      SpeechSynthesizer synth = null;
      IReadOnlyList<VoiceInformation> voices;

      try
      {
        synth = new SpeechSynthesizer();
        voices = SpeechSynthesizer.AllVoices; // this can also throw on some machines
      }
      catch (Exception ex)
      {
        synth?.Dispose();
        MarkUnavailable($"Windows speech is not usable here: {ex.Message}");
        return;
      }

      // Snapshotted rather than held across the loop: proving a voice is an await, and no lock may be held over one.
      HashSet<string> trustedVoices;

      lock (_discoveryLock)
      {
        trustedVoices = _provenVoiceIds;
      }

      try
      {
        foreach (var voice in voices)
        {
          if (voice is null)
          {
            continue;
          }

          // Trust what already proved out this session; prove anything that has not. One synthesis per unknown voice is
          // the price of knowing a half installed language pack cannot speak, and it is not paid twice.
          var trusted = trustedVoices is not null && trustedVoices.Contains(voice.Id);

          if (trusted || await IsVoicePlayableAsync(synth, voice).ConfigureAwait(false))
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

      // What proved out here is what the next engine instance gets to trust, anything proved just now included.
      var proved = new HashSet<string>();

      foreach (var voice in _validVoices)
      {
        if (voice?.Id is string id)
        {
          _ = proved.Add(id);
        }
      }

      // Worth remembering only when this pass actually looked at something: AllVoices can come back empty on a machine
      // where speech is fine, and overwriting the cache with nothing there would leave every later session re-proving
      // voices that were never the problem.
      if (voices.Count > 0)
      {
        lock (_discoveryLock)
        {
          _provenVoiceIds = proved;
        }
      }

      // Nothing playable through the modern API is only fatal if the legacy SAPI voices are missing too; plenty of
      // machines have one and not the other, and either is enough to speak with.
      if (_validVoices.Count == 0 && GetVoices().Count == 0)
      {
        MarkUnavailable("no Windows voice could be made to speak");
      }
      else
      {
        _usableVoicesProven = true;
      }
    }

    private static void MarkUnavailable(string reason)
    {
      if (_usableVoicesProven == false)
      {
        return;
      }

      _usableVoicesProven = false;
      Log.Warn($"Windows TTS is not available: {reason}.");
    }

    public List<string> GetVoices()
    {
      var list = new List<string>();

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

      list.AddRange(ReadLegacyVoiceNames());
      return list;
    }

    /* The legacy voices System.Speech reports, read once for the process. See _legacyVoiceNames. */
    private static List<string> ReadLegacyVoiceNames()
    {
      lock (_discoveryLock)
      {
        if (_legacyVoiceNames is not null)
        {
          return _legacyVoiceNames;
        }

        var names = new List<string>();

        try
        {
          using var sapi = new System.Speech.Synthesis.SpeechSynthesizer();
#pragma warning disable CA1304 // If culture info is used then not all voices are returned
          foreach (var voice in sapi.GetInstalledVoices())
          {
            if (voice is not null && voice.VoiceInfo is System.Speech.Synthesis.VoiceInfo info
                && !string.IsNullOrEmpty(info.Name))
            {
              names.Add(LegacyPrefix + info.Name);
            }
          }
#pragma warning restore CA1304 // Specify CultureInfo
        }
        catch (Exception ex)
        {
          /*
           * An empty answer is handed back but not remembered: reporting nothing once and reporting that nothing exists
           * are different things, and a SAPI that failed to answer this time may answer next time.
           */
          Log.Error("Unable to read Voices from Windows SpeechSynthesizer.", ex);
          return [];
        }

        _legacyVoiceNames = names;
        return names;
      }
    }

    public string GetDefaultVoice() =>
      OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) && GetVoiceInfo(null) is VoiceInformation { } voiceInfo
        ? voiceInfo.DisplayName
        : string.Empty;

    /*
     * Windows hands out the name a person would use - "Microsoft David" - so there is nothing to translate, and the
     * "(Legacy) " a System.Speech voice carries is worth keeping in sight: it tells you why that one sounds different.
     */
    public string GetVoiceDisplayName(string voice) => voice;

    public string GetVoice(string playerId)
    {
      lock (_lock)
      {
        if (playerId is not null && _players.TryGetValue(playerId, out var player) &&
            !string.IsNullOrEmpty(player.Voice))
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
        if (playerId is not null)
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

      if (synth is not null)
      {
        return await SynthesizeTextToByteArrayAsync(text, synth).ConfigureAwait(false);
      }

      if (sapiSynth is not null)
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
          DisposeQuietly(sapiSynth);
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
        DisposeQuietly(synth);
      }
    }

    public async Task WarmUpVoiceAsync(string voice)
    {
      // Nothing is held per voice: a synthesizer object is cheap next to a neural model, and the players that will
      // actually talk already keep one each. What is slow is the very first speak - SAPI spinning up, WinRT resolving
      // its voice - so say one word into memory through the same path a callout uses and throw the audio away.
      _ = await SynthesizeVoiceAsync(voice, WarmUpText).ConfigureAwait(false);
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

      DisposeQuietly(player.Synth);
      DisposeQuietly(player.SapiSynth);
    }

    /* Both speech APIs answer to IDisposable and both are equally uninteresting when they fail to let go. */
    private static void DisposeQuietly(IDisposable disposable)
    {
      try
      {
        disposable?.Dispose();
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
      if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) || _validVoices.Count == 0)
      {
        return null;
      }

      if (name is null)
      {
        return _validVoices[0];
      }

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

    private static async Task<(byte[] pcm, int sampleRate)> SynthesizeTextToByteArrayAsync(string tts,
      SpeechSynthesizer synth)
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

    private static async Task<(byte[] pcm, int sampleRate)> SynthesizeTextToByteArrayAsync(string tts,
      System.Speech.Synthesis.SpeechSynthesizer synth)
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
        using IRandomAccessStream stream = await synth.SynthesizeTextToStreamAsync("test").AsTask()
          .ConfigureAwait(false);
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
