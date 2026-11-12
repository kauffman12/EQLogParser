using System;
using System.Collections.Generic;

namespace EQLogParser;

// A credit belongs to one selected board walk, never to the captured line.
internal readonly record struct DamageSwingCredit(bool AttemptCountedEarlier, short ModifiersCountedEarlier,
  bool AccuracyExclusionCountedEarlier = false);

// Pair only selected, accepted records, in log order, separately for each fight and direction.
// docs/DesignNotes.md → "One swing, two lines: the outcome sentence and the number line".
internal sealed class DamageSwingAccounting
{
  private const double WindowS = 1;
  private readonly List<PendingSwing> _pending = [];

  private readonly record struct PendingSwing(string Attacker, string Defender, double Time, bool Riposte, short Spent,
    bool ExcludesFromAccuracy);

  internal DamageSwingCredit Observe(DamageRecord record, double time)
  {
    var terminal = record.Type is Labels.Melee or Labels.Absorb;
    if ((!terminal && !StatsUtil.IsDefensiveOutcome(record.Type)) || !double.IsFinite(time)) return default;

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
      // A plain outcome or a different compound tag starts a new swing, not an answer to the old one.
      var riposte = record.ModifiersMask > 0 && (record.ModifiersMask & LineModifiersParser.Riposte) != 0;
      if (terminal && struck && riposte == held.Riposte)
        return new DamageSwingCredit(true, held.Spent, held.ExcludesFromAccuracy);
    }

    if (!terminal && struck && !string.IsNullOrEmpty(record.Attacker) && !string.IsNullOrEmpty(record.Defender))
    {
      // The pair shares whatever the sentence spent in StatsUtil: its whole mask, or nothing for Miss.
      var spent = record.Type == Labels.Miss ? (short)0 : record.ModifiersMask;
      var excludesFromAccuracy = record.Type is Labels.Block or Labels.Dodge or Labels.Parry or Labels.Invulnerable;
      _pending.Add(new PendingSwing(record.Attacker, record.Defender, time, record.Type == Labels.Riposte, spent,
        excludesFromAccuracy));
    }

    return default;
  }
}
