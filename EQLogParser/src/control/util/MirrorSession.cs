using log4net;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

using EQLogParser.Mirror;

namespace EQLogParser
{
  // Owns the combat mirror for one log-open: taps the pipeline statics, receives the chat fan-out,
  // and produces derived-list snapshots. Subscriptions are process-global parser events, so exactly
  // one session may run at a time — MainWindow creates it before LogReader starts and disposes it
  // when the log closes, mirroring the harness lifecycle (RunCore) that proved this wiring.
  //
  // Derivation is quiescent: CombatMirror.DeriveQuiescent parks ingest at its gate for the pass,
  // so a snapshot never races mid-append fact mutation and no tail line is lost. The first derive
  // fires automatically once the initial bulk load goes quiet (fact count stable across two ticks);
  // the mirror window's Re-derive button repeats it on demand.
  internal sealed class MirrorSession : IDisposable
  {
    public static MirrorSession Active { get; private set; }

    public static event Action ActiveChanged;

    // Raised on the derivation thread; subscribers marshal to the dispatcher themselves.
    public event Action<MirrorSnapshot> Derived;

    public event Action<string> DeriveFailed;

    // Liveness feedback while capture is dirty (raised on the timer, i.e. the dispatcher):
    // total captured facts since no snapshot covers them yet.
    public event Action<long> Capturing;

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    private readonly DamageFactTable _facts = new(100_000);
    private readonly CombatMirror _mirror;
    private readonly DispatcherTimer _quietTimer;
    private int _deriveInFlight;
    private long _lastTickCount = -1;
    private long _lastDerivedCount = -1;
    private volatile bool _autoDeriveDisabled;
    private bool _disposed;

    public MirrorSession()
    {
      _mirror = new CombatMirror(_facts);
      ChatSink = new MirrorChatSink(_mirror);
      _quietTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
      _quietTimer.Tick += QuietTick;
    }

    // Chat fan-out target for LogProcessor (composed with the app's own sinks — see CompositeChatSink).
    public IChatSink ChatSink { get; }

    public void Start()
    {
      _mirror.Start();
      Active = this;
      ActiveChanged?.Invoke();
      _quietTimer.Start();
    }

    public void RederiveAsync()
    {
      if (_disposed || Interlocked.Exchange(ref _deriveInFlight, 1) == 1) return;

      Log.Info($"Combat mirror derive starting over {CapturedTotal:N0} captured facts");

      _ = Task.Run(() =>
      {
        try
        {
          var sw = Stopwatch.StartNew();
          var snapshot = _mirror.DeriveQuiescent(() =>
          {
            // Fresh timeline each pass: rules replay over the facts from scratch, so manual
            // overrides and mid-log registry changes re-apply cleanly (idempotent by design).
            var timeline = new EntityTimeline();
            RegistrySeed.Apply(timeline, _facts, _mirror.FirstEventTime, _mirror.LastEventTime);
            ClassificationRules.Apply(_facts, timeline);
            return MirrorFightRows.Build(FightDeriver.Derive(_facts), timeline, CapturedTotal);
          });
          sw.Stop();

          snapshot.ElapsedMs = sw.Elapsed.TotalMilliseconds;
          _lastDerivedCount = CapturedTotal;
          Log.Info($"Combat mirror derive done: {snapshot.FightCount} fights, {sw.ElapsedMilliseconds} ms");
          Derived?.Invoke(snapshot);
        }
        catch (Exception ex) when (!_disposed)
        {
          // A stale list must never fail silently: it is the one surface where a broken
          // derivation would otherwise be indistinguishable from an idle mirror. Auto-retry at
          // timer rate would spam the log — stop the loop, leave Re-derive available.
          _autoDeriveDisabled = true;
          Log.Error("Combat mirror derivation failed; auto-derive disabled", ex);
          DeriveFailed?.Invoke(ex.Message);
        }
        catch (Exception)
        {
          // Log closed mid-derive — the snapshot has no reader anymore.
        }
        finally
        {
          Interlocked.Exchange(ref _deriveInFlight, 0);
        }
      });
    }

    public void Dispose()
    {
      if (_disposed) return;
      _disposed = true;
      _quietTimer.Stop();
      _mirror.Stop();
      if (ReferenceEquals(Active, this))
      {
        Active = null;
        ActiveChanged?.Invoke();
      }
    }

    // Every fact stream counts — a log without combat damage (login/city logs) still derives
    // (an empty fight list is an honest answer; "capturing forever" is not).
    private long CapturedTotal =>
      _facts.FactCount + _facts.DeathCount + _facts.TauntCount + _facts.IdentityEventCount + _facts.EvidenceCount;

    // Quiescence proxy without touching LogReader internals: derive once the captured-fact count
    // has been stable for two ticks AND differs from the last derived count. Not a one-shot EOF
    // latch — a damage-free stretch during bulk load (raid gap in a recorded log, GC pause) may
    // fire an early pass, but any later growth re-arms the trigger, so the visible snapshot
    // converges to end-of-file and tailing refreshes whenever the log goes quiet. Derivation
    // itself parks ingest at the gate, so a completed pass always leaves count == lastDerivedCount.
    private void QuietTick(object sender, EventArgs e)
    {
      if (_disposed || _autoDeriveDisabled) return;

      var count = CapturedTotal;

      // Dirty-while-idle feedback doubles as field diagnostics: "capturing… 0 captured" separates
      // an empty log from a stalled pipeline without needing a debugger.
      if (count != _lastDerivedCount) Capturing?.Invoke(count);

      if (count == 0) return;

      if (count == _lastTickCount && count != _lastDerivedCount) RederiveAsync();
      else _lastTickCount = count;
    }

    private sealed class MirrorChatSink : IChatSink
    {
      private readonly CombatMirror _mirror;

      public MirrorChatSink(CombatMirror mirror) => _mirror = mirror;

      public void Init()
      {
      }

      public void Add(ChatType chat) => _mirror.HandleChat(chat);
    }
  }

  // Forwards to several chat sinks in order — the seam that lets ChatDB archiving and the mirror
  // share the single IChatSink slot LogProcessor takes, without Core knowing about either.
  internal sealed class CompositeChatSink(params IChatSink[] sinks) : IChatSink
  {
    public void Init()
    {
      foreach (var sink in sinks) sink.Init();
    }

    public void Add(ChatType chat)
    {
      foreach (var sink in sinks) sink.Add(chat);
    }
  }
}
