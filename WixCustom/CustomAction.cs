using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using WixToolset.Dtf.WindowsInstaller;

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
    public static ActionResult CheckDotNetVersion(Session session)
    {
      var command = "/c \"" + session["ProgramFiles64Folder"] + "dotnet\\dotnet.exe\" --list-runtimes"; // /c is important here
      session.Log("Running = " + command);

      try
      {
        var output = string.Empty;
        using (var p = new Process())
        {
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

          session["DOTNET8INSTALLED"] = (GetLatestVersionOfRuntime(Runtimes[0], output) < MinVersion) ? "0" : "1";
          return ActionResult.Success;
        }
      }
      catch (Exception)
      {
        session["DOTNET8INSTALLED"] = "0";
        return ActionResult.Success;
      }
    }

    private static Version GetLatestVersionOfRuntime(string runtime, string runtimesList)
    {
      var latestLine = runtimesList.Split(new string[] { Environment.NewLine }, StringSplitOptions.None).ToList().Where(x => x.Contains(runtime)).OrderBy(x => x).LastOrDefault();
      if (latestLine != null)
      {
        var pattern = new Regex(@"\d+(\.\d+)+");
        var m = pattern.Match(latestLine);
        var versionValue = m.Value;
        if (Version.TryParse(versionValue, out var version) && version.Major == MinVersion.Major)
        {
          return version;
        }
      }
      return null;
    }
  }
}
