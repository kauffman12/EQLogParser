using log4net;
using System;
using System.Reflection;

/*
 * Annotations only, no null-flow analysis: Nullable=disable project-wide, and this speaks in optional strings because a
 * roster row legitimately has no class. Same convention as IdentityPriorStore.
 */
#nullable enable annotations

namespace EQLogParser
{
  /*
   * THE ONE-TIME ROSTER IMPORT: players.txt -> the roster lane of identity-priors.txt, once per server folder.
   *
   * WHY IT EXISTS. `IdentityLookup.IsOneOfUs` answers "is this one of ours?" with the capture's evidence first and this
   * application's memory last (docs/DesignNotes.md → "The one seam that answers 'is this one of ours?'"). Memory used to
   * mean exactly one file, and that file has nowhere to put the one fact that makes a learned name worth keeping: the
   * class it was seen casting. The ledger gained that lane a commit ago (`RememberRoster`); this class is what fills it,
   * so the retirement of players.txt is a change of READER rather than a day the raid list has to be retyped.
   *
   * WHAT IT MOVES IS MEMBERSHIP, NOT A VERDICT. Every row is written through RememberRoster, which sets the roster bit
   * and leaves Kind/Reason/Sightings alone — so an old file of nine hundred names cannot outvote tonight's rules pass on
   * what any of them ARE, and a name on the list for a decade is still allowed to be called Npc by every capture
   * (IdentityLookup reads that as two true statements, not a contradiction). `Record` can never write this lane; the
   * importer is the only path from a file into it.
   *
   * PET OWNERS NEED NO IMPORT, and reading this file is where a reader would expect them. Ownership lives in
   * petmapping.txt (which stamps its own sightings now) and `RegistrySeed` feeds those pairs every start; an owner is on
   * the roster because PlayerRegistry.Init seeds mapping owners into the player list, exactly as it always did. Copying
   * the pairs in here too would put one fact in two files, which is how the two of them start disagreeing.
   *
   * THREE LAWS OF THE RUN:
   *
   *   It runs ONCE per folder — gated on the ledger already carrying roster rows, not on a marker file. A marker would
   *     answer "did we run?" while saying nothing about whether anything arrived; the roster bit is the same fact with
   *     the same lifetime as the data it describes, and it survives an operator deleting the archive by hand.
   *
   *   It writes NO stamps of its own. The `=ticks` a row carries is the last time this program saw that name; rows
   *     without one are statements and stay undated forever. RememberRoster moves a stamp FORWARD only, so importing an
   *     old backup cannot age out an active player, and running the whole import twice changes nothing — which is what
   *     makes a run that dies halfway harmless: start it again and the rows land where they were going anyway.
   *
   *   The SOURCE FILE IS LEFT ALONE. players.txt is still the live roster and still written on close until the commit
   *     that retires it; archiving it here would leave an operator with `players.imported.txt` AND a fresh players.txt by
   *     the end of the same session, i.e. two files that look like one migration and one rollback and are actually both
   *     in-flight. The rename belongs to that retirement commit, beside the deletion of the code that writes the file
   *     (docs/DesignNotes.md → "The one-time roster import").
   */
  internal static class RosterImport
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    internal readonly record struct Outcome(int Applied, int WithClass, int Refused)
    {
      /// <summary>Nothing was carried over — no file, already done, or nothing in the file a person could use.</summary>
      internal bool DidWork => Applied > 0;

      public override string ToString() => $"{Applied} names ({WithClass} with a class, {Refused} lines refused)";
    }

    /// <summary>The migration for the server this app is currently pointed at.</summary>
    internal static Outcome ImportPlayersFileOnce() => ImportPlayersFileOnce(ConfigUtil.ServerName);

