using System;
using System.Collections.Generic;

namespace EQLogParser;

/*
 * Owns column words, cells, keys and defaults for the picker, persistence and meter layout. Seconds has no switch.
 * Missing keys preserve the default Damage/DPS view; added rates are opt-in. Keys follow OverlayShow<What>Column,
 * separate from the OverlayShowingDps panel selector.
 * docs/DesignNotes.md → "Damage meter column switches: hiding one reserves no width" and "Three rates, three denominators".
 */
internal static class MeterColumns
{
  internal readonly record struct Column(string Header, string Key, bool DefaultOn, MeterColumn Bit, MeterSlot Slot, string Tip, string PickerNote)
  {
    // Explain RDPS's unusual window in the picker only; meter headers keep the bare word.
    internal string PickerLabel => string.IsNullOrEmpty(PickerNote) ? Header : $"{Header} ({PickerNote})";
  };

  // Drawn left to right; the picker and layout must use the same cell order.
  internal static readonly IReadOnlyList<Column> All = new[]
  {
    new Column("%", "OverlayShowPercentColumn", false, MeterColumn.Percent, MeterSlot.Percent, "This row's share of all the damage counted", ""),
    new Column("Damage", "OverlayShowDamageColumn", true, MeterColumn.Damage, MeterSlot.Damage, "Damage dealt in the encounter", ""),
    new Column("DPS", "OverlayShowDpsColumn", true, MeterColumn.Dps, MeterSlot.Dps, "Damage per second over the encounter", ""),
    new Column("RDPS", "OverlayShowRdpsColumn", false, MeterColumn.Rdps, MeterSlot.Rdps, "Damage per second over the latest six logged seconds", "6s rolling"),

    new Column("SDPS", "OverlayShowSdpsColumn", false, MeterColumn.Sdps, MeterSlot.Sdps, "Damage per second over the total elapsed time of the raid", ""),
  };

  internal const MeterColumn Default = MeterColumn.Damage | MeterColumn.Dps;

  // Seconds has no switchable row, but its header word still belongs to this vocabulary.
  internal const string SecondsHeader = "Sec";

  // Echo an unmapped cell rather than aborting the window's layout path.
  internal static string HeaderOf(MeterSlot slot)
  {
    if (slot == MeterSlot.Seconds) return SecondsHeader;

    foreach (var column in All)
    {
      if (column.Slot == slot) return column.Header;
    }

    return slot.ToString();
  }

  // Both the settings read and checkbox picker use this fold, so their masks cannot drift apart.
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

  /// <summary>Raid-time rate: total divided by the raid's elapsed seconds, never by this name's.</summary>
  Sdps = 16,
}
