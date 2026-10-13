using EQLogParser;

namespace EQLogParser
{
  internal class PreLineParser
  {
    // Classification evidence (D8): fires this parser's own recognitions with provenance. Purely additive -
    // no branch consumes or alters anything for this; CombatCapture is the only subscriber and the
    // legacy pipeline behaves exactly as before. Kind values are EvidenceFact.Ev* (parsing/derive).
    // Raised on the lane that parsed the line; this declaration is the Main-lane forwarder that session
    // subscribers (CombatCapture for the one normal stream) attach to. See ParseLane.
    internal static event Action<string, double, byte> EventsEvidence
    {
      add => ParseLane.Main.Evidence += value;
      remove => ParseLane.Main.Evidence -= value;
    }

    /*
     * The evidence wire every parser reports onto, so a recognition anywhere in the pipeline can be remembered
     * without adding an event to the class that found it. DamageLineParser uses it for the one thing worth keeping
     * off a line this pipeline refuses to count: the owner of a summoned eye (R19).
     */
    internal static void ReportEvidence(string name, double time, byte kind)
    {
      if (!string.IsNullOrEmpty(name)) ParseLane.Current.Evidence?.Invoke(name, time, kind);
    }

    // Process things that can easily identify a player
    private PreLineParser()
    {

    }

