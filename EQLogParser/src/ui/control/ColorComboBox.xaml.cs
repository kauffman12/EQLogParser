
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for ColorComboBox.xaml
  /// </summary>
  public partial class ColorComboBox : ComboBox
  {
    public enum Theme { Light, Dark };

    // https://www.materialpalette.com/colors
    private static readonly List<string> DarkColors = new List<string>()
    {
      "#b71c1c", "#c62828", "#d32f2f", "#e53935", "#f44336",
      "#880e4f", "#ad1457", "#c2185b", "#d81b60", "#e91e63",
      "#4a148c", "#6a1b9a", "#7b1fa2", "#8e24aa", "#9c27b0",
      "#311b92", "#4527a0", "#512da8", "#5e35b1", "#673ab7",
      "#1a237e", "#283593", "#303f9f", "#3949ab", "#3f51b5",
      "#0d47a1", "#1565c0", "#1976d2", "#1e88e5", "#2196f3",
      "#01579b", "#0277bd", "#0288d1", "#039be5", "#03a9f4",
      "#006064", "#00838f", "#0097a7", "#00acc1", "#00bcd4",
      "#004d40", "#00695c", "#00796b", "#00897b", "#009688",
      "#1b5e20", "#2e7d32", "#388e3c", "#43a047", "#4caf50",
      "#33691e", "#558b2f", "#689f38", "#7cb342", "#8bc34a",
      "#827717", "#9e9d24", "#afb42b", "#c0ca33", "#cddc39",
      "#f57f17", "#f9a825", "#fbc02d", "#fdd835", "#ffeb3b",
      "#ff6f00", "#ff8f00", "#ffa000", "#ffb300", "#ffc107",
      "#e65100", "#ef6c00", "#f57c00", "#fb8c00", "#ff9800",
      "#ff5722", "#f4511e", "#e64a19", "#d84315", "#bf360c",
      "#3e2723", "#4e342e", "#5d4037", "#6d4c41", "#795548",
      "#212121", "#424242", "#616161", "#757575", "#9e9e9e",
      "#263238", "#37474f", "#455a64", "#546e7a", "#607d8b"
    };

    private static readonly List<string> LightColors = new List<string>()
    {
      "#ffcdd2", "#ef9a9a", "#e57373", "#ef5350", "#f44336",
      "#f8bbd0", "#f48fb1", "#f06292", "#ec407a", "#e91e63",
      "#e1bee7", "#ce93d8", "#ba68c8", "#ab47bc", "#9c27b0",
      "#d1c4e9", "#b39ddb", "#9575cd", "#7e57c2", "#673ab7",
      "#c5cae9", "#9fa8da", "#7986cb", "#5c6bc0", "#3f51b5",
      "#bbdefb", "#90caf9", "#64b5f6", "#42a5f5", "#2196f3",
      "#b3e5fc", "#81d4fa", "#4fc3f7", "#29b6f6", "#03a9f4",
      "#b2ebf2", "#80deea", "#4dd0e1", "#26c6da", "#00bcd4",
      "#b2dfdb", "#80cbc4", "#4db6ac", "#26a69a", "#009688",
      "#c8e6c9", "#a5d6a7", "#81c784", "#66bb6a", "#4caf50",
      "#dcedc8", "#c5e1a5", "#aed581", "#9ccc65", "#8bc34a",
      "#f0f4c3", "#e6ee9c", "#dce775", "#d4e157", "#cddc39",
      "#fff9c4", "#fff59d", "#fff176", "#ffee58", "#ffeb3b",
      "#ffecb3", "#ffe082", "#ffd54f", "#ffca28", "#ffc107",
      "#ffe0b2", "#ffcc80", "#ffb74d", "#ffa726", "#ff9800",
      "#ffccbc", "#ffab91", "#ff8a65", "#ff7043", "#ff5722",
      "#d7ccc8", "#bcaaa4", "#a1887f", "#8d6e63", "#795548",
      "#f5f5f5", "#eeeeee", "#e0e0e0", "#bdbdbd", "#9e9e9e",
      "#cfd8dc", "#b0bec5", "#90a4ae", "#78909c", "#607d8b",
      "#ffffff"
    };

    public ColorComboBox()
    {
      InitializeComponent();
      LoadColors();
    }

    public ColorComboBox(Theme theme)
    {
      InitializeComponent();
      LoadColors(theme);
    }

    private void LoadColors(Theme theme = Theme.Light)
    {
      List<ColorItem> list = new List<ColorItem>();
      var choice = (theme == Theme.Dark) ? DarkColors : LightColors;
      list.AddRange(choice.Select(hex => new ColorItem { Brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)), Name = hex }));
      ItemsSource = list;
    }
  }
}
