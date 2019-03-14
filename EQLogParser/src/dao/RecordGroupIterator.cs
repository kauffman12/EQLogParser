using System.Collections;
using System.Collections.Generic;

namespace EQLogParser
{
  public class DamageGroupIterator : RecordGroupIterator
  {
    private Dictionary<string, byte> NpcNames;
    private bool ShowBane;

    public DamageGroupIterator(List<List<TimedAction>> recordGroups, Dictionary<string, byte> npcNames, bool showBane) : base(recordGroups)
    {
      NpcNames = npcNames;
      ShowBane = showBane;
    }

    override protected bool IsValid(TimedAction timedAction)
    {
      DamageRecord record = timedAction as DamageRecord;
      return record != null && (ShowBane || record.Type != Labels.BANE_NAME && NpcNames.ContainsKey(record.Defender)) && !DataManager.Instance.IsProbablyNotAPlayer(record.Attacker);
    }

    override protected DataPoint Create(TimedAction timedAction)
    {
      DataPoint dataPoint = null;
      DamageRecord record = timedAction as DamageRecord;

      if (record != null)
      {
        string attacker = record.Attacker;
        string pname = DataManager.Instance.GetPlayerFromPet(record.Attacker);
        if (pname != null || (record.AttackerOwner != "" && (pname = record.AttackerOwner) != ""))
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
    private static TimedAction StopRecord = new TimedAction();
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

      while ((record = GetRecord()) != StopRecord)
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
      TimedAction record = StopRecord;

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
          else
          {
            record = null;
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
