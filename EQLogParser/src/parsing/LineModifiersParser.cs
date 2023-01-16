﻿using System.Collections.Concurrent;
using System.Collections.Generic;

namespace EQLogParser
{
  class LineModifiersParser
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    private static readonly Dictionary<string, byte> ALL_MODIFIERS = new Dictionary<string, byte>()
    {
      { "Assassinate", 1 }, { "Crippling Blow", 1 }, { "Critical", 1 }, { "Deadly Strike", 1 }, { "Double Bow Shot", 1 }, { "Finishing Blow", 1 },
      { "Flurry", 1 }, { "Headshot", 1 }, { "Lucky", 1 }, { "Rampage", 1 }, { "Riposte", 1 }, { "Slay Undead", 1 }, { "Strikethrough", 1 },
      { "Twincast", 1 }, { "Wild Rampage", 1 },
    };

    private static readonly Dictionary<string, byte> CRIT_MODIFIERS = new Dictionary<string, byte>()
    {
      { "Crippling Blow", 1 }, { "Critical", 1 }, { "Deadly Strike", 1 }, { "Finishing Blow", 1}
    };

    public const int CRIT = 2;
    private const int TWINCAST = 1;
    private const int LUCKY = 4;
    private const int RAMPAGE = 8;
    private const int STRIKETHROUGH = 16;
    private const int RIPOSTE = 32;
    private const int ASSASSINATE = 64;
    private const int HEADSHOT = 128;
    private const int SLAY = 256;
    private const int DOUBLEBOW = 512;
    private const int FLURRY = 1024;
    private const int FINISHING = 2048;

    private static readonly ConcurrentDictionary<string, int> MaskCache = new ConcurrentDictionary<string, int>();

    internal static bool IsAssassinate(int mask) => mask > -1 && (mask & ASSASSINATE) != 0;

    internal static bool IsCrit(int mask) => mask > -1 && (mask & CRIT) != 0;

    internal static bool IsFinishingBlow(int mask) => mask > -1 && (mask & FINISHING) != 0;

    internal static bool IsFlurry(int mask) => mask > -1 && (mask & FLURRY) != 0;

    internal static bool IsHeadshot(int mask) => mask > -1 && (mask & HEADSHOT) != 0;

    internal static bool IsLucky(int mask) => mask > -1 && (mask & LUCKY) != 0;

    internal static bool IsTwincast(int mask) => mask > -1 && (mask & TWINCAST) != 0;

    internal static bool IsSlayUndead(int mask) => mask > -1 && (mask & SLAY) != 0;

    internal static bool IsRampage(int mask) => mask > -1 && (mask & RAMPAGE) != 0;

    internal static bool IsRiposte(int mask) => mask > -1 && (mask & RIPOSTE) != 0 && (mask & STRIKETHROUGH) == 0;

    internal static bool IsStrikethrough(int mask) => mask > -1 && (mask & STRIKETHROUGH) != 0;

