using EQLogParser.Audio;
using System;
using System.Globalization;
using System.Windows.Data;

namespace EQLogParser
{
  /// <summary>
  /// Shows a voice the way a person would name it: "af_nicole" reads "Nicole (US)" and a Piper model reads its pack
  /// name plus the locale it was trained on. Display only - the item itself stays the engine's voice id, which is what
  /// gets saved to config, matched against a character and handed to the engine.
  /// </summary>
  public class VoiceNameConverter : IValueConverter
  {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
      value is string { Length: > 0 } voice ? LabelOf(voice) : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
      throw new NotImplementedException();

    // A label is never worth losing a picker over, so anything the engine cannot answer falls back to the id itself.
    private static object LabelOf(string voice)
    {
      try
      {
        return AudioManager.Instance.GetVoiceDisplayName(voice);
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"Unable to label voice '{voice}': {ex.Message}");
        return voice;
      }
    }
  }
}
