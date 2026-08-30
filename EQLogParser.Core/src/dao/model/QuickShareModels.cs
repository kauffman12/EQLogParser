namespace EQLogParser
{
  // One row of the quick-share history (GINA or legacy share types). Plain data so the WPF
  // window can bind to a copy of it while the domain logic stays cross-platform.
  public class QuickShareRecord
  {
    public double BeginTime { get; set; }

    public string Type { get; set; }

    public string To { get; set; }

    public string From { get; set; }

    public string Key { get; set; }

    public bool IsMine { get; set; }

    /* Builds the history row for a share seen in chat. Both producers — GINA detection and the legacy
     * share flow — applied the same ownership and "To" rules from two verbatim copies of this block;
     * one copy means the history cannot drift between them (GINA passes Type = "GINA"). */
    public static QuickShareRecord FromChat(ChatType chatType, string type, string key, double beginTime,
      string characterId, string processorName)
    {
      var to = chatType.Channel == ChatChannels.Tell ? "You" : chatType.Channel;

      return new QuickShareRecord
      {
        BeginTime = beginTime,
        Key = key,
        From = chatType.Sender,
        To = to == "You" && processorName != null && characterId != TriggerStateDB.DefaultUser
          ? processorName
          : TextUtils.CapitalizeFirst(to),
        IsMine = chatType.SenderIsYou,
        Type = type
      };
    }
  }

  // Per-quick-share context accumulated as characters see the same share key in chat.
  internal class CharacterData
  {
    public string Sender { get; set; }

    public HashSet<string> CharacterIds { get; set; } = [];

    public bool AutoMerge { get; set; }

    public bool IsTrigger { get; set; }
  }
}
