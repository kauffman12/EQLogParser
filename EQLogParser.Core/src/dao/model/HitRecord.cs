namespace EQLogParser;

/// <summary>
/// Base of the two record types a raid produces by the million: what happened, as one hit or one heal.
/// </summary>
/// <remarks>
/// Every name on a record is stored as an id into <see cref="StringCache"/> instead of a reference to
/// a string. A record is read as text (grids, exports, the log viewers) and written once by a parser,
/// so the id costs four bytes where a pointer cost eight, and the text it stands for was already shared
/// by interning. Reading a name therefore resolves rather than dereferences; it is an array index into
/// strings that live forever, so it stays cheap enough to sit in the aggregation loops.
/// <para>
/// Setting a name assigns whatever text is handed in, exactly as before: callers that want title casing
/// keep calling <see cref="StringCache.GetOrAdd"/> themselves, and callers that display the text verbatim
/// (spell names) keep their exact spelling. Ids are matched ordinally, so two spellings that differ by
/// case stay two different values here just as they did when they were references.
/// </para>
/// </remarks>
public class HitRecord : IAction
{
  public uint Total { get; set; }
  public uint OverTotal { get; set; }
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

/// <summary>
/// One heal. Equal by value, so the manager can keep a single instance for every repeat of it —
/// heals are the most repetitive thing a raid writes (three quarters of one night's heal lines
/// restate a heal already seen), and each repeat costs an object if nothing recognises it.
/// </summary>
internal class HealRecord : HitRecord
{
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

/// <summary>
/// One damage event. Equal by value so the manager hands the same instance to every repeat of it;
/// see <see cref="FightManager.GetCachedDamageRecord"/>.
/// </summary>
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

  public string DefenderOwner
  {
    get => StringCache.GetName(_defenderOwnerId);
    set => _defenderOwnerId = StringCache.GetId(value);
  }

  public bool AttackerIsSpell { get; set; }

  public override bool Equals(object obj)
  {
    return obj is DamageRecord other && _attackerId == other._attackerId && _attackerOwnerId == other._attackerOwnerId
      && _defenderId == other._defenderId && _defenderOwnerId == other._defenderOwnerId && AttackerIsSpell == other.AttackerIsSpell
      && Total == other.Total && OverTotal == other.OverTotal && TypeId == other.TypeId && SubTypeId == other.SubTypeId
      && ModifiersMask == other.ModifiersMask;
  }

  public override int GetHashCode()
  {
    var hash1 = HashCode.Combine(_attackerId, _attackerOwnerId, _defenderId, _defenderOwnerId);
    var hash2 = HashCode.Combine(AttackerIsSpell, Total, OverTotal, TypeId);
    return HashCode.Combine(hash1, hash2, SubTypeId, ModifiersMask);
  }

  private int _attackerId;
  private int _attackerOwnerId;
  private int _defenderId;
  private int _defenderOwnerId;
}
