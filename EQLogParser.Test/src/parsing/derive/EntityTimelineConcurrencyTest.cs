using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser
{
  /// <summary>
  /// A timeline is folded on the derive worker while other threads ask it questions — the identity seam answers menu
  /// enables on the UI thread, EventViewer reads verdicts for its kill rows, SpellDamageStatsViewer filters a grid inside
  /// a Task.Run. The convention (docs/DesignNotes.md → "The one seam that answers") is: mutators take
  /// EntityTimeline.SyncRoot and a caller from another thread takes it around its own read; the rule book's own reads stay
  /// unlocked, because there is exactly one mutator at a time and paying a lock per per-fact lookup would move a pass.
  ///
  /// Two observable laws of a read taken under that lock, both raced against a table that is resizing underneath:
  /// **a claim once visible stays visible** (nothing removes from this store, so a name going missing mid-flight is a torn
  /// bucket walk), and **walking the key set does not throw**. Take the lock away from either side and both start failing —
  /// the second one deterministically, which is why it is in the loop at all.
  /// </summary>
  [TestClass]
  public class EntityTimelineConcurrencyTest
  {
    private const int WrittenNames = 20_000;

    [TestMethod]
    public void AForeignReadUnderSyncRootNeverSeesATornTable()
    {
      var timeline = new EntityTimeline();
      var writing = 1;
      Exception? failure = null;

      var readers = Enumerable.Range(0, 3).Select(slot => new Thread(() =>
      {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
          for (var spin = 0; Volatile.Read(ref writing) == 1; spin++)
          {
            lock (timeline.SyncRoot)
            {
              // The reads the foreign callers actually make. A name that went missing after being seen is a torn walk —
              // the table never removes, and neither side of the seam is allowed to answer "nobody here" about a claim
              // this capture already made.
              for (var i = 0; i < 8; i++)
              {
                var name = $"Name{(spin * 31 + i * 7) % WrittenNames}";
                var present = timeline.HasIdentity(name);
                if (present) seen.Add(name);
                else if (seen.Contains(name))
                  throw new InvalidOperationException($"{name} was present and then was not");
              }

              _ = timeline.IdentityAt("Name13", 25_000);
              _ = timeline.StateStamp();

              // Enumerating while somebody inserts throws outright ("enumerator may not be used after modification") —
              // the deterministic half of this test.
              if (spin % 512 == 0) _ = timeline.NamesWithIdentity().Count(n => n.Length > 0);
            }
          }
        }
        catch (Exception e)
        {
          Interlocked.CompareExchange(ref failure, e, null);
        }
      })).ToList();

      readers.ForEach(t => t.Start());

      // Unique names and rising times so every insert really lands: the table resizes repeatedly under the readers.
      for (var i = 0; i < WrittenNames; i++)
        timeline.SetIdentity($"Name{i}", IdentityKind.Npc, 6, "R6-npcdb", i);
      for (var i = 0; i < 2_000; i++)
        timeline.AddAffiliation(AffiliationKind.Friendly, $"Name{i}", i, i + 5, 8, "R8-party");

      Volatile.Write(ref writing, 0);
      readers.ForEach(t => t.Join());

      Assert.IsNull(failure, $"a read taken under SyncRoot tore while the table grew: {failure?.GetType().Name}: {failure?.Message}");
      Assert.AreEqual(WrittenNames, timeline.NamesWithIdentity().Count, "the folded table lost names");
      Assert.IsTrue(timeline.HasIdentity("Name1"), "a claim made during the race is not readable afterwards");
    }

    [TestMethod]
    public void ReassertingTheSameClaimStillCostsNothingToAReader()
    {
      /*
       * The mutators dedupe — a re-assertion of an identical tuple returns before touching the list — and they now do that
       * work inside the lock. Hot callers re-assert the same tuple thousands of times per name (owner-line damage facts,
       * join-line churn), so the lock is taken far more often than claims actually land; what must not change is the store:
       * the digest and the list stay put, which is also what keeps a carried derive from rebuilding.
       */
      var timeline = new EntityTimeline();
      timeline.SetIdentity("Kilsa", IdentityKind.Player, 9, "R15-healed", 10);
      var stamp = timeline.StateStamp();

      for (var i = 0; i < 5_000; i++)
        timeline.SetIdentity("Kilsa", IdentityKind.Player, 9, "R15-healed", 10);

      Assert.AreEqual(stamp, timeline.StateStamp(), "re-asserting an identical claim moved the digest");
      Assert.AreEqual(IdentityKind.Player, timeline.Identity("kilsa"));
    }
  }
}
