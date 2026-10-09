using System.Collections;

namespace EQLogParser
{
  /*
   * THE LAW OF THIS FILE: a walk hands out THE SAME `DataPoint` object on every record, and a consumer must use each value
   * inside its own loop body and let it go.
   *
   * It used to build two fresh objects per record — a `RecordWrapper` and a `DataPoint` — which on a whole-capture selection
   * (measured: 4,660,915 damage records on eqlog_Kizant_xegony-09-03-26.txt) is 960 MB of garbage per walk, charged to the
   * UI thread that paints the chart and to whichever board asked first. Reusing one instance costs nothing and deletes the
   * garbage; the price is aliasing, which is why the only consumers are the three chart series builders that aggregate in
   * the loop (LineChart.AddDataPoints) and nothing may `.ToList()`/`Select()` a walk into storage. A walk is single-pass and
   * one-at-a-time by construction: the cursors live on the collection itself, so a second `foreach` resumes where the first
   * stopped — anything wanting two passes builds a second collection, exactly as the builders do per publish.
   *
   * Because the instance is reused, EVERY field gets written on every record: `Fill` clears the whole DTO before a subclass
   * sets its own five, so a value can never be inherited from the previous record.
   */
  internal class DamageGroupCollection : RecordGroupCollection
  {
    private readonly DamageValidator _damageValidator = new(
      AppSettings.IsAssassinateDamageEnabled, AppSettings.IsBaneDamageEnabled, AppSettings.IsDamageShieldDamageEnabled,
      AppSettings.IsFinishingBlowDamageEnabled, AppSettings.IsHeadshotDamageEnabled, AppSettings.IsSlayUndeadDamageEnabled);

    /*
     * "Which raid member owns this attacker name" is a property of the NAME for the length of one walk, and asking the
     * identity stores per record meant 4.6 M lookups where there are dozens of distinct names (measured: 69 on a whole night).
     * Memoizing is also fresher than it looks: the lookup takes no time argument, so every answer in one walk came from the
     * same snapshot anyway — this only stops re-asking.
     */
    private readonly Dictionary<string, string> _ownerByAttacker = [];

    internal DamageGroupCollection(List<List<ActionGroup>> recordGroups) : base(recordGroups)
    {
    }

    protected override bool IsValid(RecordWrapper wrapper)
    {
      return wrapper.Record is DamageRecord record && _damageValidator.IsValid(record);
    }

    protected override bool TryFill(RecordWrapper wrapper, DataPoint into)
    {
      if (wrapper.Record is not DamageRecord record) return false;

      var attacker = record.Attacker;
      if (!_ownerByAttacker.TryGetValue(attacker, out var owner))
      {
        owner = IdentityLookup.OwnerOf(attacker);
        _ownerByAttacker[attacker] = owner;
      }

      /* The store's answer wins; the line's own possessive word answers only when it has no opinion (same order as before memoizing). */
      into.PlayerName = owner ?? (!string.IsNullOrEmpty(record.AttackerOwner) ? record.AttackerOwner : null);
      into.Type = record.Type;
      into.Total = record.Total;
      into.ModifiersMask = record.ModifiersMask;
      into.Name = attacker;
      into.CurrentTime = wrapper.BeginTime;
      return true;
    }
  }

  internal class HealGroupCollection : RecordGroupCollection
  {
    internal HealGroupCollection(List<List<ActionGroup>> recordGroups) : base(recordGroups)
    {
    }

    protected override bool IsValid(RecordWrapper wrapper)
    {
      return true; // validated when healing groups are initially built in the manager
    }

    protected override bool TryFill(RecordWrapper wrapper, DataPoint into)
    {
      if (wrapper.Record is not HealRecord record) return false;

      into.Name = record.Healer;
      into.Type = record.Type;
      into.Total = record.Total;
      into.ModifiersMask = record.ModifiersMask;
      into.CurrentTime = wrapper.BeginTime;
      return true;
    }
  }

  internal class TankGroupCollection : RecordGroupCollection
  {
    private readonly int _damageType;

