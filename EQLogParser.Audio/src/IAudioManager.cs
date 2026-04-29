using System;
using System.Collections.Generic;
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
  }
}