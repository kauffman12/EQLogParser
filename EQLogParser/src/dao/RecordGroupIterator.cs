using System.Collections;
using System.Collections.Generic;

namespace EQLogParser
{
  public class DamageGroupIterator : RecordGroupIterator
  {
    private Dictionary<string, byte> NpcNames;

    public DamageGroupIterator(List<List<TimedAction>> recordGroups, Dictionary<string, byte> npcNames) : base(recordGroups)
    {
      NpcNames = npcNames;
    }

    override protected bool IsValid(TimedAction timedAction)
    {
      DamageRecord record = timedAction as DamageRecord;
      return record != null && record.Type != Labels.BANE_NAME && NpcNames.ContainsKey(record.Defender) && !DataManager.Instance.IsProbablyNotAPlayer(record.Attacker);
    }

    override protected DataPoint Create(TimedAction timedAction)
    {
      DataPoint dataPoint = null;
      DamageRecord record = timedAction as DamageRecord;

      if (record != null)
      {
        string attacker = record.Attacker;
        string pname = DataManager.Instance.GetPlayerFromPet(record.Attacker);
        if (pname != null || (record.AttackerPetType != "" && (pname = record.AttackerOwner) != ""))
        {
          attacker = pname;
        }

        dataPoint = new DataPoint() { Total = record.Total, Name = attacker, CurrentTime = record.BeginTime };
      }

      return dataPoint;
    }
  }

  public class HealGroupIterator : RecordGroupIterator
  {
    public HealGroupIterator(List<List<TimedAction>> recordGroups) : base(recordGroups)
    {
      // do nothing else
    }

    override protected bool IsValid(TimedAction timedAction)
    {
      HealRecord record = timedAction as HealRecord;
      return record != null && !DataManager.Instance.IsProbablyNotAPlayer(record.Healed);
    }

    override protected DataPoint Create(TimedAction timedAction)
    {
      DataPoint dataPoint = null;
      HealRecord record = timedAction as HealRecord;

      if (record != null)
      {
        dataPoint = new DataPoint() { Total = record.Total, Name = record.Healer, CurrentTime = record.BeginTime };
      }

      return dataPoint;
    }
  }

  public abstract class RecordGroupIterator : IEnumerable<DataPoint>
  {
    private List<List<TimedAction>> RecordGroups;
    private int CurrentGroup;
    private int CurrentRecord;

    public RecordGroupIterator(List<List<TimedAction>> recordGroups)
    {
      RecordGroups = recordGroups;
      CurrentGroup = 0;
      CurrentRecord = 0;
    }

    public IEnumerator<DataPoint> GetEnumerator()
    {
      TimedAction record;

      while ((record = GetRecord()) != null)
      {
        if (IsValid(record))
        {
          yield return Create(record);
        }
      }

      yield break;
    }

    protected virtual bool IsValid(TimedAction timedAction)
    {
      return false;
    }

    protected virtual DataPoint Create(TimedAction timedAction)
    {
      return null;
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
      return GetEnumerator();
    }

    private TimedAction GetRecord()
    {
      TimedAction record = null;

      var list = RecordGroups[CurrentGroup];
      if (list.Count <= CurrentRecord)
      {
        CurrentGroup++;
        CurrentRecord = 0;

        if (RecordGroups.Count > CurrentGroup)
        {
          list = RecordGroups[CurrentGroup];
          if (list.Count > CurrentRecord)
          {
            record = list[CurrentRecord];
          }
        }
      }
      else
      {
        record = list[CurrentRecord++];
      }

      return record;
    }
  }
}
