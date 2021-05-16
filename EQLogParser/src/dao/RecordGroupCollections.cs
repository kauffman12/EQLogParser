using System.Collections;
using System.Collections.Generic;

namespace EQLogParser
{
  internal class DamageGroupCollection : RecordGroupCollection
  {
    internal DamageGroupCollection(List<List<ActionBlock>> recordGroups) : base(recordGroups)
    {
    }

    protected override bool IsValid(RecordWrapper wrapper)
    {
      DamageRecord record = wrapper?.Record as DamageRecord;
      return record.Type != Labels.BANE || MainWindow.IsBaneDamageEnabled;
    }

    protected override DataPoint Create(RecordWrapper wrapper)
    {
      DataPoint dataPoint = null;

      if (wrapper?.Record is DamageRecord record)
      {
        string origName = null;
        string pname = PlayerManager.Instance.GetPlayerFromPet(record.Attacker);
        if (pname != null || (!string.IsNullOrEmpty(record.AttackerOwner) && !string.IsNullOrEmpty(pname = record.AttackerOwner)))
        {
          origName = pname;
        }

        dataPoint = new DataPoint() { Total = record.Total, ModifiersMask = record.ModifiersMask, Name = record.Attacker, PlayerName = origName, CurrentTime = wrapper.BeginTime };
      }

      return dataPoint;
    }
  }

  internal class HealGroupCollection : RecordGroupCollection
  {
    internal HealGroupCollection(List<List<ActionBlock>> recordGroups) : base(recordGroups)
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
        dataPoint = new DataPoint() { Total = record.Total, ModifiersMask = record.ModifiersMask, Name = record.Healer, CurrentTime = wrapper.BeginTime };
      }

      return dataPoint;
    }
  }

  internal class TankGroupCollection : RecordGroupCollection
  {
    readonly int DamageType = 0;
    internal TankGroupCollection(List<List<ActionBlock>> recordGroups, int damageType) : base(recordGroups)
    {
      DamageType = damageType;
    }

    protected override bool IsValid(RecordWrapper wrapper)
    {
      bool valid = false;
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
        dataPoint = new DataPoint() { Total = record.Total, ModifiersMask = record.ModifiersMask, Name = record.Defender, CurrentTime = wrapper.BeginTime };
      }

      return dataPoint;
    }
  }

  internal abstract class RecordGroupCollection : IEnumerable<DataPoint>
  {
    private static readonly RecordWrapper StopWrapper = new RecordWrapper();
    private readonly List<List<ActionBlock>> RecordGroups;
    private int CurrentGroup;
    private int CurrentBlock;
    private int CurrentRecord;

    internal RecordGroupCollection(List<List<ActionBlock>> recordGroups)
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
      RecordWrapper wrapper = StopWrapper;

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
