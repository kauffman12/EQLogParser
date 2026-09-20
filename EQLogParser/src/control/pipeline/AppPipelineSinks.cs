namespace EQLogParser;

// App-layer adapters that wire LogProcessor's side channels to the WPF singletons. These are the
// only pipeline pieces that stay in the app project — headless runs use no-ops instead.
internal sealed class ChatDbSink : IChatSink
{
  public void Init()
  {
    ChatDB.Instance.Init();
  }

  public void Add(ChatType chat)
  {
    ChatDB.Instance.Add(chat);
  }
}

internal sealed class TriggerHookAdapter : ITriggerHook
{
  public void CheckQuickShare(ChatType chat, string action, double beginTime)
  {
    TriggerUtil.CheckQuickShare(chat, action, beginTime, false, TriggerStateDB.DefaultUser);
  }
}
