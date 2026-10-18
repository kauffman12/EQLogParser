using EQLogParser;
using System.Collections.Concurrent;
using System.Reflection;

#nullable enable annotations

namespace EQLogParserTest
{
  [TestClass]
  public class LogManagementSettingsTest
  {
    private static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly ConcurrentDictionary<string, string> Settings =
      Field("ApplicationSettings").GetValue(null) as ConcurrentDictionary<string, string>
      ?? throw new InvalidOperationException("Config settings not found");
    private Dictionary<string, string> _originalSettings = [];
    private object? _originalFile;
    private object? _originalDirty;
    private string _directory = "";
    private string SettingsPath => Path.Combine(_directory, "settings.txt");

    [TestInitialize]
    public void Setup()
    {
      _originalSettings = new(Settings);
      _originalFile = Field("_settingsFile").GetValue(null);
      _originalDirty = Field("_settingsUpdated").GetValue(null);
      _directory = Path.Combine(Path.GetTempPath(), "log-settings-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(_directory);
      Settings.Clear();
      Field("_settingsFile").SetValue(null, SettingsPath);
      Field("_settingsUpdated").SetValue(null, false);
    }

    [TestCleanup]
    public void Cleanup()
    {
      Settings.Clear();
      foreach (var pair in _originalSettings)
      {
        Settings[pair.Key] = pair.Value;
      }

      Field("_settingsFile").SetValue(null, _originalFile);
      Field("_settingsUpdated").SetValue(null, _originalDirty);
      Directory.Delete(_directory, true);
    }

    [TestMethod]
    public void EveryDialogValueSurvivesSaveAndReopenIncludingDiskReload()
    {
      foreach (var expected in DialogValues())
      {
        Assert.IsTrue(expected.TrySave(), expected.ToString());
        Assert.AreEqual(expected, LogManagementSettings.Load(), "Reopening in the same process");
        ReloadFromDisk();
        Assert.AreEqual(expected, LogManagementSettings.Load(), "Restarting after Save: " + expected);
      }
    }

    [TestMethod]
    public void BothAnyThresholdsReplaceThePreviousValuesWhenScheduled()
    {
      var previous = new LogManagementSettings { Enabled = true, ArchiveFolder = @"C:\Archives" };
      Assert.IsTrue(previous.TrySave());
      var expected = previous with { Type = "Selected Day/Time", MinFileSize = "Any Size", MinFileAge = "Any Age" };

      Assert.IsTrue(expected.TrySave());
      ReloadFromDisk();

      Assert.AreEqual(expected, LogManagementSettings.Load());
    }

    [TestMethod]
    [DataRow(true, "", "Activity", "500M", "1 Week", false)]
    [DataRow(true, " ", "Selected Day/Time", "Any Size", "Any Age", false)]
    [DataRow(false, "", "Activity", "Any Size", "Any Age", true)]
    [DataRow(true, @"C:\", "Activity", "500M", "Any Age", true)]
    [DataRow(true, @"C:\", "Activity", "Any Size", "1 Week", true)]
    [DataRow(true, @"C:\", "Activity", "Any Size", "Any Age", false)]
    [DataRow(true, @"C:\", "Selected Day/Time", "Any Size", "Any Age", true)]
    public void ValidationUsesCurrentValues(bool enabled, string folder, string type, string size, string age, bool expected)
    {
      var settings = new LogManagementSettings
      {
        Enabled = enabled, ArchiveFolder = folder, Type = type, MinFileSize = size, MinFileAge = age
      };
      Assert.AreEqual(expected, settings.CanSave);
    }

    [TestMethod]
    public void InvalidSaveCannotChangeTheActiveSettings()
    {
      var previous = new LogManagementSettings { Enabled = true, ArchiveFolder = @"C:\Archives" };
      Assert.IsTrue(previous.TrySave());

      Assert.IsFalse((previous with { ArchiveFolder = "" }).TrySave());
      Assert.IsFalse((previous with { MinFileAge = "Any Age", MinFileSize = "Any Size" }).TrySave());

      Assert.AreEqual(previous, LogManagementSettings.Load());
      ReloadFromDisk();
      Assert.AreEqual(previous, LogManagementSettings.Load());
    }

    [TestMethod]
    public void DisabledSaveWithoutAFolderNeverPersistsThePlaceholder()
    {
      var expected = new LogManagementSettings { Compress = "No" };
      Assert.IsTrue(expected.TrySave());
      Assert.IsNull(ConfigUtil.GetSetting("LogManagementArchiveFolder"));
      ReloadFromDisk();
      Assert.AreEqual(expected, LogManagementSettings.Load());
    }

    [TestMethod]
    public void FailedSaveRestoresActiveSettingsAndCanBeRetried()
    {
      var previous = new LogManagementSettings { Enabled = true, ArchiveFolder = @"C:\Archives" };
      Assert.IsTrue(previous.TrySave());
      var edited = previous with { ArchiveFolder = @"D:\Raid=Logs", Compress = "No" };
      // A directory cannot be written as a file, even when tests run as an administrator.
      Field("_settingsFile").SetValue(null, _directory);

      Assert.IsFalse(edited.TrySave());
      Assert.AreEqual(previous, LogManagementSettings.Load());
      Assert.AreNotEqual(previous, edited, "The dialog must still have unsaved edits");

      Field("_settingsFile").SetValue(null, SettingsPath);
      Assert.IsTrue(edited.TrySave());
      ReloadFromDisk();
      Assert.AreEqual(edited, LogManagementSettings.Load());
    }

    [TestMethod]
    public void FailedConfigWriteKeepsThePendingSaveForRetry()
    {
      ConfigUtil.SetSetting("LogManagementArchiveFolder", @"C:\Raids=2026");
      Field("_settingsFile").SetValue(null, _directory);
      Assert.IsFalse(ConfigUtil.Save());

      Field("_settingsFile").SetValue(null, SettingsPath);
      Assert.IsTrue(ConfigUtil.Save(), "A retry with no further edits must still write the settings");
      ReloadFromDisk();
      Assert.AreEqual(@"C:\Raids=2026", ConfigUtil.GetSetting("LogManagementArchiveFolder"));
    }

    private static IEnumerable<LogManagementSettings> DialogValues()
    {
      var saved = new LogManagementSettings { ArchiveFolder = @"C:\Archives" };
      yield return saved;
      yield return saved with { Enabled = true };
      yield return saved with { ArchiveFolder = @"C:\" };
      yield return saved with { ArchiveFolder = @"\\server\share\" };
      yield return saved with { ArchiveFolder = @"D:\Raid=Logs" };
      foreach (var value in new[] { "Any Size", "25M", "50M", "75M", "100M", "250M", "500M", "750M", "1G", "1.5G", "2G" })
      {
        yield return saved with { MinFileSize = value };
      }
      foreach (var value in new[] { "Any Age", "1 Day", "3 Days", "1 Week", "2 Weeks", "3 Weeks", "1 Month" })
      {
        yield return saved with { MinFileAge = value };
      }
      yield return saved with { Compress = "No" };
      yield return saved with { Organize = "Server and Character Folders" };
      yield return saved with { Type = "Selected Day/Time" };
      foreach (var value in new[] { "Daily", "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" })
      {
        yield return saved with { Day = value };
      }
      for (var hour = 1; hour <= 12; hour++)
      {
        yield return saved with { Hour = hour.ToString("00", System.Globalization.CultureInfo.InvariantCulture) };
      }
      foreach (var value in new[] { "00", "15", "30", "45" })
      {
        yield return saved with { Minute = value };
      }
      yield return saved with { AmPm = "PM" };
    }

    private void ReloadFromDisk()
    {
      Settings.Clear();
      var load = typeof(ConfigUtil).GetMethod("LoadProperties", PrivateStatic)
        ?? throw new InvalidOperationException("Config loader not found");
      load.Invoke(null, [Settings, ConfigUtil.ReadList(SettingsPath)]);
    }

    private static FieldInfo Field(string name) => typeof(ConfigUtil).GetField(name, PrivateStatic)
      ?? throw new InvalidOperationException("Config field not found: " + name);
  }
}
