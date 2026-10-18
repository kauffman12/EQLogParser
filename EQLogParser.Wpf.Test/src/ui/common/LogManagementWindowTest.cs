using EQLogParser;
using System.Collections.Concurrent;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser.Wpf.Test;

[TestClass]
public class LogManagementWindowTest
{
  [TestMethod]
  public void RevertingAnEditReturnsToTheSavedSnapshot()
  {
    var changed = false;
    var reverted = false;
    var snapshotRestored = false;
    WithWindow(window =>
    {
      var combo = Control<ComboBox>(window, "compress");
      var save = Control<Button>(window, "saveButton");
      var saved = ReadSettings(window);
      combo.SelectedIndex = 1;
      changed = save.IsEnabled;
      combo.SelectedIndex = 0;
      reverted = !save.IsEnabled && Equals(Control<Button>(window, "closeButton").Content, "Close");
      snapshotRestored = ReadSettings(window) == saved;
    });

    Assert.IsTrue(changed);
    Assert.IsTrue(reverted);
    Assert.IsTrue(snapshotRestored, "Archive Now compares this snapshot, not whether an event has ever fired");
  }

  [TestMethod]
  public void ReturningTheEnableCheckboxToItsSavedValueRevalidatesOtherEdits()
  {
    var invalid = false;
    var disabledCanSave = false;
    var reenabledInvalid = false;
    WithWindow(window =>
    {
      var enabled = Control<CheckBox>(window, "enableCheckBox");
      var save = Control<Button>(window, "saveButton");
      Control<ComboBox>(window, "fileSizes").SelectedIndex = 0;
      Control<ComboBox>(window, "fileAges").SelectedIndex = 0;
      invalid = !save.IsEnabled;
      enabled.IsChecked = false;
      disabledCanSave = save.IsEnabled;
      enabled.IsChecked = true;
      reenabledInvalid = !save.IsEnabled;
    });

    Assert.IsTrue(invalid);
    Assert.IsTrue(disabledCanSave);
    Assert.IsTrue(reenabledInvalid, "Re-checking must not leave Save enabled for unbounded activity archiving");
  }

  [TestMethod]
  [DataRow("fileSizes", nameof(LogManagementSettings.MinFileSize))]
  [DataRow("fileAges", nameof(LogManagementSettings.MinFileAge))]
  [DataRow("compress", nameof(LogManagementSettings.Compress))]
  [DataRow("organize", nameof(LogManagementSettings.Organize))]
  [DataRow("type", nameof(LogManagementSettings.Type))]
  [DataRow("day", nameof(LogManagementSettings.Day))]
  [DataRow("hour", nameof(LogManagementSettings.Hour))]
  [DataRow("min", nameof(LogManagementSettings.Minute))]
  [DataRow("ampm", nameof(LogManagementSettings.AmPm))]
  public void EveryComboChoiceFeedsTheMatchingSetting(string controlName, string propertyName)
  {
    var results = new List<(string Expected, string Actual)>();
    WithWindow(window =>
    {
      var combo = Control<ComboBox>(window, controlName);
      var property = typeof(LogManagementSettings).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Setting not found: " + propertyName);
      for (var i = 0; i < combo.Items.Count; i++)
      {
        combo.SelectedIndex = i;
        var expected = ((ComboBoxItem)combo.SelectedItem).Content.ToString() ?? "";
        results.Add((expected, property.GetValue(ReadSettings(window)) as string ?? ""));
      }
    });

    foreach (var result in results)
    {
      Assert.AreEqual(result.Expected, result.Actual, controlName);
    }
  }

  private static void WithWindow(Action<LogManagementWindow> body)
  {
    Sta.Run(() =>
    {
      var flags = BindingFlags.NonPublic | BindingFlags.Static;
      var settings = typeof(ConfigUtil).GetField("ApplicationSettings", flags)?.GetValue(null)
        as ConcurrentDictionary<string, string> ?? throw new InvalidOperationException("Config settings not found");
      var dirtyField = typeof(ConfigUtil).GetField("_settingsUpdated", flags)
        ?? throw new InvalidOperationException("Config dirty flag not found");
      var saved = settings.ToArray();
      var dirty = dirtyField.GetValue(null);
      settings.Clear();
      ConfigUtil.SetSetting("LogManagementEnabled", true);
      ConfigUtil.SetSetting("LogManagementArchiveFolder", @"C:\Archives");
      try
      {
        var app = Application.Current ?? new Application();
        app.Resources["CustomCheckBoxTemplate"] ??= new ControlTemplate(typeof(CheckBox));
        var window = new LogManagementWindow();
        try
        {
          body(window);
        }
        finally
        {
          window.Close();
        }
      }
      finally
      {
        settings.Clear();
        foreach (var pair in saved)
        {
          settings[pair.Key] = pair.Value;
        }
        dirtyField.SetValue(null, dirty);
      }
    });
  }

  private static T Control<T>(LogManagementWindow window, string name) where T : FrameworkElement =>
    window.FindName(name) as T ?? throw new InvalidOperationException("Control not found: " + name);

  private static LogManagementSettings ReadSettings(LogManagementWindow window) =>
    typeof(LogManagementWindow).GetMethod("ReadSettings", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(window, null)
      as LogManagementSettings ?? throw new InvalidOperationException("Settings reader not found");
}
