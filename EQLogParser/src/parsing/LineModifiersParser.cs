using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace EQLogParser
{
  internal static class LineModifiersParser
  {
    private static readonly Dictionary<string, byte> AllModifiers = new()
    {
      { "Assassinate", 1 }, { "Crippling Blow", 1 }, { "Critical", 1 }, { "Deadly Strike", 1 }, { "Double Bow Shot", 1 }, { "Finishing Blow", 1 },
      { "Flurry", 1 }, { "Headshot", 1 }, { "Lucky", 1 }, { "Rampage", 1 }, { "Riposte", 1 }, { "Slay Undead", 1 }, { "Strikethrough", 1 },
      { "Twincast", 1 }, { "Wild Rampage", 1 },
    };

    private static readonly Dictionary<string, byte> CritModifiers = new()
    {
      { "Crippling Blow", 1 }, { "Critical", 1 }, { "Deadly Strike", 1 }, { "Finishing Blow", 1}
    };

    public const short None = -1;
    public const short Crit = 2;
    private const short Twincast = 1;
    private const short Lucky = 4;
    private const short Rampage = 8;
    private const short Strikethrough = 16;
    private const short Riposte = 32;
    private const short Assassinate = 64;
    private const short Headshot = 128;
    private const short Slay = 256;
    private const short Doublebow = 512;
    private const short Flurry = 1024;
    private const short Finishing = 2048;

    private static readonly ConcurrentDictionary<string, short> MaskCache = new();

    internal static bool IsAssassinate(int mask) => mask > -1 && (mask & Assassinate) != 0;
    internal static bool IsCrit(int mask) => mask > -1 && (mask & Crit) != 0;
    internal static bool IsDoubleBowShot(int mask) => mask > -1 && (mask & Doublebow) != 0;
    internal static bool IsFinishingBlow(int mask) => mask > -1 && (mask & Finishing) != 0;
    internal static bool IsFlurry(int mask) => mask > -1 && (mask & Flurry) != 0;
    internal static bool IsHeadshot(int mask) => mask > -1 && (mask & Headshot) != 0;
    internal static bool IsLucky(int mask) => mask > -1 && (mask & Lucky) != 0;
    internal static bool IsTwincast(int mask) => mask > -1 && (mask & Twincast) != 0;
    internal static bool IsSlayUndead(int mask) => mask > -1 && (mask & Slay) != 0;
    internal static bool IsRampage(int mask) => mask > -1 && (mask & Rampage) != 0;
    internal static bool IsRiposte(int mask) => mask > -1 && (mask & Riposte) != 0 && (mask & Strikethrough) == 0;
    internal static bool IsStrikethrough(int mask) => mask > -1 && (mask & Strikethrough) != 0;

    internal static void UpdateStats(HitRecord record, Attempt playerStats, Attempt theHit = null)
    {
      if (record.ModifiersMask > -1 && record.Type != Labels.Miss)
      {
        if ((record.ModifiersMask & Assassinate) != 0)
        {
          playerStats.AssHits++;
          playerStats.TotalAss += record.Total;

          if (theHit != null)
          {
            theHit.AssHits++;
          }
        }

        if ((record.ModifiersMask & Doublebow) != 0)
        {
          playerStats.DoubleBowHits++;

          if (theHit != null)
          {
            theHit.DoubleBowHits++;
          }
        }

        if ((record.ModifiersMask & Flurry) != 0)
        {
          playerStats.FlurryHits++;

          if (theHit != null)
          {
            theHit.FlurryHits++;
          }
        }

        if ((record.ModifiersMask & Headshot) != 0)
        {
          playerStats.HeadHits++;
          playerStats.TotalHead += record.Total;

          if (theHit != null)
          {
            theHit.HeadHits++;
          }
        }

        if ((record.ModifiersMask & Finishing) != 0)
        {
          playerStats.FinishingHits++;
          playerStats.TotalFinishing += record.Total;

          if (theHit != null)
          {
            theHit.FinishingHits++;
          }
        }

        if ((record.ModifiersMask & Twincast) != 0)
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

        if ((record.ModifiersMask & Rampage) != 0)
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

        if ((record.ModifiersMask & Slay) != 0)
        {
          playerStats.SlayHits++;
          playerStats.TotalSlay += record.Total;

          if (theHit != null)
          {
            theHit.SlayHits++;
          }
        }

        if ((record.ModifiersMask & Crit) != 0)
        {
          playerStats.CritHits++;

          if (theHit != null)
          {
            theHit.CritHits++;
          }

          if ((record.ModifiersMask & Lucky) == 0)
          {
            playerStats.TotalCrit += record.Total;

            if (theHit != null)
            {
              theHit.TotalCrit += record.Total;
            }

            if ((record.ModifiersMask & Twincast) == 0)
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

        if ((record.ModifiersMask & Lucky) != 0)
        {
          playerStats.LuckyHits++;
          playerStats.TotalLucky += record.Total;

          if (theHit != null)
          {
            theHit.LuckyHits++;
            theHit.TotalLucky += record.Total;
          }

          if ((record.ModifiersMask & Twincast) == 0)
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

    internal static short ParseDamage(string player, string modifiers, double currentTime, bool isPlayer)
    {
      var result = Parse(modifiers);

      if (isPlayer)
      {
        string classAbility = null;
        string className = null;
        if (IsAssassinate(result))
        {
          PlayerManager.Instance.AddVerifiedPlayer(player, currentTime);
          classAbility = "Assassinate";
          className = Resource.ROG;
        }
        else if (IsHeadshot(result) || IsDoubleBowShot(result))
        {
          PlayerManager.Instance.AddVerifiedPlayer(player, currentTime);
          classAbility = IsHeadshot(result) ? "Headshot" : "Double Bow Shot";
          className = Resource.RNG;
        }
        else if (IsSlayUndead(result))
        {
          PlayerManager.Instance.AddVerifiedPlayer(player, currentTime);
          classAbility = "Slay Undead";
          className = Resource.PAL;
        }

        if (!string.IsNullOrEmpty(classAbility) && !string.IsNullOrEmpty(className))
        {
          PlayerManager.Instance.SetActivePlayerClass(player, className, 1, currentTime);
        }
      }

      return result;
    }

    internal static short ParseHeal(string player, string modifiers, double currentTime)
    {
      var result = Parse(modifiers);

      if (IsTwincast(result))
      {
        PlayerManager.Instance.AddVerifiedPlayer(player, currentTime);
      }

      return result;
    }

    private static short Parse(string modifiers)
    {
      short result = -1;
      if (!string.IsNullOrEmpty(modifiers))
      {
        if (!MaskCache.TryGetValue(modifiers, out result))
        {
          result = BuildVector(modifiers);
          MaskCache[modifiers] = result;
        }
      }

      return result;
    }

    internal static short BuildVector(string modifiers)
    {
      short result = 0;

      const int bufferSize = 64;
      var buffer = ArrayPool<char>.Shared.Rent(bufferSize);
      var spanPos = 0;

      var span = modifiers.AsSpan();
      var start = 0;
      for (var i = 0; i <= span.Length; i++)
      {
        if (i == span.Length || span[i] == ' ')
        {
          var wordLen = i - start;
          if (wordLen > 0)
          {
            if (spanPos + wordLen >= buffer.Length)
            {
              var newBuffer = ArrayPool<char>.Shared.Rent(Math.Max(buffer.Length * 2, spanPos + wordLen + 16));
              Buffer.BlockCopy(buffer, 0, newBuffer, 0, spanPos * sizeof(char));
              ArrayPool<char>.Shared.Return(buffer);
              buffer = newBuffer;
            }

            span.Slice(start, wordLen).CopyTo(buffer.AsSpan(spanPos));
            spanPos += wordLen;

            if (AllModifiers.ContainsKey(buffer.AsSpan(0, spanPos).ToString()))
            {
              var key = buffer.AsSpan(0, spanPos).ToString();
              if (CritModifiers.ContainsKey(key))
              {
                result |= Crit;
              }

              switch (key)
              {
                case "Lucky":
                  result |= Lucky;
                  break;
                case "Assassinate":
                  result |= Assassinate;
                  break;
                case "Double Bow Shot":
                  result |= Doublebow;
                  break;
                case "Finishing Blow":
                  result |= Finishing;
                  break;
                case "Flurry":
                  result |= Flurry;
                  break;
                case "Headshot":
                  result |= Headshot;
                  break;
                case "Twincast":
                  result |= Twincast;
                  break;
                case "Rampage":
                case "Wild Rampage":
                  result |= Rampage;
                  break;
                case "Riposte":
                  result |= Riposte;
                  break;
                case "Strikethrough":
                  result |= Strikethrough;
                  break;
                case "Slay Undead":
                  result |= Slay;
                  break;
                case "Locked":
                  break;
              }

              spanPos = 0;
            }
            else
            {
              if (spanPos >= buffer.Length)
              {
                var newBuffer = ArrayPool<char>.Shared.Rent(buffer.Length * 2);
                Buffer.BlockCopy(buffer, 0, newBuffer, 0, spanPos * sizeof(char));
                ArrayPool<char>.Shared.Return(buffer);
                buffer = newBuffer;
              }
              buffer[spanPos++] = ' ';
            }
          }

          start = i + 1;
        }
      }

      ArrayPool<char>.Shared.Return(buffer);
      return result;
    }
  }
}
