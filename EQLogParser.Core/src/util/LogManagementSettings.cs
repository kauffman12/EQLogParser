namespace EQLogParser
{
  // The dialog edits a snapshot; neither widget events nor a failed write commit it.
  internal sealed record LogManagementSettings
  {
    internal bool Enabled { get; init; }
    internal string ArchiveFolder { get; init; } = "";
    internal string MinFileSize { get; init; } = "500M";
    internal string MinFileAge { get; init; } = "1 Week";
    internal string Compress { get; init; } = "Yes";
    internal string Organize { get; init; } = "Individual Files";
    internal string Type { get; init; } = "Activity";
    internal string Day { get; init; } = "Sunday";
    internal string Hour { get; init; } = "12";
    internal string Minute { get; init; } = "00";
    internal string AmPm { get; init; } = "AM";

    internal bool CanSave => !Enabled || (!string.IsNullOrWhiteSpace(ArchiveFolder)
      && (Type != "Activity" || MinFileSize != "Any Size" || MinFileAge != "Any Age"));

    internal static LogManagementSettings Load() => new()
    {
      Enabled = ConfigUtil.IfSet("LogManagementEnabled"),
      ArchiveFolder = ConfigUtil.GetSetting("LogManagementArchiveFolder", ""),
      MinFileSize = ConfigUtil.GetSetting("LogManagementMinFileSize", "500M"),
      MinFileAge = ConfigUtil.GetSetting("LogManagementMinFileAge", "1 Week"),
      Compress = ConfigUtil.GetSetting("LogManagementCompressArchive", "Yes"),
      Organize = ConfigUtil.GetSetting("LogManagementOrganize", "Individual Files"),
      Type = ConfigUtil.GetSetting("LogManagementType", "Activity"),
      Day = ConfigUtil.GetSetting("LogManagementScheduleDay", "Sunday"),
      Hour = ConfigUtil.GetSetting("LogManagementScheduleHour", "12"),
      Minute = ConfigUtil.GetSetting("LogManagementScheduleMinute", "00"),
      AmPm = ConfigUtil.GetSetting("LogManagementScheduleAMPM", "AM")
    };

    internal bool TrySave()
    {
      if (!CanSave)
      {
        return false;
      }

      var previous = Load();
      Store();
      if (ConfigUtil.Save())
      {
        return true;
      }

      previous.Store();
      return false;
    }

    private void Store()
    {
      ConfigUtil.SetSetting("LogManagementEnabled", Enabled);
      ConfigUtil.SetSetting("LogManagementArchiveFolder", string.IsNullOrEmpty(ArchiveFolder) ? null : ArchiveFolder);
      ConfigUtil.SetSetting("LogManagementMinFileSize", MinFileSize);
      ConfigUtil.SetSetting("LogManagementMinFileAge", MinFileAge);
      ConfigUtil.SetSetting("LogManagementCompressArchive", Compress);
      ConfigUtil.SetSetting("LogManagementOrganize", Organize);
      ConfigUtil.SetSetting("LogManagementType", Type);
      ConfigUtil.SetSetting("LogManagementScheduleDay", Day);
      ConfigUtil.SetSetting("LogManagementScheduleHour", Hour);
      ConfigUtil.SetSetting("LogManagementScheduleMinute", Minute);
      ConfigUtil.SetSetting("LogManagementScheduleAMPM", AmPm);
    }
  }
}