    internal static bool NeedProcessing(LineData lineData, Action<string, double> addVerifiedPlayer, Func<string, int, bool> isPossiblePlayerName, Action<string> addMerc)
    {
      var found = false;
      var action = lineData.Action;

      if (action.Length > 8)
      {
        /*
         * A line whose data begins with a tilde is not a sentence about the world at all - the client writes it for a
         * thought/emote of its own (`~ Kizant wonders.`) and nothing downstream has ever had a use for it. Claiming it here
         * is the cheapest exit in the pipeline: the four real parsers each split the action and walk their own tables over
         * it today, and every one of them answers "not mine". Reported shape only (no local capture carries the form yet,
         * so this rests on the field example rather than on a census - see docs/DesignNotes.md), and deliberately narrow:
         * a bare `~` with nothing after it is not this shape and is left alone.
         */
        if (action.StartsWith("~ ", StringComparison.Ordinal))
        {
          found = true;
        }
        /*
         * "Targeted (NPC): <name>" - the client naming whatever the button is pointed at. This parser takes the whole line:
         * no parser downstream has ever consumed it (it was discarded before R1 existed and still is), so letting it walk
         * Damage/Healing/Misc/Cast produced nothing but work. Reported through `ReportEvidence` rather than the event
         * directly, because a line whose tail is empty (`Targeted (NPC): `) used to fire an evidence fact about "".
         */
        else if (action.StartsWith("Targeted (NPC)", StringComparison.OrdinalIgnoreCase))
        {
          /*
           * The slice needs its own length check: the branch matches on a 14-character prefix while the name starts at 15, so a
           * line that is the bare prefix (`Targeted (NPC)`) used to throw out of `action[15..]`. A Targeted line belongs to this
           * parser whatever its tail - claiming it without reporting anything is the honest reading of an empty one.
           */
          if (action.Length > 15)
          {
            var target = action[15..].Trim();
            if (target.Length > 0) ReportEvidence(target, lineData.BeginTime, EvidenceFact.EvTargetedNpc);
          }
          found = true;
        }
        /*
         * An achievement announcement naming somebody in the local player's OWN guild list (R22). The recognizer is exact
         * (`Your guildmate <name> has completed …`), so a line it accepts is identity evidence and nothing else - the same
         * argument as above, and the reason `found` follows only when the parse actually produced a name: a line that merely
         * starts with those words (`Your guildmate said things.`) stays available to the parsers like any other sentence.
         */
        else if (TryGetGuildmate(action, out var guildmate))
        {
          ReportEvidence(guildmate, lineData.BeginTime, EvidenceFact.EvGuildmate);
          found = true;
        }
        else if (action.StartsWith("Targeted (Player)", StringComparison.OrdinalIgnoreCase))
        {
          // Same law as the NPC half above: 17 characters of prefix, the name starting at 19 - an empty tail is claimed, not sliced.
          if (action.Length > 19)
          {
            var target = action[19..];
            addVerifiedPlayer(target, lineData.BeginTime);
            ParseLane.Current.Evidence?.Invoke(target.Trim(), lineData.BeginTime, EvidenceFact.EvTargetedPlayer);
          }
          found = true; // ignore anything that starts with Targeted
        }
        else if (action.EndsWith(" joined the raid.", StringComparison.OrdinalIgnoreCase) && !action.StartsWith("You have", StringComparison.OrdinalIgnoreCase))
        {
          if (isPossiblePlayerName(action, action.Length - 17))
          {
            var test = action[..^17];
            addVerifiedPlayer(test, lineData.BeginTime);
            ParseLane.Current.Evidence?.Invoke(test, lineData.BeginTime, EvidenceFact.EvJoinedRaid);
            found = true;
          }
        }
        else if (action.EndsWith(" has joined the group.", StringComparison.OrdinalIgnoreCase))
        {
          var test = action[..^22];
          if (isPossiblePlayerName(test, -1))
          {
            addVerifiedPlayer(test, lineData.BeginTime);
            ParseLane.Current.Evidence?.Invoke(test, lineData.BeginTime, EvidenceFact.EvJoinedGroup);
          }
          else
          {
            addMerc(test);
            ParseLane.Current.Evidence?.Invoke(test, lineData.BeginTime, EvidenceFact.EvMercJoinedGroup);
          }

          found = true;
        }
        else if (action.EndsWith(" has left the raid.", StringComparison.OrdinalIgnoreCase))
        {
          var test = action[..^19];
          if (isPossiblePlayerName(test, -1))
          {
            addVerifiedPlayer(test, lineData.BeginTime);
            ParseLane.Current.Evidence?.Invoke(test, lineData.BeginTime, EvidenceFact.EvLeftRaid);
            found = true;
          }
        }
        else if (action.EndsWith(" has left the group.", StringComparison.OrdinalIgnoreCase))
        {
          var test = action[..^20];
          if (isPossiblePlayerName(test, -1))
          {
            addVerifiedPlayer(test, lineData.BeginTime);
            ParseLane.Current.Evidence?.Invoke(test, lineData.BeginTime, EvidenceFact.EvLeftGroup);
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
            ParseLane.Current.Evidence?.Invoke(test, lineData.BeginTime, EvidenceFact.EvRaidLeader);
            found = true;
          }
        }
        else if (TryGetConsumer(action, out var consumer))
        {
          addVerifiedPlayer(consumer, lineData.BeginTime);
          ParseLane.Current.Evidence?.Invoke(consumer, lineData.BeginTime, EvidenceFact.EvSelfFeeds);
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
     * same line with a different sound, so it verifies the same way and feeds R17 (docs/DesignNotes.md → "The identity rule book").
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

    /*
     * The one shape the guild announcement takes:
     *
     *   "Your guildmate Blazem has completed Partisan of Candlemaker's Workshop achievement."
     *
     * Measured over four captures before being trusted: 10,541 lines, and EVERY one of them is
     * "Your guildmate <name> has completed" - no other verb appeared on any capture. The 433 distinct names two of
     * those captures print are letters only (no apostrophe, no dot, no server qualifier), and NONE of them is placed
     * Npc or Pet by the rule book, which is the check that makes this a claim worth writing down: the client writes
     * the sentence out of its own guild list, so it reports a character rather than guessing from behaviour. It is
     * also the only evidence some silent regulars ever give - see docs/DesignNotes.md -> "What the cold misses
     * actually are", which counts why combat evidence cannot reach them and why tells and `begins singing` were
     * refused (both name NPCs: `] Bane tells General:1, 'WTS Full NoS collect sets 3kr each'` is a bazaar hailer
     * whose name is in npcs.txt, and `Shalowain begins singing her Rhapsody of Pain.` is a NPC bard).
     */
    private const string GuildmatePrefix = "Your guildmate ";
    private const string GuildmateVerb = "has completed";

    internal static bool TryGetGuildmate(string action, out string name)
    {
      name = string.Empty;

      if (!action.StartsWith(GuildmatePrefix, StringComparison.OrdinalIgnoreCase)) return false;

      var from = GuildmatePrefix.Length;
      while (from < action.Length && action[from] == ' ') from++;

      // The same recognizer every other identity branch uses: letters only, and a dot means a server-qualified
      // name, which is never a key this pipeline takes.
      var end = FindPossiblePlayerName(action, out var isCrossServer, from, -1, ' ');
      if (end == -1 || isCrossServer) return false;

      if (!action.AsSpan(end).TrimStart().StartsWith(GuildmateVerb, StringComparison.OrdinalIgnoreCase)) return false;

      name = action[from..end];
      return name.Length > 0;
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
