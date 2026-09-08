using System;

namespace EQLogParser
{
  /*
   * Overlay presentation settings kept in settings.ini instead of a dialog — there is no FCT configuration UI yet, and
   * the one that exists today (motion style) is a switch a player flips while deciding which reads better in their own HUD,
   * so it lives on the overlay header rather than in a modal. Deliberately outside the fct/
   * folder: the layout and motion maths stay free of ConfigUtil so they can be unit tested on their own, and this is
   * the only place that knows these key names or their historical spelling.
   */
  internal static class FctOverlaySettings
  {
    public const string MotionKey = "FctOverlayMotion";

    /*
     * The two modes the configure row offers, over the engine's three schemes: fountain is bands geometry wearing spray
     * motion (numbers spew out of a clear middle; its only settings are the two direction dials), and split is the side
     * columns with a shape — parabola or straight. Engine names stay where the geometry is tested; ini speaks the words
     * the player chose. The retired FctOverlayLayout key ("halves"/"bytype"/"bands") belonged to the layout+motion combos
     * this replaced and is no longer read — nothing shipped, so nothing migrates.
     */
    public const string ModeKey = "FctOverlayMode";
    public const string ShapeKey = "FctOverlayShape";

    /*
     * The region scheme and how it is oriented (see FctStage): which side incoming owns in halves, which side healing owns
     * in by type, and which way each side travels. halves is the shipped default — with its failure mode fixed — and these
     * keys are read on every start; missing values land on that choice, junk on bands (ParseMode explains the split).
     */
    public const string LayoutKey = "FctOverlayLayout";
    public const string IncomingSideKey = "FctOverlayIncomingSide";

    /* Split's categories, per category: which column it owns and which way it travels. Healing has always asked;
       the two damage streams learned to answer separately in the same engine pass that made bands steerable. */
    public const string HealSideKey = "FctOverlayHealSide";
    public const string HealDirectionKey = "FctOverlayHealDirection";
    public const string TakenDamageSideKey = "FctOverlayTakenDamageSide";
    public const string DealtDamageSideKey = "FctOverlayDealtDamageSide";

    /*
     * Which categories of number the overlay draws at all (the gate in FctIngest): my damage, damage on me, and
     * healing either direction. Absent or unreadable means ON — these are opt-outs by design ("hide what I don't
     * want"), so a fresh overlay shows everything without anybody having to switch it on, and a corrupt value fails
     * toward information rather than toward silence.
     */
    public const string ShowDealtKey = "FctOverlayShowDealt";
    public const string ShowTakenKey = "FctOverlayShowTaken";
    public const string ShowHealsKey = "FctOverlayShowHeals";
    public const string IncomingDirectionKey = "FctOverlayIncomingDirection";
    public const string OutgoingDirectionKey = "FctOverlayOutgoingDirection";

    /* The two dials on the configure row, stored as multipliers rather than percentages: the file is a place where a human may look, and the value
       that means something to the code is the one worth writing. Size runs 0.5 - 1.5; speed runs 0.76 - 2.28 around a shipped 1.14, which are the ends of
       a dial measured in percent of time taken rather than in rate (see FctScale). FctScale clamps on the way in, so a hand-edited "big" or 12 lands on
       that dial's default instead of drawing nothing or running at four times tempo. */
    public const string TextScaleKey = "FctOverlayTextScale";
    public const string SpeedKey = "FctOverlaySpeed";

    public static double LoadTextScale() => FctScale.ClampSize(ConfigUtil.GetSettingAsDouble(TextScaleKey, FctScale.SizeDefault));

    public static void SaveTextScale(double scale) => ConfigUtil.SetSetting(TextScaleKey, FctScale.ClampSize(scale));

    /*
     * Retired: FctOverlayTimeScale, which held a duration multiplier while that dial was called "time". It is still read once when the speed key is
     * absent and inverted on the way through, because 0.8 in that key meant "20 % shorter", which is 25 % faster - reusing the number as if it were
     * a speed would quietly set most people's overlay to run slow, in the opposite direction from the one they asked for. It is never written again,
     * so an old key simply stops being mentioned after one session.
     */
    private const string LegacyTimeScaleKey = "FctOverlayTimeScale";

