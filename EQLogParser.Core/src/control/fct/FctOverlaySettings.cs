using System;

namespace EQLogParser
{
  /*
   * Overlay presentation settings kept in settings.ini, read once when the overlay starts and written by the configure
   * session: FctSettingsWindow hands a snapshot to FctOverlayWindow, which is the only thing that writes here. Deliberately
   * outside the fct/ folder, so the layout and motion maths stay free of ConfigUtil and can be unit tested on their own;
   * this is the only place that knows these key names or their historical spelling.
   */
  internal static class FctOverlaySettings
  {
    public const string MotionKey = "FctOverlayMotion";

    /* The window itself: whether the View menu had it showing when the app closed, and where it sat. Named here so the
     * spelling of every FCT key exists once, including the ones MainWindow and FctOverlayWindow write directly. */
    public const string EnabledKey = "FctOverlayEnabled";
    public const string WindowLeftKey = "FctOverlayLeft";
    public const string WindowTopKey = "FctOverlayTop";
    public const string WindowWidthKey = "FctOverlayWidth";
    public const string WindowHeightKey = "FctOverlayHeight";

    /*
     * The two modes the settings panel offers, and they are the engine's two schemes: fountain is bands geometry wearing
     * spray motion (numbers spew out of a clear middle — spray or freeze, plus the two direction dials), and split is the
     * columns with a shape — arc or line, nothing that rests in a scroll. Engine names stay where the geometry is
     * tested; ini speaks the words the player chose. The retired FctOverlayLayout key ("halves"/"bytype"/"bands") belonged
     * to the layout+motion combos this replaced and is no longer read — nothing shipped, so nothing migrates.
     */
    public const string ModeKey = "FctOverlayMode";
    public const string ShapeKey = "FctOverlayShape";

    /*
     * Split's categories, per category: which column it owns and which way it travels. Healing has always asked;
     * the two damage streams learned to answer separately in the same engine pass that made bands steerable.
     *
     * A lane key also accepts "none", which switches that category off in split mode — the replacement for the old
     * damage-in / damage-out / healing checkboxes, now that the show list carries nine finer rows instead (FctShowList).
     * Fountain ignores these keys entirely, "none" included.
     */
    public const string HealSideKey = "FctOverlayHealSide";
    public const string HealLaneKey = "FctOverlayHealLane";
    public const string HealDirectionKey = "FctOverlayHealDirection";
    public const string TakenDamageSideKey = "FctOverlayTakenDamageSide";
    public const string TakenDamageLaneKey = "FctOverlayTakenDamageLane";
    public const string IncomingDirectionKey = "FctOverlayIncomingDirection";
    public const string OutgoingDirectionKey = "FctOverlayOutgoingDirection";

    /* Where the "(source)" label sits by its amount: left, below (the shipped line), or right. */
    public const string LabelSideKey = "FctOverlayLabelSide";
    public const string DealtDamageSideKey = "FctOverlayDealtDamageSide";
    public const string DealtDamageLaneKey = "FctOverlayDealtDamageLane";

    /*
     * Which kinds of number the overlay draws at all (the gate in FctIngest). There used to be three keys here — my damage,
     * damage on me, healing either direction — and they are gone: that question is now answered one level finer by the nine
     * show-list rows, whose keys live beside their labels in FctShowList, and "my side entirely" is answered by giving the
     * side no lane. Procs keeps its own key named here because it is the one row named before the rows existed; the rest of
     * the table reaches LoadShown/SaveShown below with the same promise: absent or unreadable means ON, so these are opt-outs
     * and a corrupt value fails toward information rather than toward silence.
     */
    public const string ShowProcsKey = "FctOverlayShowProcs";

    /* One key per event word, same LoadShown opt-out semantics as the categories: absent is shown, and only an
     * explicit 0 quiets a word — a junk value must never eat somebody's "resist". */
    public const string ShowMissKey = "FctOverlayShowMiss";
    public const string ShowParryKey = "FctOverlayShowParry";
    public const string ShowDodgeKey = "FctOverlayShowDodge";
    public const string ShowBlockKey = "FctOverlayShowBlock";
    public const string ShowRiposteKey = "FctOverlayShowRiposte";
    public const string ShowResistKey = "FctOverlayShowResist";
    public const string ShowAbsorbKey = "FctOverlayShowAbsorb";
    public const string ShowInvulnerableKey = "FctOverlayShowInvulnerable";

