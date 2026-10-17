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
     * consume heals without re-parsing. Fires on the log reader thread, replay and live alike — on the lane that
     * parsed the line; this static event is the forwarder onto the Main lane every session subscriber uses. */
    public static event Action<HealProcessedEvent> EventsHealProcessed
    {
      add => ParseLane.Subscribe(ref ParseLane.Main.HealProcessed, value);
      remove => ParseLane.Unsubscribe(ref ParseLane.Main.HealProcessed, value);
    }

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
            /*
             * No copy goes to RecordsStore any more. This record's permanent home is the capture's heal fact table
             * (the tap beside this method appends the row), and HealRecordSource hands records back out of those rows
             * to the two surfaces that used to read a live object list — measured at 766,713 HealRecord / 29.25 MB
             * retained for the whole length of a session, on data already stored in 32-byte rows. The record itself
             * stays allocated here because the FCT overlay and EventsHealProcessed want it NOW; it simply stops being
             * remembered (docs/DesignNotes.md → "Where a large capture's bytes actually are").
             */

            // hoisted so the event object is not built per heal when nothing is listening (FCT off)
            var healHandler = ParseLane.Current.HealProcessed;
            if (healHandler is not null)
            {
              healHandler(new HealProcessedEvent { Record = record, BeginTime = lineData.BeginTime, IsMonitor = lineData.IsMonitor });
            }
          }

          return true;
        }
      }
      catch (Exception ex) when (ex is ArgumentNullException or NullReferenceException or ArgumentOutOfRangeException or ArgumentException)
      {
        /*
         * The same four shapes this method has always swallowed, in one guard so the voice-cap below has somewhere to live: anything else still
         * escapes, and a per-line fault speaks once with the line that threw rather than once per line (RepeatCap).
         */
        if (RepeatCap.SpeaksOnce($"heal.Process {ex.GetType().Name}"))
        {
          Log.Error($"heal line parse failed (this failure repeats silently): {ex} | line: {RepeatCap.Clip(lineData.Action)}");
        }
      }

      return false;
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

    /*
     * The actor named at the head of a heal sentence, with the two things that are not part of a name taken off:
     * an active perfect auxiliary (`The gods have` -> `The gods`) and any passive shape, which has no actor in it at
     * all (`Foob is healed by a reflected flame.` - the subject is the patient, so this answers empty and the line
     * stays dropped the way it always was).
     */
    internal static string SubjectActor(string subject)
    {
      var s = subject.Trim();
      if (s.Length == 0)
      {
        return "";
      }

      var lastSpace = s.LastIndexOf(' ');
      if (lastSpace <= 0 || lastSpace >= s.Length - 1)
      {
        return IsPassiveAuxiliary(s) ? "" : s;
      }

      var tail = s[(lastSpace + 1)..];
      if (IsPassiveAuxiliary(tail))
      {
        return "";
      }

      return tail is "has" or "have" or "had" ? s[..lastSpace].Trim() : s;
    }

    private static bool IsPassiveAuxiliary(string word) =>
      word.Equals("is", StringComparison.OrdinalIgnoreCase)
      || word.Equals("are", StringComparison.OrdinalIgnoreCase)
      || word.Equals("was", StringComparison.OrdinalIgnoreCase)
      || word.Equals("were", StringComparison.OrdinalIgnoreCase)
      || word.Equals("be", StringComparison.OrdinalIgnoreCase)
      || word.Equals("been", StringComparison.OrdinalIgnoreCase)
      || word.Equals("am", StringComparison.OrdinalIgnoreCase);

    /*
     * Whether the subject of a heal line is the SPELL rather than a fighter: `Bastion of Divinity healed Xxuro over
     * time for 6670 hit points by Bastion of Divinity Effect.` - the "by" clause names the same thing the subject
     * does, which is how a cast-less line tells itself apart from `a killer moth healed Shmid … by Divine
     * Interstition Rk. III.`. A rank tail (`Rk. II`) is not part of the name for this comparison, because the shape
     * arrives both with and without it (see IsSentenceEnd). This is what keeps the standing law - nobody is named on
     * a cast-less heal, so nothing is stored - while the multi-word subject fallback runs.
     */
    internal static bool IsSubjectIsSpell(string subject, string spell)
    {
      if (string.IsNullOrEmpty(spell))
      {
        return false;
      }

      var s = subject.Trim();
      var rank = s.LastIndexOf(" Rk.", StringComparison.OrdinalIgnoreCase);
      if (rank > 0)
      {
        s = s[..rank].Trim();
      }

      return s.Length >= 3 && spell.StartsWith(s, StringComparison.OrdinalIgnoreCase);
    }

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
      var subjectFallback = false;   // true when the healer came from the whole subject of a one-sentence line
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
          else if ((healer = SubjectActor(test)) != string.Empty)
          {
            subjectFallback = true;

            /*
             * A multi-word subject is the actor: `a killer moth healed Shmid for 852000 hit points by Divine
             * Interstition Rk. III.` and `Tallongast, The Egg healed Foob …` name their healer in full, and this
             * branch used to leave `healer` empty, so the IsNullOrEmpty guard below dropped the row - 2,297 lines /
             * 16.5 M healing, plus 29,660 lines / 52.1 M of pet self-heals (`Jazrakhan`s pet healed itself for 6109
             * hit points by Mark of the Righteous XIV Rk. III.`), measured on one real capture. The owner of a pet
             * is NOT the healer of a Mark of the Righteous tick: it is a reverse damage shield riding on the pet, and
             * the pet is the thing that healed itself (operator ruling). An item ward is the opposite case and keeps
             * folding to the player who dropped it, which is why the ward branch runs first.
             *
             * Three shapes must NOT get a healer from this:
             *   - a PASSIVE line (`Foob is healed by a reflected flame.`): the subject is the patient. An auxiliary
             *     in the subject is the tell; the "has been"/"have been"/"are" branches above own the passives they
             *     recognise and this refuses the rest of that family.
             *   - the active perfect (`The gods have healed Shmid for 852000 points of damage.`): the auxiliary is
             *     not part of the name, so strip it.
             *   - a CAST-LESS heal whose subject is the SPELL (`Bastion of Divinity healed Xxuro over time for 6670
             *     hit points by Bastion of Divinity Effect.`): see IsSubjectIsSpell. The standing law there is that
             *     nobody is named, and putting the spell on the healer seat would move the healing board.
             */
            healer = SubjectActor(test);
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
            var digits = part.AsSpan(amountIndex, amountEnd - amountIndex);

            /*
             * EQ prints some heal-over-time ticks NEGATIVE - `Tilwedarx healed Foob over time for -146133 (47344) hit
             * points by Abundant Healing LXIX.` - and the tick did not hurt anybody: the magnitude is what landed
             * (operator ruling; measured at 84 lines in one capture, all of them HOT). ParseUInt refuses the sign, so
             * before this the whole row was lost. HOT only: no other heal shape is known to print this way, and a
             * minus on a direct heal is a line this parser does not understand, which is worth more than a guess.
             */
            if (digits.Length > 1 && digits[0] == '-' && type == Labels.Hot)
            {
              digits = digits[1..];
            }

            var value = TextUtils.ParseUInt(digits);
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

        /*
         * A spell subject is not a person: drop the cast-less line, per IsSubjectIsSpell. This applies to the fallback
         * only, so every shape the branch chain used to name (the "has" branch, and the `… by Theft of Essence …`
         * filler below) keeps the answer it has always given.
         */
        if (subjectFallback && IsSubjectIsSpell(test, spell))
        {
          return null;
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
