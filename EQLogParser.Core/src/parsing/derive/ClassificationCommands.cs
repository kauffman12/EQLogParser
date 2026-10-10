using System;
using System.Collections.Generic;

/*
 * Annotations only, no null-flow analysis: the project builds with Nullable=disable, and this API speaks in optional
 * strings/kinds because a name legitimately has no class, no owner and no verdict.
 */
#nullable enable annotations

namespace EQLogParser
{
  /*
   * The two things an operator can DO about a name in this engine, in one place, so the Names window and the fight grids'
   * right-click menus cannot drift into writing different files again (they do today: "Add player" writes
   * players.txt while "Set as Pet" writes identity-overrides.txt).
   *
   * Every command leaves the capture untouched — these are readings, not edits of history — so the caller re-runs
   * the derive afterwards (DeriveEngine.RunDeriveAsync) and the whole board updates without a re-parse. Nothing here
   * touches a dispatcher: keep it callable from a test and from a background command.
   */
  internal static class ClassificationCommands
  {
    /*
     * The ONE door for "this is what that name is", from every surface: the Names window's Type cell, the fight grid's and the
     * summary panes' Set-as cascade, and the Assign-as-pet items (which now come through here too instead of writing a pet-map row
     * alone). Two halves, in this order, and THE ORDER IS THE RULE:
     *
     *   1. FORGET — an operator decision replaces everything this application believed about the name (2026-10-09: "if im making
     *      a manual decision id like to remove all previous knowledge and accept what im saying"). The ledger row (its verdict, its
     *      "one of ours" roster bit, the class it carried) and every registry claim: verified player (players.txt), verified pet,
     *      mercenary, game-generated name, the took-an-action flag, and the pet-map row (petmapping.txt). A verdict that only ADDS a
     *      claim leaves those answers standing underneath, and several of them are read by seams that never ask the override — which
     *      is the field report this replaces: "changed a player to an NPC and they kept behaving like a player".
     *   2. ASSERT — the override (R10-manual) is Certain and applied after the rule book on every pass, so it outranks what the rules
     *      conclude AND what they learn afterwards; the operator's word is not a tie-breaker new evidence can outvote. Forgetting
     *      first is also what makes the pet case work at all: AddPetToPlayerNoLock refuses to reassign a VERIFIED PLAYER, and a name
     *      the parser verified as a player is exactly the population an operator assigns, so that menu write used to land nowhere.
     *
     * IdentityKind.Unknown means "take my claim back": forget, remove the override, let this capture's own lines answer again. A click
     * that changes nothing writes nothing — evicting in order to re-say the same thing would cost a roster row for free.
     */
    public static bool ApplyVerdict(string? name, IdentityKind kind, string? petOf = null)
    {
      if (string.IsNullOrWhiteSpace(name)) return false;
      var n = name!;

      if (IdentityOverrideStore.Instance.TryGet(n, out var existing) && existing == kind
          && (kind != IdentityKind.Pet || string.Equals(IdentityLookup.OwnerOf(n), petOf, StringComparison.OrdinalIgnoreCase)))
        return false;

      Forget(n);

      if (kind == IdentityKind.Unknown) IdentityOverrideStore.Instance.Remove(n);
      else IdentityOverrideStore.Instance.Set(n, kind);

      // The owner is the operator's claim too, and it lands on the pair — never invented: no petOf means "a pet of nobody I know".
      if (kind == IdentityKind.Pet && !string.IsNullOrWhiteSpace(petOf)) PlayerRegistry.Instance.AddPetToPlayer(n, petOf!);

      return true;
    }

    /// <summary>Drop every memory this application holds about a name: the ledger row and the registry's own claims. Writes no verdict.</summary>
    public static void Forget(string name)
    {
      if (string.IsNullOrEmpty(name)) return;

      IdentityPriorStore.Instance.Remove(name);
      PlayerRegistry.Instance.ForgetName(name);
    }

    /// <summary>"Forget what this server's older logs concluded about this name." Removes the ledger row only —
    /// verdicts, roster membership and pet mappings are separate files and stay as they are. With no entry left the
    /// name reads Unknown again on a capture whose own lines say nothing.</summary>
    public static void ClearPrior(IdentityPriorStore priors, string name) => priors.Remove(name);

    /*
     * The Name column's calculator — THE ONE door for "forget everything this application has ever written down about this name,
     * then let only this capture answer" (2026-11; the operator: "purely going to show what the current log thinks").
     *
     * It is `ApplyVerdict(name, Unknown)` — the whole forget-then-withdraw law, so no narrower copy can drift from it: the
     * override row goes, the ledger row goes (older-log verdict, roster "one of ours" bit, the class on that row, the owner
     * column), and every registry claim goes (verified player — the players.txt lineage — verified pet, mercenary, generated
     * name, took-an-action flag, the live pet-map pair). On top of it stands `ForgetDefaultClass`: the stored FALLBACK class goes —
     * the players.txt row, the class an operator typed at the pencil — so memory cannot shadow what this log says. This capture's
     * own cast-learned WINDOWS stay: class is live per-second evidence and it moves often inside one file (kind is the static half of
     * that split, which is why a kind verdict touches neither lane). "Purely going to show what the current log thinks" includes the
     * class this log learned; it does not include last night's.
     *
     * What remains after it is one thing only: what the open log says. A name nothing in it places drops to *Not Placed* (or off
     * the list); a name its lines DO place comes back with its rule beside it — `npcs.txt`, a ``X`s pet`` spelling, an
     * R24-petslot defender, R15's heal crowd, R7's graph, tonight's casts. Deleting stored beliefs does not delete the lines that
     * earned them: that asymmetry is the verb, and the pane shows it while it is out there (the "Recalculating…" label stays up
     * at least as long as the pass takes, so an identical answer still says it tried).
     *
     * What it is NOT: a suppression. There is no asserted Unknown and no "never classify this again" — an unplaced name stays
     * eligible for every rule on every pass, because that silence is what an operator asks the capture to fill, not a verdict to
     * write down. The two doors it replaces are deleted rather than parked: the Type dropdown's "Reset" entry and the Owner
     * dropdown's "No Owner" entry — both wrote a narrower forget that an operator had to combine by hand. `PlayerRegistry.
     * ForgetPetMapping` survives as the narrow verb with its test, but has no UI door: taking the pair back now means taking
     * everything back, and a second, weaker take-back beside it is how this feature shipped once before.
     */
    public static void Recalculate(string? name)
    {
      if (string.IsNullOrWhiteSpace(name)) return;

      ApplyVerdict(name!, IdentityKind.Unknown);
      PlayerRegistry.Instance.ForgetDefaultClass(name!);
    }

  }
}
