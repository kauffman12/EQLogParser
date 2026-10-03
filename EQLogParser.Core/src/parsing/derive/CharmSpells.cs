namespace EQLogParser
{
  /*
   * The closed vocabulary of spell names whose line proves a CHARM, and nothing else.
   *
   * Why this exists as its own table: the only line that ends a charm in the text is
   *   "Your Charm XVII spell has worn off of a candlefolk flame worshipper."
   * and it shares that exact shape with every other buff wear-off line, which EQ writes by the
   * thousand. Measured over three captures (eqlog_Incogitable_xegony, eqlog_Kizant_xegony-01-06-24,
   * eqlog_Kizant_xegony-9-18-22): 15,943 "spell has worn off of" lines, SEVEN of them a charm. The
   * other 15,936 are upkeep — in that one Kizant file the top names are "Your Shadebright Vortex
   * Effect" (1,609), "Your Frost Shackles I" (941) and "Your Group Perfected Invisibility I" (376).
   * Before this table, ANY of those closed a charm window, which ended raid-wide charm uptime on
   * somebody's root. So the wear-off line only counts when its spell is in the list below.
   *
   * The list is measured, and it is deliberately short:
   *   Charm        13 casts / 7 wear-offs      the enchanter charm line, "Charm XVII"
   *   Compulsion    5 casts / 0 wear-offs      the instant charm
   * Two words that look eligible and are NOT:
   *   "Beguiler's Directed Banishment" 1,018 casts across two files against ~10 charms — an unrelated
   *     spell whose name starts like the pet ability "Beguile". Matching on a prefix or a substring
   *     ("beguil") reads it as charm evidence and every one of its wear-offs closes a window.
   *   "Bewilderment of Lights II" (15 casts) — mez-line wording, never followed by a charm success.
   * A new charm spell arrives the way every other vocabulary word does: measured in a log, added
   * here next to its count, and tested. Substring matching is not available as a shortcut; the rank
   * tail below is roman-numeral-only on purpose so "Charm of the …" style names cannot ride in.
   */
  internal static class CharmSpells
  {
    private static readonly string[] CharmNames = ["charm", "compulsion"];

    // Spell names in log text are rank-suffixed: "Charm XVII", "Compulsion IV". Anything else after
    // the word means it is a different spell that happens to start with it.
    internal static bool IsCharmSpellName(string spell)
    {
      if (string.IsNullOrEmpty(spell)) return false;

      var s = spell.Trim();
      foreach (var name in CharmNames)
      {
        if (s.Length < name.Length || !s.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;

        var rest = s.AsSpan(name.Length);
        if (rest.IsEmpty) return true;                       // bare word, e.g. "Charm"
        if (rest[0] != ' ') continue;                        // "Charmwise" is not a charm spell

        var rank = rest.TrimStart();
        if (rank.IsEmpty) continue;                          // trailing space, no rank: not a rank line
        var any = false;
        foreach (var c in rank)
        {
          // Measured ranks in these captures run I..XIX; IVX covers every charm rank EQ ships.
          if (c is not ('I' or 'V' or 'X')) { any = false; break; }
          any = true;
        }
        if (any) return true;
      }
      return false;
    }

    // True when the cast line's caster text proves WHOSE buff line this is: "Your Charm XVII" is the
    // log author's, so the owner key is the same one R0 stamps ("You").
    internal static bool IsLocalOwnerWord(string ownerWord)
      => !string.IsNullOrEmpty(ownerWord) && ownerWord.Equals("your", StringComparison.OrdinalIgnoreCase);
  }
}
