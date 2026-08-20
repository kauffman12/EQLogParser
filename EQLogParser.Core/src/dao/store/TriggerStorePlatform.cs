using System;

namespace EQLogParser
{
  // Platform-dependent behavior used by TriggerStateDB, injected by the WPF host at startup.
  // Defaults keep the store runnable (and testable) without a UI: icons are treated as valid,
  // sounds as absent, and sprite paths pass through unchanged.
  internal static class TriggerStorePlatform
  {
    /* File path of the user's trigger database (WPF host sets this before first Instance use). */
    internal static Func<string> GetDbFile;

    /* Whether an icon file can be loaded as a bitmap. Default: assume valid. */
    internal static Func<string, bool> IconIsValid = _ => true;

    /* Whether a trigger sound resolves to an existing file. Default: none exist. */
    internal static Func<string, bool> SoundExists = _ => false;

    /* Re-validate an icon path, replacing it when the sprite was found in another EQ install.
     * Return null or an unchanged value for no update (host contract, see CheckMissingMedia). */
    internal static Func<TriggerConfig, string, string> ValidateSpritePath = (_, path) => path;

    /* Default position of the auto-created text overlay (the host scales it to the primary screen). */
    internal static Func<(long X, long Y)> DefaultTextOverlayPosition = () => (0, 0);
  }
}
