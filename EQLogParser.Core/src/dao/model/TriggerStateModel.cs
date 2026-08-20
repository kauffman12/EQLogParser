using LiteDB;
using System.Collections.Generic;
using System.ComponentModel;

namespace EQLogParser
{
  /* Legacy overlay format for the pre-1.0 json upgrade path. */
  internal class LegacyOverlay : Overlay
  {
    public string Id { get; set; }
    public string Name { get; set; }

    // Explicit port of the old Mapperly mapping (the mapper stays in the WPF host): copies all
    // Overlay fields, drops the legacy-only Id/Name.
    public Overlay ToOverlay()
    {
      return new Overlay
      {
        Source = Source,
        OverlayComments = OverlayComments,
        FontSize = FontSize,
        FontWeight = FontWeight,
        SortBy = SortBy,
        HorizontalAlignment = HorizontalAlignment,
        VerticalAlignment = VerticalAlignment,
        FontColor = FontColor,
        FontFamily = FontFamily,
        ActiveColor = ActiveColor,
        BackgroundColor = BackgroundColor,
        IdleColor = IdleColor,
        ResetColor = ResetColor,
        OverlayColor = OverlayColor,
        IdleTimeoutSeconds = IdleTimeoutSeconds,
        FadeDelay = FadeDelay,
        UseStandardTime = UseStandardTime,
        ShowMillis = ShowMillis,
        IsTimerOverlay = IsTimerOverlay,
        IsTextOverlay = IsTextOverlay,
        IsDefault = IsDefault,
        ShowActive = ShowActive,
        ShowIdle = ShowIdle,
        ShowReset = ShowReset,
        StreamerMode = StreamerMode,
        HideDuplicates = HideDuplicates,
        UseTextDropShadow = UseTextDropShadow,
        TextOverlayWrap = TextOverlayWrap,
        TimerMode = TimerMode,
        Height = Height,
        Width = Width,
        Top = Top,
        Left = Left,
        ClosePattern = ClosePattern,
        UseCloseRegex = UseCloseRegex
      };
    }
  }
  internal class TriggerState
  {
    [BsonId]
    public string Id { get; set; }
    public Dictionary<string, bool?> Enabled { get; set; } = [];
  }


  /* Character entry in Advanced Trigger Manager. Parent/Index organize the folder tree; empty Parent is root. */
  internal class TriggerCharacter : INotifyPropertyChanged
  {
    private bool _isEnabled;
    private bool? _isWaiting = true;

    public string Id { get; set; }
    public string Name { get; set; }
    public string FilePath { get; set; }
    public bool IsEnabled
    {
      get => _isEnabled;
      set
      {
        if (_isEnabled != value)
        {
          _isEnabled = value;
          PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
      }
    }
    public string Voice { get; set; }
    public int VoiceRate { get; set; }
    public int CustomVolume { get; set; } = -1;
    public string ActiveColor { get; set; }
    public string IdleColor { get; set; }
    public string ResetColor { get; set; }
    public string FontColor { get; set; }
    /* Folder id that contains this character. Null/empty means the character is at the tree root. */
    public string Parent { get; set; }
    /* Sort order among siblings in the same folder (or at root). */
    public int Index { get; set; }
    [BsonIgnore]
    public bool? IsWaiting
    {
      get => _isWaiting;
      set
      {
        if (_isWaiting != value)
        {
          _isWaiting = value;
          PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsWaiting)));
        }
      }
    }
    [BsonIgnore]
    public event PropertyChangedEventHandler PropertyChanged;
  }

  /* Nested folder used only for organizing characters. Checking a folder enables/disables descendant characters. */
  internal class TriggerCharacterFolder
  {
    public string Id { get; set; }
    public string Name { get; set; }
    /* Parent folder id. Null/empty means this folder is at the tree root. */
    public string Parent { get; set; }
    public int Index { get; set; }
    public bool IsExpanded { get; set; } = true;
  }

  internal class TriggerConfig
  {
    [BsonId]
    public string Id { get; set; }
    public bool IsAdvanced { get; set; }
    public List<TriggerCharacter> Characters { get; set; } = [];
    public List<TriggerCharacterFolder> CharacterFolders { get; set; } = [];
    public bool IsEnabled { get; set; }
    public string Voice { get; set; }
    public int VoiceRate { get; set; }
  }


  internal class LegacyTriggerNode
  {
    public bool? IsEnabled { get; set; } = false;
    public bool IsExpanded { get; set; }
    public string Name { get; set; }
    public List<LegacyTriggerNode> Nodes { get; set; } = [];
    public Trigger TriggerData { get; set; }
    public LegacyOverlay OverlayData { get; set; }
  }

}
