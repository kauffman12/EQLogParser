using EQLogParser;

namespace EQLogParser;

/*
 * What survives a cleared session, which is the promise behind the fight list's Clear All.
 *
 * The button cannot wipe rows (a projection re-derives them from the facts, so deleting them reads as a button that does
 * nothing), and what it actually does — re-open the same file as a monitor session — fans out through
 * LifecycleManager.Clear. So the question the operator asked ("keep the list of pets or players you figured out, like when
 * we open a file and it loads the previous data") is answered by ONE structural fact: the identity memory is not on the
 * lifecycle list at all. Registered there are the per-capture stores — EQDataStore, PlayerRegistry, RaidRosterStore,
 * RecordsStore — while identity-overrides.txt and identity-priors.txt sit outside it, loaded by Init(serverName) when the
 * SERVER changes and saved as they go.
 *
 * Hence both halves below: a clear leaves the memory standing under either payload (the operator's saved verdicts are not a
 * session's property to throw away), and moving servers is what swaps it — which is also why MainWindow loads the three
 * stores in a fixed order before the engine's first pass.
 *
 * Temp config dir per test, exactly as IdentityPriorStoreTest does: these write real files.
 */
[TestClass]
public sealed class ClearedSessionMemoryTest
{
  private string _savedConfigDir = "";
  private string _savedServerName = "";
  private string _tempDir = "";

  private const string Server = "Clear All Memory";
  private const string OtherServer = "Clear All Memory Other";

  [TestInitialize]
  public void Setup()
  {
    _savedConfigDir = ConfigUtil.ConfigDir;
    _savedServerName = ConfigUtil.ServerName;

    _tempDir = Path.Combine(Path.GetTempPath(), "clearall-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_tempDir);
    ConfigUtil.ConfigDir = _tempDir;
    ConfigUtil.ServerName = Server;

    IdentityOverrideStore.Instance.Init(Server);
    IdentityPriorStore.Instance.Init(Server);
  }

  [TestCleanup]
  public void Cleanup()
  {
    // Init on a throwaway name so no row written here is readable by the next test in the run.
    IdentityOverrideStore.Instance.Init("clearall-cleanup-" + Guid.NewGuid().ToString("N"));
    IdentityPriorStore.Instance.Init("clearall-cleanup-" + Guid.NewGuid().ToString("N"));
    ConfigUtil.ConfigDir = _savedConfigDir;
    ConfigUtil.ServerName = _savedServerName;
    try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch (IOException) { }
  }

  [TestMethod]
  [Description("Clearing a session under either payload leaves the operator's saved verdicts and the ledger standing")]
  public void AClearKeepsTheSavedIdentity()
  {
    IdentityOverrideStore.Instance.Set("Zeus", IdentityKind.Pet);
    IdentityPriorStore.Instance.RememberPet("Fluffy", "Ziggy", NowS());

    LifecycleManager.Clear(serverChanged: false);

    Assert.IsTrue(IdentityOverrideStore.Instance.TryGet("Zeus", out var kind), "Clear All may empty the raid, not the override file");
    Assert.AreEqual(IdentityKind.Pet, kind);
    Assert.IsTrue(IdentityPriorStore.Instance.TryGetOwner("Fluffy", out var owner), "and not the ownership lane of the ledger");
    Assert.AreEqual("Ziggy", owner);

    // The other payload is a different server's open, which clears the per-server stores on the way IN — it still has no
    // hand on these two, because their files belong to whichever folder this app is pointing at.
    LifecycleManager.Clear(serverChanged: true);

    Assert.IsTrue(IdentityOverrideStore.Instance.TryGet("Zeus", out _));
    Assert.IsTrue(IdentityPriorStore.Instance.TryGetOwner("Fluffy", out _));
    Assert.AreEqual(Server, IdentityPriorStore.Instance.ServerName, "a clear is not a server switch; Init does that, and only for the server it was handed");
  }

  [TestMethod]
  [Description("Reloading the folder reads the same memory back off disk, so a cleared session can be opened again")]
  public void TheSavedIdentityComesBackOnReload()
  {
    IdentityOverrideStore.Instance.Set("Zeus", IdentityKind.Pet);
    IdentityPriorStore.Instance.RememberPet("Fluffy", "Ziggy", NowS());

    // What a fresh open over the same folder does: the stores Init from their files. Between the two, the session is gone.
    LifecycleManager.Clear(serverChanged: false);
    IdentityOverrideStore.Instance.Init(Server);
    IdentityPriorStore.Instance.Init(Server);

    Assert.IsTrue(IdentityOverrideStore.Instance.TryGet("Zeus", out var kind));
    Assert.AreEqual(IdentityKind.Pet, kind);
    Assert.IsTrue(IdentityPriorStore.Instance.TryGetOwner("Fluffy", out var owner));
    Assert.AreEqual("Ziggy", owner);

    // ...and a different folder's memory is that folder's alone — the reason MainWindow's load order files under the
    // server it holds rather than the one the last raid happened to be on.
    IdentityOverrideStore.Instance.Init(OtherServer);
    IdentityPriorStore.Instance.Init(OtherServer);

    Assert.IsFalse(IdentityOverrideStore.Instance.TryGet("Zeus", out _), "one server's verdicts do not follow the app into another folder");
    Assert.IsFalse(IdentityPriorStore.Instance.TryGetOwner("Fluffy", out _));
  }

  private static long NowS() => (long)DateUtil.ToDotNetSeconds(DateTime.Now);
}
