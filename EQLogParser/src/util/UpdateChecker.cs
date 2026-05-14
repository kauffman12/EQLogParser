using log4net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Checks GitHub for new releases and handles download/install of updates.
  /// </summary>
  internal static class UpdateChecker
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private static readonly JsonSerializerOptions VersionCheckSerializationOptions = new() { PropertyNameCaseInsensitive = true };

    internal static async Task CheckVersionAsync()
    {
      var version = Application.ResourceAssembly.GetName().Version;

      try
      {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/kauffman12/EQLogParser/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("EQLogParser", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        var response = await MainActions.TheHttpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        // parse json
        var json = await response.Content.ReadAsStringAsync();
        var release = JsonSerializer.Deserialize<GitHubRelease>(json, VersionCheckSerializationOptions);

        if (version != null && release != null && !string.IsNullOrEmpty(release.Tag_name) &&
            Version.TryParse(release.Tag_name, out var latestVersion) &&
            (latestVersion.Major > version.Major ||
             (latestVersion.Major == version.Major && latestVersion.Minor > version.Minor) ||
             (latestVersion.Major == version.Major && latestVersion.Minor == version.Minor && latestVersion.Build > version.Build)))
        {
          await UiUtil.InvokeAsync(async () =>
          {
            var main = MainActions.GetOwner() as MainWindow;
            var wasHidden = false;
            if (main?.Visibility != Visibility.Visible)
            {
              wasHidden = true;
              main?.Show();
            }

            var msg = new MessageWindow($"Version {release.Tag_name} is Available. Download and Install?", Resource.CHECK_VERSION,
              MessageWindow.IconType.Question, "Yes");
            msg.ShowDialog();

            if (msg.IsYes1Clicked)
            {
              var installerAsset = release.Assets?.FirstOrDefault(a =>
                a.Name?.StartsWith("EQLogParser-install", StringComparison.OrdinalIgnoreCase) == true && !a.Name?.Contains("pipertts") == true);

              if (installerAsset == null || string.IsNullOrEmpty(installerAsset.Browser_download_url))
              {
                new MessageWindow("Unable to Find Installer URL. Can Not Download Update.", Resource.CHECK_VERSION).ShowDialog();
                return;
              }

              var url = installerAsset.Browser_download_url;

              try
              {
                await DownloadAndInstallAsync(url, release.Tag_name, main);
              }
              catch (Exception ex2)
              {
                new MessageWindow("Problem Installing Updates. Check Error Log for Details.", Resource.CHECK_VERSION).ShowDialog();
                Log.Error("Error Installing Updates", ex2);
              }
            }

            if (wasHidden)
            {
              main?.Hide();
            }
          }, DispatcherPriority.Background);
        }
        else
        {
          // cleanup downloads
          Cleanup();
        }
      }
      catch (Exception ex)
      {
        Log.Error($"Error Checking for Updates: {ex.Message}");
        await UiUtil.InvokeAsync(() => (MainActions.GetOwner() as MainWindow)?.SetErrorText("Update Check Failed. Firewall?"));
      }
    }

    internal static void Cleanup()
    {
      try
      {
        if (!NativeMethods.TryGetDownloadsFolderPath(out var path) || !Directory.Exists(path)) return;

        path += "\\AutoUpdateEQLogParser";
        if (Directory.Exists(path))
        {
          foreach (var file in Directory.GetFiles(path))
          {
            var test = Path.GetFileName(file).Trim();
            if (test.StartsWith("EQLogParser", StringComparison.OrdinalIgnoreCase) && test.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            {
              File.Delete(file);
            }
          }
        }
      }
      catch (Exception e)
      {
        Log.Error(e);
      }
    }

    private static async Task DownloadAndInstallAsync(string url, string tagName, MainWindow main)
    {
      await using var download = await MainActions.TheHttpClient.GetStreamAsync(url);
      if (!NativeMethods.TryGetDownloadsFolderPath(out var path) || !Directory.Exists(path))
      {
        new MessageWindow("Unable to Access Downloads Folder. Can Not Download Update.", Resource.CHECK_VERSION).ShowDialog();
        return;
      }

      path += "\\AutoUpdateEQLogParser";
      if (!Directory.Exists(path))
      {
        Directory.CreateDirectory(path);
      }

      var fullPath = $"{path}\\EQLogParser-install-{tagName}.exe";
      await using (var fs = new FileStream(fullPath, FileMode.Create))
      {
        await download.CopyToAsync(fs);
      }

      if (File.Exists(fullPath))
      {
        var process = Process.Start(fullPath);
        if (process is { HasExited: false })
        {
          await Task.Delay(1000);
          await UiUtil.InvokeAsync(() => main?.Close());
        }
      }
    }

    private class GitHubRelease
    {
      public string Tag_name { get; set; }
      public List<GitHubAsset> Assets { get; set; }
    }

    private class GitHubAsset
    {
      public string Name { get; set; }
      public string Browser_download_url { get; set; }
    }
  }
}
