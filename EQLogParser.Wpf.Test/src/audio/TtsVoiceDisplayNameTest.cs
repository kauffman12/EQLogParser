using EQLogParser.Audio;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EQLogParser.Wpf.Test.src.audio
{
  /// <summary>
  /// Voice pickers show a name a person would use while the value stored and spoken stays the engine's id. The label
  /// rules live in each engine, so that is what is pinned here: Kokoro reads its accent out of the voice id, Piper out
  /// of the locale its models declare, Windows leaves well enough alone.
  /// </summary>
  [TestClass]
  public class TtsVoiceDisplayNameTest
  {
    [TestMethod]
    public void KokoroLabelsAmericanVoices()
    {
      Assert.AreEqual("Nicole (US)", KokoroTtsEngine.DisplayNameFor("af_nicole"));
      Assert.AreEqual("Heart (US)", KokoroTtsEngine.DisplayNameFor("af_heart"));
      Assert.AreEqual("Fenrir (US)", KokoroTtsEngine.DisplayNameFor("am_fenrir"));
    }

    [TestMethod]
    public void KokoroLabelsBritishVoices()
    {
      Assert.AreEqual("Emma (GB)", KokoroTtsEngine.DisplayNameFor("bf_emma"));
      Assert.AreEqual("George (GB)", KokoroTtsEngine.DisplayNameFor("bm_george"));
      Assert.AreEqual("Isabella (GB)", KokoroTtsEngine.DisplayNameFor("bf_isabella"));
    }

    [TestMethod]
    public void KokoroLabelsTheOtherLocalesItCanShip()
    {
      Assert.AreEqual("Dora (ES)", KokoroTtsEngine.DisplayNameFor("ef_dora"));
      Assert.AreEqual("Siwis (FR)", KokoroTtsEngine.DisplayNameFor("ff_siwis"));
      Assert.AreEqual("Yunxi (CN)", KokoroTtsEngine.DisplayNameFor("zm_yunxi"));
      Assert.AreEqual("Kumo (JP)", KokoroTtsEngine.DisplayNameFor("jm_kumo"));
    }

    [TestMethod]
    public void KokoroLeavesAnythingElseAsStored()
    {
      // A voice kept from another engine, a hand named embedding, nothing at all: dressing any of those up would put
      // text in the picker that no longer matches what is saved.
      Assert.AreEqual("Microsoft David Desktop", KokoroTtsEngine.DisplayNameFor("Microsoft David Desktop"));
      Assert.AreEqual("en_US-lessac-medium", KokoroTtsEngine.DisplayNameFor("en_US-lessac-medium"));
      Assert.AreEqual("qf_xena", KokoroTtsEngine.DisplayNameFor("qf_xena"));
      Assert.AreEqual("af_", KokoroTtsEngine.DisplayNameFor("af_"));
      Assert.AreEqual(string.Empty, KokoroTtsEngine.DisplayNameFor(string.Empty));
      Assert.IsNull(KokoroTtsEngine.DisplayNameFor(null));
    }

    [TestMethod]
    public void PiperLabelsUseTheLocaleOfTheVoice()
    {
      Assert.AreEqual("HFC Male (US)", PiperTtsEngine.FormatDisplayName("HFC Male", "US"));
      Assert.AreEqual("Alba (GB)", PiperTtsEngine.FormatDisplayName("Alba", "GB"));

      // No locale known is not a reason to show an empty parenthesis.
      Assert.AreEqual("HFC Male", PiperTtsEngine.FormatDisplayName("HFC Male", null));
      Assert.AreEqual("HFC Male", PiperTtsEngine.FormatDisplayName("HFC Male", string.Empty));
    }

    [TestMethod]
    public void PiperReadsTheLocaleOutOfVoiceMetadata()
    {
      Assert.AreEqual("US", PiperTtsEngine.LocaleFromMetadata(
        "{ \"language\": { \"code\": \"en_US\", \"family\": \"en\", \"name_english\": \"English\" } }"));
      Assert.AreEqual("CN", PiperTtsEngine.LocaleFromMetadata("{ \"language\": { \"code\": \"zh_CN\" } }"));

      // Some releases carry a region alone, and some say nothing worth showing.
      Assert.AreEqual("GB", PiperTtsEngine.LocaleFromMetadata("{ \"language\": { \"region\": \"GB\" } }"));
      Assert.IsNull(PiperTtsEngine.LocaleFromMetadata("{ \"audio\": { \"sample_rate\": 22050 } }"));
      Assert.IsNull(PiperTtsEngine.LocaleFromMetadata("{ \"language\": 7 }"));
      Assert.IsNull(PiperTtsEngine.LocaleFromMetadata("not json at all"));
    }

    [TestMethod]
    public void PiperFallsBackToTheVoiceFileName()
    {
      // Piper names models locale first, which is what a pack with no readable metadata still tells the reader.
      Assert.AreEqual("US", PiperTtsEngine.LocaleFromPath("hfc_male/en_US-hfc_male-medium.onnx"));
      Assert.AreEqual("GB", PiperTtsEngine.LocaleFromPath("en_GB-alba-medium.onnx.json"));
      Assert.AreEqual("CN", PiperTtsEngine.LocaleFromPath("zh_CN-huayan-medium.onnx"));
      Assert.IsNull(PiperTtsEngine.LocaleFromPath("lessac-medium.onnx"));
      Assert.IsNull(PiperTtsEngine.LocaleFromPath(null));
    }

    [TestMethod]
    public void LocaleRegionsAcceptWhatPacksActuallyDeclare()
    {
      Assert.AreEqual("US", PiperTtsEngine.RegionOf("en_US"));
      Assert.AreEqual("US", PiperTtsEngine.RegionOf(" US "));
      Assert.AreEqual("PT", PiperTtsEngine.RegionOf("pt_BR"));

      // Nothing to print for a token with no region in it.
      Assert.IsNull(PiperTtsEngine.RegionOf(null));
      Assert.IsNull(PiperTtsEngine.RegionOf("   "));
      Assert.IsNull(PiperTtsEngine.RegionOf("english"));
      Assert.IsNull(PiperTtsEngine.RegionOf("en_"));
    }
  }
}
