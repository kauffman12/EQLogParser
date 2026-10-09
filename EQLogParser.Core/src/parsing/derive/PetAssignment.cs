using log4net;
using System.Reflection;

/*
 * Annotations only, no null-flow analysis (the project builds with Nullable=disable): both arguments are optional here
 * because a menu can be clicked over nothing, and refusing that is the API's job rather than a warning's.
 */
#nullable enable annotations

namespace EQLogParser;

/*
 * The ONE write behind "this summon belongs to that raider", whoever asked.
 *
 * Three DOORS make that claim, all of them a hand: the damage summary's and tanking summary's `Assign <name> as Pet of ▸` items
 * (picked from the board the operator is reading) and the Owner cell in Player/NPC Identity (picked where the name is read — this seam
 * replaced the Pet Owners window's picker when that pane was retired on 2026-10-09, so there is still exactly one way to make the claim).
 * Chat deliberately stays off this path:
 * `ChatDB` reads `"Stormclaw says, 'My leader is Beorun.'"` straight into `PlayerRegistry`, and that is right — it is evidence
 * arriving by the hundred during a pull, not an act somebody performed, and no announce is owed for it.
 *
 * All three used to be a bare
 * `PlayerRegistry.AddPetToPlayer`, which wrote the pair down — and stopped there: the claim reached no board until some unrelated event
 * happened to rebuild one (and it refused, in silence, any name the parser had already verified as a player). They now go through
 * `ClassificationCommands.ApplyVerdict`, which forgets every earlier claim about that name, asserts Kind = Pet, and writes the
 * pair — the eviction being what makes the write land at all (see `Assign`).
 *
 * A re-derive is what makes it land, not a grid patch. An ownership claim decides WHICH ROW a fact belongs to:
 * `RegistrySeed.ApplyPetMappings` writes the pair as an OWNERSHIP interval (Strong, over all time), and `FightSummarySource`
 * folds a fact's `AttackerOwner` from `EntityTimeline.OwnerOf` — so the next full pass moves that summon's damage under its
 * person (`+Pets`), and the identity digest moves with it, which is what tells every surface to repaint. Re-implementing the
 * fold inside a click handler would mean two answers to "whose is this", drifting the first time ownership rules change — same
 * reason `IdentityVerdictMenu.Write` patches nothing and lets the pass answer.
 *
 * A pair already in effect is refused before anything is spent: no file write, no derive pass. Picking the owner that is
 * already on screen is a click on nothing. Two stores are asked, because two stores hold the claim: `IdentityLookup.OwnerOf`
 * answers the chain the fold will read (the open session's charm owners, then petmapping.txt), and the store's OWN keys are
 * ordinal — operator data, kept exactly as written — so its rows are also scanned case-insensitively. A second row for the same
 * summon under a second spelling would give one pet two owners depending on which line spelled it which way — the twin failure
 * mode the fact tables' own name pool has already had to be cured of, so a claim mints at most one row per summon.
 */
internal static class PetAssignment
{
  private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

  /*
   * The app's door back into its own derive loop, wired once at startup (Core cannot know about `DeriveEngine`). Left null,
   * an assignment is still a fact for next time: the pair is saved and the next session's seed picks it up — which is exactly
   * what happens if a pair is written while no session is running.
   */
  internal static Action? Reroute { get; set; }

  /// <summary>Write the pair and ask for the pass that routes it. False when nothing was written (blank or already in effect).</summary>
  internal static bool Assign(string? pet, string? owner)
  {
    if (string.IsNullOrWhiteSpace(pet) || string.IsNullOrWhiteSpace(owner)) return false;

    if (AlreadyClaimed(pet!, owner!)) return false;

    /*
     * The same door a Set-as-NPC uses (ClassificationCommands.ApplyVerdict), and that is the whole fix: FORGET what this
     * application believed about `pet`, then assert Kind = Pet and write the pair. Going straight to AddPetToPlayer used to be a
     * silent no-op for exactly the names an operator assigns — AddPetToPlayerNoLock refuses to reassign a VERIFIED PLAYER, and a
     * summon the parser verified as a player (it cast spells, it took heals) is precisely what "this is somebody's pet" is said
     * ABOUT. It also left the kind alone, so the name kept its own row on every board.
     */
    ClassificationCommands.ApplyVerdict(pet, IdentityKind.Pet, owner);

    // Which claim was made, in the file that carries the raid: a pair appearing in petmapping.txt with no line beside it is
    // the kind of thing that needs a name-shaped explanation later. Debug would be the wrong level — this is an operator act,
    // not traffic, and it happens a handful of times a night.
    Log.Info($"pet assigned: {pet} -> {owner}");

    // The pass we are about to ask for announces itself as ContentMoved/RowEdited, which a large selection declines (UnaskedRefresh);
    // this write was a click, so it lends its gesture to that announce. Set BEFORE the request: the pass can land on any thread.
    UnaskedRefresh.OweGesture($"pet claim {pet} -> {owner}");

    Reroute?.Invoke();
    return true;
  }

  /// <summary>Did somebody already make this exact claim? Read both stores, ignore case in both.</summary>
  private static bool AlreadyClaimed(string pet, string owner)
  {
    if (string.Equals(IdentityLookup.OwnerOf(pet), owner, StringComparison.OrdinalIgnoreCase)) return true;

    // Operator-sized list (tens of pairs), read on a click: no index needed, and an index here would be a second copy of the
    // store's case rules — the thing this loop exists to not have.
    foreach (var mapping in PlayerRegistry.Instance.GetPetMappings())
    {
      if (string.Equals(mapping.Pet, pet, StringComparison.OrdinalIgnoreCase)
          && string.Equals(mapping.Owner, owner, StringComparison.OrdinalIgnoreCase)) return true;
    }

    return false;
  }
}
