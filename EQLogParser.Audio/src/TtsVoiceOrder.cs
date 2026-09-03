using System;
using System.Collections.Generic;
using System.Linq;

namespace EQLogParser.Audio
{
  /*
   * A voice picker is read by a person, so it has to be alphabetical in the words it actually prints. Engines are not
   * naturally in that order: Kokoro ids lead with the accent and the gender - af_aria, af_nicole, am_adam, bf_emma -
   * which puts every American woman in front of Adam and drops "Yunxi (CN)" between the Hindi and Italian voices, and
   * Piper's voices.json arrives in whatever order the pack was built. Each engine hands this its ids along with the
   * label it shows, and the list comes back ordered by that label.
   *
   * Sorting is display only. The stored setting stays the engine's id, and the id is the tie break, so two voices that
   * carry the same label keep a stable order instead of trading places between one call and the next.
   */
  internal static class TtsVoiceOrder
  {
    internal static List<string> ByLabel(IEnumerable<string> voices, Func<string, string> label) =>
      voices
        .OrderBy(voice => label(voice), StringComparer.OrdinalIgnoreCase)
        .ThenBy(voice => voice, StringComparer.OrdinalIgnoreCase)
        .ToList();
  }
}
