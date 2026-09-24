using log4net;
using Microsoft.Win32;
using System;
using System.IO;
using System.Windows;

namespace EQLogParser
{
  /// <summary>
  /// The only place in the app that builds a chooser: one way to open a file, one way to pick a folder, one way
  /// to save one. That is a deliberate consolidation — this used to be spread over three implementations (the
  /// Windows API Code Pack, Microsoft.Win32 and a WinForms folder browser), which bought nothing since the whole
  /// feature set in use is a filter, a suggested name, a title and a starting folder, and cost a crash class:
  /// each implementation checked its starting directory in its own way, and only some of them threw.
  /// </summary>
  internal static class FileDialogUtil
  {
    private static readonly ILog Log = LogManager.GetLogger(typeof(FileDialogUtil));

    /// <summary>
    /// Picks one file, or returns null for cancelled *and* for failed — either way the caller has nothing to do.
    /// <paramref name="startPath"/> may be a folder or a full file path; the dialog only opens there if that
    /// place still exists. <paramref name="label"/> names the control in the log, since "a chooser threw" is not
    /// something a player can tell us.
    /// </summary>
    internal static string PickFile(Window owner, string startPath, string label, string filter, string title = null, string defaultExt = null)
    {
      return Show(owner, startPath, label, (window, from) =>
      {
        var dialog = new OpenFileDialog
        {
          Filter = filter,
          Title = title,
          InitialDirectory = from,
        };

        if (!string.IsNullOrEmpty(defaultExt))
        {
          dialog.DefaultExt = defaultExt;
        }

        return dialog.ShowDialog(window) == true ? dialog.FileName : null;
      });
    }

    /// <summary>
    /// Picks one folder. Same rules as <see cref="PickFile"/>: null means nothing was picked, whether that is a
    /// cancel or a chooser that could not be shown.
    /// </summary>
    internal static string PickFolder(Window owner, string startPath, string label, string title = null)
    {
      return Show(owner, startPath, label, (window, from) =>
      {
        var dialog = new OpenFolderDialog
        {
          Multiselect = false,
          Title = title,
          InitialDirectory = from,
        };

        return dialog.ShowDialog(window) == true ? dialog.FolderName : null;
      });
    }

    /// <summary>
    /// Saves one file. Returns the path chosen, or null for cancelled and for failed. <paramref name="fileName"/>
    /// is the name offered in the box — the sanitising of characters a filename cannot hold stays with the caller,
    /// since each export builds its own suggested name.
    /// </summary>
    internal static string SaveFile(Window owner, string startPath, string label, string filter, string fileName = null, string title = null, string defaultExt = null)
    {
      return Show(owner, startPath, label, (window, from) =>
      {
        var dialog = new SaveFileDialog
        {
          Filter = filter,
          Title = title,
          InitialDirectory = from,
        };

        if (!string.IsNullOrEmpty(fileName))
        {
          dialog.FileName = fileName;
        }

        if (!string.IsNullOrEmpty(defaultExt))
        {
          dialog.DefaultExt = defaultExt;
        }

        return dialog.ShowDialog(window) == true ? dialog.FileName : null;
      });
    }

    /// <summary>
    /// The directory to open a chooser in, out of whatever the caller has — a folder, a full file path, or
    /// nothing — but only if it exists. Null means "let Windows decide", which is the safe answer: pointing a
    /// chooser at a folder that isn't there is how this whole class of crash got here. Exposed so a caller with a
    /// list of candidates (the log file, then the recent files) can take the first one that resolves.
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

    // The one loop every chooser runs: ask for the caller's folder first, and if the shell cannot be moved to
    // show it, try once more with no folder at all so Windows opens wherever it keeps this kind of dialog. That
    // retry is what turns "their EQ folder moved" into a working chooser instead of an exception on a click
    // handler — and a handler that throws here is a window that closes, which is the report we keep getting.
    private static string Show(Window owner, string startPath, string label, Func<Window, string, string> run)
    {
      var from = ResolveDirectory(startPath);

      for (var attempt = 1; attempt <= 2; attempt++)
      {
        try
        {
          // The second pass drops the owner as well as the folder: WPF refuses outright to show a dialog for a
          // window whose handle has not been created, and a chooser parented to nothing still works — which is
          // what several of these call sites did on purpose before there was one place to do it.
          return run(attempt == 1 ? owner : null, attempt == 1 ? from : null);
        }
        catch (Exception e)
        {
          // Nothing to rethrow. The second pass gets the same shot at a default location, and if that fails too
          // the answer is "nothing was picked" with the reason logged under the name of the control.
          Log.Error($"The {label} chooser failed from {(attempt == 1 ? $"'{from}'" : "no start folder")}", e);
        }
      }

      // Both attempts failed, which is the state a player experiences as "I clicked Export and nothing happened".
      // It used to be a crash, then a silence; say something instead, since a broken chooser on some Windows build
      // we cannot reproduce is otherwise indistinguishable from a mis-click. Its own try because an error path that
      // can throw is not an error path.
      try
      {
        new MessageWindow($"The {label} file chooser could not be opened.\n\nCheck the EQLogParser error log for details.",
          "File Chooser").ShowDialog();
      }
      catch (Exception e)
      {
        Log.Error("Could not tell the user that the file chooser failed", e);
      }

      return null;
    }
  }
}
