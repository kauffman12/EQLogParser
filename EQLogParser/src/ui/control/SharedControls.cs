using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;

namespace EQLogParser
{
  internal class SharedControls
  {
    internal static void PopulateClassesList(ComboBox classesList, List<string> selected)
    {
      // classes combo
      var playerClasses = PlayerManager.Instance.GetClassList();
      selected.AddRange(playerClasses);
      var comboList = playerClasses.Select(name => new ComboBoxItemDetails { IsChecked = true, Text = name }).ToList();
      comboList.Insert(0, new ComboBoxItemDetails { IsChecked = false, Text = "Unselect All" });
      comboList.Insert(0, new ComboBoxItemDetails { IsChecked = true, Text = "Select All" });
      classesList.ItemsSource = comboList;
      UiElementUtil.SetComboBoxTitle(classesList, Resource.CLASSES_SELECTED, true);
    }

    internal static bool ClassesListSelectedChanged(ComboBox classesList, List<string> selected)
    {
      if (classesList?.Items?.Count > 0)
      {
        selected.Clear();
        for (var i = 2; i < classesList.Items.Count; i++)
        {
          if (classesList.Items[i] is ComboBoxItemDetails { } classItem && classItem.IsChecked == true)
          {
            selected.Add(classItem.Text);
          }
        }

        UiElementUtil.SetComboBoxTitle(classesList, Resource.CLASSES_SELECTED, true);
        return true;
      }
      return false;
    }

    internal static void ClassPreviewMouseDown(ComboBox classesList, object sender)
    {
      if (sender is ComboBoxItem { Content: ComboBoxItemDetails details })
      {
        UiElementUtil.PreviewSelectAllComboBox(classesList, details, PlayerManager.Instance.GetClassList().Count);
      }
    }
  }
}
