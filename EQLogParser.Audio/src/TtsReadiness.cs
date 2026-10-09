using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser.Audio
{
  /*
   * One question, answered once per launch: is there a speech engine to speak with yet?
   *
   * Building one is not instant. Kokoro constructs an inference session over a 156 MB graph; the Windows engine proves
   * each of its voices by synthesizing a word into a stream. Two kinds of caller want to know when that is over -
   * something on the startup path, which wants to list the voices it found - and something in a callout, which wants
   * to know whether to wait a moment or say nothing at all. Both used to be answered by ordering alone: startup waited
   * for the build before showing the main window, so no caller could arrive early and the engine field could promise a
   * value to anybody who asked at any time.
   *
   * Three rules, each one the reason this is a type rather than a Task field.
   *
   *   - **The signal always fires.** A build that fails still releases its waiters. A callout with no engine says
   *     nothing, which is correct; a callout parked forever is a feature that looks hung, and nothing can retire the
   *     wait when the thing it waited on already gave up. Whoever completes this owns it through a finally - a pass
   *     left "still building" would name itself in every later question about voice delay.
   *   - **The release carries no verdict.** Releasing means "the first build attempt is over". Whether a voice can be
   *     spoken with is `HasEngine`, read afterwards: the two facts come apart, because an engine can arrive later than
   *     the attempt that failed (an operator picks one in the TTS window), and a released-with-false task would keep
   *     every later callout silent for the rest of the evening.
   *   - **State reads never block.** `HasEngine` answers false until an engine exists, so a synchronous reader - a
   *     dropdown drawing a display name during layout - falls back instead of waiting on inference on the UI thread.
   */
  internal sealed class TtsReadiness
  {
    private readonly object _gate = new();
    private TaskCompletionSource<bool> _signal = NewSignal();
    private volatile bool _hasEngine;

    /*
     * A completion source that never runs a continuation inline: whoever completes this is the thread pool building the
     * engine, and an awaiter resuming on it would put voice-listing work inside the build's own tail.
     */
    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when this launch's first engine build has finished, however it finished. Carries no verdict -
    /// ask HasEngine. Awaiting costs nothing after the fact and hands back the same task to every waiter.</summary>
    internal Task ReadyAsync
    {
      get
      {
        lock (_gate)
        {
          return _signal.Task;
        }
      }
    }

    /// <summary>Whether a speech engine exists to speak with. False until the first build produces one.</summary>
    internal bool HasEngine => _hasEngine;

    /// <summary>Records what the attempt produced and releases whoever is waiting. Safe to call from a finally, safe to
    /// call twice, and safe to call after a previous failure: an engine that exists keeps existing, and one that
    /// arrives late restores speech rather than leaving the process muted by its own first attempt.</summary>
    internal void Complete(bool hasEngine)
    {
      if (hasEngine)
      {
        _hasEngine = true;
      }

      TaskCompletionSource<bool> signal;
      lock (_gate)
      {
        signal = _signal;
      }

      // TrySet rather than Set: the first release is the one everybody was waiting on, and a second completion of an
      // already-released signal would throw at whoever cleaned up after a failed rebuild.
      signal.TrySetResult(hasEngine);
    }
  }
}
