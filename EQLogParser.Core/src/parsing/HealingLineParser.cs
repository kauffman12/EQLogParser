using EQLogParser;
using System;
using log4net;
using System.Reflection;

namespace EQLogParser
{
  internal class HealingLineParser
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    /* Mirrors DamageLineParser.EventsDamageProcessed so the FCT feed (and anything else) can
     * consume heals without re-parsing. Fires on the log reader thread, replay and live alike. */
    public static event Action<HealProcessedEvent> EventsHealProcessed;

    // Heal lines run at about a quarter of the damage rate on the logs this was measured against.
    private const int ExpectedHealOffers = 1_000_000;

    /* A raid restates the same heal over and over — on a two night log, three quarters of the heal lines
     * repeat one already seen. HealRecord is equal by value, so a repeat costs a slot in the store instead of
     * another object, and the objects stay alive as long as the loaded log does. Sharing an instance means
     * sharing it with the FCT feed and the store both, which heals tolerate because nothing writes to a heal
     * after it is handed out — unlike damage, where HandleDamageProcessed rewrites record.Attacker some hundred
     * lines after its own cache lookup, changing every earlier event that shared the instance and moving a live
     * key. First sightings take no entry, as in the damage line's own RepeatStore cache; numbers in
     * docs/DesignNotes.md → What a loaded raid costs in memory. */
    private static readonly RepeatStore<HealRecord> _healCache = new(ExpectedHealOffers);

    private HealingLineParser()
    {

    }

    public static bool Process(LineData lineData)
    {
      var action = lineData.Action;
      try
      {
        int index;
        if (action.Length >= 23 && (index = action.LastIndexOf(" healed ", action.Length, StringComparison.Ordinal)) > -1 &&
          HandleHealed(action, index, lineData.BeginTime) is { } record)
        {
          /*
           * A heal spent on somebody's summoned eye is the same non-event as a hit on it: the summon takes one blow
           * and dies, and the raid's answer to it is a wasted click, not healing. Legacy already refused the damage
           * side (DamageLineParser.InIgnoreList) and this refuses the heal side, in the parser rather than in a
           * viewer, because dropping it later would still intern the eye's name — and an ignored name that reaches
           * the engine's pool sits in the identity list as a permanent Unknown for a thing the pipeline has already
           * decided not to count. The line is still a heal line: handled, just not stored (R19,
           * ClassificationRules.EyeSummonOwnerInName).
           */
          if (ClassificationRules.EyeSummonOwnerInName(record.Healed) is null)
          {
            record = GetCachedHealRecord(record);

            /*
             * No copy goes to RecordsStore any more. This record's permanent home is the capture's heal fact table
             * (the tap beside this method appends the row), and HealRecordSource hands records back out of those rows
             * to the two surfaces that used to read a live object list — measured at 766,713 HealRecord / 29.25 MB
             * retained for the whole length of a session, on data already stored in 32-byte rows. The record itself
             * stays allocated here because the FCT overlay and EventsHealProcessed want it NOW; it simply stops being
             * remembered (docs/DesignNotes.md → "Where a large capture's bytes actually are").
             */

            // hoisted so the event object is not built per heal when nothing is listening (FCT off)
            var healHandler = EventsHealProcessed;
            if (healHandler is not null)
            {
              healHandler(new HealProcessedEvent { Record = record, BeginTime = lineData.BeginTime, IsMonitor = lineData.IsMonitor });
            }
          }

          return true;
        }
      }
      catch (ArgumentNullException ne)
      {
        Log.Error(ne);
      }
      catch (NullReferenceException nr)
      {
        Log.Error(nr);
      }
      catch (ArgumentOutOfRangeException aor)
      {
        Log.Error(aor);
      }
      catch (ArgumentException ae)
      {
        Log.Error(ae);
      }

      return false;
    }

    /*
     * Drops the shared heal instances. Called when the manager clears active data.
     */
    internal static void ClearCaches() => _healCache.Clear();

    // Exact repeats of a heal collapse onto one instance; two heals that differ in any stored field at all
    // — target, spell, amount, overage, modifiers — stay separate records.
    private static HealRecord GetCachedHealRecord(HealRecord incoming)
    {
      if (_healCache.TryGet(incoming, out var cached))
      {
        return cached;
      }

      _healCache.Offer(incoming);
      return incoming;
    }

