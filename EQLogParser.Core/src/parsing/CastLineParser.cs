using log4net;
using System.Reflection;

namespace EQLogParser
{
  internal static class CastLineParser
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly char[] OldSpellChars = ['<', '>'];

    private static readonly Dictionary<string, string> SpecialCastCodes = new(StringComparer.OrdinalIgnoreCase)
    {
      { "Glyph of Ultimate Power", "G" }, { "Glyph of Destruction", "G" }, { "Glyph of Dragon", "D" },
      { "Intensity of the Resolute", "7" }, { "Staunch Recovery", "6" }, { "Glyph of Arcane Secrets", "S" }
    };

    private static readonly List<string> PetSpells =
    [
      "Companion Relocation", "Fortify Companion", "Bestial Bloodrage", "Frenzied Burnout", "Frenzy of the Dead", "Empowered Minion",
      "Companion's Aegis", "Second Wind Ward", "Zeal of the Elements"
    ];

    // Mirror evidence (D8): caster + resolved spell name per non-interrupted cast; R4 tier-1
    // identity comes from spell identity alone (curated epics / Spires, see EQDataStore seed).
    // Raised on the parsing lane; this is the Main-lane forwarder session subscribers use (see ParseLane).
    internal static event Action<string, string, double> EventsCastEvidence
    {
      add => ParseLane.Main.CastEvidence += value;
      remove => ParseLane.Main.CastEvidence -= value;
    }

