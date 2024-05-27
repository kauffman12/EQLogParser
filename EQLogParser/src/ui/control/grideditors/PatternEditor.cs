using Syncfusion.Windows.PropertyGrid;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  internal class PatternEditor : BaseTypeEditor
  {
    private TextBox _theTextBox;
    private CheckBox _theCheckBox;
    private bool _userTurnedOff;

    public void SetForeground(string foreground)
    {
      // this only works if there's one reference to this editor...
      // TODO figure out better way
      _theTextBox?.SetResourceReference(Control.ForegroundProperty, foreground);
    }

    public override void Attach(PropertyViewItem property, PropertyItem info)
    {
      var binding = new Binding("Value")
      {
        Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
        Source = info,
        ValidatesOnExceptions = true,
        ValidatesOnDataErrors = true,
        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
      };

      BindingOperations.SetBinding(_theTextBox, TextBox.TextProperty, binding);

      var bindName = info.Name switch
      {
        "Pattern" => "UseRegex",
        "EndEarlyPattern" => "EndUseRegex",
        "EndEarlyPattern2" => "EndUseRegex2",
        _ => null
      };

      if (bindName != null)
      {
        binding = new Binding(bindName)
        {
          Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
          Source = info.SelectedObject,
          ValidatesOnExceptions = true,
          ValidatesOnDataErrors = true,
        };

        BindingOperations.SetBinding(_theCheckBox, ToggleButton.IsCheckedProperty, binding);
      }
    }

    public override object Create(PropertyInfo propertyInfo) => Create();
    public override object Create(PropertyDescriptor descriotor) => Create();

    private object Create()
    {
      _userTurnedOff = false;
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
        BorderThickness = new Thickness(0, 0, 0, 0)
      };

      _theTextBox.SetValue(Grid.ColumnProperty, 0);
      _theTextBox.TextChanged += TheTextChanged;

      _theCheckBox = new CheckBox { Content = "Use Regex" };
      _theCheckBox.SetValue(Grid.ColumnProperty, 1);
      _theCheckBox.Checked += TheCheckBoxChecked;
      _theCheckBox.Unchecked += TheCheckBoxChecked;
      grid.Children.Add(_theTextBox);
      grid.Children.Add(_theCheckBox);
      return grid;
    }

    private void TheTextChanged(object sender, TextChangedEventArgs e)
    {
      if (!_userTurnedOff && TriggerUtil.IsProbRegex(_theTextBox?.Text) && _theCheckBox.IsChecked == false)
      {
        _theCheckBox.IsChecked = true;
      }
    }

    private void TheCheckBoxChecked(object sender, RoutedEventArgs e)
    {
      // used for init check
      if (sender is CheckBox { DataContext: not null })
      {
        // turn off listeners
        _theTextBox.TextChanged -= TheTextChanged;
        // toggle text to trigger the ValueChanged event
        var previous = _theTextBox.Text;
        _theTextBox.Text += " ";
        _theTextBox.Text = previous;
        _theTextBox.SelectionStart = _theTextBox.Text.Length;
        // put listener back on
        _theTextBox.TextChanged += TheTextChanged;

        // if the toggle is changed to false it must have been a user decision
        if (_theCheckBox.IsChecked == false)
        {
          _userTurnedOff = true;
        }
      }
      else
      {
        // reset just in case
        _userTurnedOff = false;
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
        _theTextBox.TextChanged -= TheTextChanged;
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
    }
  }
}