    /*
     * Whether the character at `index` is the end of a SENTENCE, i.e. whether the healer's name starts right after
     * it. The client glues two sentences onto one log line ("Your ward heals you as it breaks! You healed Niktaza
     * for 8970 (86306) hit points by Healing Ward.", "Rowanoak is soothed by Brell's Soothing Wave. Farzi healed
     * Rowanoak for 524 …"), so a period or bang before the last word means "the actor begins here" - and EQ person
     * names are single tokens, which is why taking that last word has always worked for them.
     *
     * A spell's RANK formulation is not a sentence end. The client writes `Bastion of Divinity Rk. II healed Xxuro
     * over time for 6670 hit points by Bastion of Divinity Effect II.` with the SPELL as subject and nobody named, and
     * the old test read the period of "Rk." as a sentence end: it took the word after it and minted a healer called
     * "II" (measured on eqlog_Kizant_xegony-2.txt: 8 such lines, names "II" and "III", both landing in the identity
     * list as permanent Unknown rows - the only way a rank fragment can be born anywhere in this parser, since no
     * other branch takes a name from after a period). With the marker recognised the subject is what it always was,
     * not a person, and the line is dropped exactly like its rank-free sibling "Bastion of Divinity healed Xxuro over
     * time for 6670 hit points by Bastion of Divinity Effect.", which has no healer and has never been stored.
     * Resurrecting caster-less heals onto the spell's own name is a separate decision (it would move the healing
     * board); what this refuses is inventing a fighter out of a roman numeral.
     */
    private static bool IsSentenceEnd(string test, int index) =>
      test[index] == '!' || (test[index] == '.' &&
        !(index >= 2 && char.ToUpperInvariant(test[index - 2]) == 'R' && char.ToUpperInvariant(test[index - 1]) == 'K'));

