using EQLogParser.Audio;
using System.Windows;

namespace EQLogParser.Wpf.Test
{
  /*
   * The engine dialog has a single action button, so the decision about what it should say is the decision about
   * whether the engine can be used at all. It grew out of a bug where the button was shown only for engines with a
   * runtime pack to download, which left the Windows voices - ready to use, nothing to fetch - with no Use button and
   * no way back to them once another engine had taken over.
   *
   * PlanAction takes the three facts the dialog asks AudioManager for and answers on its own, so none of these cases
   * need a window, a pack on disk, or voices installed.
   */
  [TestClass]
  public class TtsEngineActionButtonTest
  {
    private const long KokoroBytes = 228L * 1024 * 1024;
    private const long PiperBytes = 682L * 1024 * 1024;

    [TestMethod]
    public void PlanAction_WindowsReadyWhileAnotherEngineSpeaks_OffersUse()
    {
      // The reported case: Kokoro is speaking, the Windows voices are there, nothing to download
      var action = TtsEngineWindow.PlanAction(AudioManager.WindowsEngine, 0, true, false, false);

      Assert.AreEqual(Visibility.Visible, action.Visibility);
      Assert.AreEqual("Use Windows", action.Content);
      Assert.IsTrue(action.IsEnabled);
    }

    [TestMethod]
    public void PlanAction_WindowsVoicesMissing_NoButtonAtAll()
    {
      // Under Wine there is neither a pack to fetch nor a voice to speak with, so nothing is offered
      var action = TtsEngineWindow.PlanAction(AudioManager.WindowsEngine, 0, false, false, false);

      Assert.AreEqual(Visibility.Collapsed, action.Visibility);
      Assert.IsFalse(action.IsEnabled);
    }

    [TestMethod]
    public void PlanAction_WindowsAlreadySpeaking_ReportsInUse()
    {
      var action = TtsEngineWindow.PlanAction(AudioManager.WindowsEngine, 0, true, true, false);

      Assert.AreEqual(Visibility.Visible, action.Visibility);
      Assert.AreEqual("In use", action.Content);
      Assert.IsFalse(action.IsEnabled);
    }

    [TestMethod]
    public void PlanAction_PackMissing_OffersDownloadWithSize()
    {
      var action = TtsEngineWindow.PlanAction(AudioManager.KokoroEngine, KokoroBytes, false, false, false);

      Assert.AreEqual(Visibility.Visible, action.Visibility);
      Assert.AreEqual("Download Kokoro (228 MB)", action.Content);
      Assert.IsTrue(action.IsEnabled);
    }

    [TestMethod]
    public void PlanAction_PackInstalledButNotSpeaking_OffersUse()
    {
      var action = TtsEngineWindow.PlanAction(AudioManager.PiperEngine, PiperBytes, true, false, false);

      Assert.AreEqual(Visibility.Visible, action.Visibility);
      Assert.AreEqual("Use Piper", action.Content);
      Assert.IsTrue(action.IsEnabled);
    }

    [TestMethod]
    public void PlanAction_PackInstalledAndSpeaking_ReportsInUse()
    {
      var action = TtsEngineWindow.PlanAction(AudioManager.PiperEngine, PiperBytes, true, true, false);

      Assert.AreEqual(Visibility.Visible, action.Visibility);
      Assert.AreEqual("In use", action.Content);
      Assert.IsFalse(action.IsEnabled);
    }

    [TestMethod]
    public void PlanAction_SwitchingOrDownloading_LeavesButtonVisibleButDisabled()
    {
      // A press mid-switch or mid-download must do nothing, but the button cannot vanish on its own
      var switching = TtsEngineWindow.PlanAction(AudioManager.WindowsEngine, 0, true, false, true);
      Assert.AreEqual(Visibility.Visible, switching.Visibility);
      Assert.IsFalse(switching.IsEnabled);

      var downloading = TtsEngineWindow.PlanAction(AudioManager.KokoroEngine, KokoroBytes, false, false, true);
      Assert.AreEqual(Visibility.Visible, downloading.Visibility);
      Assert.IsFalse(downloading.IsEnabled);
    }
  }
}
