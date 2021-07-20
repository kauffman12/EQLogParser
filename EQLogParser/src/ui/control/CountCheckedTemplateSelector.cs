using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  public class CountCheckedTemplateSelector : DataTemplateSelector
  {
    public string Header { get; set; }

    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
      DataTemplate template = null;

      if (item is HitLogRow row)
      {
        uint value = 0;
        DataTemplate countTemplate = null;

        switch (Header)
        {
          case "Hits":
            value = row.Count;
            countTemplate = Application.Current.Resources["CountTemplate"] as DataTemplate;
            break;
          case "Critical":
            value = row.CritCount;
            countTemplate = Application.Current.Resources["CritCountTemplate"] as DataTemplate;
            break;
          case "Lucky":
            value = row.LuckyCount;
            countTemplate = Application.Current.Resources["LuckyCountTemplate"] as DataTemplate;
            break;
          case "Twincast":
            value = row.TwincastCount;
            countTemplate = Application.Current.Resources["TwincastCountTemplate"] as DataTemplate;
            break;
          case "Rampage":
            value = row.RampageCount;
            countTemplate = Application.Current.Resources["RampageCountTemplate"] as DataTemplate;
            break;
          case "Riposte":
            value = row.RiposteCount;
            countTemplate = Application.Current.Resources["RiposteCountTemplate"] as DataTemplate;
            break;
          case "Strikethrough":
            value = row.StrikethroughCount;
            countTemplate = Application.Current.Resources["StrikethroughCountTemplate"] as DataTemplate;
            break;
        }

        if (value == 0)
        {
          template = Application.Current.Resources["NoDataTemplate"] as DataTemplate;
        }
        else if (value == 1 && !row.IsGroupingEnabled)
        {
          template = Application.Current.Resources["CheckTemplate"] as DataTemplate;
        }
        else
        {
          template = countTemplate;
        }
      }

      return template;
    }
  }
}
