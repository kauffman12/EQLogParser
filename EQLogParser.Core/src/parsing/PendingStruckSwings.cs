namespace EQLogParser;

/*
 * A defensive sentence whose swing has not met its number line yet.
 *
 * `(Strikethrough)` on an outcome sentence is the game saying the defense failed and the blow landed, and the line
 * carrying the number follows it:
 *
 *   [Wed Jul 24 20:14:02 2026] A mob tries to slash Foob, but Foob ripostes! (Strikethrough)
 *   [Wed Jul 24 20:14:02 2026] A mob slashes Foob for 56110 damage! (Riposte Strikethrough)
 *
 * Two lines, one swing at one defender. Counting both as an attempt — and crediting the tag twice, which is what
 * `MeleeUndefended = MeleeHits - StrikethroughHits` then silently eats — is what makes a tank's `# Attempts` read
 * above the swings that happened. So the sentence registers here, the number line claims the registration, and the
 * counting layer pays the attempt and the scored tags once.
 *
 * Eight scanned slots rather than a dictionary: a struck swing is answered two or three records later — 14,809 of the
 * 15,527 pairs on eqlog_Kizant_xegony-09-20-25.txt sit two or three records behind their sentence, 15,454 of them in the
 * same log second — so the live set is a handful, and an oldest-wins scan of eight interned-name comparisons allocates
 * nothing where a composite (attacker, defender) key would owe one per asking line. A full table overwrites its oldest
 * entry: a registration older than a second is worthless anyway.
 *
 * Only a SAME-PAIR match counts, and a pair is directional: `A mob → Foob` (the mob's swing) and `Foob → A mob`
 * (Foob's riposte landing) are two events that the boards count on two different rows, which is exactly why a
 * successful riposte's return line may not claim its own sentence. Which words register, and the numbers behind
 * the window: docs/DesignNotes.md → "One swing, two lines: the outcome sentence and the number line".
 */
internal sealed class PendingStruckSwings
{
  private const int Slots = 8;

  // The number line arrives in the same second as its sentence in every paired case these captures hold.
  private const double WindowS = 1.0;

  private readonly string[] _attacker = new string[Slots];
  private readonly string[] _defender = new string[Slots];
  private readonly short[] _spentMask = new short[Slots];
  private readonly double[] _time = new double[Slots];

  /// <summary>
  /// An outcome sentence whose tag says the swing landed. `spentMask` is what that sentence's own counting already
  /// scored, which is nothing for the two words that read no modifiers at all (see StatsUtil.UpdateDamageStats).
  /// </summary>
  internal void Note(string attacker, string defender, short spentMask, double time)
  {
    if (double.IsNaN(time) || string.IsNullOrEmpty(attacker) || string.IsNullOrEmpty(defender)) return;

    var slot = -1;
    for (var i = 0; i < Slots; i++)
    {
      if (SameName(_attacker[i], attacker) && SameName(_defender[i], defender))
      {
        slot = i; // This pair struck through twice before answering: the newer sentence is the one owed a number line.
        break;
      }

      if (slot < 0 || _time[i] < _time[slot]) slot = i;
    }

    Write(slot, attacker, defender, spentMask, time);
  }

  /// <summary>
  /// Does this line carry the number of a swing already registered? `spentMask` answers what the sentence scored,
  /// so the caller pays only what is left. Claiming clears the registration: one number line per swing.
  /// </summary>
  internal bool TryClaim(string attacker, string defender, double time, out short spentMask)
  {
    spentMask = 0;
    if (double.IsNaN(time) || string.IsNullOrEmpty(attacker) || string.IsNullOrEmpty(defender)) return false;

    for (var i = 0; i < Slots; i++)
    {
      var held = _attacker[i];
      if (held is null) continue;

      if (!SameName(held, attacker) || !SameName(_defender[i], defender)) continue;

      var age = time - _time[i];
      if (age < 0 || age > WindowS)
      {
        _attacker[i] = null;
        _defender[i] = null;
        continue;
      }

      spentMask = _spentMask[i];
      _attacker[i] = null;
      _defender[i] = null;
      return true;
    }

    return false;
  }

  internal void Clear()
  {
    Array.Clear(_attacker, 0, Slots);
    Array.Clear(_defender, 0, Slots);
    Array.Clear(_spentMask, 0, Slots);
  }

  private void Write(int slot, string attacker, string defender, short spentMask, double time)
  {
    _attacker[slot] = attacker;
    _defender[slot] = defender;
    _spentMask[slot] = spentMask;
    _time[slot] = time;
  }

  // Records name entities, and entities are asked about without caring about letter case.
  private static bool SameName(string a, string b) =>
    ReferenceEquals(a, b) || (a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase));
}
