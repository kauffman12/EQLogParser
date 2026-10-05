using System;

/*
 * Annotations only, no null-flow analysis: the project builds with Nullable=disable, and this seam's contract is about
 * optional answers (a name with no session, no verdict and no roster row), which is exactly what nullable annotations
 * are for. Same convention as IdentityPriorStore.
 */
#nullable enable annotations

namespace EQLogParser
{
  /*
   * ONE QUESTION, ONE ADDRESS: "is this name one of ours?". Half a hundred places used to ask it of players.txt, and the
   * file answered from 25 years of accumulated names whether or not tonight's raid contained them (docs/DesignNotes.md →
   * "Breadth of evidence, measured"). The answer now comes out of what this capture saw, with the app's own memory as the
   * fallback it always should have been:
   *
   *   1. the operator's verdict       identity-overrides.txt - a person decided, nothing else is asked;
   *   2. what the capture watched     the open derive session's timeline (override already replayed into it by R10, so
   *                                   an override can also arrive here - step 1 exists for when no session is open);
   *   3. this application's memory    the roster lane of identity-priors.txt (players.txt's heir) and the in-memory
   *                                   roster that mirrors it, which is what answered alone until now.
   *
   * It owns NO dictionary: every hop reads a store that already exists, and this class adds no state to keep in step -
   * which is the whole reason it is one function rather than a fourth place names are remembered.
   *
   * WHAT "OURS" MEANS HERE: a name the evidence calls PLAYER. Mercenary is deliberately not folded in even though a merc
   * fights beside us - every caller that meant "player OR merc" says so today (`… || PlayerRegistry.IsMerc(name)`), and
   * quietly widening this answer would move those callers' menus and filters without anybody choosing it. A Pet or Npc
   * verdict is a real answer, not a missing one: NO. For the other kinds, read the timeline verdict directly
   * (IdentityKind has all four) rather than broadening this question.
   *
   * WHY THE CAPTURE OUTRANKS MEMORY AND NOT THE REVERSE: an operator's saved list is older than whatever is on screen,
   * and its whole failure mode is that it cannot be wrong about a name it collected years ago. A pass over the rules can
   * be wrong too - but it is wrong about the fight in front of the window, which is what a meter, a menu and a pet
   * folding decision are actually about. Memory therefore answers only where this log was silent.
   */
  internal static class IdentityLookup
  {
    /// <summary>
    /// The open session's verdicts, wired by DeriveEngine while a capture is open and cleared when it closes (Core cannot
    /// see the engine; this is the same seam shape as CombatRecordLookup). Null = no session, so nothing was watched and
    /// the app's memory answers alone.
    /// </summary>
    internal static Func<string, double, IdentityKind>? LiveVerdict { get; set; }

    /// <summary>"Is this name one of ours?" — as of the end of what we know about it.</summary>
    internal static bool IsOneOfUs(string? name) => IsOneOfUs(name, double.PositiveInfinity);

    /*
     * The timed form, for a caller asking about a MOMENT rather than about the file: a raider charmed at 21:04 is not one
     * of ours at 21:05, and a pet's owner learned halfway through a night was not ours at the pull before. Times are the
     /// same dotnet-epoch seconds every fact in the engine carries (DamageFact.TimeS), and PositiveInfinity reads the
     * latest verdict — which is what a grid row or a context menu means by "is this a player".
     */
    internal static bool IsOneOfUs(string? name, double t)
    {
      if (string.IsNullOrEmpty(name)) return false;

      /*
       * The frozen words first, before any store: "You have been slain", "Betebeatz's pet" where the owner field is a
       * pronoun, the "Unassigned" row a stats table builds out of nothing. They are vocabulary rather than knowledge -
       * PlayerRegistry keeps the lists and docs why (a name that takes "your" is the local player) — and no capture can
       * place them, so asking the rules first would answer Unknown about a row this app always knew was one of ours.
       */
      if (PlayerRegistry.IsPersonWord(name)) return true;

      // 1. The operator. This is the one answer that ends the question rather than adding to it.
      if (IdentityOverrideStore.Instance.TryGet(name, out var chosen)) return chosen == IdentityKind.Player;

      // 2. Tonight's evidence, when there is any. An Unknown here means the rules never saw the name, not that they
      //    decided against it - so it falls through instead of answering no.
      var live = LiveVerdict;
      if (live is not null)
      {
        var kind = live(name, t);
        if (kind != IdentityKind.Unknown) return kind == IdentityKind.Player;
      }

      // 3. Memory: what this application wrote down about the name. Both halves are the same lane - the ledger is the
      //    durable roster and PlayerRegistry is its in-memory mirror, fed from it at log open (PlayerRegistry.Init).
      return IdentityPriorStore.Instance.TryGetRoster(name) || PlayerRegistry.Instance.IsVerifiedPlayer(name);
    }
  }
}