    private static HealRecord HandleHealed(string part, int optional, double beginTime)
    {
      // [Sun Feb 24 21:00:58 2019] Foob's promised interposition is fulfilled Foob healed himself for 44238 hit points by Promised Interposition Heal V. (Lucky Critical)
      // [Sun Feb 24 21:01:01 2019] Rowanoak is soothed by Brell's Soothing Wave. Farzi healed Rowanoak for 524 hit points by Brell's Sacred Soothing Wave.
      // [Sun Feb 24 21:00:52 2019] Kuvani healed Tolzol over time for 11000 hit points by Spirit of the Wood XXXIV.
      // [Sun Feb 24 21:00:52 2019] Kuvani healed Foob over time for 9409 (11000) hit points by Spirit of the Wood XXXIV.
      // [Sun Feb 24 21:00:58 2019] Fllint healed Foob for 11820 hit points by Blessing of the Ancients III.
      // [Sun Feb 24 21:01:00 2019] Tolzol healed itself for 548 hit points.
      // [Sun Feb 24 21:01:01 2019] Piemastaj`s pet has been healed for 15000 hit points by Enhanced Theft of Essence Effect X.
      // [Sun Feb 24 23:30:51 2019] Piemastaj`s pet glows with holy light. Findawenye healed Piemastaj`s pet for 2823 (78079) hit points by Mending Splash Rk. III. (Critical)
      // [Mon Feb 18 21:21:12 2019] Nylenne has been healed over time for 8211 hit points by Roar of the Lion 6.
      // [Mon Feb 18 21:20:39 2019] You have been healed over time for 1063 (8211) hit points by Roar of the Lion 6.
      // [Mon Feb 18 21:17:35 2019] Snowzz healed Malkatar over time for 8211 hit points by Roar of the Lion 6.
      // [Wed Nov 06 14:19:54 2019] Your ward heals you as it breaks! You healed Niktaza for 8970 (86306) hit points by Healing Ward. (Critical)

      HealRecord record = null;
      var test = part[..optional];

      var done = false;
      var healer = "";
      var healed = "";
      string spell = null;
      string subType = null;
      var type = Labels.Heal;
      var heal = uint.MaxValue;
      uint overHeal = 0;

      var previous = test.Length >= 2 ? test.LastIndexOf(' ', test.Length - 2) : -1;
      if (previous > -1)
      {
        if (test.IndexOf("are ", previous + 1, StringComparison.Ordinal) > -1)
        {
          done = true;
        }
        else if ((previous - 1 >= 0 && IsSentenceEnd(test, previous - 1)) || (previous - 9 > 0 &&
          test.IndexOf("fulfilled", previous - 9, StringComparison.Ordinal) > -1))
        {
          healer = test[(previous + 1)..];
        }
        else if (previous - 4 >= 0 && test.IndexOf("has been", previous - 3, StringComparison.Ordinal) > -1)
        {
          healed = test[..(previous - 4)];

          if (part.Length > optional + 17 && part.IndexOf("over time", optional + 8, 9, StringComparison.Ordinal) > -1)
          {
            type = Labels.Hot;
          }
        }
        else if (previous >= 0 && test.IndexOf("has", previous, StringComparison.Ordinal) > -1)
        {
          healer = test[..previous];
          type = Labels.Heal;
          subType = Labels.Heal;
        }
        else if (previous - 5 >= 0 && test.IndexOf("have been", previous - 4, StringComparison.Ordinal) > -1)
        {
          healed = test[..(previous - 5)];

          if (part.Length > optional + 17 && part.IndexOf("over time", optional + 8, 9, StringComparison.Ordinal) > -1)
          {
            type = Labels.Hot;
          }
        }
        else
        {
          var wardIndex = test.IndexOf("`s ward", StringComparison.OrdinalIgnoreCase);
          if (wardIndex > 0)
          {
            // assign owner of ward as healer
            healer = test[..wardIndex];
          }
        }
      }
      else
      {
        healer = test[..optional];
      }

      if (!done)
      {
        var amountIndex = -1;
        if (healed.Length == 0)
        {
          var afterHealed = optional + 8;
          var forIndex = part.IndexOf(" for ", afterHealed, StringComparison.Ordinal);

          if (forIndex > 1)
          {
            if (forIndex - 9 >= 0 && part.IndexOf("over time", forIndex - 9, StringComparison.Ordinal) > -1)
            {
              type = Labels.Hot;
              healed = part.Substring(afterHealed, forIndex - afterHealed - 10);
            }
            else
            {
              healed = part[afterHealed..forIndex];
            }

            amountIndex = forIndex + 5;
          }
        }
        else
        {
          if (type == Labels.Heal)
          {
            amountIndex = optional + 12;
          }
          else if (type == Labels.Hot)
          {
            amountIndex = optional + 22;
          }
        }

        if (amountIndex > -1)
        {
          var amountEnd = part.IndexOf(' ', amountIndex);
          if (amountEnd > -1)
          {
            var value = TextUtils.ParseUInt(part[amountIndex..amountEnd]);
            if (value != uint.MaxValue)
            {
              heal = value;
            }

            var overEnd = -1;
            if (part.Length > amountEnd + 1 && part[amountEnd + 1] == '(')
            {
              overEnd = part.IndexOf(')', amountEnd + 2);
              if (overEnd > -1)
              {
                var value2 = TextUtils.ParseUInt(part.AsSpan(amountEnd + 2, overEnd - amountEnd - 2));
                if (value2 != uint.MaxValue)
                {
                  overHeal = value2;
                }
              }
            }

            var rest = overEnd > -1 ? overEnd : amountEnd;
            var byIndex = part.IndexOf(" by ", rest, StringComparison.Ordinal);
            if (byIndex > -1)
            {
              var periodIndex = part.LastIndexOf('.');
              if (periodIndex > -1 && periodIndex - byIndex - 4 > 0)
              {
                spell = part.Substring(byIndex + 4, periodIndex - byIndex - 4);
              }
            }
          }
        }

        // verify heal actually parsed
        if (heal == uint.MaxValue)
          return null;

        if (string.IsNullOrEmpty(healed))
          return null;

        // fix healer
        if (string.IsNullOrEmpty(healer) && spell?.StartsWith("Theft of Essence", StringComparison.OrdinalIgnoreCase) is true)
        {
          healer = Labels.Unk;
        }

        // verify healer parsed properly
        if (string.IsNullOrEmpty(healer) || healer.Length > 64)
          return null;

        healer = ParserUtil.ReplacePlayer(healer, ConfigUtil.PlayerName, healed);
        healed = ParserUtil.ReplacePlayer(healed, ConfigUtil.PlayerName, healer);

        // check for pets
        var possessive = healed.IndexOf("`s ", StringComparison.Ordinal);
        if (possessive > -1 && IdentityLookup.IsNameOfOurPerson(healed[..possessive]))
        {
          PlayerRegistry.Instance.AddVerifiedPet(healed);
        }

        // found a bst/mag/nec pet
        if (spell?.StartsWith("Mend Companion", StringComparison.OrdinalIgnoreCase) is true ||
          spell?.StartsWith("Warder's Shielding", StringComparison.OrdinalIgnoreCase) is true ||
          spell?.StartsWith("Might of the Wild Spirits", StringComparison.OrdinalIgnoreCase) is true)
        {
          if (PlayerRegistry.IsPossiblePlayerName(healer))
          {
            PlayerRegistry.Instance.AddVerifiedPlayer(healer, beginTime);
            PlayerRegistry.Instance.AddPetToPlayer(healed, healer);
          }
        }

        // fix subtype
        if (subType == null)
        {
          subType = string.IsNullOrEmpty(spell) ? Labels.SelfHeal : StringCache.GetOrAdd(spell);
        }

        record = new HealRecord
        {
          Total = heal,
          OverTotal = overHeal,
          Healer = StringCache.GetOrAdd(healer),
          Healed = StringCache.GetOrAdd(healed),
          // no GetOrAdd: a label is one of two words the table already holds, and there is nothing to share
          Type = type,
          ModifiersMask = -1,
          SubType = subType
        };

        if (part[^1] == ')')
        {
          // using 4 here since the shortest modifier should at least be 3 even in the future. probably.
          var firstParen = part.LastIndexOf('(', part.Length - 4);
          if (firstParen > -1)
          {
            record.ModifiersMask = LineModifiersParser.ParseHeal(record.Healer,
              part.Substring(firstParen + 1, part.Length - 1 - firstParen - 1), beginTime);
          }
        }
      }

      return record;
    }
  }
}
