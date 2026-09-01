using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace EQLogParser.Audio
{
  /*
   * A speech engine turns callout text into 16 bit mono PCM. AudioManager talks to exactly one engine at a time and
   * swaps that instance when the user picks another one, so adding an engine means adding a class instead of
   * branching through the manager.
   *
   * Threading: SynthesizeForPlayerAsync / SynthesizeVoiceAsync are serialized by AudioManager. Piper keeps a
   * process-wide native voice table and Kokoro runs a single inference session; neither is documented as safe to
   * call concurrently, so engines may assume one synthesis at a time. Everything else must tolerate being called
   * from the UI thread. See docs/DesignNotes.md -> Speech synthesis and TTS engines.
   */
  internal interface ITtsEngine : IDisposable
  {
    /* Engine name as it appears in the TtsEngine setting, the engine picker and the logs. */
    string Name { get; }

    /* Voice discovery that cannot be done synchronously. Windows proves a voice by synthesizing into a stream. */
    Task LoadVoicesAsync();

    /* Voice names offered to the UI. */
    List<string> GetVoices();

    /* The voice used when a player has none configured. */
    string GetDefaultVoice();

    /* The voice a player will actually speak with, empty resolved to the default. Used for cache keys and logs. */
    string GetVoice(string playerId);

    /*
     * Bind a player to a voice, called when the player is registered, whenever the user changes it, and again for
     * every player when a new engine takes over. A name this engine does not have must be dropped rather than kept,
     * so the player falls back to this engine's default voice.
     */
    void SetVoice(string playerId, string voice);

    /* Drop everything held for a player. Safe to call for an unknown player. */
    void RemoveVoice(string playerId);

    /* Speak with the voice bound to this player. Returns null when nothing could be synthesized. */
    Task<(byte[] pcm, int sampleRate)> SynthesizeForPlayerAsync(string playerId, string text);

    /* Speak a voice no player owns: the preview button and WAV export. Returns null when nothing was synthesized. */
    Task<(byte[] pcm, int sampleRate)> SynthesizeVoiceAsync(string voice, string text);
  }

  internal static class TtsEngineFactory
  {
    /*
     * Pick the engine for this session. A preference that cannot be satisfied falls through rather than failing:
     * a user whose Piper voice pack was removed should still get speech, and a fresh install has no preference at
     * all and keeps the historic Piper, Kokoro, Windows order.
     */
    internal static ITtsEngine Create(string preferredEngine)
    {
      var order = preferredEngine switch
      {
        AudioManager.KokoroEngine => new[] { AudioManager.KokoroEngine, AudioManager.PiperEngine, AudioManager.WindowsEngine },
        AudioManager.PiperEngine => new[] { AudioManager.PiperEngine, AudioManager.WindowsEngine },
        // Someone who asked for Windows voices on a machine that turns out not to have them (Wine, a stripped image)
        // is better off with an engine that can speak than with the preference honored and silence delivered.
        AudioManager.WindowsEngine => WindowsTtsEngine.IsAvailable()
          ? new[] { AudioManager.WindowsEngine }
          : new[] { AudioManager.PiperEngine, AudioManager.KokoroEngine, AudioManager.WindowsEngine },
        _ => new[] { AudioManager.PiperEngine, AudioManager.KokoroEngine, AudioManager.WindowsEngine }
      };

      foreach (var name in order)
      {
        if (CreateNamed(name) is { } engine)
        {
          return engine;
        }
      }

      // Last resort even when the Windows voices are known to be broken: AudioManager needs something to talk to, and
      // a named engine that stays quiet is easier to reason about than a null in every caller. The picker disables it
      // and says why, which is where this user goes next.
      return new WindowsTtsEngine();
    }

    /* Returns the engine with this exact name, or null when it is not usable on this machine. */
    internal static ITtsEngine CreateNamed(string name) => name switch
    {
      AudioManager.KokoroEngine => KokoroTtsEngine.TryCreate(),
      AudioManager.PiperEngine => PiperTtsEngine.TryCreate(),
      _ => new WindowsTtsEngine()
    };
  }
}
