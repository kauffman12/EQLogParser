namespace EQLogParser
{
  // Close on an observed signal or the bounded timeout; a late signal must not stretch the window.
  internal enum CharmEndReason : byte
  {
    WearOff = 0,     // "<Owner>'s <charm spell> spell has worn off of X." (CharmSpells gates this)
    HitOurSide = 1,  // the charmed name swung at somebody on our side: the charm broke, EQ does not say so
    Death = 2,       // the charmed name died (the raid's usual way of ending a charm)
    Cap = 3,         // MaxWindowS elapsed with no signal — the normal expiry path, see below
    LogEnd = 4       // the file ran out before the ceiling did (the span still ends at the ceiling)
  }

  // Re-charms can change hands inside one window; each segment credits only the charmer who held it then.
  internal readonly record struct CharmOwnerSegment(double FromS, double ToS, string Owner);

  internal sealed class CharmWindow
  {
    public string Name;
    public string Owner;          // first named charmer; Segments carries any later ownership changes
    public double T0;
    public double T1;             // exclusive; the ceiling still bounds a window that outlives the log
    public CharmEndReason Reason;
    public int Starts;            // charm-success sightings merged into this window
    public int FactCount;         // facts with this name as attacker inside the span
    public int SameNameFactCount; // …against another mob with the SAME name: genuinely ambiguous credit
    public long CreditedTotal;    // sum of those facts — what moves from the NPC row to the owner's pet row

    public List<CharmOwnerSegment> Segments = [];   // per-charmer holds; their union is [T0, T1)

    public bool Owned => !string.IsNullOrEmpty(Owner);

  }

  // Carried event tables and cursors are valid only while classification is frozen; otherwise rebuild them.
  // docs/DesignNotes.md → "Charm ownership follows the spell's lands-on-other message".
  internal sealed class CharmCarry
  {
    internal readonly Dictionary<string, List<(double T, string Owner, int Seq)>> Starts =
      new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, List<(double T, string Owner)>> WearOffs = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, List<double>> Deaths = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, List<double>> Broke = new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<CharmWindow> PreviousWindows = [];

    internal double LastTime;
    internal int EvidenceCursor;
    internal int DeathCursor;
    internal int FactCursor;
  }

  internal static class CharmWindowPolicy
  {
    /*
     * Hard ceiling on a charm window. Not a guess about EQ's charm duration: the log never reports
     * expiry, so this is the amount of time we are willing to keep crediting a mob name as "ours" with no
     * supporting evidence, taken from how an operator actually re-charms (measured in
     * eqlog_Kizant_xegony-01-06-24: charms of "an imbued whipgrass" at 13:11:03 and 13:12:15 — the second
     * sighting lands inside the first window and merges, which is the behaviour this constant tunes).
     */
    internal const double MaxWindowS = 360;

    // The parser confirms a landing against the newest matching cast inside this inclusive lookback.
    // A matching non-charm, interruption or absent cast refuses the start before any identity claim is made.
    internal const double OwnerCastLookbackS = 6;

    public static List<CharmWindow> Apply(IFactTable facts, EntityTimeline timeline)
      => Apply(facts, timeline, new CharmCarry(), frozen: false);

    /*
     * Incremental contract (see ClassificationState): `frozen` promises nothing has moved since this policy last
     * walked that changes how ALREADY-WALKED events read — no verdict moved, so every past break-signal answer
     * ("did this charmed name hit one of ours") and every IsOurSide check still stands. The event tables then
     * take only [cursor..tail], window counters extend from the new fact slice, and windows are rebuilt from the
     * carried tables — which cost what the charms cost (tens), not what the capture costs (millions).
     *
     * Not frozen means rebuild from nothing, exactly as every pass did before this existed, and the caller
     * re-clocks the rule. Either way the AFFILIATION writes below run over the full rebuilt window list per
     * pass: unchanged segments hit EntityTimeline's dedupe and move neither the store nor its digest.
     */
    public static List<CharmWindow> Apply(IFactTable facts, EntityTimeline timeline, CharmCarry carry, bool frozen)
    {
      var starts = carry.Starts;
      var wearOffs = carry.WearOffs;

      if (!frozen)
      {
        starts.Clear();
        wearOffs.Clear();
        carry.Deaths.Clear();
        carry.Broke.Clear();
        carry.PreviousWindows.Clear();
        carry.LastTime = 0d;
        carry.EvidenceCursor = carry.DeathCursor = carry.FactCursor = 0;
      }

      var evidence = facts.Evidence;
      for (var i = carry.EvidenceCursor; i < evidence.Length; i++)
      {
        var e = evidence[i];
        var name = facts.NameOf(e.NameIdx);
        if (string.IsNullOrEmpty(name)) continue;

        if (e.Kind == EvidenceFact.EvCharmStart)
        {
          if (!starts.TryGetValue(name, out var list)) starts[name] = list = [];
          list.Add((e.TimeS, facts.AuxOf(e.AuxIdx), e.Seq));
        }
        else if (e.Kind == EvidenceFact.EvCharmEnd)
        {
          if (!wearOffs.TryGetValue(name, out var list)) wearOffs[name] = list = [];
          list.Add((e.TimeS, facts.AuxOf(e.AuxIdx)));
        }
      }

      var windows = new List<CharmWindow>();
      if (starts.Count == 0) return windows;
      carry.EvidenceCursor = evidence.Length;

      var deaths = carry.Deaths;
      var deathList = facts.Deaths;
      for (var i = carry.DeathCursor; i < deathList.Length; i++) AddTime(deaths, facts.NameOf(deathList[i].KilledIdx), deathList[i].TimeS);
      carry.DeathCursor = deathList.Length;

      // The break signal: a charmed name hitting somebody the timeline already puts on our side. This
      // runs after every other identity rule for that reason — a defender called ours by R1/R2/R3/R6/R7
      // is what ends a charm, so the identities have to be settled first.
      var broke = carry.Broke;
      var countFrom = carry.FactCursor;   // window counters extend over the same slice this walk covers
      foreach (var slice in FactRowWalk.Damage(facts, countFrom))
      {
        for (var i = 0; i < slice.Length; i++)
        {
          var f = slice[i];
          if (f.TimeS > carry.LastTime) carry.LastTime = f.TimeS;   // the log's own end: a charm past it never closes
          var atk = facts.NameOf(f.AtkIdx);
          if (!starts.ContainsKey(atk)) continue;
          var t = f.TimeS;
          if (IsOurSide(timeline, facts.NameOf(f.DefIdx), t)) AddTime(broke, atk, t);
        }
      }

      carry.FactCursor = facts.FactCount;

      foreach (var (name, startTimes) in starts)
      {
        startTimes.Sort(static (a, b) => a.T != b.T ? a.T.CompareTo(b.T) : a.Seq.CompareTo(b.Seq));
        var closes = BuildCloses(name, wearOffs, deaths, broke);
        windows.AddRange(BuildWindows(name, startTimes, closes, carry.LastTime));
      }

      windows.Sort(static (a, b) => a.T0.CompareTo(b.T0));
      CountFacts(facts, windows, carry, countFrom);

      foreach (var w in windows)
      {
        // Identity stays Npc; the confirmed start supplies time-scoped Friendly allegiance and its matched caster.
        // Owner segments move credit when another caster takes over without creating permanent ownership.
        //
        // One interval per OWNER SEGMENT rather than one per window: when a second charmer takes the mob
        // over mid-span, the credit has to move at that moment. The segments tile [T0, T1), so the flip
        // itself covers exactly what one window-wide interval would have.
        // The spelling here is the one the confirm line used ("an imbued whipgrass"), which is NOT the spelling
        // the projection asks with ("An imbued whipgrass", capitalized by the parser wherever the mob starts a
        // sentence). That meets for free now that EntityTimeline keys names case-insensitively — registering both
        // spellings by hand was this seam's local workaround, and it is gone.
        if (string.IsNullOrEmpty(w.Name)) continue;

        if (w.Segments.Count == 0)
        {
          timeline.AddAffiliation(AffiliationKind.Friendly, w.Name, w.T0, w.T1, RuleStrength.Certain, "R9-charm", w.Owner);
          continue;
        }
        foreach (var seg in w.Segments)
          timeline.AddAffiliation(AffiliationKind.Friendly, w.Name, seg.FromS, seg.ToS, RuleStrength.Certain, "R9-charm", seg.Owner);
      }

      return windows;
    }

    // Union of the three event kinds, ascending, each tagged with its reason.
    private static List<(double T, CharmEndReason Reason, string Owner)> BuildCloses(
      string name,
      Dictionary<string, List<(double T, string Owner)>> wearOffs,
      Dictionary<string, List<double>> deaths,
      Dictionary<string, List<double>> broke)
    {
      var closes = new List<(double, CharmEndReason, string)>();

      if (wearOffs.TryGetValue(name, out var ws))
      {
        foreach (var (t, owner) in ws) closes.Add((t, CharmEndReason.WearOff, owner));
      }
      if (deaths.TryGetValue(name, out var ds))
      {
        foreach (var t in ds) closes.Add((t, CharmEndReason.Death, null));
      }
      if (broke.TryGetValue(name, out var bs))
      {
        foreach (var t in bs) closes.Add((t, CharmEndReason.HitOurSide, null));
      }

      closes.Sort(static (a, b) => a.Item1.CompareTo(b.Item1));
      return closes;
    }

    // Walk sightings and closes in time order, merging re-charms into the window they fall inside.
    private static List<CharmWindow> BuildWindows(
      string name,
      List<(double T, string Owner, int Seq)> startTimes,
      List<(double T, CharmEndReason Reason, string Owner)> closes,
      double lastTime)
    {
      var windows = new List<CharmWindow>();

      var t0 = double.NaN;
      var lastStart = 0d;
      var starts = 0;
      var owner = (string)null;
      var open = false;
      var segs = new List<CharmOwnerSegment>();
      var segStart = 0d;

      // Closes the hold in progress. The ceiling/re-charm bookkeeping updates lastStart, not the hold:
      // the same charmer re-casting does not hand the mob to itself as a new owner.
      void EndHold(double at)
      {
        if (!open || at <= segStart) return;
        segs.Add(new CharmOwnerSegment(segStart, at, owner));
      }

      void Close(double at, CharmEndReason reason, string closeOwner)
      {
        if (!open) return;
        EndHold(at);
        // A wear-off can fill an unnamed segment but must never override an already matched charmer.
        if (closeOwner is not null)
          for (var i = 0; i < segs.Count; i++)
            if (segs[i].Owner is null) segs[i] = segs[i] with { Owner = closeOwner };
        var finalOwner = null as string;
        foreach (var s in segs)
          if (s.Owner is not null) { finalOwner = s.Owner; break; }
        // A hold that closed in the same second it opened records no span, so the segments cannot answer
        // who cast it — but the window still knows, and dropping it here would lose the only owner a
        // sighting ever had (measured: one whipgrass window in eqlog_Kizant_xegony-01-06-24).
        finalOwner ??= owner ?? closeOwner;
        windows.Add(new CharmWindow
        {
          Name = name, Owner = finalOwner ?? closeOwner, T0 = t0, T1 = at, Reason = reason,
          Starts = starts, Segments = segs
        });
        segs = [];   // the next window of this name must not share the list just handed out
        open = false;
      }

      var si = 0;
      var ci = 0;
      while (si < startTimes.Count || ci < closes.Count)
      {
        var nextStart = si < startTimes.Count ? startTimes[si].T : double.PositiveInfinity;
        var nextClose = ci < closes.Count ? closes[ci].T : double.PositiveInfinity;

        if (nextStart <= nextClose)
        {
          var (t, startOwner, _) = startTimes[si++];
          if (open && t < lastStart + MaxWindowS)
          {
            // Re-charm inside the open span: one entry, clock resets from this sighting. But a cast by
            // somebody ELSE is a steal — the mob changes hands at that second, so the credit splits while
            // the window stays one row. Uncorroborated landing lines never reach this start list.
            lastStart = t;
            starts++;
            var recast = startOwner;
            if (recast is not null && !string.Equals(recast, owner, StringComparison.Ordinal))
            {
              EndHold(t);
              owner = recast;
              segStart = t;
            }
            continue;
          }

          // Either nothing was open, or this sighting lands past the ceiling of the open one: settle the
          // earlier window at its cap and start a fresh charm here.
          Close(lastStart + MaxWindowS, CharmEndReason.Cap, null);
          t0 = t;
          lastStart = t;
          starts = 1;
          owner = startOwner;
          segStart = t;
          open = true;
        }
        else
        {
          var (t, reason, closeOwner) = closes[ci++];
          if (!open || t < t0) continue;   // a close with no charm sighting behind it says nothing

          /*
           * A signal that lands past the ceiling settles the window at the cap and is dropped: MaxWindowS is how long we are
           * willing to credit this name as ours on one sighting, and a death or wear-off four minutes after the cap would extend
           * the window well past that - which is exactly how a charm of 13:47 used to stretch to 15:09 off one late slain line,
           * re-friending every later mob of that name for six minutes and dead-marking its true death as "not while charmed".
           * The boundary matches the start side, where a sighting at exactly the cap already fails to merge: t >= cap settles.
           */
          if (t >= lastStart + MaxWindowS)
          {
            Close(lastStart + MaxWindowS, CharmEndReason.Cap, null);
            continue;
          }

          Close(t, reason, closeOwner);
        }
      }

      if (open)
      {
        var cap = lastStart + MaxWindowS;
        Close(cap, cap <= lastTime ? CharmEndReason.Cap : CharmEndReason.LogEnd, null);
      }
      return windows;
    }

    // Player-side defender test for the break signal. A name another charm window put on our side counts,
    // but only through its own R9 interval: identity alone would call a charmed raid-buff target hostile.
    private static bool IsOurSide(EntityTimeline timeline, string name, double t)
    {
      if (string.IsNullOrEmpty(name)) return false;

      var kind = timeline.IdentityAt(name, t);
      if (kind is IdentityKind.Player or IdentityKind.Merc or IdentityKind.Pet) return true;
      if (kind is not IdentityKind.Npc) return false;

      return timeline.AffiliationAt(name, t, out var source) == AffiliationKind.Friendly
             && source is not null && source.StartsWith("R9-charm", StringComparison.Ordinal);
    }

    /*
     * Window fact counts extend rather than restart: a window's identity across passes is (name, T0) — its span
     * may CLOSE at a newer event but never moves backward under already-counted facts, because closes arrive
     * with their own timestamps and every fact walked before that event is older than it. So the counters from
     * the previous pass's list seed this one, and only [countFrom..tail] gets walked. A full rebuild passes
     * countFrom 0 with empty carry and reproduces the original walk exactly.
     */
    private static void CountFacts(IFactTable facts, List<CharmWindow> windows, CharmCarry carry, int countFrom)
    {
      if (carry.PreviousWindows.Count > 0)
      {
        var done = new Dictionary<string, List<CharmWindow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in carry.PreviousWindows)
        {
          if (!done.TryGetValue(w.Name ?? string.Empty, out var list)) done[w.Name ?? string.Empty] = list = [];
          list.Add(w);
        }

        foreach (var w in windows)
        {
          if (!done.TryGetValue(w.Name ?? string.Empty, out var list)) continue;
          foreach (var p in list)
          {
            if (p.T0 != w.T0) continue;
            w.FactCount = p.FactCount;
            w.SameNameFactCount = p.SameNameFactCount;
            w.CreditedTotal = p.CreditedTotal;
            break;
          }
        }
      }

      if (windows.Count == 0) return;

      var byName = new Dictionary<string, List<CharmWindow>>(StringComparer.OrdinalIgnoreCase);
      foreach (var w in windows)
      {
        if (!byName.TryGetValue(w.Name, out var list)) byName[w.Name] = list = [];
        list.Add(w);
      }

      foreach (var slice in FactRowWalk.Damage(facts, countFrom))
      {
        for (var i = 0; i < slice.Length; i++)
        {
          var f = slice[i];
          var atk = facts.NameOf(f.AtkIdx);
          if (!byName.TryGetValue(atk, out var wlist)) continue;

          foreach (var w in wlist)
          {
            if (f.TimeS < w.T0 || f.TimeS >= w.T1) continue;
            w.FactCount++;
            w.CreditedTotal += f.Total;
            if (string.Equals(facts.NameOf(f.DefIdx), atk, StringComparison.OrdinalIgnoreCase)) w.SameNameFactCount++;
            break;
          }
        }
      }

      carry.PreviousWindows.Clear();
      carry.PreviousWindows.AddRange(windows);
    }

    private static void AddTime(Dictionary<string, List<double>> map, string name, double t)
    {
      if (string.IsNullOrEmpty(name)) return;
      if (!map.TryGetValue(name, out var list)) map[name] = list = [];
      list.Add(t);
    }
  }
}
