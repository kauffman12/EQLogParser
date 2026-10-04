using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EQLogParser
{
  /*
   * True shows the element, false HIDES it — Hidden rather than Collapsed, which is the entire reason this converter
   * exists next to BooleanToVisibilityConverter instead of being replaced by it. Hidden keeps the element's measured
   * box in the layout, so a cell that has nothing to offer still reserves the icon's width and every word in that
   * column starts at the same pixel; Collapsed hands the space back, and the rows without an icon slide left of the
   * rows with one.
   *
   * The Names and Identities pane is where this shows: its Type cell carries a pencil on most rows but deliberately
   * none on a spell effect or a summon whose own spelling fixes its Type (IdentityVocabulary.CanOverrule), and its
   * Class cell carries one only once a row reads Player. With Collapsed those exceptions punched a ragged left edge
   * through the middle of both columns — a difference in what a row ALLOWS, rendered as a difference in where its
   * text begins.
   */
  public class PlaceholderVisibilityConverter : IValueConverter
  {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
      => value is bool flag && flag ? Visibility.Visible : Visibility.Hidden;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
      => value is Visibility visibility && visibility == Visibility.Visible;
  }
}
