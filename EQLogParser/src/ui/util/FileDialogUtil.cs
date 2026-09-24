using log4net;
using Microsoft.WindowsAPICodePack.Dialogs;
using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;

namespace EQLogParser
{
  /// <summary>
  /// Every chooser the app opens goes through here, because the one thing a file dialog must never do is take
  /// the window down with it. The shell call behind a chooser fails over things nobody controls — a log folder
  /// on a drive that is not mounted today, a path that grew past MAX_PATH, a folder that moved into OneDrive
  /// and dehydrated — and it arrives as whatever exception Windows felt like throwing, which is not a type
  /// worth asking a click handler to predict. So an initial directory is used only while it still exists, the
  /// dialog runs inside a try, and a failure comes back as "the user picked nothing" with the reason in the log
  /// instead of an exception escaping into the dispatcher.
  /// </summary>
  internal static class FileDialogUtil
  {
    private static readonly ILog Log = LogManager.GetLogger(typeof(FileDialogUtil));

    /// <summary>
    /// Picks one file, or returns null for cancelled *and* for failed — either way the caller has nothing to do.
    /// <paramref name="startPath"/> may be a folder or a full file path; the dialog only opens there if that
    /// place still exists. <paramref name="label"/> names the control in the log, since "a chooser threw" is
    /// not something a player can tell us.
    /// </summary>
    internal static string PickFile(Window owner, string startPath, string label, string description, string patterns)
    {
      var from = ResolveDirectory(startPath);

      // Two tries at most: the folder the caller asked for, then no folder at all and let Windows show what it
      // likes. That second attempt is what turns "their EQ folder moved" into a working dialog.
      for (var attempt = 1; attempt <= 2; attempt++)
      {
        try
        {
          using var dialog = new CommonOpenFileDialog
          {
            IsFolderPicker = false,
            InitialDirectory = attempt == 1 ? from : null,
          };

          dialog.Filters.Add(new CommonFileDialogFilter(description, patterns));

          return dialog.ShowDialog(OwnerHandle(owner)) == CommonFileDialogResult.Ok ? dialog.FileName : null;
        }
        catch (Exception e)
        {
          Log.Error($"The {label} file chooser failed from {(attempt == 1 ? $"'{from}'" : "no start folder")}", e);
        }
      }

      return null;
    }

    /// <summary>
    /// Picks one folder. Same rules as <see cref="PickFile"/>: null means nothing was picked, whether that is a
    /// cancel or a chooser that could not be shown.
    /// </summary>
    internal static string PickFolder(Window owner, string startPath, string label)
    {
      var from = ResolveDirectory(startPath);

      for (var attempt = 1; attempt <= 2; attempt++)
      {
        try
        {
          using var dialog = new CommonOpenFileDialog
          {
            IsFolderPicker = true,
            InitialDirectory = attempt == 1 ? from : null,
          };

          return dialog.ShowDialog(OwnerHandle(owner)) == CommonFileDialogResult.Ok ? dialog.FileName : null;
        }
        catch (Exception e)
        {
          Log.Error($"The {label} folder chooser failed from {(attempt == 1 ? $"'{from}'" : "no start folder")}", e);
        }
      }

      return null;
    }

    /// <summary>
    /// The directory to open a chooser in, out of whatever the caller has — a folder, a full file path, or
    /// nothing — but only if it exists. Null means "let Windows decide", which is the safe answer: pointing a
    /// chooser at a folder that isn't there is how this whole class of crash got here. Exposed so a caller with
    /// a list of candidates (the log file, then the recent files) can take the first one that resolves.
    /// </summary>
    internal static string ResolveDirectory(string path)
    {
      if (string.IsNullOrWhiteSpace(path))
      {
        return null;
      }

      try
      {
        if (Directory.Exists(path))
        {
          return path;
        }

        var dir = Path.GetDirectoryName(path);
        return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : null;
      }
      catch (Exception e)
      {
        // A malformed string — illegal characters, a bare drive letter — is bad data from wherever this was
        // saved, not a reason to refuse the chooser.
        Log.Error($"Could not read a start folder out of '{path}'", e);
        return null;
      }
    }

    // The Code Pack takes an owner as a window handle rather than a Window, and takes zero for "no owner".
    private static IntPtr OwnerHandle(Window owner) => owner != null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero;
  }
}
