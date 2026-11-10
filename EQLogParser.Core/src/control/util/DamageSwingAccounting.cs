using System;
using System.Collections.Generic;

namespace EQLogParser;

// A credit belongs to one selected board walk, never to the captured line.
internal readonly record struct DamageSwingCredit(bool AttemptCountedEarlier, short ModifiersCountedEarlier);

// Pair only selected, accepted records, in log order, separately for each fight and direction.
// docs/DesignNotes.md → "One swing, two lines: the outcome sentence and the number line".
internal sealed class DamageSwingAccounting
{
  private const double WindowS = 1;
  private readonly List<PendingSwing> _pending = [];

  private readonly record struct PendingSwing(string Attacker, string Defender, double Time, bool Riposte, short Spent);

  internal DamageSwingCredit Observe(DamageRecord record, double time)
  {
    var melee = record.Type == Labels.Melee;
    if ((!melee && !StatsUtil.IsDefensiveOutcome(record.Type)) || !double.IsFinite(time)) return default;

    var matched = -1;
    for (var i = _pending.Count - 1; i >= 0; i--)
    {
      var age = time - _pending[i].Time;
      if (age < 0 || age > WindowS)
      {
        _pending.RemoveAt(i);
      }
    }

    for (var i = 0; i < _pending.Count; i++)
    {
      var held = _pending[i];
      if (string.Equals(held.Attacker, record.Attacker, StringComparison.OrdinalIgnoreCase)
        && string.Equals(held.Defender, record.Defender, StringComparison.OrdinalIgnoreCase))
      {
        matched = i;
        break;
      }
    }

    var struck = LineModifiersParser.IsStrikethrough(record.ModifiersMask);
    if (matched >= 0)
    {
      var held = _pending[matched];
      _pending.RemoveAt(matched);
      // A plain hit, another defense, or a different compound tag starts a new swing, not an answer to the old one.
      var riposte = record.ModifiersMask > 0 && (record.ModifiersMask & LineModifiersParser.Riposte) != 0;
      if (melee && struck && riposte == held.Riposte)
        return new DamageSwingCredit(true, held.Spent);
    }

    if (!melee && struck && !string.IsNullOrEmpty(record.Attacker) && !string.IsNullOrEmpty(record.Defender))
    {
      // Absorb and Miss do not spend modifiers in StatsUtil. Only the repeated Strikethrough is shared by the pair.
      var spent = record.Type is Labels.Absorb or Labels.Miss ? (short)0 : LineModifiersParser.Strikethrough;
      _pending.Add(new PendingSwing(record.Attacker, record.Defender, time, record.Type == Labels.Riposte, spent));
    }

    return default;
  }
}
