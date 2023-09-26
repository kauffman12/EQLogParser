using Syncfusion.Windows.PropertyGrid;
using Syncfusion.Windows.Shared;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace EQLogParser
{
  internal class OptionalColorEditor : BaseTypeEditor
  {
    private TextBox TheTextBox;
    private CheckBox TheCheckBox;
    private ColorPicker TheColorPicker;

    public override void Attach(PropertyViewItem property, PropertyItem info)
    {
      var binding = new Binding("Value")
      {
        Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
        Source = info,
        ValidatesOnExceptions = true,
        ValidatesOnDataErrors = true
      };

      BindingOperations.SetBinding(TheColorPicker, ColorPicker.BrushProperty, binding);
    }

    public override object Create(PropertyInfo propertyInfo) => Create();
    public override object Create(PropertyDescriptor descriotor) => Create();

    private object Create()
    {
      var grid = new Grid();
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200, GridUnitType.Star) });
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });

      TheTextBox = new TextBox
      {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(0, 2, 0, 2),
        TextWrapping = TextWrapping.Wrap,
        VerticalContentAlignment = VerticalAlignment.Center,
        BorderThickness = new Thickness(0, 0, 0, 0),
        Text = "Use Color Configured by the Overlay",
        IsReadOnly = true,
        FontStyle = FontStyles.Italic
      };

      TheTextBox.SetValue(Grid.ColumnProperty, 0);

      TheCheckBox = new CheckBox { Content = "Use Custom" };
      TheCheckBox.SetValue(Grid.ColumnProperty, 1);
      TheCheckBox.Checked += TheCheckBoxChecked;
      TheCheckBox.Unchecked += TheCheckBoxChecked;

      TheColorPicker = new ColorPicker
      {
        EnableSolidToGradientSwitch = false,
        BorderThickness = new System.Windows.Thickness(0, 0, 0, 0),
        Visibility = Visibility.Collapsed
      };

      TheColorPicker.SetValue(Grid.ColumnProperty, 0);
      TheColorPicker.SelectedBrushChanged += TheColorPickerBrushChanged;

      grid.Children.Add(TheColorPicker);
      grid.Children.Add(TheTextBox);
      grid.Children.Add(TheCheckBox);
      return grid;
    }

    private void TheColorPickerBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      if (TheCheckBox != null)
      {
        TheCheckBox.IsChecked = e.NewValue != null;
      }
    }

    private void TheCheckBoxChecked(object sender, RoutedEventArgs e)
    {
      if (sender is CheckBox checkBox)
      {
        TheTextBox.Visibility = checkBox.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        TheColorPicker.Visibility = checkBox.IsChecked == true ? Visibility.Visible : Visibility.Hidden;
        if (checkBox.IsChecked == false)
        {
          TheColorPicker.Brush = null;
        }
        else
        {
          if (TheColorPicker.Brush == null)
          {
            TheColorPicker.Brush = new SolidColorBrush { Color = Colors.White };
          }
        }
      }
    }

    public override bool ShouldPropertyGridTryToHandleKeyDown(Key key)
    {
      return false;
    }

    public override void Detach(PropertyViewItem property)
    {
      if (TheTextBox != null)
      {
        BindingOperations.ClearAllBindings(TheTextBox);
        TheTextBox = null;
      }

      if (TheCheckBox != null)
      {
        TheCheckBox.Checked -= TheCheckBoxChecked;
        TheCheckBox.Unchecked -= TheCheckBoxChecked;
        BindingOperations.ClearAllBindings(TheCheckBox);
        TheCheckBox = null;
      }

      if (TheColorPicker != null)
      {
        TheColorPicker.SelectedBrushChanged -= TheColorPickerBrushChanged;
        BindingOperations.ClearAllBindings(TheColorPicker);
        TheColorPicker?.Dispose();
        TheColorPicker = null;
      }
    }
  }
}
