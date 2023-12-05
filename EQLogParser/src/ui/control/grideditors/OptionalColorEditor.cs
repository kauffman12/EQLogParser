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
    private TextBox _theTextBox;
    private CheckBox _theCheckBox;
    private ColorPicker _theColorPicker;

    public override void Attach(PropertyViewItem property, PropertyItem info)
    {
      var binding = new Binding("Value")
      {
        Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
        Source = info,
        ValidatesOnExceptions = true,
        ValidatesOnDataErrors = true
      };

      BindingOperations.SetBinding(_theColorPicker, ColorPicker.BrushProperty, binding);
    }

    public override object Create(PropertyInfo propertyInfo) => Create();
    public override object Create(PropertyDescriptor descriotor) => Create();

    private object Create()
    {
      var grid = new Grid();
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200, GridUnitType.Star) });
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

      _theTextBox = new TextBox
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

      _theTextBox.SetValue(Grid.ColumnProperty, 0);

      _theCheckBox = new CheckBox { Content = "Use Custom" };
      _theCheckBox.SetValue(Grid.ColumnProperty, 1);
      _theCheckBox.Checked += TheCheckBoxChecked;
      _theCheckBox.Unchecked += TheCheckBoxChecked;

      _theColorPicker = new ColorPicker
      {
        EnableSolidToGradientSwitch = false,
        BorderThickness = new Thickness(0, 0, 0, 0),
        Visibility = Visibility.Collapsed
      };

      _theColorPicker.SetValue(Grid.ColumnProperty, 0);
      _theColorPicker.SelectedBrushChanged += TheColorPickerBrushChanged;

      grid.Children.Add(_theColorPicker);
      grid.Children.Add(_theTextBox);
      grid.Children.Add(_theCheckBox);
      return grid;
    }

    private void TheColorPickerBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      if (_theCheckBox != null)
      {
        _theCheckBox.IsChecked = e.NewValue != null;
      }
    }

    private void TheCheckBoxChecked(object sender, RoutedEventArgs e)
    {
      if (sender is CheckBox checkBox)
      {
        _theTextBox.Visibility = checkBox.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        _theColorPicker.Visibility = checkBox.IsChecked == true ? Visibility.Visible : Visibility.Hidden;
        if (checkBox.IsChecked == false)
        {
          _theColorPicker.Brush = null;
        }
        else
        {
          _theColorPicker.Brush ??= new SolidColorBrush { Color = Colors.White };
        }
      }
    }

    public override bool ShouldPropertyGridTryToHandleKeyDown(Key key)
    {
      return false;
    }

    public override void Detach(PropertyViewItem property)
    {
      if (_theTextBox != null)
      {
        BindingOperations.ClearAllBindings(_theTextBox);
        _theTextBox = null;
      }

      if (_theCheckBox != null)
      {
        _theCheckBox.Checked -= TheCheckBoxChecked;
        _theCheckBox.Unchecked -= TheCheckBoxChecked;
        BindingOperations.ClearAllBindings(_theCheckBox);
        _theCheckBox = null;
      }

      if (_theColorPicker != null)
      {
        _theColorPicker.SelectedBrushChanged -= TheColorPickerBrushChanged;
        BindingOperations.ClearAllBindings(_theColorPicker);
        _theColorPicker?.Dispose();
        _theColorPicker = null;
      }
    }
  }
}
