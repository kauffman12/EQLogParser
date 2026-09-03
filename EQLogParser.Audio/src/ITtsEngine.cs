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
   * Threading: SynthesizeForPlayerAsync / SynthesizeVoiceAsync / WarmUpVoiceAsync are serialized by AudioManager.
   * Piper keeps a process-wide native voice table and Kokoro runs a single inference session; neither is documented
   * as safe to call concurrently, so engines may assume one synthesis at a time. The rest - SetVoice, RemoveVoice,
   * GetVoices, GetVoice, GetDefaultVoice - are called under a lock AudioManager holds around every engine call of
   * that kind, which is also what keeps them from reaching an engine a switch has just retired. They still have to
   * tolerate the UI thread and they have to be quick: nothing audible waits behind them, so a slow one freezes a
   * dropdown instead of a callout. See docs/DesignNotes.md -> Speech synthesis and TTS engines.
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

    /*
     * How a voice is written to a person. Display only: the value a voice is stored under, matched against a config
     * and handed to SetVoice stays exactly what GetVoices returned. Kokoro decorates its ids with the accent they
     * carry (af_nicole reads "Nicole (US)"), Piper adds the locale its models declare, and an engine whose names are
     * already what somebody would call them returns the name untouched - which is also the right answer for a voice
     * this engine does not have. Called per dropdown row while it renders, so it has to be a lookup.
     */
    string GetVoiceDisplayName(string voice);

    /*
     * What to say out loud when somebody asks to hear this voice. Not the display name: a preview that reads
     * "af_heart (US)" aloud says letters and an abbreviation, so this hands back the name inside it - "Heart". The
     * value a voice is stored under and everything spoken on air are untouched by this; it exists for the one place
     * that speaks a voice's own name, which is the preview fired when a dropdown selection changes.
     */
    string GetVoiceSpokenName(string voice);

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

    /*
     * Make this voice ready so the next synthesis does not pay to build it, and let go of whatever was prepared
     * before: preparation is one voice at a time because an ONNX session is tens of megabytes. What "ready" means
     * differs per engine - Piper builds the native voice, Kokoro and Windows run one short synthesis through the
     * existing path to warm inference. Never audible. Called on a background thread under AudioManager's synthesis
     * gate, and allowed to fail quietly: synthesis still works without it, only slower.
     */
    Task WarmUpVoiceAsync(string voice);
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
      var order = Normalize(preferredEngine) switch
      {
        AudioManager.KokoroEngine =>
          new[] { AudioManager.KokoroEngine, AudioManager.PiperEngine, AudioManager.WindowsEngine },
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

    /*
     * The canonical spelling of an engine name. Settings are plain text: hand edited, copied between machines, or
     * written by an older build. Every comparison in the app - this factory, the pack table, the picker - is made
     * against the three names this class knows, so anything arriving from outside is mapped onto one of them at the
     * boundary rather than compared case by case in six places. An unknown name comes back unchanged, which means "no
     * preference" to Create and "not available" everywhere else.
     */
    internal static string Normalize(string engineName) => engineName switch
    {
      null or { Length: 0 } => engineName,
      _ when string.Equals(engineName, AudioManager.PiperEngine, StringComparison.OrdinalIgnoreCase) =>
        AudioManager.PiperEngine,
      _ when string.Equals(engineName, AudioManager.KokoroEngine, StringComparison.OrdinalIgnoreCase) =>
        AudioManager.KokoroEngine,
      _ when string.Equals(engineName, AudioManager.WindowsEngine, StringComparison.OrdinalIgnoreCase) =>
        AudioManager.WindowsEngine,
      _ => engineName
    };

    /* Returns the engine with this exact name - already normalized - or null when it is not usable on this machine. */
    internal static ITtsEngine CreateNamed(string name) => name switch
    {
      AudioManager.KokoroEngine => KokoroTtsEngine.TryCreate(),
      AudioManager.PiperEngine => PiperTtsEngine.TryCreate(),
      _ => new WindowsTtsEngine()
    };
  }
}
