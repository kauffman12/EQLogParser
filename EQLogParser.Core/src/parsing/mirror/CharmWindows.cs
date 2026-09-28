namespace EQLogParser.Mirror
{
  // Why a charm window stopped being a charm window. The distribution is the finding: EQ never logs a
  // charm expiring, so three of these five have to be inferred from behaviour. Measured over the three
  // captures with charm text (25 windows): wear-off lines accounted for 7 (all in eqlog_Incogitable_xegony,
  // all "Your Charm XVII spell has worn off of …"), death closed a few more, and the rest had no signal
  // at all — which is why a cap exists instead of an infinite window.
  internal enum CharmEndReason : byte
  {
    WearOff = 0,     // "<Owner>'s <charm spell> spell has worn off of X." (CharmSpells gates this)
    HitOurSide = 1,  // the charmed name swung at somebody on our side: the charm broke, EQ does not say so
    Death = 2,       // the charmed name died (the raid's usual way of ending a charm)
    Cap = 3,         // MaxWindowS elapsed with no signal — the normal expiry path, see below
    LogEnd = 4       // the file ran out before the ceiling did (the span still ends at the ceiling)
  }

  // One span during which a name (an NPC identity, always) was somebody's charmed pet. Re-charms of the
  // same mob name merge into the window they overlap, so six charms of "an exiled bloodhound" across an
  // evening are one entry in the pet list rather than six rows.
  // One charmer's hold on a name. A window can contain several: in EQ a second charm takes the mob off
  // whoever held it, so "two necros share one evening's pet" is one span with two credit segments — not
  // one name attached to the whole thing, and not a long-term assignment written to disk anywhere.
  internal readonly record struct CharmOwnerSegment(double FromS, double ToS, string Owner);

  internal sealed class CharmWindow
  {
    public string Name;
    public string Owner;          // null: no cast line named a caster (third-party charms write none)
    public double T0;
    public double T1;             // exclusive; double.PositiveInfinity when the log simply ended
    public CharmEndReason Reason;
    public int Starts;            // charm-success sightings merged into this window
    public int FactCount;         // facts with this name as attacker inside the span
    public int SameNameFactCount; // …against another mob with the SAME name: genuinely ambiguous credit
    public ulong CreditedTotal;   // sum of those facts — what moves from the NPC row to the owner's pet row

    public List<CharmOwnerSegment> Segments = [];   // per-charmer holds; their union is [T0, T1)

    public bool Owned => !string.IsNullOrEmpty(Owner);

    // Who held it at t — the segment that had begun and not yet ended. Null means "friendly, but no
    // charmer was identified", which is the honest answer for a log with no cast text in it.
    public string OwnerAt(double t)
    {
      for (var i = Segments.Count - 1; i >= 0; i--)
        if (Segments[i].FromS <= t && t < Segments[i].ToS) return Segments[i].Owner;
      return null;
    }
  }

  /*
   * R9 policy: a charm is an OPEN window, closed by state rather than by a single expected line.
   *
   * The log gives one fact and no bookkeeping. "an imbued whipgrass has been charmed." says the charm
   * landed; nothing in the file ever says it lapsed (measured over eqlog_Kizant_xegony-01-06-24: 3,825
   * wear-off lines and not one of them a charm), and no other line mentions the pet again — so between
   * the success line and whatever ends it, the name has to be treated as ours on the strength of the
   * policy below, exactly as an operator would read the fight.
   *
   * Closes, earliest wins:
   *   HitOurSide  the name damages a name the timeline puts on our side (the break the user describes:
   *               the charm caster vanishes, the mob turns on the raid). This is also why an invis cast by
   *               the owner is NOT itself a close: in the two files where the owner went invisible
   *               mid-charm the window's own damage is what showed the break (00:41:32 group invis → the
   *               whipgrass's next credited hit was on raid-side names), and 846 invis lines in one file
   *               make a speculative close there far too twitchy.
   *   Death       the name dies — first death at/after the start. Case-insensitive: charm lines print
   *               "a X", slain lines print "A X".
   *   WearOff     the gated spell-name wear-off line (CharmSpells), which also names the owner.
   *   Cap         MaxWindowS since the LATEST sighting of that name (a re-charm resets the clock).
   *   LogEnd      file ended with the charm standing.
   *
   * The name is the key, and that is the whole trick: it is what makes six separate charms of the same
   * mob type read as one pet, and it is also why an ambiguity number is reported instead of hidden —
   * another mob of the same species walking into the span is indistinguishable from the pet. SameNameFactCount
   * counts exactly that (pet-vs-same-name hits) so the UI can show the share rather than pretend to certainty.
   */
  /*
   * One-sided on purpose, and now measured: this reads the charms OUR raid casts (`has been charmed.`), because
   * that is all the log writes. The hostile-side shape the design doc lists as unparsed - "Raidman is under the
   * influence of ..." - occurs ZERO times across the 2022, 2024 and 2026 captures (censused alongside R18), so
   * there is nothing to recognize; building it from a spell-name guess would flip raid members on text that only
   * exists in trivia. If a capture ever does write one, the rule arrives with the sample line and a closed spell
   * vocabulary (this file's CharmSpells precedent), never with a heuristic.
   */
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

    /*
     * How far back a charm-success line may look for the cast that caused it. Measured on our own charms
     * (the only ones the text shows): "You begin casting Charm XVII." at 00:41:16 → charmed at 00:41:20,
     * and 00:44:49 (+ an interrupt/resume line) → charmed at 00:44:53 — Δ = 4 s both times.
     *
     * It is deliberately not wider. Widening this is how a charm owner gets invented: the nearest cast of
     * ANY spell before a success line sits at 3-25 s and is unrelated (Beguiler's Directed Banishment,
     * Slowing Helix, Group Perfected Invisibility — whatever the caster happened to be doing), so a
     * generous lookback attributes mobs to whoever was busy, not to the charmer. Third-party charm casts
     * are absent from these captures entirely; when one exists in range it pairs through this rule, and
     * when it does not the window is ownerless, which is the honest answer.
     */
    internal const double OwnerCastLookbackS = 8;

    public static List<CharmWindow> Apply(IFactTable facts, EntityTimeline timeline)
    {
      var starts = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
      var wearOffs = new Dictionary<string, List<(double T, string Owner)>>(StringComparer.OrdinalIgnoreCase);
      var charmCasts = new List<(double T, string Caster)>();

      foreach (var e in facts.Evidence)
      {
        if (e.Kind == EvidenceFact.EvCast)
        {
          // R4 reads these same facts for the caster's identity; here they answer "who charmed it".
          var spell = facts.AuxOf(e.AuxIdx);
          if (CharmSpells.IsCharmSpellName(spell)) charmCasts.Add((e.TimeS, facts.NameOf(e.NameIdx)));
          continue;
        }

        var name = facts.NameOf(e.NameIdx);
        if (string.IsNullOrEmpty(name)) continue;

        if (e.Kind == EvidenceFact.EvCharmStart) AddTime(starts, name, e.TimeS);
        else if (e.Kind == EvidenceFact.EvCharmEnd)
        {
          if (!wearOffs.TryGetValue(name, out var list)) wearOffs[name] = list = [];
          list.Add((e.TimeS, facts.AuxOf(e.AuxIdx)));
        }
      }

      var windows = new List<CharmWindow>();
      if (starts.Count == 0) return windows;

      var deaths = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
      foreach (var d in facts.Deaths) AddTime(deaths, facts.NameOf(d.KilledIdx), d.TimeS);

      // The break signal: a charmed name hitting somebody the timeline already puts on our side. This
      // runs after every other identity rule for that reason — a defender called ours by R1/R2/R3/R6/R7
      // is what ends a charm, so the identities have to be settled first.
      var broke = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
      var lastTime = 0d;
      foreach (var f in facts.Facts)
      {
        if (f.TimeS > lastTime) lastTime = f.TimeS;   // the log's own end: a charm past it never closes
        var atk = facts.NameOf(f.AtkIdx);
        if (!starts.ContainsKey(atk)) continue;
        var t = f.TimeS;
        if (IsOurSide(timeline, facts.NameOf(f.DefIdx), t)) AddTime(broke, atk, t);
      }

      foreach (var (name, startTimes) in starts)
      {
        startTimes.Sort();
        var closes = BuildCloses(name, wearOffs, deaths, broke);
        windows.AddRange(BuildWindows(name, startTimes, closes, charmCasts, lastTime));
      }

      windows.Sort(static (a, b) => a.T0.CompareTo(b.T0));
      CountFacts(facts, windows);

      foreach (var w in windows)
      {
        // Identity stays Npc (set on the start sighting by R9); this is the time-scoped half: the name is
        // ON our side for this span. Kind stays Friendly rather than PetOfPlayer on purpose — a window is
        // not an ownership claim, and when no caster is named there is nobody to be owned by. Where a
        // caster IS named it rides in Owner, which is what OwnerOf(name, t) answers with.
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
      List<double> startTimes,
      List<(double T, CharmEndReason Reason, string Owner)> closes,
      List<(double T, string Caster)> charmCasts,
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
        // The wear-off line is the one place a charm says whose it was. It lands at the END of the hold,
        // so it claims every stretch that named no charmer; a stretch that already names somebody else
        // keeps them — two necros are not merged into one owner by the second one's log lines.
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
        var nextStart = si < startTimes.Count ? startTimes[si] : double.PositiveInfinity;
        var nextClose = ci < closes.Count ? closes[ci].T : double.PositiveInfinity;

        if (nextStart <= nextClose)
        {
          var t = startTimes[si++];
          if (open && t < lastStart + MaxWindowS)
          {
            // Re-charm inside the open span: one entry, clock resets from this sighting. But a cast by
            // somebody ELSE is a steal — the mob changes hands at that second, so the credit splits while
            // the window stays one row. A sighting with no cast in range proves nothing and leaves the
            // current holder alone.
            lastStart = t;
            starts++;
            var recast = OwnerFromCast(charmCasts, t);
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
          owner = OwnerFromCast(charmCasts, t);
          segStart = t;
          open = true;
        }
        else
        {
          var (t, reason, closeOwner) = closes[ci++];
          if (!open || t < t0) continue;   // a close with no charm sighting behind it says nothing
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

    // The most recent charm-spell cast inside the lookback, by whoever made it.
    private static string OwnerFromCast(List<(double T, string Caster)> charmCasts, double t0)
    {
      var best = double.NegativeInfinity;
      string owner = null;
      foreach (var (t, caster) in charmCasts)
      {
        if (t > t0 || t < t0 - OwnerCastLookbackS || string.IsNullOrEmpty(caster)) continue;
        if (t >= best) { best = t; owner = caster; }
      }
      return owner;
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

    private static void CountFacts(IFactTable facts, List<CharmWindow> windows)
    {
      if (windows.Count == 0) return;

      var byName = new Dictionary<string, List<CharmWindow>>(StringComparer.OrdinalIgnoreCase);
      foreach (var w in windows)
      {
        if (!byName.TryGetValue(w.Name, out var list)) byName[w.Name] = list = [];
        list.Add(w);
      }

      foreach (var f in facts.Facts)
      {
        var atk = facts.NameOf(f.AtkIdx);
        if (!byName.TryGetValue(atk, out var list)) continue;

        foreach (var w in list)
        {
          if (f.TimeS < w.T0 || f.TimeS >= w.T1) continue;
          w.FactCount++;
          w.CreditedTotal += f.Total;
          if (string.Equals(facts.NameOf(f.DefIdx), atk, StringComparison.OrdinalIgnoreCase)) w.SameNameFactCount++;
          break;
        }
      }
    }

    private static void AddTime(Dictionary<string, List<double>> map, string name, double t)
    {
      if (string.IsNullOrEmpty(name)) return;
      if (!map.TryGetValue(name, out var list)) map[name] = list = [];
      list.Add(t);
    }
  }
}
