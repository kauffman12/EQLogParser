using System;
using System.Collections.Generic;

namespace EQLogParser
{
  /*
   * Everything the settings panel's "show" dropdown offers, in one table: the nine kinds of number (FctRow) and
   * the eight event words, each with the settings.ini key and the label it prints. They share a table because they share a
   * control — the player's question is one question, "what may draw" — and because three separate things read each entry:
   * the overlay loading its gates at startup, the overlay writing them at Save, and this dropdown building itself. An entry
   * whose key, label and meaning live in three places is an entry that gets added twice, or half-added with a switch that
   * saves under one name and reads under another.
   *
   * A row gates by what the record is (FctRow, resolved by FctManager where the parse knows who hit what). A word gates by
   * its text (FctIngest.WordShown), because a complaint about the words has always been about one word — "miss is drowning
   * everything" — and never about "the zero-damage events" as a category. Both are one checkbox here, sorted alphabetically
   * with the rest: the earlier grouping by narrative (categories, then quiet defensive words, then the loud pair) was a
   * story about a fight, and finding a word in it was a memory test.
   *
   * The two exceptions worth stating, because they surprise people who met Mik's Scrolling Battle Text first: "spell" is
   * every kind of non-melee damage the parser produces — Direct Damage, Bane, Damage Shield, Reverse DS, Other Damage — and
   * a damage-over-time tick lands in the spell rows with everything else that did its damage slowly, crit ticks included.
   * Both follow from what the eye sees: a tick folds into its neighbours on screen (FctIngest), so a switch for it alone
   * would be a switch for something nobody can pick out of the picture. "hits" means everything that did not crit, which is
   * why each pair sits beside its own crit row in the table — read down and the sentence explains itself.
   */
  internal static class FctShowList
  {
    /* Word entries carry their Labels constant and no row; rows carry their FctRow and no word. The Labels constant is the
       identity rather than a display string: it is what FctManager stamps into the command, so a word cannot drift from the
       parser's spelling and end up gated by a switch that never fires. */
    internal readonly record struct Entry(string Label, string Key, FctRow Row, string Word)
    {
      internal bool IsWord => Word is not null;

      /* Reading and writing this entry on a snapshot — the panel fills its checks and reads them back through here, so it
         never needs to know which of its two kinds it is holding. */
      internal bool Get(FctConfigState state) => IsWord ? state.GetWord(Word) : state.GetRow(Row);

      internal void Set(FctConfigState state, bool shown)
      {
        if (IsWord)
        {
          state.SetWord(Word, shown);
        }
        else
        {
          state.SetRow(Row, shown);
        }
      }

      /* The same pair of questions aimed at the gates themselves, which is how the canvas applies a snapshot without a
         branch per kind at the call site. Answers whether anything moved, because restarting a sample loop for a switch
         that did not move is worse than not restarting it. */
      internal bool ApplyTo(FctIngest ingest, bool shown)
      {
        if (IsWord)
        {
          var was = ingest.WordShown(Word);
          return ingest.SetWordShown(Word, shown) && was != shown;
        }

        var wasRow = ingest.RowShown(Row);
        return ingest.SetRowShown(Row, shown) && wasRow != shown;
      }
    }

    /* The nine rows in engine order (who fires it, then what, then whether it crit), which is how the gates read and how a
     * missing row shows up in a diff. Procs keeps the key it already had — that word named this event before there were rows
     * to file it in, and somebody who switched it off last month should still have it off. */
    public static readonly IReadOnlyList<Entry> Rows = new List<Entry>
    {
      new("melee hits", "FctOverlayShowMeleeHits", FctRow.MeleeHits, null),
      new("melee crits", "FctOverlayShowMeleeCrits", FctRow.MeleeCrits, null),
      new("spell hits", "FctOverlayShowSpellHits", FctRow.SpellHits, null),
      new("spell crits", "FctOverlayShowSpellCrits", FctRow.SpellCrits, null),
      new("pet melee", "FctOverlayShowPetMelee", FctRow.PetMelee, null),
      new("pet spells", "FctOverlayShowPetSpells", FctRow.PetSpells, null),
      new("healing", "FctOverlayShowHealing", FctRow.Healing, null),
      new("healing crits", "FctOverlayShowHealingCrits", FctRow.HealingCrits, null),
      new("procs", FctOverlaySettings.ShowProcsKey, FctRow.Procs, null),
    };

    /* The eight words, in the fight's own order for the table's sake (quiet defensive words, then spell failures, then the
       loud pair). The dropdown sorts everything by label anyway; keeping the natural order here is what makes an omission
       visible when someone adds the ninth. */
    public static readonly IReadOnlyList<Entry> Words = new List<Entry>
    {
      new("miss", FctOverlaySettings.ShowMissKey, FctRow.Word, Labels.Miss),
      new("parry", FctOverlaySettings.ShowParryKey, FctRow.Word, Labels.Parry),
      new("dodge", FctOverlaySettings.ShowDodgeKey, FctRow.Word, Labels.Dodge),
      new("block", FctOverlaySettings.ShowBlockKey, FctRow.Word, Labels.Block),
      new("riposte", FctOverlaySettings.ShowRiposteKey, FctRow.Word, Labels.Riposte),
      new("resist", FctOverlaySettings.ShowResistKey, FctRow.Word, Labels.Resist),
      new("absorb", FctOverlaySettings.ShowAbsorbKey, FctRow.Word, Labels.Absorb),
      new("invulnerable", FctOverlaySettings.ShowInvulnerableKey, FctRow.Word, Labels.Invulnerable),
    };

    /* What the dropdown shows: seventeen entries, one alphabetical list, because a player looks for a word. */
    public static readonly IReadOnlyList<Entry> All = Sorted();

    /* Every switch ON, which is what a fresh settings.ini means: these are all opt-outs, so a first overlay shows everything
       without anybody having to discover the list, and an unreadable key fails this way too (FctOverlaySettings.LoadShown). */
    public static void SetAll(FctConfigState state, bool shown)
    {
      foreach (var entry in All)
      {
        entry.Set(state, shown);
      }
    }

    public static void LoadInto(FctConfigState state)
    {
      foreach (var entry in All)
      {
        entry.Set(state, FctOverlaySettings.LoadShown(entry.Key));
      }
    }

    public static void Save(FctConfigState state)
    {
      foreach (var entry in All)
      {
        FctOverlaySettings.SaveShown(entry.Key, entry.Get(state));
      }
    }

    private static List<Entry> Sorted()
    {
      var all = new List<Entry>(Rows.Count + Words.Count);
      all.AddRange(Rows);
      all.AddRange(Words);
      all.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));

      return all;
    }
  }
}
