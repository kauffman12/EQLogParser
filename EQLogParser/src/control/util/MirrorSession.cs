using System;
using System.Diagnostics;
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

    private readonly DamageFactTable _facts = new(100_000);
    private readonly CombatMirror _mirror;
    private readonly DispatcherTimer _quietTimer;
    private int _deriveInFlight;
    private bool _firstDerived;
    private long _lastTickCount = -1;
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
            return MirrorFightRows.Build(FightDeriver.Derive(_facts), timeline, _facts.FactCount);
          });
          sw.Stop();

          snapshot.ElapsedMs = sw.Elapsed.TotalMilliseconds;
          _firstDerived = true;
          Derived?.Invoke(snapshot);
        }
        catch (Exception) when (_disposed)
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

    // Bulk-load completion proxy without touching LogReader internals: once facts have arrived and
    // the count stops growing between ticks, EOF was reached (tail lines only come at human speed).
    private void QuietTick(object sender, EventArgs e)
    {
      if (_firstDerived || _disposed) return;

      var count = _facts.FactCount;
      if (count == 0) return;

      if (count == _lastTickCount) RederiveAsync();
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
