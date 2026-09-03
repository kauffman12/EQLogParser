namespace EQLogParser
{
  /* Host hooks for the GINA import flow — the two places it needs a window: failure messages and
   * the merge/new-folder question. Defaults are safe no-ops (Cancel) so Core never throws when a
   * host forgets to wire them; App.xaml.cs maps caption keys to localized Resource strings and
   * shows MessageWindows on the UI thread. */
  internal static class GinaPlatform
  {
    // Semantic caption keys — the host decides how they render (localized strings).
    public const string CaptionReceivedGina = "ReceivedGina";
    public const string CaptionShareError = "ShareError";

    // Shows a modal message and completes when the user dismisses it (the original flow blocked on
    // the dialog before scheduling the next GINA task). Host must marshal to its UI thread.
    public static Func<string, string, Task> ShowMessage = (_, _) => Task.CompletedTask;

    // Asks whether to merge or import into a new folder. showMergeOption mirrors the original
    // dialog's extra auto-merge option row (it passed characterIds.Count > 0); the Cancel button
    // was always present in that dialog.
    public static Func<string, bool, Task<ImportChoice>> AskImportChoice = async (_, _) =>
    {
      await Task.CompletedTask;
      return ImportChoice.Cancel;
    };

    internal enum ImportChoice
    {
      // User dismissed the question (or no host is wired) — import nothing.
      Cancel,
      // Merge into the existing trigger tree.
      Merge,
      // Import into a freshly named folder.
      NewFolder
    }
  }
}
