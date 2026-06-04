using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /// <summary>
  /// Shared parsing utilities used by both DamageLineParser and SimpleDamageLineParser.
  /// Designed to be usable in a future standalone parsing project with minimal dependencies.
  /// </summary>
  internal static class ParserUtil
  {
    /// <summary>
    /// Second-person pronouns that should be replaced with the player name.
    /// </summary>
    private static readonly HashSet<string> SecondPerson = new(StringComparer.OrdinalIgnoreCase)
    {
      "you", "yourself", "your"
    };

    /// <summary>
    /// Third-person pronouns that should be replaced with the attacker name.
    /// </summary>
    private static readonly HashSet<string> ThirdPerson = new(StringComparer.OrdinalIgnoreCase)
    {
      "himself", "herself", "itself"
    };

    /// <summary>
    /// Replaces "You"/"Your" with player name, or third-person names with attacker.
    /// Extracted from PlayerRegistry.ReplacePlayer for use in standalone parsing.
    /// </summary>
    internal static string ReplacePlayer(string name, string playerName, string attacker)
    {
      if (string.IsNullOrEmpty(name))
      {
        return name;
      }

      if (ThirdPerson.Contains(name))
      {
        return !string.IsNullOrEmpty(attacker) && attacker != Labels.Rs && attacker != Labels.Unk
          ? attacker
          : name;
      }

      if (SecondPerson.Contains(name))
      {
        return playerName;
      }

      return name;
    }

    /// <summary>
    /// Updates the attacker name: handles corpse suffixes and player name replacement.
    /// </summary>
    /// <param name="attacker">The current attacker name</param>
    /// <param name="playerName">The actual player name (for replacing "You"/"Your")</param>
    /// <param name="subType">The fallback attacker name if attacker is null/empty</param>
    /// <returns>The updated attacker name in uppercase</returns>
    internal static string UpdateAttacker(string attacker, string playerName, string subType)
    {
      if (string.IsNullOrEmpty(attacker))
      {
        attacker = subType;
      }
      else if (attacker.EndsWith("'s corpse", StringComparison.Ordinal) || attacker.EndsWith("`s corpse", StringComparison.Ordinal))
      {
        attacker = attacker[..^9];
      }
      else if (!string.IsNullOrEmpty(playerName))
      {
        attacker = ReplacePlayer(attacker, playerName, attacker);
      }

      return TextUtils.CapitalizeFirst(attacker);
    }

    /// <summary>
    /// Updates the defender name: replaces "You"/"Your" with player name, or third-person with attacker.
    /// </summary>
    /// <param name="defender">The current defender name</param>
    /// <param name="attacker">The attacker name (used to replace third-person names)</param>
    /// <returns>The updated defender name in uppercase</returns>
    internal static string UpdateDefender(string defender, string attacker)
    {
      defender = ReplacePlayer(defender, ConfigUtil.PlayerName, attacker);
      return TextUtils.CapitalizeFirst(defender);
    }

    /// <summary>
    /// Finds the stop index by stripping trailing parentheses (e.g., from modifiers).
    /// </summary>
    internal static int FindStop(string[] split)
    {
      var stop = split.Length - 1;
      if (!string.IsNullOrEmpty(split[stop]) && split[stop][^1] == ')')
      {
        for (var i = stop; i >= 0 && stop > 2; i--)
        {
          if (!string.IsNullOrEmpty(split[i]) && split[i][0] == '(')
          {
            stop = i - 1;
            break;
          }
        }
      }
      return stop;
    }

    /// <summary>
    /// Joins a range of words from a split string array with a single space.
    /// Replaces the common pattern: string.Join(" ", split, start, count)
    /// Uses stackalloc for short results (≤256 chars) to reduce heap allocations.
    /// </summary>
    internal static unsafe string JoinWords(string[] split, int start, int count)
    {
      if (count <= 0) return string.Empty;
      if (count == 1)
      {
        var word = split[start];
        return word ?? string.Empty;
      }

      // Calculate total length needed
      var totalLength = 0;
      var wordCount = 0;
      for (var i = 0; i < count; i++)
      {
        var word = split[start + i];
        if (word != null && word.Length > 0)
        {
          totalLength += word.Length;
          wordCount++;
        }
      }

      // Add spaces between words
      totalLength += wordCount > 1 ? wordCount - 1 : 0;

      // For short results, use stackalloc to avoid heap allocation
      if (totalLength <= 256)
      {
        var buffer = stackalloc char[totalLength];
        var pos = 0;
        var first = true;
        for (var i = 0; i < count; i++)
        {
          var word = split[start + i];
          if (word != null && word.Length > 0)
          {
            if (!first)
            {
              buffer[pos++] = ' ';
            }
            foreach (var c in word)
            {
              buffer[pos++] = c;
            }
            first = false;
          }
        }
        return new string(buffer, 0, pos);
      }

      // For longer results, use string.Join
      return string.Join(" ", split, start, count);
    }

    /// <summary>
    /// Parses an unsigned integer from a string array element.
    /// Replaces the common pattern: TextUtils.ParseUInt(split[index])
    /// </summary>
    internal static uint ParseUInt(string[] split, int index)
    {
      return TextUtils.ParseUInt(split[index]);
    }

    /// <summary>
    /// Looks up the hit type plural form for a given verb.
    /// Returns null if the word is not a known hit type.
    /// Examples: "bash" → "bashes", "crushes" → "crushes" (plural form)
    /// </summary>
    internal static string GetHitType(string word)
    {
      return word switch
      {
        "bash" => "bashes",
        "backstab" => "backstabs",
        "bite" => "bites",
        "claw" => "claws",
        "cleave" => "cleaves",
        "crush" => "crushes",
        "frenzy" => "frenzies",
        "gore" => "gores",
        "hit" => "hits",
        "kick" => "kicks",
        "learn" => "learns",
        "maul" => "mauls",
        "punch" => "punches",
        "pierce" => "pierces",
        "reave" => "reaves",
        "rend" => "rends",
        "shoot" => "shoots",
        "slash" => "slashes",
        "slam" => "slams",
        "slice" => "slices",
        "smash" => "smashes",
        "smite" => "smites",
        "stab" => "stabs",
        "sting" => "stings",
        "strike" => "strikes",
        "sweep" => "sweeps",
        "bashes" or "backstabs" or "bites" or "claws" or "cleaves" or "crushes" or "frenzies" or
        "gores" or "hits" or "kicks" or "learns" or "mauls" or "punches" or "pierces" or "reaves" or
        "rends" or "shoots" or "slashes" or "slams" or "slices" or "smashes" or "smites" or "stabs" or
        "stings" or "strikes" or "sweeps" => word,
        _ => null
      };
    }

    /// <summary>
    /// Checks if a word is a hit type addition (frenzy/frenzies).
    /// </summary>
    internal static bool IsHitTypeAddition(string word) =>
      word is "frenzy" or "frenzies";
  }
}
