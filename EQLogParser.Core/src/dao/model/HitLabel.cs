using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * The label a hit record carries, as one byte instead of a string reference.
   *
   * Parsers choose a Labels constant, never arbitrary line text. Readback returns the same interned string instance,
   * including for reference-equality comparisons. Reverse DS names an attacker, not a record kind.
   *
   * Two rules, both measured rather than assumed:
   *
   *   - The backing type MUST stay `byte`. A default enum is an int, and that is 4 bytes, which puts the record back
   *     over the size step it just came down (docs/DesignNotes.md → "What a loaded raid costs in memory").
   *   - `None` (0) reads back as null. SpellCast, ReceivedSpell, CastTimeRecord and TauntRecord never set a label,
   *     and their Type has always been null; nothing may start seeing the word "Unknown" instead.
   *
   * A label outside this table cannot happen, but Parse cannot prove it either — the damage parser's vocabulary lives
   * in branches rather than types. So an unrecognised word maps to None (the record reads as typeless, as an
   * unlabeled record does today) and is counted, because silently renaming a label to another label would put one
   * event's amount into the wrong column of every view. `HitLabelTest` holds the table against its expected shape;
   * a new word arrives with an entry there, the pair below, and a settings/panel change like everything else.
   */
  internal enum HitLabel : byte
  {
    None = 0,
    Melee,
    Dd,
    Dot,
    Proc,
    Bane,
    Ds,
    OtherDmg,
    Absorb,
    Block,
    Dodge,
    Miss,
    Parry,
    Riposte,
    Invulnerable,
    Heal,
    Hot,
    DamageAbsorb,
  }

  internal static class HitLabels
  {
    // The one place a label and its word are paired: labels come from Labels, so "Direct Damage" is spelled once.
    private static readonly (HitLabel Label, string Text)[] _vocabulary =
    [
      (HitLabel.Melee, Labels.Melee),
      (HitLabel.Dd, Labels.Dd),
      (HitLabel.Dot, Labels.Dot),
      (HitLabel.Proc, Labels.Proc),
      (HitLabel.Bane, Labels.Bane),
      (HitLabel.Ds, Labels.Ds),
      (HitLabel.OtherDmg, Labels.OtherDmg),
      (HitLabel.Absorb, Labels.Absorb),
      (HitLabel.Block, Labels.Block),
      (HitLabel.Dodge, Labels.Dodge),
      (HitLabel.Miss, Labels.Miss),
      (HitLabel.Parry, Labels.Parry),
      (HitLabel.Riposte, Labels.Riposte),
      (HitLabel.Invulnerable, Labels.Invulnerable),
      (HitLabel.Heal, Labels.Heal),
      (HitLabel.Hot, Labels.Hot),
      (HitLabel.DamageAbsorb, Labels.DamageAbsorb),
    ];

    // Indexed by the enum's byte value, so index 0 stays null for the records that carry no label.
    private static readonly string[] _text = new string[(byte)HitLabel.DamageAbsorb + 1];
    private static readonly Dictionary<string, HitLabel> _label = new(StringComparer.Ordinal);

    // How many non-null words could not be mapped. Diagnostics only: it should never leave zero.
    private static int _unmapped;
    internal static int Unmapped => _unmapped;

    static HitLabels()
    {
      foreach (var (label, text) in _vocabulary)
      {
        _text[(byte)label] = text;
        _label[text] = label;
      }
    }

    // The word for a label, or null for None and for anything outside the table.
    internal static string Text(HitLabel label)
    {
      var index = (int)label;
      return index < _text.Length ? _text[index] : null;
    }

    // The label for a word, or None for a word the table does not know. Null maps to None, which is what an
    // unlabeled record held when Type was a string field nobody set.
    internal static HitLabel Parse(string text)
    {
      if (text == null)
      {
        return HitLabel.None;
      }

      if (_label.TryGetValue(text, out var label))
      {
        return label;
      }

      // Ordinal only: a word that differs in case is a different word, and guessing would move an event between
      // columns of every view built on the label.
      Interlocked.Increment(ref _unmapped);
      return HitLabel.None;
    }
  }
}
