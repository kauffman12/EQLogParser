using Syncfusion.Windows.PropertyGrid;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  internal class PatternEditor : BaseTypeEditor
  {
    private TextBox TheTextBox;
    private CheckBox TheCheckBox;

    public void SetForeground(string foreground)
    {
      // this only works if there's one reference to this editor...
      // TODO figure out better way
      if (TheTextBox != null)
      {
        TheTextBox.SetResourceReference(Control.ForegroundProperty, foreground);
      }
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

      BindingOperations.SetBinding(TheTextBox, TextBox.TextProperty, binding);

      string bindName = null;
      switch (info.Name)
      {
        case "Pattern":
          bindName = "UseRegex";
          break;
        case "EndEarlyPattern":
          bindName = "EndUseRegex";
          break;
        case "EndEarlyPattern2":
          bindName = "EndUseRegex2";
          break;
      }

      if (bindName != null)
      {
        binding = new Binding(bindName)
        {
          Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
          Source = info.SelectedObject,
          ValidatesOnExceptions = true,
          ValidatesOnDataErrors = true
        };

        BindingOperations.SetBinding(TheCheckBox, CheckBox.IsCheckedProperty, binding);
      }
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
        Tag = true
      };

      TheTextBox.SetValue(Grid.ColumnProperty, 0);
      TheCheckBox = new CheckBox { Content = "Use Regex" };
      TheCheckBox.SetValue(Grid.ColumnProperty, 1);
      TheCheckBox.Checked += TheCheckBoxChecked;
      TheCheckBox.Unchecked += TheCheckBoxChecked;
      grid.Children.Add(TheTextBox);
      grid.Children.Add(TheCheckBox);
      return grid;
    }

    private void TheCheckBoxChecked(object sender, RoutedEventArgs e)
    {
      if (TheTextBox != null)
      {
        // used for init check
        if (TheTextBox.Tag != null)
        {
          TheTextBox.Tag = null;
        }
        else
        {
          var previous = TheTextBox.Text;
          TheTextBox.Text = TheTextBox.Text + " ";
          TheTextBox.Text = previous;
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
    }
  }
}
