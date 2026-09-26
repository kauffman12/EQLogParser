namespace EQLogParser;

/*
 * Base of the two record types a raid produces by the million: what happened, as one hit or one heal.
 *
 * Every name on a record is stored as an id into StringCache instead of a reference to
 * a string. A record is read as text (grids, exports, the log viewers) and written once by a parser,
 * so the id costs four bytes where a pointer cost eight, and the text it stands for was already shared
 * by interning. Reading a name therefore resolves rather than dereferences; it is an array index into
 * strings that live forever, so it stays cheap enough to sit in the aggregation loops.
 *
 * Setting a name assigns whatever text is handed in, exactly as before: callers that want title casing
 * keep calling StringCache.GetOrAdd themselves, and callers that display the text verbatim
 * (spell names) keep their exact spelling. Ids are matched ordinally, so two spellings that differ by
 * case stay two different values here just as they did when they were references.
 *
 * What is left in the base is what both kinds of event can actually carry: an amount that landed, a modifier
 * mask, and the two labels. OverTotal used to sit here, and it cost four bytes in every damage record the
 * parser ever made because only heals have an over-amount — a heal line reads "for 9409 (11000)" and damage
 * has no counterpart. A hit record is the most numerous object in the process, millions to a night, so a
 * field one kind of line can write belongs to that kind; docs/DesignNotes.md → What a loaded raid costs in
 * memory.
 */
public class HitRecord : IAction
{
  public uint Total { get; set; }
  public short ModifiersMask { get; set; }

  public string Type
  {
    get => StringCache.GetName(_typeId);
    set => _typeId = StringCache.GetId(value);
  }

  public string SubType
  {
    get => StringCache.GetName(_subTypeId);
    set => _subTypeId = StringCache.GetId(value);
  }

  internal int TypeId => _typeId;
  internal int SubTypeId => _subTypeId;

  private int _typeId;
  private int _subTypeId;
}

/*
 * One heal. Equal by value, so the manager can keep a single instance for every repeat of it —
 * heals are the most repetitive thing a raid writes (three quarters of one night's heal lines
 * restate a heal already seen), and each repeat costs an object if nothing recognises it.
 */
internal class HealRecord : HitRecord
{
  /*
   * What the line asked for, as opposed to what landed (Total), so OverTotal - Total is the overheal the tabs
   * print. Damage has no such number: HealingLineParser is the only writer of this field in the codebase.
   */
  public uint OverTotal { get; set; }

  public string Healer
  {
    get => StringCache.GetName(_healerId);
    set => _healerId = StringCache.GetId(value);
  }

  public string Healed
  {
    get => StringCache.GetName(_healedId);
    set => _healedId = StringCache.GetId(value);
  }

  public override bool Equals(object obj)
  {
    return obj is HealRecord other && _healerId == other._healerId && _healedId == other._healedId && TypeId == other.TypeId
      && SubTypeId == other.SubTypeId && Total == other.Total && OverTotal == other.OverTotal && ModifiersMask == other.ModifiersMask;
  }

  public override int GetHashCode() => HashCode.Combine(_healerId, _healedId, TypeId, SubTypeId, Total, OverTotal, ModifiersMask);

  private int _healerId;
  private int _healedId;
}

/*
 * One damage event. Equal by value so the manager hands the same instance to every repeat of it;
 * see FightManager.GetCachedDamageRecord.
 */
internal class DamageRecord : HitRecord
{
  public string Attacker
  {
    get => StringCache.GetName(_attackerId);
    set => _attackerId = StringCache.GetId(value);
  }

  public string AttackerOwner
  {
    get => StringCache.GetName(_attackerOwnerId);
    set => _attackerOwnerId = StringCache.GetId(value);
  }

  public string Defender
  {
    get => StringCache.GetName(_defenderId);
    set => _defenderId = StringCache.GetId(value);
  }

  public bool AttackerIsSpell { get; set; }

  public override bool Equals(object obj)
  {
    return obj is DamageRecord other && _attackerId == other._attackerId && _attackerOwnerId == other._attackerOwnerId
      && _defenderId == other._defenderId && AttackerIsSpell == other.AttackerIsSpell && Total == other.Total
      && TypeId == other.TypeId && SubTypeId == other.SubTypeId && ModifiersMask == other.ModifiersMask;
  }

  public override int GetHashCode()
  {
    var hash1 = HashCode.Combine(_attackerId, _attackerOwnerId, _defenderId);
    var hash2 = HashCode.Combine(AttackerIsSpell, Total, TypeId);
    return HashCode.Combine(hash1, hash2, SubTypeId, ModifiersMask);
  }

  /*
   * Two fields used to sit here: OverTotal, which damage never writes (it is on HealRecord now), and the
   * defender's owner. Nothing read that owner — the pet naming that wants an owner names the *attacker*
   * (RecordGroupCollections, DamageStatsBuilder, HitLogViewer) — so it was written, capitalised and compared,
   * never displayed. Losing them takes a damage record from 56 bytes to 48 (the allocator steps down once the
   * payload crosses under 32). OverTotal cannot merge anything that was distinct, being always 0 here; the owner
   * can, in one case — it was derived from the defender's name *and* from whether PlayerRegistry had verified
   * that player yet, so the same pet could carry a null owner early in a session and a real one later, which was
   * two cache entries and is now one. That only ever retains less, and the field it turns on is unread. The
   * parser still resolves the defender's owner for its side effect, PlayerRegistry.AddPetToPlayer.
   */
  private int _attackerId;
  private int _attackerOwnerId;
  private int _defenderId;
}
