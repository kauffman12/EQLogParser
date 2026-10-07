namespace EQLogParser;

/*
 * A clock for fixtures that build fact rows.
 *
 * Facts store their second as seconds-since-2000 (FactTime), which is the rebasing that took a damage row from 32 bytes
 * to 24. That window is 1931-2068, and a test's `T0 = 1_000` — a stand-in "second" that was only ever meant to be
 * subtracted from, never read as a date — sits outside it and would CLAMP, giving every fact in the fixture the same
 * timestamp and turning a projection test into a test that everything happened at once. So fixture clocks are real-shaped:
 * 2025-01-01 in dotnet-epoch seconds, which is what the parser actually hands the capture, and any clamp in a test run is
 * therefore a bug rather than a fixture's shorthand (asserted by FactTimeTest).
 */
internal static class FixtureTime
{
    /// <summary>2025-01-01T00:00:00 as dotnet-epoch seconds — 63,871,286,400.</summary>
    internal const double Base = 63_871_286_400d;

    /// <summary>The same constant where a long is wanted (fact constructors take dotnet-epoch seconds as a long).</summary>
    internal const long BaseL = 63_871_286_400L;
}
