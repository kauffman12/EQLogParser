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

    /* Port to the current model: Mapperly's generated deep clone of the base type copies every
     * Overlay member and drops the legacy-only Id/Name. The hand-written field list this replaced
     * silently dropped any Overlay field added later; the generated clone is rebuilt from the type
     * on every compile, and OverlayCloneTest asserts full coverage. */
    public Overlay ToOverlay() => ModelMapper.Clone(this);
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
