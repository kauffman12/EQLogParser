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

          session["DOTNET8INSTALLED"] = FindMinVersionOfRuntime(Runtimes[0], output);
          return ActionResult.Success;
        }
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
