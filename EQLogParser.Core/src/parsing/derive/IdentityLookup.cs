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

    /// <summary>
    /// The second hop the engine wires beside LiveVerdict: "whose pet is this, according to this capture?" — a charm
    /// window's owner, which no file holds. Null with no session, so OwnerOf falls through to memory (docs/DesignNotes.md
    /// -> "A charm takes a mob off the enemy list"). Same dependency direction as LiveVerdict: Core owns the question.
    /// </summary>
    internal static Func<string, string?>? LiveOwner { get; set; }

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

    /*
     * The SECOND question, named instead of spelled out at every call site: "player OR mercenary". Half a dozen panes ask
     * it — the spell-damage viewer's "players only" filter, the "Set as Pet of" enable rules — and each one today writes
     * `IsVerifiedPlayer(x) || PlayerRegistry.IsMerc(x)` by hand. Folding Merc into IsOneOfUs was refused (class comment: a
     * merc is on nobody's roster, and widening that answer moves menus nobody chose to move), so the widening lives HERE,
     * where the difference is one line rather than six call sites free to drift apart.
     *
     * A merc is claimed by what was DECIDED or SEEN, never by memory alone: `/target` fills PlayerRegistry's mercenary set
     * for the session (it is not persisted — docs/DesignNotes.md → "What this application remembers"), and an operator
     * override saying Merc counts too, because step 1 of the seam ends questions in both directions.
     */
    internal static bool IsOneOfUsOrMerc(string? name) => IsOneOfUsOrMerc(name, double.PositiveInfinity);

    internal static bool IsOneOfUsOrMerc(string? name, double t)
    {
      if (string.IsNullOrEmpty(name)) return false;
      if (IsOneOfUs(name, t)) return true;

      // The operator before the session memory: an override is an answer, not a hint, and a stale /target reading must not
      // outvote what a person decided about this name.
      if (IdentityOverrideStore.Instance.TryGet(name, out var chosen)) return chosen == IdentityKind.Merc;

      var live = LiveVerdict;
      if (live is not null && live(name, t) == IdentityKind.Merc) return true;

      return PlayerRegistry.Instance.IsMerc(name);
    }
  /*
   * THE KIND QUESTIONS — what a name IS, answered by the verdict chain rather than by any store's bookkeeping:
   * the operator's override, then this capture (the live session's timeline, which is the whole rule book including the
   * charm and pet-ownership windows), then what a previous capture left in identity-priors.txt. The Mercenary question has
   its own seam already (`IsMercenary` below) because the operator's roster of mercenaries is a store, not a verdict.
   *
   * What deliberately does NOT answer a kind question is PlayerRegistry._verifiedPlayers: that list is MEMBERSHIP
   * ("this raid had this person"), and membership has its own seam above. Reading it as a kind is how a name listed for a
   * decade outvotes every rule, which is the failure the whole roster lane exists to stop (docs/DesignNotes.md ->
   * "The one seam that answers 'is this one of ours?'"). `IsPlayerSide` keeps Mercenary as its own kind: a mercenary is
   * somebody's summon, not a raider, and folding one into the other moves damage onto a column nothing else fills.
   */
    internal static IdentityKind KindAt(string? name)
    {
      if (string.IsNullOrEmpty(name)) return IdentityKind.Unknown;

      // The operator ends the question, in both directions (docs/DesignNotes.md -> "A veto nobody could switch on").
      if (IdentityOverrideStore.Instance.TryGet(name!, out var chosen)) return chosen;

      // Tonight's evidence. Unknown means the rules never saw the name, so it falls through rather than answering.
      var live = LiveVerdict;
      if (live is not null)
      {
        var kind = live(name!, double.PositiveInfinity);
        if (kind != IdentityKind.Unknown) return kind;
      }

      // Memory: a verdict a previous capture's rules earned. Membership is NOT here — that is IsOneOfUs's question.
      if (IdentityPriorStore.Instance.TryGet(name!, out var prior) && prior.Kind != IdentityKind.Unknown) return prior.Kind;

      return IdentityKind.Unknown;
    }

    internal static bool IsPlayer(string? name) => KindAt(name) == IdentityKind.Player;
    internal static bool IsPet(string? name) => KindAt(name) == IdentityKind.Pet;
    /// <summary>Player or Merc: the two kinds that stand on our side. Not membership — see IsOneOfUs.</summary>
    internal static bool IsPlayerSide(string? name) => KindAt(name) is IdentityKind.Player or IdentityKind.Merc;

    /*
     * "Whose pet is this?" for a surface that is not inside the parse (a click summary, a report, a fight classifier).
     *
     * The live session answers FIRST when it has an owner, because a charm window is what the capture watched happen
     * tonight; the registry answers after, and the registry is already seeded from identity-priors.txt's ownership lane at
     * startup (PlayerRegistry.Init), which is what lets petmapping.txt stop being read without these callers losing a pair.
     *
     * WHAT STILL ASKS THE REGISTRY DIRECTLY, and why that is not an oversight: the ingest path (DamageLineParser's
   * "is this my pet", ChatDB/ChatFilter's sender filters, CastLineParser's target check, DamageBreakdown/HitLogViewer's
   * name-shape filters) reads the store the parser itself is filling at that moment, and several of them pair the lookup
   * with IsPossiblePlayerName — tonight's line grammar, which no verdict chain reproduces. Asking a timeline while the
   * parser is mid-line would also take that table's lock from inside the parse. Those are the checks documented as KEPT in
   * docs/DesignNotes.md -> "The checks that decide who is a player", and each one is a deliberate stay, not a leftover.
   *
   * The write side has NOT moved yet: the Pet Owners window still edits the registry (and writes petmapping.txt), so the
     * registry stays the freshest copy of an operator's hand during a session and has to be the fallback rather than the
     * last word. When that window writes the lane instead, this lookup narrows to timeline → lane; until then it is the one
     * call site, and nine copies of `GetPlayerFromPet` are gone.
     */
    internal static string? OwnerOf(string? pet)
    {
      if (string.IsNullOrEmpty(pet)) return null;

      var owner = LiveOwner?.Invoke(pet!);
      if (!string.IsNullOrEmpty(owner)) return owner;

      var mapped = PlayerRegistry.Instance.GetPlayerFromPet(pet!);
      return string.IsNullOrEmpty(mapped) ? null : mapped;
    }


  /*
   * Two WIDENED reads for the parse path, where a name has to be ownable right now. They are supersets on purpose: the
   * verdict chain first, then the store this parser is filling, so moving authority onto the seam cannot lose a fold that
   * works today. The guard that is NOT a superset is `IsPersonWord`: IsOneOfUs answers true for the vocabulary rows ("you",
   * "himself", "Unknown Pet Owner") because membership-wise they are ours, and a possessive owner slot must never accept
   * one — that would mint a mapping to a pronoun. (docs/DesignNotes.md → "The checks that decide who is a player")
   */
  internal static bool IsNameOfOurPerson(string? name) => !PlayerRegistry.IsPersonWord(name) && IsOneOfUs(name);

  /// <summary>Pet by tonight's verdict or by the store (charm windows fold; a mapping with no verdict still counts).</summary>
  internal static bool IsKnownPet(string? name) => IsPet(name) || PlayerRegistry.Instance.IsVerifiedPet(name);

  }
}