    /* The two size dials in the settings panel, stored as multipliers rather than percentages: the file is a place where a human may look, and the value
       that means something to the code is the one worth writing. Size runs 0.5 - 1.5; speed runs 0.76 - 2.28 around a shipped 1.14, which are the ends of
       a dial measured in percent of time taken rather than in rate (see FctScale). FctScale clamps on the way in, so a hand-edited "big" or 12 lands on
       that dial's default instead of drawing nothing or running at four times tempo. */
    public const string TextScaleKey = "FctOverlayTextScale";
    public const string CritScaleKey = "FctOverlayCritScale";
    public const string SpeedKey = "FctOverlaySpeed";

    public static double LoadTextScale() => FctScale.ClampSize(ConfigUtil.GetSettingAsDouble(TextScaleKey, FctScale.SizeDefault));

    public static void SaveTextScale(double scale) => ConfigUtil.SetSetting(TextScaleKey, FctScale.ClampSize(scale));

    /* The crit dial rides the same band as the text dial and ships at +10 % — see FctScale.CritSizeDefault. */
    public static double LoadCritScale() => FctScale.ClampCritSize(ConfigUtil.GetSettingAsDouble(CritScaleKey, FctScale.CritSizeDefault));

    public static void SaveCritScale(double scale) => ConfigUtil.SetSetting(CritScaleKey, FctScale.ClampCritSize(scale));

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
     * The "threshold": MSBT's damageThreshold, off by default like theirs, and named the same word at the same dial —
     * the label spent a while as "hide below" (a verb phrase in a panel of nouns) and went back to the genre's term.
     * It used to be a ladder —
     * off, 250, 500, 1k, 2k, 5k — because a combo cannot offer eighty positions; the panel now carries a numeric
     * spinner instead, and every whole number between zero and just under ten million is a legitimate opinion, so
     * loading stopped snapping. A stored 300 means 300. Junk reads as off, which is both the MSBT default and the
     * safe direction.
     */
    public const string ThresholdKey = "FctOverlayThreshold";

    /* The spinner's ceiling: larger than any hit the parser can log, so the control cannot name a number that filters
       something real. Stored values above it (hand-edited or ancient) come back down rather than draw a lie. */
    public const double ThresholdMax = 9_999_999;

    public static double LoadThreshold() => ClampThreshold(ConfigUtil.GetSettingAsDouble(ThresholdKey, 0));

    public static void SaveThreshold(double value) => ConfigUtil.SetSetting(ThresholdKey, ClampThreshold(value));

    /* Pure for testability: finite and in range, or off — nonsense both lands on off. */
    internal static double ClampThreshold(double value) =>
      !double.IsFinite(value) || value <= 0 ? 0 : Math.Min(value, ThresholdMax);

    /*
     * Whether anybody has ever pressed Save in the settings panel. The overlay's defaults are defensible but they are opinions, and a first enable that
     * shows nothing but numbers being what the build decided keeps a player from learning that size, speed and motion are theirs to set - so the menu
     * opens configure mode instead (see MainWindow.SetFctOverlayVisible). Written only by Save: Cancel, Esc and closing all leave it unset, which means
     * the offer comes back rather than nagging into a setting nobody agreed to.
     */
    public const string ConfiguredKey = "FctOverlayConfigured";

    public static bool IsConfigured() => ConfigUtil.IfSet(ConfiguredKey);

    public static void SaveConfigured() => ConfigUtil.SetSetting(ConfiguredKey, true);

    /*
     * The whole panel in one key, and the whole panel out of one key. This is the only place that knows what the settings are
     * called, and the only place that assembles a FctConfigState from the file: the overlay loads once at startup and writes
     * once at Save, and every setting the panel gains has exactly one load and one save here rather than one in each of the
     * five places the old per-field staging touched.
     *
     * Every read goes through the same pure helpers that guard the other keys, so junk lands on a shipped default instead of
     * reaching geometry as garbage. Directions go through ShippedDirections, whose answer for a value nobody wrote is the
     * shipped spread's own.
     */

    /*
     * The direction dials' never-set policy in one pure place, so the shipped answers are pinned where they are written and
     * testable without a file: my numbers rise (the strip invariant in bands, the genre's classic read in the columns) and the
     * heals rise with them - "heals rise while damage falls" is the look most asked for, and a direction nobody set should not
     * arrive pointing down merely because the file happened to load in the other mode first, because Save writes every dial out
     * explicitly and one first-run save would otherwise bake that accident into somebody's file forever. What lands on the player
     * sinks. A saved word always wins, so this only ever answers for values the file does not carry.
     */
    internal static (bool HealUp, bool TakenUp, bool DealtUp) ShippedDirections(string healRaw, string takenRaw, string dealtRaw) =>
      (ParseUpOrNull(healRaw) ?? true, ParseUpOrNull(takenRaw) ?? false, ParseUpOrNull(dealtRaw) ?? true);

