#nullable enable annotations
namespace EQLogParser
{
  internal static class Labels
  {
    public const string Absorb = "Absorb";
    public const string Dd = "Direct Damage";
    public const string Dot = "DoT Tick";
    public const string Ds = "Damage Shield";
    public const string Rs = "Reverse DS";

    /*
     * The attacker a line does not name. `<X> was chilled to the bone for N points of non-melee damage.` has no source in it,
     * and this branch used to answer with Labels.Rs - "Reverse DS", a DAMAGE-TYPE word that then sat on the board among entity
     * names and read as one (5.74 billion damage under "Reverse DS" on one capture, while no rule claimed it). This word says
     * what the line says. ClassificationRules.IsUnattributedName is the recognizer; Labels.Rs stays in it for the old spelling.
     */
    public const string Unattributed = "Unattributed Damage";
    public const string Bane = "Bane Damage";
    public const string OtherDmg = "Other Damage";
    public const string Proc = "Proc";
    public const string Hot = "HoT Tick";
    public const string Heal = "Direct Heal";
    public const string Melee = "Melee";
    public const string SelfHeal = "Melee Heal";
    public const string NoData = "No Data Available";
    public const string NoNpcs = "No NPCs Selected";
    public const string PetPlayerOption = "Players +Pets";
    public const string PlayerOption = "Players";
    public const string PetOption = "Pets";
    public const string RaidOption = "Raid";
    public const string RaidTotals = "Totals";
    public const string Riposte = "Riposte";
    public const string AllOption = "Uncategorized";
    public const string ByGroupOption = "Group View";
    /*
     * The owner slot of a summon nobody was ever named for. It is a SENTINEL rather than a name — the boards ask "is this a real
     * owner?" before folding damage onto a person, the roster refuses it as a curated row, and the Pet Owners dropdown lists it as an
     * entry — so what it SAYS is UI wording, and the wording changed (2026-10-09) on the operator's request: "it used something in
     * Labels for unknown owner. i would prefer if it were changed to just say No Owner."
     *
     * The old phrase is not retired data: identity-priors.txt and petmapping.txt rows already on disk carry it, so asking the question
     * with `==` would give a stored placeholder a second life as a person called "Unknown Pet Owner". Ask IsUnassignedOwner instead.
     */
    public const string Unassigned = "No Owner";

    /// <summary>The wording this sentinel carried before 2026-10-09. Files hold it; nothing writes it.</summary>
    internal const string LegacyUnassigned = "Unknown Pet Owner";

    /// <summary>Is this owner text the "nobody was ever named" placeholder — in either wording a file can hold?</summary>
    internal static bool IsUnassignedOwner(string? owner) =>
      !string.IsNullOrEmpty(owner)
      && (Unassigned.Equals(owner, StringComparison.OrdinalIgnoreCase)
          || LegacyUnassigned.Equals(owner, StringComparison.OrdinalIgnoreCase));
    public const string Unk = "Unknown";
    public const string UnkSpell = "Unknown Spell";
    public const string ReceivedHealParse = "Received Healing";
    public const string HealParse = "Healing";
    public const string TankParse = "Tanking";
    public const string TopHealParse = "Top Heals";
    public const string DamageParse = "Damage";
    public const string Miss = "Miss";
    public const string Dodge = "Dodge";
    public const string Parry = "Parry";
    public const string Block = "Block";
    public const string Invulnerable = "Invulnerable";

    /* FCT label for a spell event with no number: the log spells it past tense ("X resisted your Y") and the
     * overlay speaks the same one-word dialect as Miss/Dodge/Block. See FctManager.HandleResist. */
    public const string Resist = "Resist";
  }
}
