using Syncfusion.Windows.PropertyGrid;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace EQLogParser
{
  internal class CheckComboBoxEditor : ITypeEditor
  {
    private readonly List<ComboBox> TheComboBoxes = new List<ComboBox>();

    public void Attach(PropertyViewItem property, PropertyItem info)
    {
      Binding binding = new Binding("Value")
      {
        Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
        Source = info,
        ValidatesOnExceptions = true,
        ValidatesOnDataErrors = true
      };

      BindingOperations.SetBinding(TheComboBoxes.Last(), ComboBox.ItemsSourceProperty, binding);
    }

    public object Create(PropertyInfo propertyInfo) => Create();
    public object Create(PropertyDescriptor _) => Create();

    private object Create()
    {
      var comboBox = new ComboBox
      {
        ItemTemplateSelector = new ComboBoxItemTemplateSelector()
      };

      comboBox.DropDownClosed += TheComboBoxDropDownClosed;
      comboBox.DataContextChanged += TheComboBoxDataContextChanged;

      TheComboBoxes.Add(comboBox);
      return comboBox;
    }

    private void TheComboBoxDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e) => UpdateTitle(sender);
    private void TheComboBoxDropDownClosed(object sender, System.EventArgs e) => UpdateTitle(sender, true);

    private void UpdateTitle(object sender, bool save = false)
    {
      var comboBox = sender as ComboBox;
      if (comboBox?.ItemsSource is ObservableCollection<ComboBoxItemDetails> details)
      {
        var count = details.Where(item => item.IsChecked).Count();
        if (count == 1 && details.First(item => item.IsChecked) is ComboBoxItemDetails single)
        {
          single.SelectedText = single.Text;
          comboBox.SelectedIndex = -1;
          comboBox.SelectedItem = single;
        }
        else
        {
          if (comboBox.DataContext is PropertyItem propertyItem)
          {
            var label = propertyItem.Name == "SelectedTextOverlays" ? "Text Overlays" : "Timer Overlays";
            UIElementUtil.SetComboBoxTitle(comboBox, count, label);
          }
        }

        if (save && comboBox.Items.Count > 0 && comboBox.DataContext is PropertyItem item && item.PropertyGrid.Parent is FrameworkElement elem)
        {
          while (elem != null)
          {
            if (elem.Parent is TriggersView view)
            {
              view.saveButton.IsEnabled = true;
              view.cancelButton.IsEnabled = true;
              break;
            }

            elem = elem.Parent as FrameworkElement;
          }
        }
      }
    }

    public void Detach(PropertyViewItem property)
    {
      TheComboBoxes.ForEach(comboBox =>
      {
        comboBox.DropDownClosed -= TheComboBoxDropDownClosed;
        comboBox.DataContextChanged -= TheComboBoxDataContextChanged;
        BindingOperations.ClearAllBindings(comboBox);
      });

      TheComboBoxes.Clear();
    }
  }
}
