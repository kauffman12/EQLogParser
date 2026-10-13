using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser.Test
{
  /*
   * The sharded seam, second look: a buff line near a shard boundary asks "did anyone cast this rank in the last
   * eight seconds" while the lane that owns that cast has not reached it yet. The store was never sharded, so at
   * merge time every cast IS present — and a bulk open re-asks its still-open questions once, each anchored to the
   * row's OWN timestamp (the parse-time query anchors to "newest in cache", which at load end is the end of the file).
   *
   * These tests reproduce the seam directly: an unresolved row, a cast that arrived after it asked, and the retry.
   * The gate the whole feature hangs on: live parsing never opens the hold, so it never enqueues and never retries.
   */
  [TestClass]
  public class RecordsStoreAmbiguityRetryTest
  {
    private const int Self = (int)SpellTarget.Self;          // any caster of that rank proves it
    private const int SingleTarget = (int)SpellTarget.Singletarget; // only the receiver's own cast proves it

    // The seam gate reads the ambient lane's first stamp, so each test runs against a FRESH unstamped lane: its
    // FirstStampS is NaN (no line parsed), which AddPendingAmbiguity treats as "seam unknown — keep", giving these
    // store-level tests their plain semantics without depending on what other LogProcessor tests stamped onto Main.
    [TestInitialize]
    public void TestInit()
    {
      ParseLane.BindCurrentThread(new ParseLane());
      RecordsStore.Instance.Clear();
    }

    [TestCleanup]
    public void TestCleanup() => ParseLane.BindCurrentThread(null);

    private static SpellData Rank(string name, int target) => new() { Name = name, Target = (byte)target };

    // The shape a line with an unresolved ambiguous name leaves behind: candidates, no answer.
    private static ReceivedSpell UnresolvedRow(string receiver, params (string Name, int Target)[] ranks)
    {
      var spell = new ReceivedSpell { Receiver = receiver };
      var candidates = new List<SpellData>();
      foreach (var (name, target) in ranks) candidates.Add(Rank(name, target));
      spell.AddAmbiguity(candidates);
      return spell;
    }

    // A cast line as the store records it: only ambiguous names ever build history at all.
    private static void AddCast(string caster, (string Name, int Target) rank, double atS)
    {
      var data = Rank(rank.Name, rank.Target);
      data.HasAmbiguity = true;
      RecordsStore.Instance.Add(new SpellCast { Caster = caster, Spell = rank.Name, SpellData = data }, atS);
    }

    [TestMethod]
    public void RetryResolvesWhatTheSeamMissed()
    {
      RecordsStore.Instance.BeginCastHistoryHold();
      var row = UnresolvedRow("Bob", ("Chill Skin I", Self), ("Chill Skin II", Self));
      RecordsStore.Instance.AddPendingAmbiguity(row, 103);

      // The other lane owns this cast and reaches it later in wall-clock time: the row's question was already asked.
      AddCast("Zoe", ("Chill Skin II", Self), 100);

      var (pending, resolved) = RecordsStore.Instance.FinishCastHistoryHold();
      Assert.AreEqual(1, pending);
      Assert.AreEqual(1, resolved, "a cast inside the row's own window must answer on the second look");
      Assert.AreEqual("Chill Skin II", row.SpellData?.Name);
    }

    [TestMethod]
    public void CastOutsideTheWindowStillDoesNotAnswer()
    {
      RecordsStore.Instance.BeginCastHistoryHold();
      var row = UnresolvedRow("Bob", ("Chill Skin I", Self), ("Chill Skin II", Self));
      RecordsStore.Instance.AddPendingAmbiguity(row, 103);

      AddCast("Zoe", ("Chill Skin II", Self), 94);   // nine seconds before: outside the 8-second question
      AddCast("Zoe", ("Chill Skin II", Self), 106);  // after the row: a later cast never proves an earlier buff

      var (pending, resolved) = RecordsStore.Instance.FinishCastHistoryHold();
      Assert.AreEqual(1, pending);
      Assert.AreEqual(0, resolved);
      Assert.IsNull(row.SpellData, "the retry asks the SAME question the parse asked — it may not widen it");
    }

    [TestMethod]
    public void TargetedSpellNeedsTheReceiverOwnCast()
    {
      RecordsStore.Instance.BeginCastHistoryHold();
      var row = UnresolvedRow("Bob", ("Runes Mark", SingleTarget));
      RecordsStore.Instance.AddPendingAmbiguity(row, 103);
      AddCast("Zoe", ("Runes Mark", SingleTarget), 100); // someone else cast it: proves nothing about Bob's line

      var (_, resolved) = RecordsStore.Instance.FinishCastHistoryHold();
      Assert.AreEqual(0, resolved);
    }

    [TestMethod]
    public void HoldKeepsTheHistoryTheRetryNeeds()
    {
      // The horizon sweep would have eaten the 12-second-old cast long before t=50_000; under hold it survives —
      // which is the entire mechanism: rows from hours into a file get retried with the casts from those hours.
      RecordsStore.Instance.BeginCastHistoryHold();
      AddCast("Zoe", ("Chill Skin II", Self), 12);
      AddCast("Zoe", ("Chill Skin I", Self), 50_000);

      var row = UnresolvedRow("Bob", ("Chill Skin I", Self), ("Chill Skin II", Self));
      RecordsStore.Instance.AddPendingAmbiguity(row, 20);

      var (pending, resolved) = RecordsStore.Instance.FinishCastHistoryHold();
      Assert.AreEqual(1, pending);
      Assert.AreEqual(1, resolved, "the cast from t=12 answered a row at t=20 hours of load later");
      Assert.AreEqual("Chill Skin II", row.SpellData?.Name);

      // And with the hold closed, the horizon is earned back immediately: that cast can no longer answer.
      Assert.IsFalse(RecordsStore.Instance.WasCastWithin("Chill Skin II", 50_001, null),
        "after Finish the sweep must have caught up — a held horizon may never become the standing state");
    }

    [TestMethod]
    public void LiveParsingNeverEnqueues()
    {
      // No Begin: this is the live shape. The door is closed, so nothing accumulates and nothing retries.
      var row = UnresolvedRow("Bob", ("Chill Skin I", Self), ("Chill Skin II", Self));
      RecordsStore.Instance.AddPendingAmbiguity(row, 103);
      AddCast("Zoe", ("Chill Skin II", Self), 100);

      var (pending, resolved) = RecordsStore.Instance.FinishCastHistoryHold();
      Assert.AreEqual(0, pending);
      Assert.AreEqual(0, resolved);
      Assert.IsNull(row.SpellData);
    }

    [TestMethod]
    public void OnlySeamAdjacentRowsAreQueued()
    {
      // A lane that begins at t=100 (its own first stamp). Only rows within the seam window of that stamp can have
      // been starved by the cut; a row deep in the lane already saw its whole lookback and is never even queued.
      var lane = new ParseLane();
      lane.NoteFirstStamp(100);
      ParseLane.BindCurrentThread(lane);
      RecordsStore.Instance.BeginCastHistoryHold();

      var nearSeam = UnresolvedRow("Bob", ("Chill Skin II", Self));
      RecordsStore.Instance.AddPendingAmbiguity(nearSeam, 103); // 3 s past the seam: qualifies

      var deepInLane = UnresolvedRow("Zoe", ("Chill Skin II", Self));
      RecordsStore.Instance.AddPendingAmbiguity(deepInLane, 50_000); // hours into the lane: its own parses covered it

      AddCast("Wex", ("Chill Skin II", Self), 50_995); // inside deepInLane's window — yet a sequential read saw it too

      var (pending, _) = RecordsStore.Instance.FinishCastHistoryHold();
      Assert.AreEqual(1, pending, "only the near-seam row is a question the seam could have gotten wrong");
    }

    [TestMethod]
    public void ClearClosesTheDoorFromTheOtherSide()
    {
      // A load that died mid-file must not leave the horizon stopped for whatever opens next.
      RecordsStore.Instance.BeginCastHistoryHold();
      var row = UnresolvedRow("Bob", ("Chill Skin I", Self));
      RecordsStore.Instance.AddPendingAmbiguity(row, 5);

      RecordsStore.Instance.Clear();

      var (pending, resolved) = RecordsStore.Instance.FinishCastHistoryHold();
      Assert.AreEqual(0, pending);
      Assert.AreEqual(0, resolved);
    }
  }
}