    internal static void UpdateStats(HitRecord record, Attempt playerStats, Attempt theHit = null)
    {
      if (record.ModifiersMask > -1 && record.Type != Labels.MISS)
      {
        if ((record.ModifiersMask & ASSASSINATE) != 0)
        {
          playerStats.AssHits++;
          playerStats.TotalAss += record.Total;

          if (theHit != null)
          {
            theHit.AssHits++;
          }
        }

        if ((record.ModifiersMask & DOUBLEBOW) != 0)
        {
          playerStats.DoubleBowHits++;

          if (theHit != null)
          {
            theHit.DoubleBowHits++;
          }
        }

        if ((record.ModifiersMask & FLURRY) != 0)
        {
          playerStats.FlurryHits++;

          if (theHit != null)
          {
            theHit.FlurryHits++;
          }
        }

        if ((record.ModifiersMask & HEADSHOT) != 0)
        {
          playerStats.HeadHits++;
          playerStats.TotalHead += record.Total;

          if (theHit != null)
          {
            theHit.HeadHits++;
          }
        }

        if ((record.ModifiersMask & FINISHING) != 0)
        {
          playerStats.FinishingHits++;
          playerStats.TotalFinishing += record.Total;

          if (theHit != null)
          {
            theHit.FinishingHits++;
          }
        }

        if ((record.ModifiersMask & TWINCAST) != 0)
        {
          playerStats.TwincastHits++;

          if (theHit != null)
          {
            theHit.TwincastHits++;
          }
        }
        else
        {
          playerStats.TotalNonTwincast += record.Total;
        }

        if ((record.ModifiersMask & RAMPAGE) != 0)
        {
          playerStats.RampageHits++;

          if (theHit != null)
          {
            theHit.RampageHits++;
          }
        }

        // A Strikethrough Riposte is the attacker attacking through a riposte from the defender
        if (IsRiposte(record.ModifiersMask))
        {
          playerStats.RiposteHits++;
          playerStats.TotalRiposte += record.Total;

          if (theHit != null)
          {
            theHit.RiposteHits++;
          }
        }

        if (IsStrikethrough(record.ModifiersMask))
        {
          playerStats.StrikethroughHits++;

          if (theHit != null)
          {
            theHit.StrikethroughHits++;
          }
        }

        if ((record.ModifiersMask & SLAY) != 0)
        {
          playerStats.SlayHits++;
          playerStats.TotalSlay += record.Total;

          if (theHit != null)
          {
            theHit.SlayHits++;
          }
        }

        if ((record.ModifiersMask & CRIT) != 0)
        {
          playerStats.CritHits++;

          if (theHit != null)
          {
            theHit.CritHits++;
          }

          if ((record.ModifiersMask & LUCKY) == 0)
          {
            playerStats.TotalCrit += record.Total;

            if (theHit != null)
            {
              theHit.TotalCrit += record.Total;
            }

            if ((record.ModifiersMask & TWINCAST) == 0)
            {
              playerStats.NonTwincastCritHits++;
              playerStats.TotalNonTwincastCrit += record.Total;

              if (theHit != null)
              {
                theHit.NonTwincastCritHits++;
                theHit.TotalNonTwincastCrit += record.Total;
              }
            }
          }
        }

        if ((record.ModifiersMask & LUCKY) != 0)
        {
          playerStats.LuckyHits++;
          playerStats.TotalLucky += record.Total;

          if (theHit != null)
          {
            theHit.LuckyHits++;
            theHit.TotalLucky += record.Total;
          }

          if ((record.ModifiersMask & TWINCAST) == 0)
          {
            playerStats.NonTwincastLuckyHits++;
            playerStats.TotalNonTwincastLucky += record.Total;

            if (theHit != null)
            {
              theHit.NonTwincastLuckyHits++;
              theHit.TotalNonTwincastLucky += record.Total;
            }
          }
        }
      }
    }

    internal static int Parse(string player, string modifiers, double currentTime)
    {
      int result = -1;

      if (!string.IsNullOrEmpty(modifiers))
      {
        if (!MaskCache.TryGetValue(modifiers, out result))
        {
          result = BuildVector(player, modifiers, currentTime);
          MaskCache[modifiers] = result;
        }
      }

      return result;
    }

    private static int BuildVector(string player, string modifiers, double currentTime)
    {
      int result = 0;

      bool lucky = false;
      bool critical = false;

      string temp = "";
      foreach (string modifier in modifiers.Split(' '))
      {
        temp += modifier;
        if (ALL_MODIFIERS.ContainsKey(temp))
        {
          if (!critical && CRIT_MODIFIERS.ContainsKey(temp))
          {
            result |= CRIT;
          }

          if (!lucky && "Lucky" == temp)
          {
            result |= LUCKY;
          }

          switch (temp)
          {
            case "Assassinate":
              result |= ASSASSINATE;
              PlayerManager.Instance.AddVerifiedPlayer(player, currentTime);
              PlayerManager.Instance.SetPlayerClass(player, SpellClass.ROG);
              break;
            case "Double Bow Shot":
              result |= DOUBLEBOW;
              PlayerManager.Instance.AddVerifiedPlayer(player, currentTime);
              PlayerManager.Instance.SetPlayerClass(player, SpellClass.RNG);
              break;
            case "Finishing Blow":
              result |= FINISHING;
              break;
            case "Flurry":
              result |= FLURRY;
              break;
            case "Headshot":
              result |= HEADSHOT;
              PlayerManager.Instance.AddVerifiedPlayer(player, currentTime);
              PlayerManager.Instance.SetPlayerClass(player, SpellClass.RNG);
              break;
            case "Twincast":
              result |= TWINCAST;
              break;
            case "Rampage":
            case "Wild Rampage":
              result |= RAMPAGE;
              break;
            case "Riposte":
              result |= RIPOSTE;
              break;
            case "Strikethrough":
              result |= STRIKETHROUGH;
              break;
            case "Slay Undead":
              result |= SLAY;
              PlayerManager.Instance.AddVerifiedPlayer(player, currentTime);
              PlayerManager.Instance.SetPlayerClass(player, SpellClass.PAL);
              break;
            case "Locked":
              // do nothing for now
              break;
          }

          temp = ""; // reset
        }
        else
        {
          temp += " ";
        }
      }

      if (!string.IsNullOrEmpty(temp))
      {
        LOG.Debug("Unknown Modifiers: " + modifiers);
      }

      return result;
    }
  }
}
