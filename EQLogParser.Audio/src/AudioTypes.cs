namespace EQLogParser.Audio
{
  /// <summary>Metadata for a Piper TTS voice.</summary>
  public class PiperVoice
  {
    public string Name { get; set; }
    public string Model { get; set; }
    public string Config { get; set; }
    public int Sample { get; set; }
  }

  /// <summary>Root JSON structure for Piper voices catalog.</summary>
  public class PiperVoiceData
  {
    public System.Collections.Generic.List<PiperVoice> Voices { get; set; }
  }
}