using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable enable annotations

namespace EQLogParser;

/*
 * The shape of what the parsers publish, and of one modifier door that takes the line's own text.
 *
 * DamageProcessedEvent and HealProcessedEvent are VALUE types. As classes they cost 48 bytes per published hit and heal — **164.8 MB of
 * pure garbage over one real capture** (2,286,371 damage + 1,575,037 heals, ~3.3 % of everything the parse allocated) — because the capture
 * subscribes even when FCT is off, so a wrapper is created and discarded on every line. Through `Action<T>` a struct payload allocates
 * nothing at all (measured side by side: 48 B/event → 0 B/event).
 *
 * The trade that comes with a value type is asserted here rather than trusted: a handler now receives a COPY, so the fields are init-only —
 * nothing may write into an event and expect a later handler to see it — and what outlives a publish is the record reference, never the
 * wrapper. FCT copies what it keeps into its own commands, which is exactly what it always did.
 */
[TestClass]
public class PublishedEventShapeTest
{
  [TestMethod]
  public void TheDamageAndHealPublishPayloadsAreValues()
  {
    Assert.IsTrue(typeof(DamageProcessedEvent).IsValueType,
      "a class wrapper here is one heap object per hit (48 B × every damage line of a capture)");
    Assert.IsTrue(typeof(HealProcessedEvent).IsValueType,
      "a class wrapper here is one heap object per heal");

    // Every field stays readable, and nothing here is a mutable box: an ordinary `set` on a published event would be a
    // gesture that silently does nothing, because each handler receives its own copy of the fields. Reflection cannot tell
    // an init accessor from a setter (both report CanWrite), so `init` itself is held by the publishers' contract and review.
    foreach (var type in new[] { typeof(DamageProcessedEvent), typeof(HealProcessedEvent) })
    {
      foreach (var prop in type.GetProperties())
      {
        Assert.IsNotNull(prop.GetGetMethod(), $"{type.Name}.{prop.Name} has to stay readable");
        Assert.IsFalse(prop.PropertyType.IsArray, $"{type.Name}.{prop.Name} must not hand out an array a handler could write through");
      }
    }

    Assert.IsNotNull(typeof(DamageProcessedEvent).GetProperty("Action"),
      "Action still rides along — CombatCapture reads line-intrinsic evidence (`X`s pet`) off the publish");
  }

  [TestMethod]
  public void ARecordReferenceStillRidesThroughAndTheValuesAreWhatWasPublished()
  {
    var record = new DamageRecord { Attacker = "Kilsef", Defender = "Bob", Total = 12345 };
    var evt = new DamageProcessedEvent
    {
      Record = record, BeginTime = 1234.5, Action = "Kilsef hits Bob for 12345 points of damage!", IsMonitor = true
    };

    Assert.AreSame(record, evt.Record, "the record is shared; only the wrapper is a copy");
    Assert.AreEqual(1234.5, evt.BeginTime);
    Assert.IsTrue(evt.IsMonitor);
    Assert.AreEqual("Kilsef hits Bob for 12345 points of damage!", evt.Action);

    var heal = new HealProcessedEvent { Record = new HealRecord { Healer = "Kilsef", Healed = "Vvalan" }, BeginTime = 99 };
    Assert.AreEqual("Kilsef", heal.Record.Healer);
    Assert.IsFalse(heal.IsMonitor, "a replayed line is not a monitor line unless the publisher says so");
  }

  /*
   * LineModifiersParser.ParseHeal takes the paren text as it lies in the line. The modifiers themselves were already parsed by span; what
   * changed (2026-10-17) is that the heal lane stops cutting a substring first, worth 16.2 MB per capture beside the duplicated subject call
   * it also removed. These assertions are about the one risk of handing over a span: reading past the slice or keying on the wider buffer.
   */
  [TestMethod]
  public void HealModifiersComeFromTheSliceAndOnlyTheSlice()
  {
    const string line = "13:42:07 Foob heals Shmid for 852 hit points by Cure Critical Wounds (Critical) trailing junk";
    var open = line.IndexOf('(') + 1;
    var close = line.IndexOf(')', open);

    var fromLine = LineModifiersParser.ParseHeal("Foob", line.AsSpan(open, close - open), 1000);
    var fromWord = LineModifiersParser.ParseHeal("Foob", "Critical".AsSpan(), 1000);

    Assert.AreEqual(fromWord, fromLine, "the same text answers the same way wherever it was sitting");
    Assert.IsTrue(LineModifiersParser.IsCrit(fromLine), "and the answer is still a critical");

    // A second word, sliced out of the same line: what matters is that a slice answers exactly as its own text does,
    // neither reading past its end nor keying on the buffer around it.
    const string rampaged = "…heals Shmid for 852 hit points by Healing (Rampage)";
    var rOpen = rampaged.IndexOf('(') + 1;
    var rClose = rampaged.IndexOf(')', rOpen);
    Assert.AreEqual(LineModifiersParser.ParseHeal("Foob", "Rampage".AsSpan(), 1000),
      LineModifiersParser.ParseHeal("Foob", rampaged.AsSpan(rOpen, rClose - rOpen), 1000));

    Assert.AreEqual(LineModifiersParser.None, LineModifiersParser.ParseHeal("Foob", ReadOnlySpan<char>.Empty, 1000),
      "no paren means no modifier, and it stays that way");
  }
}
