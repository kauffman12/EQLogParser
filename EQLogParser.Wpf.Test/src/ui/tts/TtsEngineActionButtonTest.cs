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

    /*
     * The line under the engine description carries the states a button cannot: whether this engine is speaking now,
     * and - the one that read as a bug twice - whether the files on disk are a runtime or the leftovers of a removal
     * that could not finish while an earlier engine still had them mapped.
     */
    [TestMethod]
    public void PlanHint_PackOnDiskAndWorking_OffersTheSwitch()
    {
      var hint = TtsEngineWindow.PlanHint(AudioManager.KokoroEngine, true, true, true, false, null);

      StringAssert.Contains(hint.Text, "Press Use");
      Assert.IsNull(hint.BrushKey);
    }

    [TestMethod]
    public void PlanHint_NothingOnDisk_SaysNotInstalled()
    {
      var hint = TtsEngineWindow.PlanHint(AudioManager.PiperEngine, false, false, true, false, null);

      StringAssert.Contains(hint.Text, "not installed");
      Assert.IsNull(hint.BrushKey);
    }

    [TestMethod]
    public void PlanHint_HalfARemovedPack_SaysTheFilesDoNotWork()
    {
      /*
       * A directory that exists but is not a usable pack: Download and Remove both have a job to do there, which is
       * exactly what looks like the dialog contradicting itself unless somebody says what is on disk.
       */
      var hint = TtsEngineWindow.PlanHint(AudioManager.PiperEngine, false, true, true, false, null);

      StringAssert.Contains(hint.Text, "incomplete or damaged");
      StringAssert.Contains(hint.Text, "Download");
      Assert.IsNotNull(hint.BrushKey);

      // and the button agrees: replacing those files is what the download does
      var action = TtsEngineWindow.PlanAction(AudioManager.PiperEngine, PiperBytes, false, false, false);
      StringAssert.StartsWith(action.Content, "Download Piper");
    }

    [TestMethod]
    public void PlanHint_InUseWithTheSavedChoice_SaysItPlainly()
    {
      var saved = TtsEngineWindow.PlanHint(AudioManager.KokoroEngine, true, true, true, true, "Kokoro");
      StringAssert.Contains(saved.Text, "currently in use");
      Assert.IsNull(saved.BrushKey);

      // Nothing saved yet is not a disagreement; the first session has no preference to contradict.
      var fresh = TtsEngineWindow.PlanHint(AudioManager.KokoroEngine, true, true, true, true, null);
      StringAssert.Contains(fresh.Text, "currently in use");
      Assert.IsNull(fresh.BrushKey);

      // The setting is plain text and gets hand edited: a saved 'kokoro' names the engine that is speaking rather
      // than one that failed to come up, and must not be reported as a fallback.
      var typed = TtsEngineWindow.PlanHint(AudioManager.KokoroEngine, true, true, true, true, "kokoro");
      StringAssert.Contains(typed.Text, "currently in use");
      Assert.IsNull(typed.BrushKey);
    }

    [TestMethod]
    public void PlanHint_InUseAgainstTheSavedChoice_NamesTheFallback()
    {
      var hint = TtsEngineWindow.PlanHint(AudioManager.WindowsEngine, true, false, false, true, "Kokoro");

      StringAssert.Contains(hint.Text, "Kokoro");
      Assert.IsNotNull(hint.BrushKey);
    }

    [TestMethod]
    public void PlanHint_NothingAnywhereIsUsable_SaysWhereToGo()
    {
      // Windows under Wine: no pack to fetch, no voices to give, and no fix on this row
      var hint = TtsEngineWindow.PlanHint(AudioManager.WindowsEngine, false, false, false, false, null);

      StringAssert.Contains(hint.Text, "Wine");
      Assert.IsNotNull(hint.BrushKey);
    }
  }
}
