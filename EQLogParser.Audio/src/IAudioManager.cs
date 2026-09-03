using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser.Audio
{
  /// <summary>
  /// Public contract for the audio subsystem. Implementation details (NAudio, Piper TTS,
  /// device management, caching) are hidden behind this interface.
  /// </summary>
  public interface IAudioManager : IDisposable
  {
    /// <summary>Fires when the available audio device list changes.</summary>
    event Action<bool> DeviceListChanged;

    // --- Lifecycle ---
    Task LoadValidVoicesAsync();

    // --- Volume ---
    int GetVolume();
    void SetVolume(int volume);

    // --- Voice enumeration ---
    List<string> GetVoiceList();
    string GetDefaultVoice();

    /// <summary>How a voice from GetVoiceList reads in a picker ("Nicole (US)"). Never the stored value.</summary>
    string GetVoiceDisplayName(string voice);

    /// <summary>
    /// What a preview says when asked for this voice: the name on its own ("Nicole"), never the identifier and never
    /// the locale tag the picker adds beside it.
    /// </summary>
    string GetVoiceSpokenName(string voice);
    void SetVoice(string playerId, string voiceName);

    // --- Device selection ---
    void SelectDevice(string deviceId);

    // --- Playback queue (per-player) ---
    void Add(string playerId, string voice);
    void StartAudio(string playerId);
    void StopAudio(string playerId, bool remove = false);

    // --- Async playback requests ---
    void SpeakFileAsync(string playerId, string filePath, long priority, int playerVolume, int adjustedVolume);
    void SpeakTtsAsync(string playerId, string text, long priority, int rate, int playerVolume, int adjustedVolume);

    // --- Test / preview ---
    void TestSpeakFileAsync(string filePath, int adjustedVolume = 4);
    void TestSpeakTtsAsync(string text, string voice = null, int rate = 0, int playerVolume = -1, int adjustedVolume = 4);

    // --- TTS: play or export to WAV ---
    void SpeakOrSaveTtsAsync(string text, string voice, string deviceId, float volume, int rate, string fileName = null);

    // --- Speech runtime packs (Piper and Kokoro download their engines on demand) ---
    /// <summary>True when the engine's runtime files are on disk, so it can be selected now.</summary>
    bool IsEngineAvailable(string engine);

    /// <summary>True when this engine's files are in local app data, which is where every runtime pack lives and the
    /// only place the app can delete them. Anything an old installer left beside the program is not a pack: the app
    /// never reads it, and the installer removes it.</summary>
    bool IsEngineDownloaded(string engine);

    /// <summary>Rough archive size in bytes, for wording a download button with.</summary>
    long GetEngineDownloadBytes(string engine);

    /// <summary>Downloads an engine's runtime pack into local app data and installs it. Progress is 0..1 across the
    /// whole job; false leaves whatever was installed before untouched.</summary>
    Task<bool> InstallEngineAsync(string engine, IProgress<float> progress,
      CancellationToken cancellationToken = default);

    /// <summary>Deletes an installed runtime pack to reclaim disk space. Refuses for the engine in use.</summary>
    bool RemoveEngineFiles(string engine);

    // --- TTS engine selection ---
    /// <summary>The engine actually in use for this running session.</summary>
    string GetActiveEngine();

    /// <summary>Switches the speech engine without a restart; only engines whose components are already on disk can
    /// be selected. Returns false when the switch did not happen, leaving the current engine speaking.</summary>
    Task<bool> SwitchEngineAsync(string engine);
  }
}