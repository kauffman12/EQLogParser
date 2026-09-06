using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace EQLogParser
{
  /*
   * Throwaway performance harness for the FCT render backends (Tools > FCT Simulation). Generates a fixed,
   * realistic mix of damage/heal/evade records over 60 seconds — melee/spell/DoT damage dealt, damage taken,
   * heals, crits, periodic ticks and misses — and feeds them at the recorded pace to either backend (SkiaSharp
   * or WPF vector) while its header reports fps, active hits, frame time, draw ops/sec and drops. The seed is
   * fixed so runs are comparable; click or Esc stops early. Folding/count-up is no longer simulated here: the
   * canvas does it for real through FctIngest, which is the point of the comparison. The region scheme comes from
   * the same setting the live overlay uses, so a bands run and a halves run differ in nothing but layout.
   */
  public partial class FctSimulationWindow : Window
  {
    private const double DurationMs = 60_000.0;
    private const int RandomSeed = 20_260_714;

    /*
     * Raid-pull stress: a busy 4-man pull produces ~15-45 FCT-relevant events/s (own hits, damage taken,
     * heals received); x10 of the baseline stream is the target we must survive. Set to 1.0 for the lighter
     * solo-fight baseline.
     */
    private const double RateMultiplier = 10.0;

    /* One simulated record, as it would arrive off the DamageRecord/HealRecord live events. */
    private readonly record struct FctSimEvent(double AtMs, FctLane Lane, double Value, string Action, bool Crit, bool Minor, bool Periodic, string ValueText);

    private static readonly string[] MeleeActions = ["Sweep", "Flurry", "Cleaver", "Double Slice", "Spinning Attack", "Backstab"];
    private static readonly string[] SpellActions = ["Fireball", "Frost Nova", "Lightning Bolt", "Pyroclasm", "Arcane Missile"];
    private static readonly string[] DotActions = ["Immolation", "Acid Poison", "Crippling Poison", "Frostbite"];
    private static readonly string[] TakenActions = ["Rends", "Bites", "Claws", "Crushing Blow", "Impale", "Fireball"];
    private static readonly string[] HealActions = ["Healing Word", "Roar of the Lion", "Blessed Light", "Prayer of Fealty"];
    private static readonly string[] HotActions = ["Regeneration", "Prayer of Fealty", "Soothing Song"];
    private static readonly string[] MissLabels = ["Dodge", "Miss", "Parry", "Miss", "Riposte"];
    private static readonly string[] DefensiveLabels = ["Block", "Absorb", "Block", "Invulnerable"];

    private readonly List<FctSimEvent> _events = [];
    private int _eventIndex;
    private long _generatedCount;
    private double _lastHeaderUpdateMs = -1000;
    private bool _closed;

    /* Active render backend: SkiaSharp (FctSkiaCanvas) or WPF vector (FctSimCanvas), behind one interface. */
    private IFctCanvas _canvas;
    private IFctDiagnostics _diagnostics;

    public FctSimulationWindow(bool useSkia = false)
    {
      InitializeComponent();
      BuildEvents();

      _canvas = useSkia ? fctSkiaCanvas : fctCanvas;
      _diagnostics = (IFctDiagnostics)_canvas;
      _canvas.Layout = FctOverlaySettings.LoadLayout();
      fctCanvas.Visibility = useSkia ? Visibility.Collapsed : Visibility.Visible;
      fctSkiaCanvas.Visibility = useSkia ? Visibility.Visible : Visibility.Collapsed;
      titleText.Text = $"FCT Render Simulation ({(useSkia ? "SkiaSharp" : "WPF vector")}) — 60 seconds, {_events.Count:N0} simulated records (rate ×{RateMultiplier:0.#})";
    }

    protected override void OnContentRendered(EventArgs e)
    {
      base.OnContentRendered(e);
      CenterOnScreen();
      _canvas.EventsFrame += OnSimFrame;
      _canvas.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
      if (!_closed)
      {
        _closed = true;
        _canvas.EventsFrame -= OnSimFrame;
        _canvas.Stop();
      }

      base.OnClosed(e);
    }

    private void RootMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Close();

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.Escape)
      {
        Close();
      }
    }

    /* Runs once per rendered frame from the canvas clock: feed due records, refresh the header at 2 Hz, auto-close. */
    private void OnSimFrame(double nowMs)
    {
      if (_closed)
      {
        return;
      }

      while (_eventIndex < _events.Count && _events[_eventIndex].AtMs <= nowMs)
      {
        Feed(_events[_eventIndex]);
        _eventIndex++;

        // auto-close fires OnClosed (which stops the canvas) from inside this callback
        if (_closed)
        {
          return;
        }
      }

      if (nowMs - _lastHeaderUpdateMs >= 500)
      {
        _lastHeaderUpdateMs = nowMs;
        statsText.Text = $"t {nowMs / 1000.0:0} s / {DurationMs / 1000.0:0} s   |   records {_generatedCount}/{_events.Count}   |   active {_canvas.ActiveCount}" +
                         $"   |   fps {_diagnostics.Fps:0} on {_diagnostics.DisplayHz:0} Hz   |   frame {_diagnostics.LastFrameMs:0.##} ms (avg {_diagnostics.AvgFrameMs:0.##} / max {_diagnostics.MaxFrameMs:0.##})" +
                         $"   |   draw ops {_diagnostics.DrawsPerSec:0}/s   |   dropped {_diagnostics.DroppedCount}";
      }

      if (nowMs >= DurationMs)
      {
        Close();
      }
    }

    private void Feed(FctSimEvent ev)
    {
      _generatedCount++;
      _canvas.AddHit(ev.Lane, ev.Value, ev.Action, ev.Crit, ev.Minor, ev.Periodic, ev.ValueText);
    }

    private void CenterOnScreen()
    {
      var work = SystemParameters.WorkArea;
      Left = work.Left + ((work.Width - Width) / 2.0);
      Top = work.Top + ((work.Height - Height) / 2.0);
    }

    /* Builds the full 60 s schedule up front so record timing is independent of render performance. */
    private void BuildEvents()
    {
      var rand = new Random(RandomSeed);

      // outgoing (top band, or the right half in halves mode): yellow damage, orange crits, green heals I cast
      AddStream(rand, 2.0, 0.18, FctLane.DamageDealt, MeleeActions, () => 600 * Math.Exp(NextGaussian(rand) * 0.55), 250, 2600);
      AddStream(rand, 0.85, 0.15, FctLane.DamageDealt, SpellActions, () => 1400 * Math.Exp(NextGaussian(rand) * 0.6), 700, 5600);
      AddStream(rand, 1.5, 0.05, FctLane.DamageDealt, DotActions, () => 180 * Math.Exp(NextGaussian(rand) * 0.5), 90, 750, minor: true, periodic: true);
      AddStream(rand, 2.3, 0.12, FctLane.HealingDealt, HealActions, () => 1500 * Math.Exp(NextGaussian(rand) * 0.55), 600, 7000);
      AddStream(rand, 0.9, 0.0, FctLane.HealingDealt, HotActions, () => 250 * Math.Exp(NextGaussian(rand) * 0.4), 120, 900, minor: true, periodic: true);

      // incoming (bottom band, or the left half): red damage taken, green heals I receive
      AddStream(rand, 1.7, 0.10, FctLane.DamageTaken, TakenActions, () => 450 * Math.Exp(NextGaussian(rand) * 0.6), 150, 3200);
      AddStream(rand, 1.6, 0.0, FctLane.HealingReceived, HealActions, () => 900 * Math.Exp(NextGaussian(rand) * 0.5), 400, 5200);

      // zero-damage labels: the text-only lanes are why the command carries a literal display string
      AddLabelStream(rand, 0.3, FctLane.Missed, MissLabels);
      AddLabelStream(rand, 0.25, FctLane.Defensive, DefensiveLabels);

      _events.Sort((a, b) => a.AtMs.CompareTo(b.AtMs));
    }

    private void AddStream(Random rand, double rate, double critChance, FctLane lane, string[] actions, Func<double> rawValue, double min, double max, bool minor = false, bool periodic = false)
    {
      rate *= RateMultiplier;
      var t = NextPoissonGap(rand, rate);

      while (t < DurationMs)
      {
        var value = Math.Clamp(rawValue(), min, max);
        var crit = rand.NextDouble() < critChance;
        if (crit)
        {
          value = Math.Min(max * 2.2, value * 2.2);
        }

        _events.Add(new(t, lane, value, actions[rand.Next(actions.Length)], crit, minor, periodic, null));
        t += NextPoissonGap(rand, rate * (InBurst(t) ? 2.2 : 1.0));
      }
    }

    /* Dodge/Parry/Block style events carry no number at all. */
    private void AddLabelStream(Random rand, double rate, FctLane lane, string[] labels)
    {
      rate *= RateMultiplier;
      var t = NextPoissonGap(rand, rate);

      while (t < DurationMs)
      {
        _events.Add(new(t, lane, 0, "", false, true, false, labels[rand.Next(labels.Length)]));
        t += NextPoissonGap(rand, rate);
      }
    }

    /* Two "nuke phase" windows double the hit rate, like a real fight. */
    private static bool InBurst(double t) => (t >= 18_000 && t < 23_000) || (t >= 44_000 && t < 49_000);

    /* Poisson inter-arrival gap in ms for a per-second rate. */
    private static double NextPoissonGap(Random rand, double rate) => -Math.Log(1.0 - rand.NextDouble()) / rate * 1000.0;

    private static double NextGaussian(Random rand)
    {
      var u1 = 1.0 - rand.NextDouble();
      var u2 = 1.0 - rand.NextDouble();
      return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
  }
}
