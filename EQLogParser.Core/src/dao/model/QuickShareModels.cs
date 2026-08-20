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
