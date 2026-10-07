#nullable enable annotations

using FontAwesome5;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser;

/*
 * The "Set <name> as ▸" cascade — one builder for every pane that lets an operator name what something is.
 *
 * Four surfaces want this menu (damage, healing and tanking summaries, and the fight list) and they must show the SAME words in the
 * SAME order doing the SAME write; four XAML copies is how the retired per-pane items drifted into "Set as Verified Player" here,
 * "Set as Player" there, NPC offered twice, and an unset nobody could find. The words come from IdentityVocabulary.TypeOptions, so a
 * fifth kind would arrive once — in the vocabulary — and appear everywhere.
 *
 * Three rules this class exists to hold:
 *
 *   1. **Words from the vocabulary, never retyped.** Player / Pet / Mercenary / NPC, in TypeOptions' order, minus the two entries that
 *      belong to a row-aware dropdown rather than to a blind cascade ("Spell" is a kind the rules reach and "Clear claim" needs the row's
 *      own evidence to mean anything). Mercenary IS offered here because these panes ask it as a judgement about a name they are looking
 *      at, and the operator asked for all four; the Names pane still trims it per row (IdentityVocabulary.TypeOptionsFor), which is a
 *      difference the pane can afford to state rather than invent a second vocabulary over.
 *
 *   2. **The header names the subject.** "Set Frostmaw as" reads as a sentence with the click; "Set as" (nothing selected) reads as what
 *      it is — an action with no object yet, still clickable because a greyed menu is a menu nobody discovers. Several rows selected says
 *      how many, because the plural cannot fit in one name's place.
 *
 *   3. **One write path for every pane.** Write() does what NamesTable's Type cell does — the operator's verdict into
 *      identity-overrides.txt AND this server's ledger row dropped — so a verdict written in the damage summary cannot behave differently
 *      from one written in the identity pane. The prior goes because the old fight-list item's failure is the evidence: it wrote the
 *      override only, and the name came back on the next pass wearing "… in previous log".
 */
internal static class IdentityVerdictMenu
{
  /// <summary>The header while nothing is selected — the action with no object yet.</summary>
  internal const string BaseHeader = "Set as";

  // The four kinds a cascade offers, taken from the vocabulary rather than restated. Built once: the entries are constants.
  private static readonly IdentityVocabulary.TypeOption[] Offered =
  [
    IdentityVocabulary.PlayerOption,
    IdentityVocabulary.PetOption,
    IdentityVocabulary.MercOption,
    IdentityVocabulary.NpcOption,
  ];

  /*
   * Fill a menu the markup declared. Each pane's XAML owns the position of the entry (it sits under the Assign items in the summaries,
   * above Refresh and Clear All in the fight list); this fills its four children, once, at construction — a per-right-click rebuild would
   * re-allocate items and lose whatever the grid did to them. Idempotent, because a UserControl's constructor is not the only thing that can
   * call it (a test or a re-created pane would otherwise add eight children).
   */
  internal static void Populate(MenuItem root, Action<IdentityKind> onPick)
  {
    if (root.Items.Count > 0) return;

    root.Icon ??= MakeIcon(EFontAwesomeIcon.Regular_Edit);
    root.ToolTip ??= "Say what this name is. Written for this server and re-derived, so the boards re-read the facts under the new verdict.";

    foreach (var option in Offered)
    {
      var kind = option.Kind;
      var item = new MenuItem { Header = option.Word, Icon = MakeIcon(IconFor(kind)) };
      item.Click += (_, _) => onPick(kind);
      root.Items.Add(item);
    }
  }

  /// <summary>The glyph each kind wears, so the same four words look the same in all four panes. Every name here is one this
  /// application already draws elsewhere (the fight list and identity pane use the same four), which keeps it compile-checked.</summary>
  private static EFontAwesomeIcon IconFor(IdentityKind kind) => kind switch
  {
    IdentityKind.Player => EFontAwesomeIcon.Solid_User,
    IdentityKind.Pet => EFontAwesomeIcon.Solid_Paw,
    IdentityKind.Merc => EFontAwesomeIcon.Solid_ShieldAlt,
    _ => EFontAwesomeIcon.Solid_Skull,
  };

  /*
   * The theme's icon style when there is one. The test host has no Application (see FightTableStartupTest's stub resources), and a menu
   * built for a test must not depend on a resource lookup - the icon is decoration, the MenuItem and its Kind are the contract.
   */
  private static ImageAwesome MakeIcon(EFontAwesomeIcon icon) => new()
  {
    Icon = icon,
    Width = 16,
    Height = 16,
    HorizontalAlignment = HorizontalAlignment.Center,
    VerticalAlignment = VerticalAlignment.Center,
    Style = System.Windows.Application.Current?.Resources["EQIconStyle"] as Style,
  };

  /*
   * What the menu says and whether it answers right now. `selectedName` is null when nothing usable is selected; `count` is how many
   * names the write would cover, so a batch is honest about itself ("Set 12 names as"). Enabled unconditionally by design (a greyed verb
   * is a feature nobody finds) — with nothing selected the pick does nothing, which is what Present's caller relies on.
   */
  internal static void Present(MenuItem root, string? selectedName, int count)
  {
    root.Header = count switch
    {
      <= 0 => BaseHeader,
      1 => $"Set {selectedName} as",
      _ => $"Set {count} names as",
    };

    root.IsEnabled = true;
    foreach (var child in root.Items.OfType<MenuItem>()) child.IsEnabled = true;
  }

  /*
   * The one write. Batch because the fight list selects with ctrl-click ("all of these are pets"), and it writes the file ONCE for the
   * batch (IdentityOverrideStore.Apply) rather than once per name. Then the ledger row goes with it, exactly as NamesTable's Type cell does:
   * the operator has now said what the name is, so this server's remembered opinion is stale, and a left-behind row shows up in the hover as
   * "… in previous log" beside a verdict that replaced it.
   *
   * No grid is patched here on purpose: an override is a new READING of the facts, and a reading changes which side a name's damage sits
   * on, whether it keys a row at all and whose pet column it folds under — the re-derive answers all of that, the panes repaint from its result.
   */
  internal static void Write(IReadOnlyList<string> names, IdentityKind kind)
  {
    if (names.Count == 0) return;

    IdentityOverrideStore.Instance.Apply(names, kind);
    foreach (var name in names) IdentityPriorStore.Instance.Remove(name);

    DeriveEngine.Active?.RederiveAsync();
  }
}
