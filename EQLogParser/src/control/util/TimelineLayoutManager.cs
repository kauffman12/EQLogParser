using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
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
      try
      {
        Directory.CreateDirectory(GetLayoutsDirectory());
      }
      catch (IOException ex)
      {
        Log.Error(ex);
      }
      catch (UnauthorizedAccessException ex)
      {
        Log.Error(ex);
      }
    }

    internal static List<string> GetLayoutNames()
    {
      EnsureDirectoryExists();
      var names = new List<string>();

      try
      {
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
      }
      catch (IOException ex)
      {
        Log.Error(ex);
      }
      catch (UnauthorizedAccessException ex)
      {
        Log.Error(ex);
      }

      return names;
    }

    internal static void SaveLayout(string name, TimelineLayout layout)
    {
      try
      {
        EnsureDirectoryExists();
        var filePath = Path.Combine(GetLayoutsDirectory(), $"{name}{Extension}");
        var json = JsonSerializer.Serialize(layout, SerializerOptions);
        File.WriteAllText(filePath, json);
      }
      catch (IOException ex)
      {
        Log.Error(ex);
        throw;
      }
      catch (UnauthorizedAccessException ex)
      {
        Log.Error(ex);
        throw;
      }
    }

    internal static TimelineLayout LoadLayout(string name)
    {
      try
      {
        var filePath = Path.Combine(GetLayoutsDirectory(), $"{name}{Extension}");
        if (!File.Exists(filePath))
        {
          return null;
        }

        var json = File.ReadAllText(filePath);
        var layout = JsonSerializer.Deserialize<TimelineLayout>(json);
        return layout;
      }
      catch (IOException ex)
      {
        Log.Error(ex);
        return null;
      }
      catch (UnauthorizedAccessException ex)
      {
        Log.Error(ex);
        return null;
      }
      catch (JsonException ex)
      {
        Log.Error(ex);
        return null;
      }
    }

    internal static void DeleteLayout(string name)
    {
      try
      {
        var filePath = Path.Combine(GetLayoutsDirectory(), $"{name}{Extension}");
        if (File.Exists(filePath))
        {
          File.Delete(filePath);
        }
      }
      catch (IOException ex)
      {
        Log.Error(ex);
      }
      catch (UnauthorizedAccessException ex)
      {
        Log.Error(ex);
      }
    }

    internal static bool LayoutExists(string name)
    {
      try
      {
        var filePath = Path.Combine(GetLayoutsDirectory(), $"{name}{Extension}");
        return File.Exists(filePath);
      }
      catch
      {
        return false;
      }
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
