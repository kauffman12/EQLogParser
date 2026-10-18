using System.Reflection;

namespace EQLogParser
{
  [TestClass]
  public class FctHitStateCopyTest
  {
    private static readonly FieldInfo[] Fields = typeof(FctHitState)
      .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    [TestInitialize]
    public void ResetAmbient() => FctAmbient.Reset();

    [TestMethod]
    [DataRow(100)]
    [DataRow(101)]
    public void CloneTakesEveryFieldWithIt(int seed)
    {
      var src = new FctHitState();
      Stamp(src, seed);
      AssertMissing(src, src.Clone(), "Clone");
    }

    [TestMethod]
    public void CopyFromOverwritesWhateverTheBufferWasHolding()
    {
      var src = new FctHitState();
      Stamp(src, 100);
      var buffer = new FctHitState();
      // Opposite parity also makes every boolean differ; 601 is not a multiple of the enum vocabularies.
      Stamp(buffer, 701);

      var back = buffer.CopyFrom(src);
      Assert.AreSame(buffer, back, "CopyFrom should return the target it wrote");
      AssertMissing(src, buffer, "CopyFrom");
    }

    private static void Stamp(FctHitState target, int seed)
    {
      var i = 0;
      foreach (var field in Fields) field.SetValue(target, Sample(field.FieldType, seed + ++i));
    }

    private static object Sample(Type type, int n)
    {
      if (type == typeof(bool)) return n % 2 == 0;
      if (type == typeof(string)) return $"v{n}";
      if (type == typeof(int)) return n;
      if (type == typeof(double)) return n + 0.25;
      if (type.IsEnum) return Enum.ToObject(type, n % Enum.GetValues(type).Length);
      throw new AssertFailedException($"Unhandled field type {type.Name}; extend the copy fixture.");
    }

    private static void AssertMissing(FctHitState expected, FctHitState actual, string what)
    {
      var stale = Fields.Where(f => !Equals(f.GetValue(expected), f.GetValue(actual)))
        .Select(f => $"{f.Name}: expected {f.GetValue(expected)}, got {f.GetValue(actual)}").ToList();
      Assert.AreEqual(0, stale.Count,
        $"{what} left {stale.Count} field(s) behind:\n  " + string.Join("\n  ", stale));
    }
  }
}
