using System.Collections;
using System.Collections.Generic;

namespace EQLogParser
{
  public class DamageGroupCollection : RecordGroupCollection
  {
    public DamageGroupCollection(List<List<ActionBlock>> recordGroups) : base(recordGroups)
    {
    }

    override protected bool IsValid(RecordWrapper wrapper)
    {
      DamageRecord record = wrapper?.Record as DamageRecord;
      return DamageStatsManager.Instance.IsValidDamage(record) && (record.Type != Labels.BANE || MainWindow.IsBaneDamageEnabled);
    }

    override protected DataPoint Create(RecordWrapper wrapper)
    {
      DataPoint dataPoint = null;

      if (wrapper?.Record is DamageRecord record)
      {
        string attacker = record.Attacker;
        string pname = DataManager.Instance.GetPlayerFromPet(record.Attacker);
        if (pname != null || (!string.IsNullOrEmpty(record.AttackerOwner) && !string.IsNullOrEmpty((pname = record.AttackerOwner))))
        {
          attacker = pname;
        }

        dataPoint = new DataPoint() { Total = record.Total, ModifiersMask = record.ModifiersMask, Name = attacker, CurrentTime = wrapper.BeginTime };
      }

      return dataPoint;
    }
  }

  public class HealGroupCollection : RecordGroupCollection
  {
    public HealGroupCollection(List<List<ActionBlock>> recordGroups) : base(recordGroups)
    {
    }

    override protected bool IsValid(RecordWrapper wrapper)
    {
      HealRecord record = wrapper?.Record as HealRecord;
      return HealingStatsManager.IsValidHeal(record);
    }

    override protected DataPoint Create(RecordWrapper wrapper)
    {
      DataPoint dataPoint = null;

      if (wrapper?.Record is HealRecord record)
      {
        dataPoint = new DataPoint() { Total = record.Total, ModifiersMask = record.ModifiersMask, Name = record.Healer, CurrentTime = wrapper.BeginTime };
      }

      return dataPoint;
    }
  }

  public class TankGroupCollection : RecordGroupCollection
  {
    public TankGroupCollection(List<List<ActionBlock>> recordGroups) : base(recordGroups)
    {
    }

    override protected bool IsValid(RecordWrapper wrapper)
    {
      DamageRecord record = wrapper?.Record as DamageRecord;
      return TankingStatsManager.Instance.IsValidDamage(record);
    }

    override protected DataPoint Create(RecordWrapper wrapper)
    {
      DataPoint dataPoint = null;

      if (wrapper?.Record is DamageRecord record)
      {
        dataPoint = new DataPoint() { Total = record.Total, ModifiersMask = record.ModifiersMask, Name = record.Defender, CurrentTime = wrapper.BeginTime };
      }

      return dataPoint;
    }
  }

  public abstract class RecordGroupCollection : IEnumerable<DataPoint>
  {
    private static readonly RecordWrapper StopWrapper = new RecordWrapper();
    private readonly List<List<ActionBlock>> RecordGroups;
    private int CurrentGroup;
    private int CurrentBlock;
    private int CurrentRecord;

    public RecordGroupCollection(List<List<ActionBlock>> recordGroups)
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

    protected class RecordWrapper : TimedAction
    {
      internal IAction Record { get; set; }
    }
  }
}
