using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

using EQLogParser;

namespace EQLogParser;

/*
 * The cast caches are process-global - shared across captures, not owned by one (that is RecordsStore's own header) - while the code that
 * fills them is per line and the code that asks them a question runs wherever a line is being parsed. Two things make that more than one
 * thread: `ParseLane` exists so several readers can be alive at once (LogProcessor binds its lane for the whole run; null means Main), and
 * the log-preview pane re-parses damage lines itself - `EQLogViewer` calls DamageLineParser.ParseLine on its own task, with no capture open,
 * beside whatever the open session's reader is doing. Every one of those threads both writes (`RecordsStore.Add`, reached from the damage,
 * cast and misc parsers plus the capture that taps them) and reads: ambiguous-abbreviation resolution walks the cast history through
 * `EQDataStore.FindPreviousCast` → GetCastsBySpellName several times per line, and a horizon prune can be emptying the same cache on another
 * lane. The trigger lane is not one of these writers - TriggerProcessor reads chat shapes only (ChatLineParser, ConditionParser).
 *
 * That mix was a latent single-thread design: GetCastsBySpellName read the ambiguity cache's anchor — Count and the last entry —
 * OUTSIDE the lock that Add and the horizon prune hold, and a prune's RemoveRange CLEARS the vacated slots. A reader
 * that latched the old Count mid-sweep could index a nulled entry; the throw folded through the inlined FindPreviousCast
 * onto `EQDataStore.FindByLandsOn` with no frame naming the real method (field NRE 2026-10-13 while reading
 * a 1.3 GB capture: "cast line parse failed … Thundablast's detrimental magic splits with Improved Twincast.").
 *
 * This test hammers both halves against the shared RecordsStore (its own state is cleared before and after): two writer
 * lanes stamp casts at 40 s per step so the quarter-horizon prune fires every few casts, and two reader lanes ask the
 * eight-second question continuously. Before the lock, one of the readers faults (NullReference or IndexOutOfRange
 * depending on where the sweep catches it); after, the same storm is merely busy. Lock order under test is cache →
 * history, the order Add has always used — this run deadlocking would be the day that order was inverted somewhere.
 */
[TestClass]
[DoNotParallelize]
public class RecordsStoreConcurrencyTest
{
  private const int PerLane = 30_000;

  [TestInitialize]
  public void ClearStores() => RecordsStore.Instance.Clear();

  [TestCleanup]
  public void CleanupStores() => RecordsStore.Instance.Clear();

  [TestMethod]
  public void CastStormWithConcurrentPruneNeverTears()
  {
    // Add's ambiguity branch is the ONLY thing that fills _playerAmbiguityCastCache, and it needs a SpellData flagged
    // ambiguous; the caches key off spell.Spell.
    var ambiguous = new SpellData { Name = "Chill Skin", HasAmbiguity = true };

    var errors = new ConcurrentQueue<Exception>();

    /*
     * Both lanes advance LOG time in 40 s steps (a quarter of the 120 s cast-history horizon), so `PruneCastHistoryLocked`
     * re-arms and sweeps almost every cast — which is precisely the half that clears list slots. The phase offset just
     * keeps the two writers from stepping in lockstep.
     */
    var writers = new[]
    {
      Task.Run(() => StampCasts(ambiguous, "Storm A", 0, errors)),
      Task.Run(() => StampCasts(ambiguous, "Storm B", PerLane / 2 * 40, errors)),
    };

    var readers = new[]
    {
      Task.Run(() => QuerySpells(errors)),
      Task.Run(() => QuerySpells(errors)),
    };

    Task.WaitAll([.. writers, .. readers], TimeSpan.FromSeconds(120));

    Assert.IsTrue(errors.IsEmpty,
      $"cast-cache storm tore the shared state {errors.Count} time(s): {errors.FirstOrDefault()?.GetType().Name}: {errors.FirstOrDefault()?.Message}");
  }

  private static void StampCasts(SpellData spellData, string caster, long startS, ConcurrentQueue<Exception> errors)
  {
    try
    {
      for (var i = 0; i < PerLane; i++)
      {
        var t = startS + (long)i * 40;
        RecordsStore.Instance.Add(new SpellCast { Caster = caster, Spell = $"Storm Spell {i % 7}", SpellData = spellData }, t);
      }
    }
    catch (Exception e)
    {
      errors.Enqueue(e);
    }
  }

  private static void QuerySpells(ConcurrentQueue<Exception> errors)
  {
    try
    {
      var sink = 0;
      for (var i = 0; i < PerLane; i++)
      {
        var casts = RecordsStore.Instance.GetCastsBySpellName($"Storm Spell {i % 7}", 8);
        sink += casts.Count;
      }

      GC.KeepAlive(sink); // the query surviving is the assertion; its count is not
    }
    catch (Exception e)
    {
      errors.Enqueue(e);
    }
  }
}
