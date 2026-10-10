using System.Collections.Generic;

namespace EQLogParser;

/*
 * The question the boards are allowed to ask on every derive pass: "did anything we are SHOWING change what it IS?"
 *
 * The operator's rule, stated after two field sessions (2026-10-08): "i really dont want updates happening dynamically because new
 * damage came in either. id rather it be like a snapshot of what was selected at the time except for the pet changes or player turning
 * npc, etc — classification changes that matter." Damage is not news to a board you are reading; a verdict is. That killed the
 * capture-wide content stamp as a rebuild trigger (and the selection-scoped fingerprint built on the same day, whose remaining half was
 * still "my rows' numbers moved" — see docs/DesignNotes.md → "Snapshot unless a name changed what it is").
 *
 * So why not just gate on the identity digest? Measured over `eqlog_Incogitable_xegony.txt` replayed as 7 growing prefixes: the digest
 * moved on **6 of 6** passes, and every single one of those moves was a name being placed for the FIRST time (median 58 newly placed per
 * pass, max 220) with **zero** flips of an already-placed name. New names cannot change what a board that lists fixed names displays —
 * digest-gating would have rebuilt once per pass and looked exactly like the flicker being fixed. Scope is the whole idea: remember the
 * answer for each name the boards actually show, and ask only those.
 *
 * Three laws:
 *   - **only watched names are read**, so a capture that keeps meeting new mobs costs nothing;
 *   - an answer that FLIPS fires (Unknown→Pet when a pet pair is finally learned, Player→Npc while a raider is charmed), and so does a
 *     watched name going back to Unknown — `Reset` in the identity pane must refresh the boards it just un-decided;
 *   - an empty watch never fires. Nothing on screen means nothing can have changed on screen, and inventing a rebuild for it would put
 *     the flicker straight back.
 */
internal sealed class IdentityWatch
{
  /*
   * Two slots, because two threads own the answers: the board names are captured at the end of a build (the summary gate runs on a pool
   * thread) and the selected rows' names are captured when the pane announces (the UI thread). One shared dictionary would have either
   * writer tearing the other's enumeration, so each side owns one and a reader takes whichever reference it finds - both are honest
   * answers about what is on screen, and neither can throw mid-walk.
   */
  private volatile Dictionary<string, IdentityKind> _board = Empty();
  private volatile Dictionary<string, IdentityKind> _selection = Empty();

  internal int Count => _board.Count + _selection.Count;

  /// <summary>Forget everything: a new capture, or a board that no longer exists.</summary>
  internal void Clear()
  {
    _board = Empty();
    _selection = Empty();
  }

  /// <summary>What the NAMES on the built boards currently are. Called by whoever built them, from the names it displayed.</summary>
  internal void CaptureBoard(EntityTimeline timeline, IReadOnlyList<string> names) => _board = AnswersFor(timeline, names);

  /*
   * What the SELECTED rows' own names are. This is what catches a selected mob being charmed out from under the reader: that flips the
   * row's routing - its damage folding under a charmer, its outcome reading "dead, charmed" - and it happens whether or not any name
   * already listed on the board changed its answer.
   */
  internal void CaptureSelection(EntityTimeline timeline, IReadOnlyList<string> names) => _selection = AnswersFor(timeline, names);

  /// <summary>Did anything displayed change what it IS? The cheap test: O(names on screen), a few dozen to a few hundred.</summary>
  internal bool AnyChanged(EntityTimeline timeline) => FirstChanged(timeline) is not null;

  /*
   * The first name whose answer moved, as "Old>New Name" for the log line; null when nothing did. A dictionary lookup per displayed name,
   * which is why this may run on every pass of a live raid while materializing may not.
   */
  internal string FirstChanged(EntityTimeline timeline)
  {
    if (timeline is null) return null;

    return FirstChangedIn(_board, timeline) ?? FirstChangedIn(_selection, timeline);
  }

  private static string FirstChangedIn(Dictionary<string, IdentityKind> answers, EntityTimeline timeline)
  {
    // An empty slot never fires: nothing displayed means nothing can have changed on screen. Inventing a rebuild for that is the flicker.
    if (answers.Count == 0) return null;

    foreach (var (name, was) in answers)
    {
      var now = timeline.Identity(name);
      if (now != was) return $"{was}>{now} {name}";
    }

    return null;
  }

  private static Dictionary<string, IdentityKind> AnswersFor(EntityTimeline timeline, IReadOnlyList<string> names)
  {
    var next = Empty();
    if (timeline is null || names is null) return next;

    foreach (var name in names)
    {
      if (!string.IsNullOrEmpty(name)) next[name] = timeline.Identity(name);
    }

    return next;
  }

  private static Dictionary<string, IdentityKind> Empty() => new(System.StringComparer.OrdinalIgnoreCase);
}
