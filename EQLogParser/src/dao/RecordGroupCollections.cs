using System.Collections;
using System.Collections.Generic;

namespace EQLogParser
{
  internal class DamageGroupCollection : RecordGroupCollection
  {
    private readonly DamageValidator DamageValidator = new();

    internal DamageGroupCollection(List<List<ActionGroup>> recordGroups) : base(recordGroups)
    {
    }

    protected override bool IsValid(RecordWrapper wrapper)
    {
      var record = wrapper?.Record as DamageRecord;
      return DamageValidator.IsValid(record);
    }

    protected override DataPoint Create(RecordWrapper wrapper)
    {
      DataPoint dataPoint = null;

      if (wrapper?.Record is DamageRecord record)
      {
        string origName = null;
        var pname = PlayerManager.Instance.GetPlayerFromPet(record.Attacker);
        if (pname != null || (!string.IsNullOrEmpty(record.AttackerOwner) && !string.IsNullOrEmpty(pname = record.AttackerOwner)))
        {
          origName = pname;
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
    readonly int DamageType;
    internal TankGroupCollection(List<List<ActionGroup>> recordGroups, int damageType) : base(recordGroups)
    {
      DamageType = damageType;
    }

    protected override bool IsValid(RecordWrapper wrapper)
    {
      var valid = false;
      if (wrapper.Record is DamageRecord damage)
      {
        valid = DamageType == 0 || (DamageType == 1 && TankingStatsManager.IsMelee(damage)) || (DamageType == 2 && !TankingStatsManager.IsMelee(damage));
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
    private readonly List<List<ActionGroup>> RecordGroups;
    private int CurrentGroup;
    private int CurrentBlock;
    private int CurrentRecord;

    internal RecordGroupCollection(List<List<ActionGroup>> recordGroups)
    {
      RecordGroups = recordGroups;
      CurrentGroup = 0;
      CurrentBlock = 0;
      CurrentRecord = 0;
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

      yield break;
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

      if (RecordGroups.Count > CurrentGroup)
      {
        var blocks = RecordGroups[CurrentGroup];

        if (blocks.Count > CurrentBlock)
        {
          var block = blocks[CurrentBlock];

          if (block.Actions.Count > CurrentRecord)
          {
            wrapper = new RecordWrapper() { Record = block.Actions[CurrentRecord], BeginTime = block.BeginTime };
            CurrentRecord++;
          }
          else
          {
            CurrentRecord = 0;
            CurrentBlock++;
            wrapper = null;
          }
        }
        else
        {
          CurrentBlock = 0;
          CurrentRecord = 0;
          CurrentGroup++;
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