    public static bool Process(LineData lineData)
    {
      try
      {
        var split = lineData.Words;
        if (split.Length > 1 && !split[0].Contains('.') && !split[^1].EndsWith(')') && !CheckLandsOnMessages(split, lineData.BeginTime))
        {
          string player = null;
          string spellName = null;
          var isCasting = false;
          var isInterrupted = false;
          var isYou = false;

          // [Sat Mar 14 19:57:48 2020] You activate Venon's Vindication.
          // [Mon Mar 02 19:46:09 2020] You begin casting Shield of Destiny Rk. II.
          // [Sat Mar 14 19:45:40 2020] You begin singing Agilmente's Aria of Eagles.
          // [Tue Dec 25 11:38:42 2018] You begin casting Focus of Arcanum VI.
          // [Sun Dec 02 16:33:37 2018] You begin singing Vainglorious Shout VI.
          // [Mon Mar 02 19:44:27 2020] Stabborz activates Conditioned Reflexes Rk. II.
          // [Mon Mar 02 19:47:43 2020] Sancus begins casting Burnout XIV Rk. II.
          // [Mon Mar 02 19:33:49 2020] Iggokk begins singing Shauri's Sonorous Clouding III.
          // [Tue Dec 25 09:58:20 2018] Sylfvia begins to cast a spell. <Syllable of Mending Rk. II>
          // [Tue Dec 25 14:19:57 2018] Sonozen begins to sing a song. <Lyre Leap>
          // [Thu Apr 18 01:38:10 2019] Incogitable's Dizzying Wheel Rk. II spell is interrupted.
          // [Thu Apr 18 01:38:00 2019] Your Stormjolt Vortex Rk. III spell is interrupted.
          // [Sun Mar 01 22:34:58 2020] You have entered The Eastern Wastes.
          if (split[0] == "You")
          {
            isYou = true;
            player = ConfigUtil.PlayerName;
            if (split[1] == "activate" && split.Length > 2)
            {
              spellName = ParserUtil.ParseSpellOrNpc(split, 2);
            }
            else if (split[1] == "begin" && split.Length > 3)
            {
              if (split[2] == "casting")
              {
                spellName = ParserUtil.ParseSpellOrNpc(split, 3);
                isCasting = true;
              }
              else if (split[2] == "singing")
              {
                spellName = ParserUtil.ParseSpellOrNpc(split, 3);
              }
            }
          }
          else if (split[1] == "activates")
          {
            player = split.StringAt(0);
            spellName = ParserUtil.ParseSpellOrNpc(split, 2);
          }
          else if (split.Length > 3 && FindBegins(split) is var bIndex and > -1 && (bIndex + 2) < split.Length)
          {
            if (split[bIndex + 1] == "casting")
            {
              player = split.JoinRaw(0, bIndex);
              spellName = ParserUtil.ParseSpellOrNpc(split, bIndex + 2);
              isCasting = true;
            }
            else if (split[bIndex + 1] == "singing")
            {
              player = split.JoinRaw(0, bIndex);
              spellName = ParserUtil.ParseSpellOrNpc(split, bIndex + 2);
            }
            else if (split.Length > 5 && split[2] == "to" && split[4] == "a")
            {
              if (split[3] == "cast" && split[5] == "spell.")
              {
                player = split.StringAt(0);
                spellName = ParseOldSpellName(split, 6);
                isCasting = true;
              }
              else if (split[3] == "sing" && split[5] == "song.")
              {
                player = split.StringAt(0);
                spellName = ParseOldSpellName(split, 6);
              }
            }
          }
          else if (split.Length > 4 && split[^1] == "interrupted." && split[^2] == "is" && split[^3] == "spell")
          {
            isInterrupted = true;
            spellName = split.JoinRaw(1, split.Length - 4);

            if (split[0] == "Your")
            {
              player = ConfigUtil.PlayerName;
            }
            else if (split[0].Length > 3 && split[0][^1] == 's' && split[0][^2] == '\'')
            {
              player = split.StringAt(0)[..^2];
            }
          }

          if (!string.IsNullOrEmpty(player) && !string.IsNullOrEmpty(spellName))
          {
            var currentTime = lineData.BeginTime;

            if (!isInterrupted)
            {
              string specialKey = null;

              if (isCasting)
              {
                // For some reason Glyphs don't show up for current player so this special case should limit the checks
                // and allow glyph to work
                if (CheckForSpecial(SpecialCastCodes, spellName, player, currentTime) is { } found && isYou)
                {
                  specialKey = found;
                }
              }

              var spellData = EQDataStore.Instance.GetSpellByName(spellName);

              if (spellData is not null)
              {
                spellData.SeenRecently = true;
              }
              else
              {
                // unknown spell
                spellData = EQDataStore.Instance.AddUnknownSpell(spellName);
              }

              var cast = new SpellCast { Caster = StringCache.GetOrAdd(player), Spell = StringCache.GetOrAdd(spellName), SpellData = spellData };
              RecordsStore.Instance.Add(cast, currentTime);

              // Mirror evidence (D8): fire-only, covers every non-interrupted cast verb form.
              ParseLane.Current.CastEvidence?.Invoke(player, spellName, currentTime);

              if (!spellData.IsUnknown && EQDataStore.Instance.GetSpellClass(spellData.Name) is { } theClass)
              {
                PlayerRegistry.Instance.SetActivePlayerClass(player, theClass, 2, currentTime);
              }

              if (specialKey is not null && spellData is not null)
              {
                AdpsTracker.Instance.UpdateAdps(spellData);
              }
            }
            else
            {
              foreach (var (beginTime, action) in RecordsStore.Instance.GetSpellsDuring(currentTime - 10, currentTime, true))
              {
                if (action is SpellCast sc && sc.Spell == spellName && sc.Caster == player)
                {
                  sc.Interrupted = true;
                  break;
                }
              }
            }

            return true;
          }
        }
      }
      catch (Exception e)
      {
        // Per-line path: one fault, said once with the line that caused it; repeats counted (RepeatCap).
        if (RepeatCap.SpeaksOnce($"cast.Process {e.GetType().Name}"))
        {
          Log.Error($"cast line parse failed (this failure repeats silently): {e} | line: {RepeatCap.Clip(lineData.Action)}");
        }
      }

      return false;
    }

