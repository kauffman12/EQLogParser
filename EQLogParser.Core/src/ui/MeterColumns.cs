using System;
using System.Collections.Generic;

namespace EQLogParser;

/*
 * Which of the damage meter's numeric columns are drawn, in one table: what each is called on the meter, the sentence that explains
 * it, the settings key that remembers it, what it does when no key exists yet, and WHICH CELL it is drawn in. One table because three
 * doors read the same list — the setup dropdown builds itself from it, Save writes it, the overlay lays out from it — so a column
 * cannot be saved under one name, read back under another, or ordered differently by the picker and the meter. Elapsed seconds is
 * deliberately NOT in here: duration is not a decoration, every other number on the row is read against it, and nothing may add a
 * switch or a key for it — only its header word, below, because the header still has to print something.
 *
 * Defaults matter more than they look: a missing key is what every existing install has. `%` stays off (its shipped behavior),
 * Damage and DPS stay on (they were never hideable), and RDPS arrives opt-in rather than in front of everybody.
 *
 * **A column key is named `OverlayShow<What>Column`.** The suffix is not decoration: `OverlayShowing…` already belongs to a different switch —
 * `OverlayShowingDps` remembers WHICH PANEL IS OPEN (damage or tanking) — and a column key spelled like it invites somebody to merge two settings that have
 * nothing to do with each other. Every column follows it, `%` included: `%` used to save as `OverlayShowDamagePercent`, and that name is abandoned rather than
 * read — a meter whose columns are now configurable may reset one rarely-used switch to its default, which is an operator decision, not an oversight.
 * `ConfigUtil.Save()` retires the old line. Any column added later takes the suffix.
 *
 * docs/DesignNotes.md → "Damage meter column switches: four hideable numbers, zero width when off".
 */
internal static class MeterColumns
{
  internal readonly record struct Column(string Header, string Key, bool DefaultOn, MeterColumn Bit, MeterSlot Slot, string Tip);

  // Drawn left to right, and each row names its own rendered cell so the layout cannot order the columns one way and the picker another. The percent header is the
  // bare glyph because the meter has never had a word for it, and the tips ride here rather than in the picker so the word on the meter and the sentence
  // explaining it cannot disagree.
  internal static readonly IReadOnlyList<Column> All = new[]
  {
    new Column("%", "OverlayShowPercentColumn", false, MeterColumn.Percent, MeterSlot.Percent, "This row's share of all the damage counted"),
    new Column("Damage", "OverlayShowDamageColumn", true, MeterColumn.Damage, MeterSlot.Damage, "Damage dealt in the encounter"),
    new Column("DPS", "OverlayShowDpsColumn", true, MeterColumn.Dps, MeterSlot.Dps, "Damage per second over the encounter"),
    new Column("RDPS", "OverlayShowRdpsColumn", false, MeterColumn.Rdps, MeterSlot.Rdps, "Damage per second over the latest six logged seconds"),
  };

  // The columns a meter draws before anybody has switched anything.
  internal const MeterColumn Default = MeterColumn.Damage | MeterColumn.Dps;

  // The word above elapsed seconds. Duration earns no table row (no bit, no key, no switch), so its word is the one constant here —
  // and it stays in this file so every header word on the meter has exactly one home.
  internal const string SecondsHeader = "Sec";

  /*
   * The word a rendered cell wears: the four switchable words come from the table, `Sec` is the slot no switch owns. A cell with no
   * row in the table ECHOES ITSELF rather than throwing — this runs on the meter's layout path, inside the window constructor, where
   * an unmapped new cell must cost one odd header word and not the whole board (the same law `HitLabels.Unmapped` follows).
   */
  internal static string HeaderOf(MeterSlot slot)
  {
    if (slot == MeterSlot.Seconds) return SecondsHeader;

    foreach (var column in All)
    {
      if (column.Slot == slot) return column.Header;
    }

    return slot.ToString();
  }

  /*
   * The one fold from "is this column on" to the mask the meter lays out with and the ini keys speak. Both doors use it: `Load` asks the
   * settings file, the setup window asks its checkboxes — so a previewed tick and a saved tick cannot come to mean different things, and a
   * fifth column needs no change in either caller because this walks the table.
   */
  internal static MeterColumn FromSwitches(Func<Column, bool> on)
  {
    var shown = MeterColumn.None;
    foreach (var column in All)
    {
      if (on(column))
      {
        shown |= column.Bit;
      }
    }

    return shown;
  }

  /// <summary>Read every column key with its own fallback: a missing or unreadable key answers that column's default.</summary>
  internal static MeterColumn Load() => FromSwitches(c => ConfigUtil.IfSetOrElse(c.Key, c.DefaultOn));

  /// <summary>Write every key, the off ones too, so a later read never has to guess what an absent key meant.</summary>
  internal static void Save(MeterColumn shown)
  {
    foreach (var column in All)
    {
      ConfigUtil.SetSetting(column.Key, Includes(shown, column.Bit));
    }
  }

  internal static bool Includes(MeterColumn shown, MeterColumn bit) => (shown & bit) != 0;
}

/// <summary>A mask of which numeric columns the damage meter draws. Elapsed seconds is not one of them.</summary>
[Flags]
internal enum MeterColumn
{
  None = 0,
  Percent = 1,
  Damage = 2,
  Dps = 4,
  Rdps = 8,
}
