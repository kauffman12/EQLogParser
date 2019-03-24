using System.Collections;
using System.Collections.Generic;

namespace EQLogParser
{
  public class DamageGroupIterator : RecordGroupIterator
  {
    private bool ShowBane;

    public DamageGroupIterator(List<List<ActionBlock>> recordGroups, bool showBane) : base(recordGroups)
    {
      ShowBane = showBane;
    }

    override protected bool IsValid(RecordWrapper wrapper)
    {
      DamageRecord record = wrapper.Record as DamageRecord;
      return DamageStatsManager.IsValidDamage(record) && (ShowBane || record.Type != Labels.BANE_NAME);
    }

    override protected DataPoint Create(RecordWrapper wrapper)
    {
      DataPoint dataPoint = null;
      DamageRecord record = wrapper.Record as DamageRecord;

      if (record != null)
      {
        string attacker = record.Attacker;
        string pname = DataManager.Instance.GetPlayerFromPet(record.Attacker);
        if (pname != null || (record.AttackerOwner != "" && (pname = record.AttackerOwner) != ""))
        {
          attacker = pname;
        }

        dataPoint = new DataPoint() { Total = record.Total, ModifiersMask = record.ModifiersMask, Name = attacker, CurrentTime = wrapper.BeginTime };
      }

      return dataPoint;
    }
  }

  public class HealGroupIterator : RecordGroupIterator
  {
    private bool ShowAE;
    public HealGroupIterator(List<List<ActionBlock>> recordGroups, bool showAE) : base(recordGroups)
    {
      ShowAE = showAE;
    }

    override protected bool IsValid(RecordWrapper wrapper)
    {
      HealRecord record = wrapper.Record as HealRecord;
      return HealStatsManager.IsValidHeal(record, ShowAE);
    }

    override protected DataPoint Create(RecordWrapper wrapper)
    {
      DataPoint dataPoint = null;
      HealRecord record = wrapper.Record as HealRecord;

      if (record != null)
      {
        dataPoint = new DataPoint() { Total = record.Total, ModifiersMask = record.ModifiersMask, Name = record.Healer, CurrentTime = wrapper.BeginTime };
      }

      return dataPoint;
    }
  }

  public abstract class RecordGroupIterator : IEnumerable<DataPoint>
  {
    private static RecordWrapper StopWrapper = new RecordWrapper();
    private List<List<ActionBlock>> RecordGroups;
    private int CurrentGroup;
    private int CurrentBlock;
    private int CurrentRecord;

    public RecordGroupIterator(List<List<ActionBlock>> recordGroups)
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
      public Action Record { get; set; }
    }
  }
}
