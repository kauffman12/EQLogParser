using EQLogParser.Mirror;

namespace EQLogParser
{
  internal class PreLineParser
  {
    // Mirror evidence (D8): fires this parser's own recognitions with provenance. Purely additive -
    // no branch consumes or alters anything for this; CombatMirror is the only subscriber and the
    // legacy pipeline behaves exactly as before. Kind values are EvidenceFact.Ev* (mirror module).
    internal static event Action<string, double, byte> EventsEvidence;

    // Process things that can easily identify a player
    private PreLineParser()
    {

    }

    internal static bool NeedProcessing(LineData lineData, Action<string, double> addVerifiedPlayer, Func<string, int, bool> isPossiblePlayerName, Action<string> addMerc)
    {
      var found = false;
      var action = lineData.Action;

      // Fire-only recognition: "Targeted (NPC): <name>" lines are discarded by the legacy pipeline
      // today (no parser consumes them); keeping that fate unchanged, this only reports the name.
      if (action.StartsWith("Targeted (NPC)", StringComparison.OrdinalIgnoreCase))
      {
        EventsEvidence?.Invoke(action[15..].Trim(), lineData.BeginTime, EvidenceFact.EvTargetedNpc);
      }

      if (action.Length > 10)
      {
        if (action.Length > 20 && action.StartsWith("Targeted (Player)", StringComparison.OrdinalIgnoreCase))
        {
          addVerifiedPlayer(action[19..], lineData.BeginTime);
          EventsEvidence?.Invoke(action[19..].Trim(), lineData.BeginTime, EvidenceFact.EvTargetedPlayer);
          found = true; // ignore anything that starts with Targeted
        }
        else if (action.EndsWith(" joined the raid.", StringComparison.OrdinalIgnoreCase) && !action.StartsWith("You have", StringComparison.OrdinalIgnoreCase))
        {
          if (isPossiblePlayerName(action, action.Length - 17))
          {
            var test = action[..^17];
            addVerifiedPlayer(test, lineData.BeginTime);
            EventsEvidence?.Invoke(test, lineData.BeginTime, EvidenceFact.EvJoinedRaid);
            found = true;
          }
        }
        else if (action.EndsWith(" has joined the group.", StringComparison.OrdinalIgnoreCase))
        {
          var test = action[..^22];
          if (isPossiblePlayerName(test, -1))
          {
            addVerifiedPlayer(test, lineData.BeginTime);
            EventsEvidence?.Invoke(test, lineData.BeginTime, EvidenceFact.EvJoinedGroup);
          }
          else
          {
            addMerc(test);
            EventsEvidence?.Invoke(test, lineData.BeginTime, EvidenceFact.EvMercJoinedGroup);
          }

          found = true;
        }
        else if (action.EndsWith(" has left the raid.", StringComparison.OrdinalIgnoreCase))
        {
          var test = action[..^19];
          if (isPossiblePlayerName(test, -1))
          {
            addVerifiedPlayer(test, lineData.BeginTime);
            EventsEvidence?.Invoke(test, lineData.BeginTime, EvidenceFact.EvLeftRaid);
            found = true;
          }
        }
        else if (action.EndsWith(" has left the group.", StringComparison.OrdinalIgnoreCase))
        {
          var test = action[..^20];
          if (isPossiblePlayerName(test, -1))
          {
            addVerifiedPlayer(test, lineData.BeginTime);
            EventsEvidence?.Invoke(test, lineData.BeginTime, EvidenceFact.EvLeftGroup);
          }
          else
          {
            addMerc(test);
          }

          found = true;
        }
        else if (action.EndsWith(" is now the leader of your raid.", StringComparison.OrdinalIgnoreCase))
        {
          var test = action[..^32];
          if (isPossiblePlayerName(test, -1))
          {
            addVerifiedPlayer(test, lineData.BeginTime);
            EventsEvidence?.Invoke(test, lineData.BeginTime, EvidenceFact.EvRaidLeader);
            found = true;
          }
        }
        else if (TryGetConsumer(action, out var consumer))
        {
          addVerifiedPlayer(consumer, lineData.BeginTime);
          EventsEvidence?.Invoke(consumer, lineData.BeginTime, EvidenceFact.EvSelfFeeds);
          found = true;
        }
      }

      return !found;
    }

    /*
     * The two lines EQ writes when somebody consumes something:
     *
     *   "Glug, glug, glug...  Bithika takes a drink from their Water Flask."
     *   "Chomp, chomp, chomp...  Bithika takes a bite from their Fresh Fish."
     *
     * Neither half of the match is specific on purpose. The **vessel** is not part of it — EQ writes whatever item
     * is being consumed ("their Water Flask", "an Ironbone Mead") — and the **sound effect** is client emote text,
     * so it is matched without case. Only the actor field between them matters.
     *
     * And only a player character eats or drinks: measured across three captures, 370 such lines naming 67
     * distinct actors, none article-shaped ("a X") and exactly one colliding with npcs.txt (a player who happens
     * to share a mob's name). The drink half has been verifying players into the registry for years; eating is the
     * same line with a different sound, so it verifies the same way and feeds R17 (docs/combat-mirror-design.md).
     */
    private static readonly (string Sound, string Verb)[] ConsumeShapes =
    [
      ("Glug, glug, glug...", "takes a drink"),
      ("Chomp, chomp, chomp...", "takes a bite"),
    ];

    private static bool TryGetConsumer(string action, out string name)
    {
      name = string.Empty;

      foreach (var (sound, verb) in ConsumeShapes)
      {
        if (!action.StartsWith(sound, StringComparison.OrdinalIgnoreCase)) continue;

        // The client pads the sound with two spaces; skipping whatever run it wrote keeps this from breaking on
        // a client that writes one.
        var from = sound.Length;
        while (from < action.Length && action[from] == ' ') from++;

        var end = FindPossiblePlayerName(action, out var isCrossServer, from, -1, ' ');
        if (end == -1 || isCrossServer) return false;   // server-qualified: never a key this pipeline uses

        if (!action.AsSpan(end).TrimStart().StartsWith(verb, StringComparison.OrdinalIgnoreCase)) return false;

        name = action[from..end];
        return name.Length > 0;
      }

      return false;
    }

    internal static int FindPossiblePlayerName(string action, out bool isCrossServer, int startIndex, int stopIndex, char stopChar)
    {
      isCrossServer = false;

      if (action is null)
      {
        return -1;
      }

      var stop = stopIndex > -1 ? stopIndex : action.Length;
      if (startIndex > stop || (stop - startIndex) < 3)
      {
        return -1;
      }

      var dotCount = 0;

      for (var i = startIndex; i < stop; i++)
      {
        if (action[i] == stopChar)
        {
          return i;
        }

        if (i > startIndex && action[i] == '.')
        {
          isCrossServer = true;
          if (++dotCount > 1)
          {
            return -1;
          }
        }
        else if (!char.IsLetter(action, i))
        {
          return -1;
        }
      }

      return -1;
    }
  }
}
