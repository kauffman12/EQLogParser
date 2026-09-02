namespace EQLogParser.Audio
{
  /// <summary>Metadata for a Piper TTS voice.</summary>
  public class PiperVoice
  {
    public string Name { get; set; }
    public string Model { get; set; }
    public string Config { get; set; }
    public int Sample { get; set; }

    // Optional in voices.json. A pack that leaves it out gets the locale read from the voice's own model metadata, or
    // from the file name, when the engine starts. Used to label the voice picker only; nothing spoken depends on it.
    public string Locale { get; set; }
  }

  /// <summary>Root JSON structure for Piper voices catalog.</summary>
  public class PiperVoiceData
  {
    public System.Collections.Generic.List<PiperVoice> Voices { get; set; }
  }
}