    internal TankGroupCollection(List<List<ActionGroup>> recordGroups, int damageType) : base(recordGroups)
    {
      _damageType = damageType;
    }

    protected override bool IsValid(RecordWrapper wrapper)
    {
      return wrapper.Record is DamageRecord damage
             && (_damageType == 0 || (_damageType == 1 && StatsUtil.IsMelee(damage)) || (_damageType == 2 && !StatsUtil.IsMelee(damage)));
    }

    protected override bool TryFill(RecordWrapper wrapper, DataPoint into)
    {
      if (wrapper.Record is not DamageRecord record) return false;

      into.Name = record.Defender;
      into.Type = record.Type;
      into.Total = record.Total;
      into.ModifiersMask = record.ModifiersMask;
      into.CurrentTime = wrapper.BeginTime;
      return true;
    }
  }

  internal abstract class RecordGroupCollection : IEnumerable<DataPoint>
  {
    /* The two objects a walk used to rebuild per record. One instance each, written in place. See the law at the top of the file. */
    private readonly RecordWrapper _wrapper = new();
    private readonly DataPoint _point = new();

    private readonly List<List<ActionGroup>> _recordGroups;
    private int _currentGroup;
    private int _currentBlock;
    private int _currentRecord;

    internal RecordGroupCollection(List<List<ActionGroup>> recordGroups)
    {
      _recordGroups = recordGroups;
      _currentGroup = 0;
      _currentBlock = 0;
      _currentRecord = 0;
    }

    public IEnumerator<DataPoint> GetEnumerator()
    {
      for (; _currentGroup < _recordGroups.Count; _currentGroup++)
      {
        var blocks = _recordGroups[_currentGroup];

        for (; _currentBlock < blocks.Count; _currentBlock++)
        {
          var block = blocks[_currentBlock];
          var actions = block.Actions;

          for (; _currentRecord < actions.Count; _currentRecord++)
          {
            /* One wrapper, re-pointed: it exists to carry the block's time beside the record, nothing more. */
            _wrapper.Record = actions[_currentRecord];
            _wrapper.BeginTime = block.BeginTime;

            if (!IsValid(_wrapper)) continue;

            /* Reset here rather than in each subclass: a subclass that forgets one field would leak the previous record into it. */
            ClearTo(_point);

            /* `false` means this record is not the kind this collection reads — skipping it is the old `Create` returning null. */
            if (TryFill(_wrapper, _point)) yield return _point;
          }

          _currentRecord = 0;
        }

        _currentBlock = 0;
      }
    }

    protected virtual bool IsValid(RecordWrapper wrapper)
    {
      return false;
    }

    /*
     * Write EVERY field this collection answers with — and only after `ClearTo` has reset the DTO. Returning false is how a
     * subclass declines a record; the walk then yields nothing rather than handing out half-written state.
     */
    protected abstract bool TryFill(RecordWrapper wrapper, DataPoint into);

    IEnumerator IEnumerable.GetEnumerator()
    {
      return GetEnumerator();
    }

    /*
     * The reset is not decoration: the instance outlives every record, so a field nobody writes would keep the value the
     * PREVIOUS record left in it. `RecordGroupCollectionTest` proves every untouched field stays default across a walk.
     */
    protected static void ClearTo(DataPoint into)
    {
      into.Name = null;
      into.PlayerName = null;
      into.Type = null;
      into.Total = 0;
      into.ModifiersMask = 0;
      into.CurrentTime = 0;
      into.Avg = 0;
      into.FightTotal = 0;
      into.FightHits = 0;
      into.FightCritHits = 0;
      into.FightTcHits = 0;
      into.RollingTotal = 0;
      into.RollingDps = 0;
      into.CritsPerSecond = 0;
      into.TcPerSecond = 0;
      into.AttemptsPerSecond = 0;
      into.HitsPerSecond = 0;
      into.TotalPerSecond = 0;
      into.ValuePerSecond = 0;
      into.CritRate = 0;
      into.TcRate = 0;
      into.BeginTime = 0;
      into.DateTime = default;
    }

    internal sealed class RecordWrapper : TimedAction
    {
      internal IAction Record { get; set; }
    }
  }
}