    public static FctConfigState LoadConfig()
    {
      var fountain = LoadIsFountain();
      var (healUp, takenUp, dealtUp) = ShippedDirections(
        ConfigUtil.GetSetting(HealDirectionKey, null),
        ConfigUtil.GetSetting(IncomingDirectionKey, null),
        ConfigUtil.GetSetting(OutgoingDirectionKey, null));

      var state = new FctConfigState
      {
        Fountain = fountain,
        /* Lanes are the question now; the old side keys parse through them too ("left" means that side's outer lane), so an
         * older config lands where it looked. The shipped spread gives each category its own column - healing in left 1,
         * what happens to the player in left 2, my damage out in right 1 - and leaves right 2 free for anyone who wants a
         * fourth column (or hides a category). */
        HealLane = FctRailLanes.Parse(ConfigUtil.GetSetting(HealLaneKey, null) ?? ConfigUtil.GetSetting(HealSideKey, null),
          FctConfigState.HealLaneDefault),
        HealUp = healUp,
        TakenLane = FctRailLanes.Parse(
          ConfigUtil.GetSetting(TakenDamageLaneKey, null) ?? ConfigUtil.GetSetting(TakenDamageSideKey, null),
          FctConfigState.TakenLaneDefault),
        TakenUp = takenUp,
        DealtLane = FctRailLanes.Parse(
          ConfigUtil.GetSetting(DealtDamageLaneKey, null) ?? ConfigUtil.GetSetting(DealtDamageSideKey, null),
          FctConfigState.DealtLaneDefault),
        DealtUp = dealtUp,
        Threshold = LoadThreshold(),
        LabelSide = LoadLabelSide(),
        TextScale = LoadTextScale(),
        CritScale = LoadCritScale(),
        Speed = LoadSpeed(),
      };

      /* The shape belongs to the mode, and a file that pairs them wrongly (bands + arc) is not a decision anybody made,
         so it resolves to the mode's own rather than reaching the engine as an illegal combination. */
      state.Shape = LoadShapeFor(fountain ? FctLayoutMode.Bands : FctLayoutMode.ByType);

      /* Rows and words together: FctShowList owns both tables and their keys, so a switch added to the dropdown is loaded by
         being added there and by nothing else. */
      FctShowList.LoadInto(state);

      return state;
    }

    /* Save is one call, so a key cannot be added to the panel and forgotten at the write. The lane tokens include "none":
       a switched-off category comes back off, which is the whole point of offering it. */
    public static void SaveConfig(FctConfigState state)
    {
      if (state is null)
      {
        return;
      }

      var layout = state.BuildLayout();
      SaveIsFountain(state.Fountain);
      SaveShape(state.Shape);
      ConfigUtil.SetSetting(HealLaneKey, FctRailLanes.Token(state.HealLane));
      ConfigUtil.SetSetting(HealDirectionKey, state.HealUp ? "up" : "down");
      ConfigUtil.SetSetting(TakenDamageLaneKey, FctRailLanes.Token(state.TakenLane));
      ConfigUtil.SetSetting(DealtDamageLaneKey, FctRailLanes.Token(state.DealtLane));
      ConfigUtil.SetSetting(IncomingDirectionKey, state.TakenUp ? "up" : "down");
      ConfigUtil.SetSetting(OutgoingDirectionKey, state.DealtUp ? "up" : "down");
      SaveThreshold(state.Threshold);
      FctShowList.Save(state);
      SaveLabelSide(state.LabelSide);
      SaveTextScale(state.TextScale);
      SaveCritScale(state.CritScale);
      SaveSpeed(state.Speed);
    }

    /* The mode itself, in the player's words; absent is FOUNTAIN. The plume is what makes someone look twice at the
     * first crit of the first fight, and split asks for a few decisions (lanes, directions) before it looks like its
     * own idea — so the untouched settings.ini opens with the mode that needs no explanation. Only the explicit word
     * "split" splits; junk reads as the default, as it does for every key here. */
    public static bool LoadIsFountain() =>
      !string.Equals(ConfigUtil.GetSetting(ModeKey, null), "split", StringComparison.OrdinalIgnoreCase);

    public static void SaveIsFountain(bool fountain) => ConfigUtil.SetSetting(ModeKey, fountain ? "fountain" : "split");