    /*
     * Same walk the array version made, over the word view. The old period loop truncated a copy of the array;
     * the equivalent here is a BOUND handed to the spell-tree door — first word ending in '.' decides how far back
     * a lookup may read, and a non-final "Rk." means this is a rank line, not a lands-on line (returned false then,
     * false now). Traced equivalence of that old loop: it acted only on the FIRST '.'-ending word (after truncating,
     * its own index walked past the shortened length and it exited), which is exactly what the break below does.
     */
    private static bool CheckLandsOnMessages(LineWords split, double beginTime)
    {
      // ZONE EVENT - moved here to keep it in the same thread as lands on message parsing
      if (split.Length > 3 && split[1] == "have" && split[2] == "entered")
      {
        var zone = split.JoinWords(3, split.Length - 3).TrimEnd('.');
        RecordsStore.Instance.Add(new ZoneRecord { Zone = zone }, beginTime);
        if (!zone.StartsWith("an area", StringComparison.OrdinalIgnoreCase))
        {
          AdpsTracker.Instance.RemoveSongSpells();
          return true;
        }
      }

      // old logs sometimes had received messages on the same line as a heal
      // [Sun Aug 04 23:39:56 2019] You are generously healed. You healed Kizant for 35830 (500745) hit points by Staunch Recovery.
      var bound = split.Length - 1;
      for (var i = 0; i < split.Length; i++)
      {
        if (split[i].EndsWith('.'))
        {
          // if it's a spell
          if (i != split.Length - 1)
          {
            if (split[i] == "Rk.")
            {
              return false;
            }

            bound = i;
          }

          break;
        }
      }

      // lands on you
      if (EQDataStore.Instance.TryGetLandsOnYou(split, bound, out var searchResult))
      {
        AddReceived(ConfigUtil.PlayerName, searchResult);
        return true;
      }

      // wear off you
      if (EQDataStore.Instance.TryGetWearOff(split, bound, out searchResult))
      {
        AddReceived(ConfigUtil.PlayerName, searchResult, true);
        return true;
      }

      // lands on other
      if (EQDataStore.Instance.TryGetLandsOnOther(split, bound, out searchResult, out var target))
      {
        AddReceived(target, searchResult);

        // if it's a pet spell, add the pet to the registry so we can track it better
        if (searchResult.SpellData[0].Target == (int)SpellTarget.Pet || searchResult.SpellData[0].Target == (int)SpellTarget.Pet2)
        {
          // dont change a pet into a player by accident
          if (searchResult.SpellData.Count == 1 && !IdentityLookup.IsKnownPet(target) && !IdentityLookup.IsNameOfOurPerson(target))
          {
            foreach (var spell in PetSpells)
            {
              if (searchResult.SpellData[0].Name?.StartsWith(spell, StringComparison.OrdinalIgnoreCase) is true)
              {
                PlayerRegistry.Instance.AddVerifiedPet(target);
                break;
              }
            }
          }
        }

        return true;
      }

      void AddReceived(string receiver, SpellTreeResult result, bool isWearOff = false)
      {
        var newSpell = new ReceivedSpell { Receiver = StringCache.GetOrAdd(receiver), IsWearOff = isWearOff };
        if (result.SpellData.Count == 1)
        {
          newSpell.SpellData = result.SpellData[0];
        }
        else
        {
          newSpell.AddAmbiguity(result.SpellData);
          // Bulk opens remember their open questions for one retry after the merge (a lane cannot see the casts the
          // other lane has not parsed yet). No-ops unless a sharded load opened the door — live parsing never enqueues.
          RecordsStore.Instance.AddPendingAmbiguity(newSpell, beginTime);
        }

        RecordsStore.Instance.Add(newSpell, beginTime);
      }

      return false;
    }

    private static string CheckForSpecial(Dictionary<string, string> codes, string spellName, string player, double currentTime)
    {
      string found = null;
      if (!string.IsNullOrEmpty(spellName))
      {
        foreach (var (special, code) in codes)
        {
          if (spellName.Contains(special, StringComparison.OrdinalIgnoreCase))
          {
            RecordsStore.Instance.Add(new SpecialRecord { Code = code, Player = player }, currentTime);
            found = special;
            break;
          }
        }
      }
      return found;
    }

    // Array.FindIndex(split, 1, length-1, == "begins") over the word view — same index range, no lambda capture.
    private static int FindBegins(LineWords split)
    {
      for (var i = 1; i < split.Length; i++)
      {
        if (split[i] == "begins")
        {
          return i;
        }
      }

      return -1;
    }

    // The old logs' tail shape: "begins to cast a spell. <Spell Name>" — raw-join the range, trim the brackets off.
    private static string ParseOldSpellName(LineWords split, int spellIndex)
    {
      return split.JoinRaw(spellIndex, split.Length - spellIndex).Trim(OldSpellChars);
    }
  }
}