    /*
     * Called at log open, AFTER IdentityPriorStore.Init(server) and BEFORE PlayerRegistry.Init(), so the registry's seed
     * reads a ledger that already holds this folder's roster. Safe to call on every open: every guard below returns
     * quietly, and the whole thing costs one File.Exists after the migration has run.
     */
    internal static Outcome ImportPlayersFileOnce(string? serverName)
    {
      if (string.IsNullOrEmpty(serverName)) return default;   // no log open = no folder to attribute the names to

      var ledger = IdentityPriorStore.Instance;

      /*
       * Two refusals that are easy to get wrong and silent either way. WRONG FOLDER first: the ledger files whatever it is
       * told under the server name IT holds, and MainWindow loads the registry before the ledger on a switch — writing
       * here while it still answers for the previous server would move one server's raid into another's file. And
       * ALREADY CARRIED: the roster lane is the record that this ran, which is why re-running is free rather than merely
       * harmless (a hand-restored players.txt is not a reason to import again).
       */
      if (!string.Equals(ledger.ServerName, serverName, StringComparison.OrdinalIgnoreCase)) return default;
      if (ledger.HasRosterRows) return default;

      // Absent and empty are different answers, and only this one distinguishes them: no file means this folder never
      // had a roster, so there is nothing to carry and NOTHING TO LOG. An empty file falls through and says so below.
      if (!ConfigUtil.ServerFileExists(ConfigUtil.PlayersFile, serverName)) return default;

      var applied = 0;
      var withClass = 0;
      var refused = 0;

      foreach (var line in ConfigUtil.ReadPlayers(serverName))
      {
        // One grammar for both readers of this file — see PlayerRegistry.TryReadRosterLine.
        if (!PlayerRegistry.TryReadRosterLine(line, out var name, out var seenAtS, out var className))
        {
          if (!string.IsNullOrWhiteSpace(line)) refused++;
          continue;
        }

        /*
         * "You" in a hand-edited file means whoever is playing NOW, which is the same reading PlayerRegistry.Init gives
         * it. With no character open there is nobody to mean, and the row is skipped rather than putting the literal word
         * "You" on a roster — a name the identity rules would then meet in every later capture.
         */
        if ("You".Equals(name, StringComparison.OrdinalIgnoreCase)) name = ConfigUtil.PlayerName ?? string.Empty;
        /*
         * Junk, named exactly: the person words ("you", "himself", "Unknown Pet Owner") are vocabulary rather than
         * knowledge, and the unknown marker is a placeholder somebody's editor wrote. What is deliberately NOT here is
         * PlayerRegistry.IsPossiblePlayerName - it wants letters only, so it refuses "Akini, Xanathan" (one summon, two
         * masters) and any hand-typed name with a space. Refusing those would put a curated name in the registry's memory
         * but not in the ledger that outlives the file: the divergence this class exists to prevent.
         */
        if (string.IsNullOrEmpty(name) || PlayerRegistry.IsPersonWord(name) ||
            Labels.Unk.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
          refused++;
          continue;
        }

        /*
         * The parser capitalizes every name it hands out, and identity is keyed case-insensitively — so a row written in
         * whatever case the operator typed would display as two different spellings of one person across the panes that
         * read the census and the ledger. Capitalizing the first letter matches the file's own dominant shape and makes
         * the ledger's key the same string the rules use (AGENTS: "Entity names are looked up without case").
         */
        ledger.RememberRoster(TextUtils.CapitalizeFirst(name), (long)seenAtS, className, persist: false);
        applied++;
        if (className is not null) withClass++;
      }

      /*
       * One write for the whole list. A thousand names through the persisting overload would rewrite the ledger a
       * thousand times, and a crash halfway would leave a roster nobody could explain.
       */
      if (applied > 0) ledger.FlushChanges();

      // One line per migration, silent afterwards — this fires at most once per server folder, so it must say enough to
      // diagnose the one capture where the roster did not arrive. A file with nothing usable in it says so too: an
      // operator who just watched a curated list do nothing needs to know the lines were read and refused.
      if (applied > 0 || refused > 0)
      {
        Log.Info($"roster: imported {applied} names from {ConfigUtil.PlayersFile}"
                 + (withClass > 0 ? $" ({withClass} with a class)" : string.Empty)
                 + (refused > 0 ? $", {refused} lines refused" : string.Empty));
      }

      return new Outcome(applied, withClass, refused);
    }
  }
}
