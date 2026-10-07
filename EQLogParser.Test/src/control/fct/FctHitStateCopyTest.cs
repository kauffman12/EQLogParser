using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable enable annotations

namespace EQLogParser
{
  /*
   * A row state is copied a lot: every placement trial resets one from the pristine row, and the winner's fields are copied
   * back into the object that goes on screen (FctPlacement.FctTrialBench). That copy used to be `MemberwiseClone`, which
   * cannot forget a field; it is now a hand-written list, because "copy into an object that already exists" is the whole
   * reason the search stopped allocating eighteen rows per number.
   *
   * A forgotten field here is not a compile error and not a geometry assertion — it is a row drawn with somebody else's
   * band, clamp or rail tempo, which looks like a layout bug. So the list is held to the type by reflection: put a distinct
   * value in every instance field, copy, and refuse any field that did not travel. The same sweep runs against a target that
   * starts full of different values, because a field never written by `CopyFrom` would otherwise pass by keeping whatever the
   * buffer happened to hold from the last number.
   */
  [TestClass]
  public class FctHitStateCopyTest
  {
    private static readonly FieldInfo[] Fields = typeof(FctHitState)
      .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    [TestInitialize]
    public void ResetAmbient() => FctAmbient.Reset();

    [TestMethod]
    public void TheRowIsStillAWideFlatRecord()
    {
      // Guards the harness itself: if the row ever stops being "just fields", the sweep below would silently cover less.
      Assert.IsTrue(Fields.Length >= 40, $"expected a row of mostly fields, found {Fields.Length}");
      foreach (var f in Fields)
      {
        Assert.IsFalse(f.FieldType.IsByRef, $"{f.Name} is by-ref and cannot be swept");
      }
    }

    [TestMethod]
    public void CloneTakesEveryFieldWithIt()
    {
      var src = new FctHitState();
      Stamp(src, 100);

      var clone = src.Clone();
      AssertMissing(src, clone, "Clone");
    }

    [TestMethod]
    public void CopyFromOverwritesWhateverTheBufferWasHolding()
    {
      var src = new FctHitState();
      Stamp(src, 100);

      // A buffer reused from an earlier number: every field carries a different value that must not survive the copy.
      var buffer = new FctHitState();
      Stamp(buffer, 700);

      var back = buffer.CopyFrom(src);
      Assert.AreSame(buffer, back, "CopyFrom should return the target it wrote");
      AssertMissing(src, buffer, "CopyFrom");
    }

    [TestMethod]
    public void TheProbeWouldSeeFieldThatDidNotTravel()
    {
      // Control: the sweep above is only evidence if it can fail. Two rows differing in one field must be reported.
      var a = new FctHitState();
      Stamp(a, 100);
      var b = new FctHitState();
      Stamp(b, 100);
      b.BandMaxY = a.BandMaxY + 12;

      Assert.AreNotEqual(a.BandMaxY, b.BandMaxY);
      Assert.IsTrue(Fields.Any(f => !Equals(f.GetValue(a), f.GetValue(b))),
        "the field sweep cannot see a difference between two rows — it would pass on a copy that does nothing");
    }

    private static void Stamp(FctHitState target, int seed)
    {
      var i = 0;
      foreach (var f in Fields)
      {
        i++;
        f.SetValue(target, Sample(f.FieldType, seed + i));
      }
    }

    private static object Sample(Type type, int n)
    {
      if (type == typeof(bool)) return (n % 3) != 0;
      if (type == typeof(string)) return $"v{n}";
      if (type == typeof(int)) return n;
      if (type == typeof(double)) return n + 0.25;
      if (type.IsEnum) return Enum.ToObject(type, n % Enum.GetValues(type).Length);
      throw new AssertInconclusiveException($"unhandled field type {type.Name} - extend the sweep, do not skip it");
    }

    private static void AssertMissing(FctHitState expected, FctHitState actual, string what)
    {
      var stale = Fields.Where(f => !Equals(f.GetValue(expected), f.GetValue(actual)))
                        .Select(f => $"{f.Name}: expected {f.GetValue(expected)}, got {f.GetValue(actual)}")
                        .ToList();
      Assert.AreEqual(0, stale.Count,
        $"{what} left {stale.Count} field(s) behind — add them to FctHitState.CopyFrom:\n  " + string.Join("\n  ", stale));
    }
  }
}
