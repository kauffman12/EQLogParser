using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EQLogParser
{
  internal static class TimelineLayoutManager
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private const string Extension = ".json";
    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
      WriteIndented = true,
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static string GetLayoutsDirectory() => ConfigUtil.GetTimelineLayoutsDir();

    internal static void EnsureDirectoryExists()
    {
      ExceptionUtil.CatchIoExceptions(() => Directory.CreateDirectory(GetLayoutsDirectory()), Log.Error);
    }

    internal static List<string> GetLayoutNames()
    {
      EnsureDirectoryExists();
      return ExceptionUtil.CatchIoExceptions(() =>
      {
        var names = new List<string>();
        var dir = GetLayoutsDirectory();
        if (Directory.Exists(dir))
        {
          var files = Directory.GetFiles(dir, $"*{Extension}");
          foreach (var file in files)
          {
            var name = Path.GetFileNameWithoutExtension(file);
            names.Add(name);
          }
          names.Sort();
        }
        return names;
      }, new List<string>(), Log.Error);
    }

    internal static void SaveLayout(string name, TimelineLayout layout)
    {
      EnsureDirectoryExists();
      var filePath = Path.Combine(GetLayoutsDirectory(), $"{name}{Extension}");
      var json = JsonSerializer.Serialize(layout, SerializerOptions);
      ExceptionUtil.CatchIoExceptions(() => File.WriteAllText(filePath, json), Log.Error, true);
    }

    internal static TimelineLayout LoadLayout(string name)
    {
      var filePath = Path.Combine(GetLayoutsDirectory(), $"{name}{Extension}");
      if (!File.Exists(filePath))
      {
        return null;
      }

      var json = ExceptionUtil.CatchIoExceptions(() => File.ReadAllText(filePath), null, Log.Error);
      if (json == null)
      {
        return null;
      }

      try
      {
        return JsonSerializer.Deserialize<TimelineLayout>(json);
      }
      catch (JsonException ex)
      {
        Log.Error(ex);
        return null;
      }
    }

    internal static void DeleteLayout(string name)
    {
      var filePath = Path.Combine(GetLayoutsDirectory(), $"{name}{Extension}");
      ExceptionUtil.CatchIoExceptions(() =>
      {
        if (File.Exists(filePath))
        {
          File.Delete(filePath);
        }
      }, Log.Error);
    }

    internal static bool LayoutExists(string name)
    {
      var filePath = Path.Combine(GetLayoutsDirectory(), $"{name}{Extension}");
      return ExceptionUtil.CatchIoExceptions(() => File.Exists(filePath), false);
    }
  }

  public class TimelineLayout
  {
    public string Name { get; set; }
    public List<string> SpellOrder { get; set; }
    public HashSet<string> HiddenSpells { get; set; }
    public bool HideSelfOnly { get; set; }
    public bool ShowCasterAdps { get; set; }
    public bool ShowMeleeAdps { get; set; }
    public double PixelsPerSecond { get; set; }
    public DateTime LastModified { get; set; }
  }
}