    /* The shape both modes pick from, one key: split chooses arc | line, fountain chooses spray | freeze —
     * freezing mid-flight belongs to bands, where parking a number is the style; in a scroll it would break the chain
     * the column exists to be ("line" is what players call the unbent rail, though the engine still says Straight).
     * The saved words are the panel's own words now, so a hand-edited settings.ini says exactly what the dropdown shows.
     * Anything unknown lands back on arc — which is also what the retired "hold"/"parabola" spellings do, quietly: they
     * were never in a release, so nothing migrates and nothing complains. The retired "straight" spelling still reads
     * because it names the same rail. */
    public static FctMotionStyle LoadShape() =>
      ConfigUtil.GetSetting(ShapeKey, null) switch
      {
        string s when string.Equals(s, "line", StringComparison.OrdinalIgnoreCase) => FctMotionStyle.Straight,
        string s when string.Equals(s, "straight", StringComparison.OrdinalIgnoreCase) => FctMotionStyle.Straight,
        string s when string.Equals(s, "freeze", StringComparison.OrdinalIgnoreCase) => FctMotionStyle.Freeze,
        string s when string.Equals(s, "spray", StringComparison.OrdinalIgnoreCase) => FctMotionStyle.Spray,
        _ => FctMotionStyle.Arc,
      };

    public static void SaveShape(FctMotionStyle shape) =>
      ConfigUtil.SetSetting(ShapeKey, shape switch
      {
        FctMotionStyle.Straight => "line",
        FctMotionStyle.Freeze => "freeze",
        FctMotionStyle.Spray => "spray",
        _ => "arc",
      });

    /* A mode cannot store a shape it would only degrade, and this is where that promise is kept: bands (fountain) knows
     * spray and freeze; the column schemes know arc and line, full stop — a row that parks mid-column is not
     * a calmer stream but a broken chain, so freeze clamps away there rather than riding in as an illegal pair.
     * Stale keys, half-finished states and any other mismatch resolve to the mode's own default. */
    internal static FctMotionStyle ClampShape(FctLayoutMode mode, FctMotionStyle shape) =>
      mode is FctLayoutMode.Bands
        ? shape is FctMotionStyle.Freeze ? FctMotionStyle.Freeze : FctMotionStyle.Spray
        : shape is FctMotionStyle.Straight ? FctMotionStyle.Straight : FctMotionStyle.Arc;

    internal static FctMotionStyle LoadShapeFor(FctLayoutMode mode) => ClampShape(mode, LoadShape());

    /*
     * The parse helpers are pure on purpose: settings.ini speaks words and these are where junk gets a default, so a stray or
     * hand-edited value can never reach the geometry as garbage. All of them fall to the shipped choice's own value.
     */
    public static FctLabelSide LoadLabelSide() =>
      ConfigUtil.GetSetting(LabelSideKey, null) switch
      {
        string s when string.Equals(s, "left", StringComparison.OrdinalIgnoreCase) => FctLabelSide.Left,
        string s when string.Equals(s, "right", StringComparison.OrdinalIgnoreCase) => FctLabelSide.Right,
        _ => FctLabelSide.Below,
      };

    public static void SaveLabelSide(FctLabelSide side) =>
      ConfigUtil.SetSetting(LabelSideKey, side switch
      {
        FctLabelSide.Left => "left",
        FctLabelSide.Right => "right",
        _ => "below",
      });

    internal static bool ParseUp(string raw) =>
      string.Equals(raw, "up", StringComparison.OrdinalIgnoreCase);

    private static bool? ParseUpOrNull(string raw) =>
      string.IsNullOrEmpty(raw) ? null : ParseUp(raw);

    /* The pre-motion-style boolean: still read so an upgrade keeps the choreography somebody had already chosen. */
    private const string LegacyFountainKey = "FctOverlayFountain";

    /*
     * Reading order matters: the current key wins, and an absent one inherits the old "fountain" checkbox rather than
     * resetting a player to freeze on the first launch after an upgrade. Writing always uses the new key, so the legacy
     * entry fades out on its own and nothing has to migrate a file.
     *
     * Fountain has no control of its own in the settings panel: the panel asks for it by NAME through ModeKey and that write
     * picks the motion here. Anything else a hand-edit or FctSimulationWindow names still parses, which is what keeps an engine
     * style reachable the day someone wants it on screen.
     */
    public static FctMotionStyle LoadMotion() =>
      ConfigUtil.GetSetting(MotionKey, null) is { Length: > 0 } name ? ParseMotion(name)
        : ConfigUtil.IfSet(LegacyFountainKey) ? FctMotionStyle.Fountain
        : FctMotionStyle.Freeze;

    /* An unrecognised value falls back to the default, never to an error dialog over a cosmetic setting that a hand-edit
     * in settings.ini can produce by accident. */
    public static FctMotionStyle ParseMotion(string name) =>
      name?.ToLowerInvariant() switch
      {
        "fountain" => FctMotionStyle.Fountain,
        "spray" => FctMotionStyle.Spray,
        "arc" => FctMotionStyle.Arc,
        "straight" => FctMotionStyle.Straight,
        _ => FctMotionStyle.Freeze,
      };
  }
}