    public static double LoadSpeed()
    {
      var stored = ConfigUtil.GetSettingAsDouble(SpeedKey, double.NaN);
      if (double.IsFinite(stored))
      {
        return FctScale.ClampSpeed(stored);
      }

      /* Absent on both counts: the shipped tempo. A legacy 1.0 is a stored preference for the pace that used to ship, and stays slower than this. */
      return FctScale.SpeedFromTime(ConfigUtil.GetSettingAsDouble(LegacyTimeScaleKey, FctScale.TimeDefault));
    }

    public static void SaveSpeed(double speed) => ConfigUtil.SetSetting(SpeedKey, FctScale.ClampSpeed(speed));

    /* Absent or junk reads as ON — see the Show* keys above: these controls are opt-outs, so failing toward showing
     * everything is the direction that never hides somebody's heals by accident. */
    public static bool LoadShown(string key)
    {
      var raw = ConfigUtil.GetSetting(key, null);
      return !(string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase)
        || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase));
    }

    public static void SaveShown(string key, bool shown) => ConfigUtil.SetSetting(key, shown ? "1" : "0");

    /*
     * "Hide under": the damage threshold (MSBT's damageThreshold, off by default like theirs) offered as a ladder —
     * off, 250, 500, 1k, 2k, 5k — rather than a continuum, because the dial's whole job is "stop the white noise",
     * which wants three positions a glance can set, not eighty. Stored as a number; loading snaps to the nearest rung,
     * so a hand-edited settings.ini of 300 becomes a threshold the combo can actually show rather than a filter that
     * works while its control lies about it. Junk reads as off, which is the MSBT default and the safe direction.
     */
    public const string ThresholdKey = "FctOverlayThreshold";

    internal static readonly double[] ThresholdLadder = [0d, 250d, 500d, 1000d, 2000d, 5000d];

    public static double LoadThreshold() => SnapToLadder(ConfigUtil.GetSettingAsDouble(ThresholdKey, 0));

    public static void SaveThreshold(double value) => ConfigUtil.SetSetting(ThresholdKey, SnapToLadder(value));

    /* Pure for testability: nearest rung wins; nothing and nonsense both land on off. */
    internal static double SnapToLadder(double value)
    {
      if (!double.IsFinite(value) || value <= 0)
      {
        return 0;
      }

      var best = ThresholdLadder[0];
      for (var i = 1; i < ThresholdLadder.Length; i++)
      {
        if (Math.Abs(value - ThresholdLadder[i]) < Math.Abs(value - best))
        {
          best = ThresholdLadder[i];
        }
      }

      return best;
    }

    /*
     * Whether anybody has ever pressed Save on the configure row. The overlay's defaults are defensible but they are opinions, and a first enable that
     * shows nothing but numbers being what the build decided keeps a player from learning that size, speed and motion are theirs to set - so the menu
     * opens configure mode instead (see MainWindow.SetFctOverlayVisible). Written only by Save: Cancel, Esc and closing all leave it unset, which means
     * the offer comes back rather than nagging into a setting nobody agreed to.
     */
    public const string ConfiguredKey = "FctOverlayConfigured";

    public static bool IsConfigured() => ConfigUtil.IfSet(ConfiguredKey);

    public static void SaveConfigured() => ConfigUtil.SetSetting(ConfiguredKey, true);

    /*
     * A stored choice is read back through the same pure helpers that guard every other key, so junk lands on a shipped
     * default instead of reaching geometry as garbage. Omitted directions are passed through as null on purpose: bands'
     * outward invariant (in sinks, out rises) is what "never set" must mean there, and the constructor keeps it that way —
     * a fountain that defaults to its own shape needs nobody to have chosen it first.
     */
    public static FctLayoutChoice LoadLayout()
    {
      if (LoadIsFountain())
      {
        return new FctLayoutChoice(FctLayoutMode.Bands, FctRegionSide.Left,
          ParseUpOrNull(ConfigUtil.GetSetting(IncomingDirectionKey, null)),
          ParseUpOrNull(ConfigUtil.GetSetting(OutgoingDirectionKey, null)));
      }

      var takenSide = ParseSide(ConfigUtil.GetSetting(TakenDamageSideKey, null));
      return new FctLayoutChoice(FctLayoutMode.ByType, takenSide,
        ParseUp(ConfigUtil.GetSetting(IncomingDirectionKey, null)),
        ParseUp(ConfigUtil.GetSetting(OutgoingDirectionKey, null)),
        ParseSide(ConfigUtil.GetSetting(HealSideKey, null)),
        ParseUp(ConfigUtil.GetSetting(HealDirectionKey, null)),
        takenSide,
        ParseSide(ConfigUtil.GetSetting(DealtDamageSideKey, null)));
    }

    public static void SaveLayout(FctLayoutChoice layout)
    {
      ConfigUtil.SetSetting(IncomingSideKey, layout.IncomingSide == FctRegionSide.Right ? "right" : "left");
      ConfigUtil.SetSetting(HealSideKey, layout.HealSide == FctRegionSide.Right ? "right" : "left");
      ConfigUtil.SetSetting(HealDirectionKey, layout.HealUp ? "up" : "down");
      ConfigUtil.SetSetting(TakenDamageSideKey, layout.IncomingDamageSide == FctRegionSide.Right ? "right" : "left");
      ConfigUtil.SetSetting(DealtDamageSideKey, layout.OutgoingDamageSide == FctRegionSide.Right ? "right" : "left");
      ConfigUtil.SetSetting(IncomingDirectionKey, layout.IncomingUp ? "up" : "down");
      ConfigUtil.SetSetting(OutgoingDirectionKey, layout.OutgoingUp ? "up" : "down");
    }

    /* The mode itself, in the player's words; absent is split — the MSBT-shaped default. */
    public static bool LoadIsFountain() =>
      string.Equals(ConfigUtil.GetSetting(ModeKey, null), "fountain", StringComparison.OrdinalIgnoreCase);

    public static void SaveIsFountain(bool fountain) => ConfigUtil.SetSetting(ModeKey, fountain ? "fountain" : "split");

    /* split's shape: the stored motion is a full FctMotionStyle elsewhere, but this key only ever carries the two rails. */
    public static FctMotionStyle LoadShape() =>
      string.Equals(ConfigUtil.GetSetting(ShapeKey, null), "straight", StringComparison.OrdinalIgnoreCase)
        ? FctMotionStyle.Straight : FctMotionStyle.Parabola;

    public static void SaveShape(FctMotionStyle shape) => ConfigUtil.SetSetting(ShapeKey, shape is FctMotionStyle.Straight ? "straight" : "parabola");

    /*
     * The parse helpers are pure on purpose: settings.ini speaks words and these are where junk gets a default, so a stray or
     * hand-edited value can never reach the geometry as garbage. All of them fall to the shipped choice's own value.
     */
    internal static FctRegionSide ParseSide(string raw) =>
      string.Equals(raw, "right", StringComparison.OrdinalIgnoreCase) ? FctRegionSide.Right : FctRegionSide.Left;

    internal static bool ParseUp(string raw) =>
      string.Equals(raw, "up", StringComparison.OrdinalIgnoreCase);

    private static bool? ParseUpOrNull(string raw) =>
      string.IsNullOrEmpty(raw) ? null : ParseUp(raw);

    /* The pre-motion-style boolean: still read so an upgrade keeps the choreography somebody had already chosen. */
    private const string LegacyFountainKey = "FctOverlayFountain";

    /*
     * Reading order matters: the current key wins, and an absent one inherits the old "fountain" checkbox rather than
     * resetting a player to hold on the first launch after an upgrade. Writing always uses the new key, so the legacy
     * entry fades out on its own and nothing has to migrate a file.
     */
    public static FctMotionStyle LoadMotion() =>
      ConfigUtil.GetSetting(MotionKey, null) is { Length: > 0 } name ? ParseMotion(name)
        : ConfigUtil.IfSet(LegacyFountainKey) ? FctMotionStyle.Fountain
        : FctMotionStyle.Hold;

    /* An unrecognised value falls back to the default, never to an error dialog over a cosmetic setting that a hand-edit
     * in settings.ini can produce by accident. */
    public static FctMotionStyle ParseMotion(string name) =>
      name?.ToLowerInvariant() switch
      {
        "fountain" => FctMotionStyle.Fountain,
        "pulse" => FctMotionStyle.Pulse,
        "spray" => FctMotionStyle.Spray,
        "parabola" => FctMotionStyle.Parabola,
        "straight" => FctMotionStyle.Straight,
        _ => FctMotionStyle.Hold,
      };

    public static string Name(FctMotionStyle style) =>
      style switch
      {
        FctMotionStyle.Fountain => "fountain",
        FctMotionStyle.Pulse => "pulse",
        FctMotionStyle.Spray => "spray",
        FctMotionStyle.Parabola => "parabola",
        FctMotionStyle.Straight => "straight",
        _ => "hold",
      };


  }
}
