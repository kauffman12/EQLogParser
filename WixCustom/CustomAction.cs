using Microsoft.Deployment.WindowsInstaller;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace WixCustom
{
  public class CustomActions
  {
    static readonly List<string> runtimes = new List<string>()
    {
      "Microsoft.WindowsDesktop.App"//.NET Desktop Runtime
    };

    [CustomAction]
    public static ActionResult CheckDotNetVersion(Session session)
    {
      var minVersion = new Version(6, 0, 0);
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
            session["DOTNET6INSTALLED"] = "0";
            return ActionResult.Success;
          }

          session["DOTNET6INSTALLED"] = (GetLatestVersionOfRuntime(runtimes[0], output) < minVersion) ? "0" : "1";
          return ActionResult.Success;
        }
      }
      catch (Exception e)
      {
        session["DOTNET6INSTALLED"] = "0";
        return ActionResult.Success;
      }
    }

    private static Version GetLatestVersionOfRuntime(string runtime, string runtimesList)
    {
      var latestLine = runtimesList.Split(new string[] { Environment.NewLine }, StringSplitOptions.None).ToList().Where(x => x.Contains(runtime)).OrderBy(x => x).LastOrDefault();
      if (latestLine != null)
      {
        Regex pattern = new Regex(@"\d+(\.\d+)+");
        Match m = pattern.Match(latestLine);
        string versionValue = m.Value;
        if (Version.TryParse(versionValue, out var version))
        {
          return version;
        }
      }
      return null;
    }
  }
}
