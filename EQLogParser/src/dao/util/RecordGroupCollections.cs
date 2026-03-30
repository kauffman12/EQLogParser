using System.Collections;
using System.Collections.Generic;

namespace EQLogParser
{
  internal class DamageGroupCollection : RecordGroupCollection
  {
    private readonly DamageValidator _damageValidator = new();

    internal DamageGroupCollection(List<List<ActionGroup>> recordGroups) : base(recordGroups)
    {
    }

    protected override bool IsValid(RecordWrapper wrapper)
    {
      var record = wrapper?.Record as DamageRecord;
      return _damageValidator.IsValid(record);
    }

    protected override DataPoint Create(RecordWrapper wrapper)
    {
      DataPoint dataPoint = null;

      if (wrapper?.Record is DamageRecord record)
      {
        string origName = null;
        var petName = PlayerManager.Instance.GetPlayerFromPet(record.Attacker);
        if (petName != null || (!string.IsNullOrEmpty(record.AttackerOwner) && !string.IsNullOrEmpty(petName = record.AttackerOwner)))
        {
          origName = petName;
        }

        dataPoint = new DataPoint
        {
          Type = record.Type,
          Total = record.Total,
          ModifiersMask = record.ModifiersMask,
          Name = record.Attacker,
          PlayerName = origName,
          CurrentTime = wrapper.BeginTime
        };
      }

      return dataPoint;
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

    protected override DataPoint Create(RecordWrapper wrapper)
    {
      DataPoint dataPoint = null;

      if (wrapper?.Record is HealRecord record)
      {
        dataPoint = new DataPoint
        {
          Type = record.Type,
          Total = record.Total,
          ModifiersMask = record.ModifiersMask,
          Name = record.Healer,
          CurrentTime = wrapper.BeginTime
        };
      }

      return dataPoint;
    }
  }

  internal class TankGroupCollection : RecordGroupCollection
  {
    readonly int _damageType;
    internal TankGroupCollection(List<List<ActionGroup>> recordGroups, int damageType) : base(recordGroups)
    {
      _damageType = damageType;
    }

    protected override bool IsValid(RecordWrapper wrapper)
    {
      var valid = false;
      if (wrapper.Record is DamageRecord damage)
      {
        valid = _damageType == 0 || (_damageType == 1 && StatsUtil.IsMelee(damage)) || (_damageType == 2 && !StatsUtil.IsMelee(damage));
      }
      return valid;
    }

    protected override DataPoint Create(RecordWrapper wrapper)
    {
      DataPoint dataPoint = null;
      if (wrapper?.Record is DamageRecord record)
      {
        dataPoint = new DataPoint
        {
          Type = record.Type,
          Total = record.Total,
          ModifiersMask = record.ModifiersMask,
          Name = record.Defender,
          CurrentTime = wrapper.BeginTime
        };
      }

      return dataPoint;
    }
  }

  internal abstract class RecordGroupCollection : IEnumerable<DataPoint>
  {
    private static readonly RecordWrapper StopWrapper = new();
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
      RecordWrapper record;
      while ((record = GetRecord()) != StopWrapper)
      {
        if (record != null && IsValid(record))
        {
          yield return Create(record);
        }
      }
    }

    protected virtual bool IsValid(RecordWrapper wrapper)
    {
      return false;
    }

    protected virtual DataPoint Create(RecordWrapper wrapper)
    {
      return null;
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
      return GetEnumerator();
    }

    private RecordWrapper GetRecord()
    {
      var wrapper = StopWrapper;

      if (_recordGroups.Count > _currentGroup)
      {
        var blocks = _recordGroups[_currentGroup];

        if (blocks.Count > _currentBlock)
        {
          var block = blocks[_currentBlock];

          if (block.Actions.Count > _currentRecord)
          {
            wrapper = new RecordWrapper { Record = block.Actions[_currentRecord], BeginTime = block.BeginTime };
            _currentRecord++;
          }
          else
          {
            _currentRecord = 0;
            _currentBlock++;
            wrapper = null;
          }
        }
        else
        {
          _currentBlock = 0;
          _currentRecord = 0;
          _currentGroup++;
          wrapper = null;
        }
      }

      return wrapper;
    }

    internal class RecordWrapper : TimedAction
    {
      internal IAction Record { get; set; }
    }
  }
}
