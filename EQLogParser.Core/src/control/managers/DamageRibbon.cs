using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * The damage meter's event ribbon: the few moments of a fight that are not damage — a mez breaking, somebody
   * willing a named pet, a taunt landing. Parsers fire these as records into storage; nothing kept them alive in
   * the live moment until now. A DamageRibbon holds the last few formatted lines oldest-first: the meter renders
   * them under its target row, newest at the bottom edge, where "who just died" answers itself without stealing a
   * ranked row.
   *
   * What earns a line (player-signal, no raid-noise):
   *  - every mez break ("Kizant breaks mez!") — a mob breaking is an AE waking up, which is the louder case;
   *  - a player or named pet being killed: "Kizant wills Puksu" when a player did it, "{name} died" otherwise.
   *    Generic pets ("Sancus`s pet", no personal name) are ignored on purpose — nobody is mourning those;
   *  - successful taunts ("Kizant taunts a skeleton"); failures are silence.
   *
   * Singleton with settable Instance like FightManager/FctManager, so tests can own a fresh ribbon without the
   * parsers' static events. Formatting is static and pure; who counts as a player is a seam (IsPlayerName) so the
   * rules stay testable without PlayerRegistry's runtime state.
   */
  internal class DamageRibbon
  {
    // memory ceiling on raw lines; the UI caps its view lower still, this only bounds a long session
    internal const int Capacity = 20;

    internal static DamageRibbon Instance { get; set; } = new();

    private readonly object _lock = new();
    private readonly List<string> _lines = [];
    private bool _subscribed;

    internal Func<string, bool> IsPlayerName { get; set; } = PlayerRegistry.Instance.IsVerifiedPlayer;

    // Subscribe to the parsers' static events. Idempotent: an Initialize called twice must not double-feed lines.
    internal void Initialize()
    {
      if (_subscribed)
      {
        return;
      }

      DamageLineParser.EventsNewDeath += OnDeath;
      DamageLineParser.EventsNewTaunt += OnTaunt;
      MiscLineParser.EventsNewMezBreak += OnMezBreak;
      _subscribed = true;
    }

    internal IReadOnlyList<string> Lines()
    {
      lock (_lock)
      {
        return [.. _lines];
      }
    }

    internal void Clear()
    {
      lock (_lock)
      {
        _lines.Clear();
      }
    }

    private void Add(string line)
    {
      if (line is null)
      {
        return;
      }

      lock (_lock)
      {
        _lines.Add(line);
        if (_lines.Count > Capacity)
        {
          _lines.RemoveAt(0);
        }
      }
    }

    internal void OnDeath(DeathEvent e) => Add(FormatDeath(e.Record, IsPlayerName));

    internal void OnTaunt(TauntEvent e) => Add(FormatTaunt(e.Record));

    internal void OnMezBreak(MezBreakRecord r) => Add(FormatMezBreak(r));

    // ---- formatting: static, pure, and the exact wording the meter shows ----

    internal static string FormatMezBreak(MezBreakRecord r)
    {
      return r?.Breaker is null or "" ? null : $"{r.Breaker} breaks mez!";
    }

    internal static string FormatTaunt(TauntRecord r)
    {
      if (r is null || !r.Success || r.Player is null or "")
      {
        return null;
      }

      var npc = PersonalName(r.Npc);
      return npc is null ? $"{r.Player} taunts" : $"{r.Player} taunts {npc}";
    }

    internal static string FormatDeath(DeathRecord r, Func<string, bool> isPlayer)
    {
      if (r?.Killed is null or "")
      {
        return null;
      }

      string victim;
      var personal = PetPersonalName(r.Killed);
      if (personal is not null)
      {
        victim = personal;
      }
      else if (isPlayer is not null && isPlayer(r.Killed))
      {
        victim = r.Killed;
      }
      else
      {
        // a mob died — every raid kill would join the ribbon and drown the events that matter
        return null;
      }

      if (isPlayer is not null && isPlayer(r.Killer))
      {
        return $"{r.Killer} wills {victim}";
      }

      return $"{victim} died";
    }

    // a name for display: somebody's named pet shows as just its personal name, everything else passes through
    internal static string PersonalName(string display)
    {
      if (string.IsNullOrEmpty(display))
      {
        return null;
      }

      return PetPersonalName(display) ?? display;
    }

    /*
     * The pet question only: a name is worth showing when it is a player or somebody's pet WITH a personal name.
     * EQ display names fold pets into their owner — "Sancus`s pet Puksu", or "Sancus`s pet" with nothing after it
     * when the pet was never named. Ribbon lines say only "Puksu": the personal name carries everything worth
     * knowing, and an owner's name on somebody else's dead pet is noise. The backtick form is what logs carry;
     * the apostrophe is handled too because cached and hand-built names drift between the two.
     */
    internal static string PetPersonalName(string display)
    {
      if (string.IsNullOrEmpty(display))
      {
        return null;
      }

      var i = display.IndexOf("`s pet", StringComparison.Ordinal);
      if (i < 0)
      {
        i = display.IndexOf("'s pet", StringComparison.Ordinal);
      }

      if (i < 0)
      {
        return null;
      }

      var personal = display[(i + 6)..].Trim();
      return personal.Length == 0 ? null : personal;
    }
  }
}
