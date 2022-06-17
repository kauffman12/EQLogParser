﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace EQLogParser
{
  class DamageLineParser
  {
    public static event EventHandler<DamageProcessedEvent> EventsDamageProcessed;

    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly Regex CheckEyeRegex = new Regex(@"^Eye of (\w+)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Dictionary<string, string> SpellTypeCache = new Dictionary<string, string>();
    private static readonly List<string> SlainQueue = new List<string>();
    private static double SlainTime = double.NaN;

    private static readonly Dictionary<string, string> HitMap = new Dictionary<string, string>
    {
      { "bash", "bashes" }, { "backstab", "backstabs" }, { "bite", "bites" }, { "claw", "claws" }, { "crush", "crushes" },
      { "frenzy", "frenzies" }, { "gore", "gores" }, { "hit", "hits" }, { "kick", "kicks" }, { "learn", "learns" },
      { "maul", "mauls" }, { "punch", "punches" }, { "pierce", "pierces" }, { "rend", "rends" }, { "shoot", "shoots" },
      { "slash", "slashes" }, { "slam", "slams" }, { "slice", "slices" }, { "smash", "smashes" }, { "sting", "stings" },
      { "strike", "strikes" }, { "sweep", "sweeps" }
    };

    private static readonly Dictionary<string, string> HitAdditionalMap = new Dictionary<string, string>
    {
      { "frenzy", "frenzies" }, { "frenzies", "frenzies" },
    };

    private static readonly List<string> ChestTypes = new List<string>
    {
      " chest", " cache", " satchel", " treasure box"
    };

    private static readonly Dictionary<string, SpellResist> SpellResistMap = new Dictionary<string, SpellResist>
    {
      { "fire", SpellResist.FIRE }, { "cold", SpellResist.COLD }, { "poison", SpellResist.POISON },
      { "magic", SpellResist.MAGIC }, { "disease", SpellResist.DISEASE }, { "unresistable", SpellResist.UNRESISTABLE },
      { "chromatic", SpellResist.LOWEST }, { "physical", SpellResist.PHYSICAL }, { "corruption", SpellResist.CORRUPTION },
      { "prismatic", SpellResist.AVERAGE },
    };

    private static readonly Dictionary<string, string> SpecialCodes = new Dictionary<string, string>
    {
      { "Mana Burn", "M" }, { "Harm Touch", "H" }, { "Life Burn", "L" }
    };

    private static OldCritData LastCrit;

    static DamageLineParser() => HitMap.Keys.ToList().ForEach(key => HitMap[HitMap[key]] = HitMap[key]); // add two way mapping

    public static void CheckSlainQueue(double currentTime)
    {
      lock (SlainQueue)
      {
        // handle Slain queue
        if (!double.IsNaN(SlainTime) && (currentTime > SlainTime))
        {
          SlainQueue.ForEach(slain => DataManager.Instance.RemoveActiveFight(slain));
          SlainQueue.Clear();
          SlainTime = double.NaN;
        }
      }
    }

    public static void Process(LineData lineData)
    {
      bool handled = false;

      try
      {
        string[] split = lineData.Action.Split(' ');

        if (split != null && split.Length >= 2)
        {
          int stop = split.Length - 1;
          if (!string.IsNullOrEmpty(split[stop]) && split[stop][split[stop].Length - 1] == ')')
          {
            for (int i = stop; i >= 0 && stop > 2; i--)
            {
              if (!string.IsNullOrEmpty(split[i]) && split[i][0] == '(')
              {
                stop = i - 1;
                break;
              }
            }
          }

          // see if it's a died message right away
          if (split.Length > 1 && stop >= 1 && split[stop] == "died.")
          {
            var test = string.Join(" ", split, 0, stop);
            if (!string.IsNullOrEmpty(test))
            {
              UpdateSlain(test, "", lineData);
              handled = true;
            }
          }

          if (!handled)
          {
            int byIndex = -1, forIndex = -1, pointsOfIndex = -1, endDamage = -1, byDamage = -1, extraIndex = -1;
            int fromDamage = -1, hasIndex = -1, haveIndex = -1, hitType = -1, hitTypeAdd = -1, slainIndex = -1;
            int takenIndex = -1, tryIndex = -1, yourIndex = -1, isIndex = -1, dsIndex = -1, butIndex = -1;
            int missType = -1, nonMeleeIndex = -1;
            string subType = null;

            bool found = false;
            for (int i = 0; i <= stop && !found; i++)
            {
              if (!string.IsNullOrEmpty(split[i]))
              {
                switch (split[i])
                {
                  case "healed":
                    found = true; // short circuit
                    break;
                  case "but":
                    butIndex = i;
                    break;
                  case "is":
                  case "was":
                    isIndex = i;
                    break;
                  case "has":
                    hasIndex = i;
                    break;
                  case "have":
                    haveIndex = i;
                    break;
                  case "by":
                    byIndex = i;

                    if (slainIndex > -1)
                    {
                      found = true; // short circut
                    }
                    else if (i > 4 && split[i - 1] == "damage")
                    {
                      byDamage = i - 1;
                    }
                    break;
                  case "from":
                    if (i > 3 && split[i - 1] == "damage")
                    {
                      fromDamage = i - 1;
                      if (pointsOfIndex > -1 && extraIndex > -1)
                      {
                        found = true; // short circut
                      }
                      else if (stop > (i + 1) && split[i + 1] == "your")
                      {
                        yourIndex = i + 1;
                      }
                    }
                    break;
                  case "damage.":
                    if (i == stop)
                    {
                      endDamage = i;
                    }
                    break;
                  case "non-melee":
                    nonMeleeIndex = i;
                    if (i > 9 && stop == (i + 1) && split[i + 1] == "damage." && pointsOfIndex == (i - 2) && forIndex == (i - 4))
                    {
                      dsIndex = i - 5;
                      found = true; // short circut
                    }
                    break;
                  case "point":
                  case "points":
                    if (stop >= (i + 1) && split[i + 1] == "of")
                    {
                      pointsOfIndex = i;
                      if (i > 2 && split[i - 2] == "for")
                      {
                        forIndex = i - 2;
                      }
                    }
                    break;
                  case "blocks!":
                    missType = (stop == i && butIndex > -1 && i > tryIndex) ? 0 : missType;
                    break;
                  case "shield!":
                  case "staff!":
                    missType = (i > 5 && stop == i && butIndex > -1 && i > tryIndex && split[i - 2] == "with" &&
                      split[i - 3].StartsWith("block", StringComparison.OrdinalIgnoreCase)) ? 0 : missType;
                    break;
                  case "dodge!":
                  case "dodges!":
                    missType = (stop == i && butIndex > -1 && i > tryIndex) ? 1 : missType;
                    break;
                  case "miss!":
                  case "misses!":
                    missType = (stop == i && butIndex > -1 && i > tryIndex) ? 2 : missType;
                    break;
                  case "parry!":
                  case "parries!":
                    missType = (stop == i && butIndex > -1 && i > tryIndex) ? 3 : missType;
                    break;
                  case "INVULNERABLE!":
                    missType = (stop == i && butIndex > -1 && i > tryIndex) ? 4 : missType;
                    break;
                  case "riposte!":
                  case "ripostes!":
                    missType = (stop == i && butIndex > -1 && i > tryIndex && "(Strikethrough)" != split[split.Length - 1]) ? 5 : missType;
                    break;
                  case "blow!":
                    missType = (stop == i && butIndex > -1 && i > tryIndex && split[i - 2] == "absorbs") ? 6 : missType;
                    break;
                  case "slain":
                    slainIndex = i;
                    break;
                  case "taken":
                    if (i > 1 && (hasIndex == (i - 1) || haveIndex == (i - 1)))
                    {
                      takenIndex = i - 1;

                      if (stop > (i + 2) && split[i + 1] == "an" && split[i + 2] == "extra")
                      {
                        extraIndex = i + 2;
                      }
                    }
                    break;
                  // Old (EQEMU) crit and crippling blow handling
                  case "hit!":
                    if (stop == i && split.Length > 4 && split[i - 1] == "critical" && split[i - 3] == "scores")
                    {
                      LastCrit = new OldCritData { Attacker = split[0], LineData = lineData };
                    }
                    break;
                  case "Crippling":
                    if (stop == (i + 1) && split.Length > 4 && split[i + 1].StartsWith("Blow!") && split[i - 2] == "lands")
                    {
                      LastCrit = new OldCritData { Attacker = split[0], LineData = lineData };
                    }
                    break;
                  default:
                    if (slainIndex == -1 && i > 0 && string.IsNullOrEmpty(subType) && HitMap.TryGetValue(split[i], out subType))
                    {
                      hitType = i;
                      if (i < stop && HitAdditionalMap.ContainsKey(split[i]))
                      {
                        hitTypeAdd = i + i;
                      }

                      if (i > 2 && split[i - 1] == "to" && (split[i - 2] == "tries" || split[i - 2] == "try"))
                      {
                        tryIndex = i - 2;
                      }
                    }
                    break;
                }
              }
            }

            // [Sun Apr 18 19:36:39 2021] Tantor is pierced by Tolzol's thorns for 6718 points of non-melee damage.
            // [Mon Apr 19 22:02:52 2021] Honvar is tormented by Reisil's frost for 7809 points of non-melee damage.
            // [Sun Apr 25 13:47:12 2021] Test One Hundred Three is burned by YOUR flames for 5224 points of non-melee damage.
            // [Sun Apr 18 14:16:13 2021] A failed reclaimer is pierced by YOUR thorns for 193 points of non-melee damage.
            if (dsIndex > -1 && pointsOfIndex > dsIndex && isIndex > -1 && isIndex < dsIndex && byIndex > isIndex)
            {
              string attacker = string.Join(" ", split, byIndex + 1, dsIndex - byIndex - 1);
              if (!string.IsNullOrEmpty(attacker))
              {
                var valid = attacker == "YOUR";
                if (!valid && attacker.EndsWith("'s", StringComparison.OrdinalIgnoreCase))
                {
                  attacker = attacker.Substring(0, attacker.Length - 2);
                  valid = true;
                }

                if (valid)
                {
                  string defender = string.Join(" ", split, 0, isIndex);
                  uint damage = StatsUtil.ParseUInt(split[pointsOfIndex - 1]);
                  handled = CreateDamageRecord(lineData, split, stop, attacker, defender, damage, Labels.DS, Labels.DS);
                }
              }
            }
            // [Mon May 10 22:18:46 2021] A dendridic shard was chilled to the bone for 410 points of non-melee damage.
            else if (dsIndex > -1 && pointsOfIndex > dsIndex && isIndex > -1 && isIndex < dsIndex && byIndex == -1)
            {
              string defender = string.Join(" ", split, 0, isIndex);
              uint damage = StatsUtil.ParseUInt(split[pointsOfIndex - 1]);
              handled = CreateDamageRecord(lineData, split, stop, Labels.RS, defender, damage, Labels.DS, Labels.DS);
            }
            // [Tue Mar 26 22:43:47 2019] a wave sentinel has taken an extra 6250000 points of non-melee damage from Kazint's Greater Fetter spell.
            else if (extraIndex > -1 && pointsOfIndex == (extraIndex + 2) && fromDamage == (pointsOfIndex + 3) && split[stop] == "spell.")
            {
              if (split[fromDamage + 2].EndsWith("'s", StringComparison.OrdinalIgnoreCase))
              {
                string attacker = split[fromDamage + 2].Substring(0, split[fromDamage + 2].Length - 2);
                string defender = string.Join(" ", split, 0, takenIndex);
                uint damage = StatsUtil.ParseUInt(split[extraIndex + 1]);
                string spell = string.Join(" ", split, fromDamage + 3, stop - fromDamage - 3);
                var spellData = DataManager.Instance.GetDamagingSpellByName(spell);
                SpellResist resist = spellData != null ? spellData.Resist : SpellResist.UNDEFINED;
                handled = CreateDamageRecord(lineData, split, stop, attacker, defender, damage, Labels.BANE, spell, resist);
              }
            }
            // [Sun Apr 18 21:26:15 2021] Astralx crushes Sontalak for 126225 points of damage. (Strikethrough Critical)
            // [Sun Apr 18 20:20:32 2021] Susarrak the Crusader claws Villette for 27699 points of damage. (Strikethrough Wild Rampage)
            else if (!string.IsNullOrEmpty(subType) && endDamage > -1 && pointsOfIndex == (endDamage - 2) && forIndex > -1 && hitType < forIndex)
            {
              int hitTypeMod = hitTypeAdd > 0 ? 1 : 0;
              string attacker = string.Join(" ", split, 0, hitType);
              string defender = string.Join(" ", split, hitType + hitTypeMod + 1, forIndex - hitType - hitTypeMod - 1);
              subType = TextFormatUtils.ToUpper(subType);
              uint damage = StatsUtil.ParseUInt(split[pointsOfIndex - 1]);
              handled = CreateDamageRecord(lineData, split, stop, attacker, defender, damage, Labels.MELEE, subType);
            }
            // [Sun Apr 18 20:24:56 2021] Sonozen hit Jortreva the Crusader for 38948 points of fire damage by Burst of Flames. (Lucky Critical Twincast)
            else if (byDamage > 3 && pointsOfIndex == (byDamage - 3) && byIndex == (byDamage + 1) && forIndex > -1 &&
              subType == "hits" && hitType < forIndex && split[stop].Length > 0 && split[stop][split[stop].Length - 1] == '.')
            {
              string spell = string.Join(" ", split, byIndex + 1, stop - byIndex);
              if (!string.IsNullOrEmpty(spell) && spell[spell.Length - 1] == '.')
              {
                spell = spell.Substring(0, spell.Length - 1);
                string attacker = string.Join(" ", split, 0, hitType);
                string defender = string.Join(" ", split, hitType + 1, forIndex - hitType - 1);
                string type = GetTypeFromSpell(spell, Labels.DD);
                uint damage = StatsUtil.ParseUInt(split[pointsOfIndex - 1]);
                SpellResist resist = SpellResist.UNDEFINED;
                SpellResistMap.TryGetValue(split[byDamage - 1], out resist);

                // extra way to check for pets
                if (spell.StartsWith("Elemental Conversion", StringComparison.Ordinal))
                {
                  PlayerManager.Instance.AddVerifiedPet(defender);
                }

                handled = CreateDamageRecord(lineData, split, stop, attacker, defender, damage, type, spell, resist);
              }
            }
            // [Sun Apr 18 20:32:39 2021] Dovhesi has taken 173674 damage from Curse of the Shrine by Grendish the Crusader.
            // [Sun Apr 18 20:32:42 2021] Grendish the Crusader has taken 1003231 damage from Pyre of Klraggek Rk. III by Atvar. (Lucky Critical)
            // [Thu Mar 18 18:48:10 2021] You have taken 4852 damage from Nectar of Misery by Commander Gartik.
            // [Thu Mar 18 01:05:46 2021] A gnoll has taken 108790 damage from your Mind Coil Rk. II.
            // Old (eqemu) [Sat Jan 15 21:09:10 2022] Pixtt Invi Mal has taken 189 damage from Goanna by Tuyen`s Chant of Fire.
            else if (fromDamage > 3 && takenIndex == (fromDamage - 3) && (byIndex > fromDamage || yourIndex > fromDamage))
            {
              string attacker = null;
              string spell = null;
              if (byIndex > -1)
              {
                attacker = string.Join(" ", split, byIndex + 1, stop - byIndex);
                attacker = (!string.IsNullOrEmpty(attacker) && attacker[attacker.Length - 1] == '.') ? attacker.Substring(0, attacker.Length - 1) : null;
                spell = string.Join(" ", split, fromDamage + 2, byIndex - fromDamage - 2);
              }
              else if (yourIndex > -1)
              {
                attacker = split[yourIndex];
                spell = string.Join(" ", split, yourIndex + 1, stop - yourIndex);
                spell = (!string.IsNullOrEmpty(spell) && spell[spell.Length - 1] == '.') ? spell.Substring(0, spell.Length - 1) : Labels.DOT;
              }

              if (!string.IsNullOrEmpty(attacker) && !string.IsNullOrEmpty(spell))
              {
                string type;
                SpellData spellData = DataManager.Instance.GetDamagingSpellByName(spell);

                // Old (eqemu) if attacker is actually a spell then swap attacker and spell
                // Spells dont change on eqemu servers so this should always be a spell even with old spell data
                if (spellData == null && DataManager.Instance.IsOldSpell(attacker))
                {
                  // check that we can't find a spell where the player name is
                  var temp = attacker;
                  attacker = spell;
                  spell = temp;
                  type = Labels.DOT;
                }
                else
                {
                  type = GetTypeFromSpell(spell, Labels.DOT);
                }

                string defender = string.Join(" ", split, 0, takenIndex);
                uint damage = StatsUtil.ParseUInt(split[fromDamage - 1]);
                SpellResist resist = spellData != null ? spellData.Resist : SpellResist.UNDEFINED;
                handled = CreateDamageRecord(lineData, split, stop, attacker, defender, damage, type, spell, resist);
              }
            }
            // [Mon Apr 26 21:07:21 2021] Lawlstryke has taken 216717 damage by Wisp Explosion.
            else if (byDamage > -1 && takenIndex == (byDamage - 3))
            {
              string defender = string.Join(" ", split, 0, takenIndex);
              uint damage = StatsUtil.ParseUInt(split[byDamage - 1]);
              string spell = string.Join(" ", split, byDamage + 2, stop - byDamage - 1);
              if (!string.IsNullOrEmpty(spell) && spell[spell.Length - 1] == '.')
              {
                spell = spell.Substring(0, spell.Length - 1);
              }

              SpellResist resist = SpellResist.UNDEFINED;
              if (DataManager.Instance.GetDamagingSpellByName(spell) is SpellData spellData && spellData != null)
              {
                resist = spellData.Resist;
              }

              handled = CreateDamageRecord(lineData, split, stop, "", defender, damage, Labels.DOT, spell, resist, true);
            }
            // Old (eqemu direct damage) [Sat Jan 15 21:08:54 2022] Jaun hit Pixtt Invi Mal for 150 points of non-melee damage.
            else if (hitType > -1 && forIndex > -1 && forIndex < pointsOfIndex && nonMeleeIndex > pointsOfIndex)
            {
              int hitTypeMod = hitTypeAdd > 0 ? 1 : 0;
              string attacker = string.Join(" ", split, 0, hitType);
              string defender = string.Join(" ", split, hitType + hitTypeMod + 1, forIndex - hitType - hitTypeMod - 1);
              uint damage = StatsUtil.ParseUInt(split[pointsOfIndex - 1]);
              handled = CreateDamageRecord(lineData, split, stop, attacker, defender, damage, Labels.DD, Labels.DD);
            }
            // [Fri Mar 04 21:28:19 2022] A failed reclaimer tries to punch YOU, but YOUR magical skin absorbs the blow!
            // [Mon Aug 05 02:05:12 2019] An enchanted Syldon stalker tries to crush YOU, but YOU parry!
            // [Mon Aug 05 02:05:12 2019] An enchanted Syldon stalker tries to crush YOU, but YOU riposte! (Strikethrough)
            // [Sun Jan 30 18:37:55 2022] Zelnithak tries to hit Fllint, but Fllint ripostes! (Strikethrough)
            // [Mon Aug 05 02:05:12 2019] An enchanted Syldon stalker tries to crush YOU, but misses! (Strikethrough)
            // [Sat Aug 03 00:20:57 2019] You try to crush a Kar`Zok soldier, but miss! (Riposte Strikethrough)
            // [Sat Apr 24 01:08:49 2021] Test One Hundred Three tries to punch Kazint, but misses!
            // [Sat Apr 24 01:08:49 2021] Test One Hundred Three tries to punch Kazint, but Kazint dodges!
            // [Sat Apr 24 01:10:17 2021] Test One Hundred Three tries to punch YOU, but YOU dodge!
            // [Sat Apr 24 01:10:17 2021] Kazint tries to crush Test One Hundred Three, but Test One Hundred Three dodges!
            // [Sun Apr 18 19:45:21 2021] You try to crush a primal guardian, but a primal guardian parries!
            // [Mon May 31 20:29:49 2021] A bloodthirsty gnawer tries to bite Vandil, but Vandil parries!
            // [Sun Apr 25 22:56:22 2021] Romance tries to bash Vulak`Aerr, but Vulak`Aerr parries!
            // [Sun Jul 28 20:12:46 2019] Drogbaa tries to slash Whirlrender Scout, but misses! (Strikethrough)
            // [Tue Mar 30 16:43:54 2021] You try to crush a desert madman, but a desert madman blocks!
            // [Mon Apr 26 22:40:10 2021] An ancient warden tries to hit Reisil, but Reisil blocks with his shield!
            // [Sun Mar 21 00:11:31 2021] A carrion bat tries to bite YOU, but YOU block with your shield!
            // [Mon Apr 26 14:51:01 2021] A windchill sprite tries to smash YOU, but YOU block with your staff!
            // [Mon May 10 22:18:46 2021] Tolzol tries to crush Dendritic Golem, but Dendritic Golem is INVULNERABLE!
            else if (tryIndex > -1 && butIndex > tryIndex && missType > -1)
            {
              string label = null;
              switch (missType)
              {
                case 0:
                  label = Labels.BLOCK;
                  break;
                case 1:
                  label = Labels.DODGE;
                  break;
                case 2:
                  label = Labels.MISS;
                  break;
                case 3:
                  label = Labels.PARRY;
                  break;
                case 4:
                  label = Labels.INVULNERABLE;
                  break;
                case 5:
                  label = Labels.RIPOSTE;
                  break;
                case 6:
                  label = Labels.ABSORB;
                  break;
              }

              if (!string.IsNullOrEmpty(label))
              {
                int hitTypeMod = hitTypeAdd > 0 ? 1 : 0;
                string defender = string.Join(" ", split, hitType + hitTypeMod + 1, butIndex - hitType - hitTypeMod - 1);
                if (!string.IsNullOrEmpty(defender) && defender[defender.Length - 1] == ',')
                {
                  defender = defender.Substring(0, defender.Length - 1);
                  string attacker = string.Join(" ", split, 0, tryIndex);
                  subType = TextFormatUtils.ToUpper(subType);
                  handled = CreateDamageRecord(lineData, split, stop, attacker, defender, 0, label, subType);
                }
              }
            }
            // [Sun Apr 18 21:26:20 2021] Strangle`s pet has been slain by Kzerk!
            else if (slainIndex > -1 && byIndex == (slainIndex + 1) && hasIndex > 0 && stop > (slainIndex + 1) && split[hasIndex + 1] == "been")
            {
              string killer = string.Join(" ", split, byIndex + 1, stop - byIndex);
              killer = killer.Length > 1 && killer[killer.Length - 1] == '!' ? killer.Substring(0, killer.Length - 1) : killer;
              string slain = string.Join(" ", split, 0, hasIndex);
              handled = UpdateSlain(slain, killer, lineData);
              HasOwner(slain, out string t1);
              HasOwner(killer, out string t2);
            }
            // [Mon Apr 19 02:22:09 2021] You have been slain by an armed flyer!
            else if (stop > 4 && slainIndex == 3 && byIndex == 4 && split[0] == "You" && split[1] == "have" && split[2] == "been")
            {
              string killer = string.Join(" ", split, 5, stop - 4);
              killer = killer.Length > 1 && killer[killer.Length - 1] == '!' ? killer.Substring(0, killer.Length - 1) : killer;
              string slain = ConfigUtil.PlayerName;
              handled = UpdateSlain(slain, killer, lineData);
            }
            // [Mon Apr 19 02:22:09 2021] You have slain a failed bodyguard!
            else if (slainIndex == 2 && split[0] == "You" && split[1] == "have")
            {
              string killer = ConfigUtil.PlayerName;
              string slain = string.Join(" ", split, 3, stop - 2);
              slain = slain.Length > 1 && slain[slain.Length - 1] == '!' ? slain.Substring(0, slain.Length - 1) : slain;
              handled = UpdateSlain(slain, killer, lineData);
            }
          }
        }
      }
      catch (Exception e)
      {
        LOG.Error(e);
      }
    }

    private static bool UpdateSlain(string slain, string killer, LineData lineData)
    {
      bool handled = false;
      if (!string.IsNullOrEmpty(slain) && killer != null && !InIgnoreList(slain)) // killer may not be known so empty string is OK
      {
        killer = killer.Length > 2 ? PlayerManager.Instance.ReplacePlayer(killer, killer) : killer;
        slain = PlayerManager.Instance.ReplacePlayer(slain, slain);

        // clear your ADPS if you died
        if (slain == ConfigUtil.PlayerName)
        {
          DataManager.Instance.ClearActiveAdps();
        }

        double currentTime = lineData.BeginTime;
        if (!double.IsNaN(currentTime))
        {
          CheckSlainQueue(currentTime);

          lock (SlainQueue)
          {
            // we also use upper case now
            slain = TextFormatUtils.ToUpper(slain);
            if (!SlainQueue.Contains(slain) && DataManager.Instance.GetFight(slain) != null)
            {
              SlainQueue.Add(slain);
              SlainTime = currentTime;
            }
          }

          var death = new DeathRecord() { Killed = string.Intern(slain), Killer = killer };
          DataManager.Instance.AddDeathRecord(death, currentTime);
          handled = true;
        }
      }
      return handled;
    }

    private static bool CreateDamageRecord(LineData lineData, string[] split, int stop, string attacker, string defender,
      uint damage, string type, string subType, SpellResist resist = SpellResist.UNDEFINED, bool attackerIsSpell = false)
    {
      bool success = false;

      if (damage != uint.MaxValue && !string.IsNullOrEmpty(type) && !string.IsNullOrEmpty(subType) && !InIgnoreList(defender))
      {
        // Needed to replace 'You' and 'you', etc
        defender = PlayerManager.Instance.ReplacePlayer(defender, defender);

        if (string.IsNullOrEmpty(attacker))
        {
          attacker = subType;
        }
        else if (attacker.EndsWith("'s corpse", StringComparison.Ordinal))
        {
          attacker = attacker.Substring(0, attacker.Length - 9);
        }
        else
        {
          // Needed to replace 'You' and 'you', etc
          attacker = PlayerManager.Instance.ReplacePlayer(attacker, attacker);
        }

        if (resist != SpellResist.UNDEFINED && ConfigUtil.PlayerName == attacker && defender != attacker)
        {
          DataManager.Instance.UpdateNpcSpellResistStats(defender, resist);
        }

        var currentTime = lineData.BeginTime;

        int modifiersMask = -1;
        if (split.Length > stop + 1)
        {
          // improve this later so maybe the string doesn't have to be re-joined
          string modifiers = string.Join(" ", split, stop + 1, split.Length - stop - 1);
          modifiersMask = LineModifiersParser.Parse(attacker, modifiers.Substring(1, modifiers.Length - 2), currentTime);
        }

        // check for pets
        HasOwner(attacker, out string attackerOwner);
        HasOwner(defender, out string defenderOwner);

        DamageRecord record = new DamageRecord
        {
          Attacker = string.Intern(FixName(attacker)),
          Defender = string.Intern(FixName(defender)),
          Type = string.Intern(type),
          SubType = string.Intern(subType),
          Total = damage,
          AttackerOwner = attackerOwner != null ? string.Intern(attackerOwner) : null,
          DefenderOwner = defenderOwner != null ? string.Intern(defenderOwner) : null,
          ModifiersMask = modifiersMask,
          AttackerIsSpell = attackerIsSpell
        };

        if (!double.IsNaN(currentTime))
        {
          // handle old style crits for eqemu
          if (LastCrit != null && LastCrit.Attacker == record.Attacker && LastCrit.LineData.LineNumber == (lineData.LineNumber - 1))
          {
            if (!double.IsNaN(LastCrit.LineData.BeginTime) && (currentTime - LastCrit.LineData.BeginTime) <= 1)
            {
              record.ModifiersMask = (record.ModifiersMask == -1) ? LineModifiersParser.CRIT : record.ModifiersMask | LineModifiersParser.CRIT;
            }

            LastCrit = null;
          }

          CheckSlainQueue(currentTime);

          DamageProcessedEvent e = new DamageProcessedEvent { Record = record, BeginTime = currentTime };
          EventsDamageProcessed?.Invoke(record, e);

          if (record.Type == Labels.DD && SpecialCodes.Keys.FirstOrDefault(special => !string.IsNullOrEmpty(record.SubType) &&
          record.SubType.Contains(special)) is string key && !string.IsNullOrEmpty(key))
          {
            DataManager.Instance.AddSpecial(new SpecialSpell { Code = SpecialCodes[key], Player = record.Attacker, BeginTime = currentTime });
          }
          success = true;
        }
      }

      return success;
    }

    private static bool HasOwner(string name, out string owner)
    {
      bool hasOwner = false;
      owner = null;

      if (!string.IsNullOrEmpty(name))
      {
        int pIndex = name.IndexOf("`s ", StringComparison.Ordinal);
        if ((pIndex > -1 && IsPetOrMount(name, pIndex + 3, out _)) || (pIndex = name.LastIndexOf(" pet", StringComparison.Ordinal)) > -1)
        {
          var verifiedPet = PlayerManager.Instance.IsVerifiedPet(name);
          if (verifiedPet || PlayerManager.Instance.IsPossiblePlayerName(name, pIndex))
          {
            owner = name.Substring(0, pIndex);
            hasOwner = true;

            if (!verifiedPet && PlayerManager.Instance.IsVerifiedPlayer(owner))
            {
              PlayerManager.Instance.AddVerifiedPet(name);
            }
          }
        }
      }

      return hasOwner;
    }

    private static bool IsPetOrMount(string part, int start, out int len)
    {
      bool found = false;
      len = -1;

      int end = 2;
      if (part.Length >= (start + ++end) && part.Substring(start, 3) == "pet" ||
        part.Length >= (start + ++end) && part.Substring(start, 4) == "ward" && !(part.Length > (start + 5) && part[start + 5] != 'e') ||
        part.Length >= (start + ++end) && part.Substring(start, 5) == "Mount" ||
        part.Length >= (start + ++end) && (part.Substring(start, 6) == "warder" || part.Substring(start, 6) == "Warder"))
      {
        found = true;
        len = end;
      }
      return found;
    }

    private static string FixName(string name)
    {
      // changed to always be upper so merc/npc names match for spell counts and other things easier
      return TextFormatUtils.ToUpper(name);
    }

    private static string GetTypeFromSpell(string name, string type)
    {
      string key = Helpers.CreateRecordKey(type, name);
      if (string.IsNullOrEmpty(key) || !SpellTypeCache.TryGetValue(key, out string result))
      {
        result = type;
        if (!string.IsNullOrEmpty(key))
        {
          string spellName = DataManager.Instance.AbbreviateSpellName(name);
          SpellData data = DataManager.Instance.GetSpellByAbbrv(spellName);
          if (data != null)
          {
            switch (data.Proc)
            {
              case 1:
                result = Labels.PROC;
                break;
              case 2:
                result = Labels.BANE;
                break;
            }
          }
        }

        SpellTypeCache[key] = result;
      }
      return result;
    }

    private static bool InIgnoreList(string name)
    {
      bool ignore = name.EndsWith("`s Mount", StringComparison.OrdinalIgnoreCase) || ChestTypes.FindIndex(type => name.EndsWith(type, StringComparison.OrdinalIgnoreCase)) >= 0;
      if (!ignore && CheckEyeRegex.IsMatch(name))
      {
        ignore = !name.EndsWith("Veeshan") && !name.EndsWith("Despair");
      }
      return ignore;
    }

    private class OldCritData
    {
      internal string Attacker { get; set; }
      internal LineData LineData { get; set; }
    }
  }
}
