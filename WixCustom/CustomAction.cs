using IWshRuntimeLibrary;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WixToolset.Dtf.WindowsInstaller;
using File = System.IO.File;

namespace WixCustom
{
  public class CustomActions
  {
    private static readonly Version MinVersion = new(8, 0, 0);

    private static readonly List<string> Runtimes = new()
    {
      "Microsoft.WindowsDesktop.App"//.NET Desktop Runtime
    };

    [CustomAction]
    public static ActionResult CreateShortcut(Session session)
    {
      try
      {
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        var shortcutName = "EQ Log Parser.lnk"; // Name of the shortcut
        var shortcutPath = Path.Combine(desktopPath, shortcutName);

        if (!File.Exists(shortcutPath)) // Check if the shortcut already exists
        {
          // Path to the executable of your application
          var appPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EQLogParser", "EQLogParser.exe");
          session.Log("Creating Shortcut: " + shortcutPath);

          var shell = new WshShell();
          var shortcut = (IWshShortcut)shell.CreateShortcut(shortcutPath);
          shortcut.Description = "Everquest Log Parser"; // Shortcut description
          shortcut.TargetPath = appPath; // Path to the executable
          shortcut.WorkingDirectory = Path.GetDirectoryName(appPath); // Set working directory
          shortcut.IconLocation = appPath + ",0";
          shortcut.Save(); // Save the shortcut on the desktop
        }
        else
        {
          session.Log("Shortcut already exists. Do not create.");
        }
      }
      catch (Exception ex)
      {
        // Log or handle error
        session.Log("Error creating shortcut: " + ex.Message);
      }
      return ActionResult.Success;
    }

    [CustomAction]
    public static ActionResult RemoveShortcut(Session session)
    {
      try
      {
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        var shortcutName = "EQ Log Parser.lnk"; // Name of the shortcut
        var shortcutPath = Path.Combine(desktopPath, shortcutName);

        if (File.Exists(shortcutPath))
        {
          session.Log("Attempting to remove shortcut: " + shortcutPath);
          File.Delete(shortcutPath);
          session.Log("Shortcut removed successfully.");
        }
        else
        {
          session.Log("Shortcut does not exist, no action taken: " + shortcutPath);
        }
      }
      catch (Exception ex)
      {
        session.Log("Error removing shortcut: " + ex.ToString());
        return ActionResult.Failure;
      }
      return ActionResult.Success;
    }


    [CustomAction]
    public static ActionResult CheckDotNetVersion(Session session)
    {
      var command = "/c \"" + session["ProgramFiles64Folder"] + "dotnet\\dotnet.exe\" --list-runtimes"; // /c is important here
      session.Log("Running = " + command);

      try
      {
        var output = string.Empty;
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo()
        {
          FileName = "cmd.exe",
          Arguments = command,
          UseShellExecute = false,
          RedirectStandardError = true,
          RedirectStandardOutput = true,
          CreateNoWindow = true,
        };

        p.Start();
        while (!p.StandardOutput.EndOfStream)
        {
          output += $"{p.StandardOutput.ReadLine()}{Environment.NewLine}";
        }

        p.WaitForExit();

        //throw new Exception($"{p.ExitCode}:{p.StandardError.ReadToEnd()}");
        if (p.ExitCode != 0)
        {
          session["DOTNET8INSTALLED"] = "0";
          return ActionResult.Success;
        }

        session["DOTNET8INSTALLED"] = FindMinVersionOfRuntime(Runtimes[0], output);
        return ActionResult.Success;
      }
      catch (Exception)
      {
        session["DOTNET8INSTALLED"] = "0";
        return ActionResult.Success;
      }
    }

    private static string FindMinVersionOfRuntime(string runtime, string runtimesList)
    {
      foreach (var line in runtimesList.Split(new[] { Environment.NewLine }, StringSplitOptions.None).ToList()
                 .Where(x => x.Contains(runtime)))
      {
        var pattern = new Regex(@"\d+(\.\d+)+");
        var m = pattern.Match(line);
        var versionValue = m.Value;
        if (Version.TryParse(versionValue, out var version))
        {
          if (version.Major == MinVersion.Major)
          {
            return "1";
          }
        }
      }

      return "0";
    }
  }
}
