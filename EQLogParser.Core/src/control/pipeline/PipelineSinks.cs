namespace EQLogParser;

// Side channels LogProcessor used to hard-wire to app-layer singletons (ChatDB, TriggerUtil).
// The WPF app supplies real adapters (AppPipelineSinks); headless runs supply no-ops so the whole
// pipeline executes inside the test project with no WPF in sight.
internal interface IChatSink
{
  void Init();

  void Add(ChatType chat);
}

internal interface ITriggerHook
{
  void CheckQuickShare(ChatType chat, string action, double beginTime);
}
