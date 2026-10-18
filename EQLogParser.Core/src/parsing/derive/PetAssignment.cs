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
 * (picked from the board the operator is reading) and the Owner cell in the Identity Table (picked where the name is read — this seam
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
 * `IdentityOverrideStore.Apply` replays the explicit pair as an all-time ownership interval at Certain strength;
 * remembered pairs from `RegistrySeed` stay weaker. `FightSummarySource` reads `EntityTimeline.OwnerOf`, so the next
 * full pass moves the summon's damage under its person (`+Pets`) and the changed digest asks every surface to repaint.
 * Later-starting charm windows keep their temporary ownership and side flips.
 *
 * Only an identical explicit manual pair is a no-op. Accepting an inferred or remembered owner makes it manual,
 * even when the text is unchanged; ClassificationCommands owns that comparison and its case-insensitive lookup.
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

    // The command owns both explicit-owner deduplication and forget-before-assert ordering.
    if (!ClassificationCommands.ApplyVerdict(pet, IdentityKind.Pet, owner)) return false;

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

}
