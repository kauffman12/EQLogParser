using System;

namespace EQLogParser;

public class HitRecord : IAction
{
  public uint Total { get; set; }
  public uint OverTotal { get; set; }
  public string Type { get; set; }
  public string SubType { get; set; }
  public short ModifiersMask { get; set; }
}

internal class HealRecord : HitRecord
{
  public string Healer { get; set; }
  public string Healed { get; set; }
}

internal class DamageRecord : HitRecord
{
  public string Attacker { get; set; }
  public string AttackerOwner { get; set; }
  public string Defender { get; set; }
  public string DefenderOwner { get; set; }
  public bool AttackerIsSpell { get; set; }

  public override bool Equals(object obj)
  {
    return obj is DamageRecord other && Attacker == other.Attacker && AttackerOwner == other.AttackerOwner && Defender == other.Defender &&
      DefenderOwner == other.DefenderOwner && AttackerIsSpell == other.AttackerIsSpell && Total == other.Total &&
      OverTotal == other.OverTotal && Type == other.Type && SubType == other.SubType && ModifiersMask == other.ModifiersMask;
  }

  public override int GetHashCode()
  {
    var hash1 = HashCode.Combine(Attacker, AttackerOwner, Defender, DefenderOwner, AttackerIsSpell, Total);
    var hash2 = HashCode.Combine(OverTotal, Type, SubType, ModifiersMask);
    return HashCode.Combine(hash1, hash2);
  }
}
