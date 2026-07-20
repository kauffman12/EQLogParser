using Syncfusion.Windows.PropertyGrid;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace EQLogParser
{
  /// <summary>
  /// A lightweight property grid editor for condition expressions.
  /// Validates the expression against ConditionParser and turns the text red on parse errors.
  /// </summary>
  internal class ConditionEditor : BaseTypeEditor
  {
    private TextBox _textBox;

    public void SetForeground(string foregroundResourceKey)
    {
      _textBox?.SetResourceReference(Control.ForegroundProperty, foregroundResourceKey);
    }

    public override object Create(PropertyInfo _) => Create();
    public override object Create(PropertyDescriptor _) => Create();

    private object Create()
    {
      _textBox = new TextBox
      {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(0, 2, 0, 2),
        TextWrapping = TextWrapping.Wrap,
        VerticalContentAlignment = VerticalAlignment.Center,
        BorderThickness = new Thickness(0)
      };

      _textBox.TextChanged += OnTextChanged;
      return _textBox;
    }

    public override void Attach(PropertyViewItem property, PropertyItem info)
    {
      var binding = new Binding("Value")
      {
        Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
        Source = info,
        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
      };

      BindingOperations.SetBinding(_textBox, TextBox.TextProperty, binding);
    }

    public override void Detach(PropertyViewItem property)
    {
      if (_textBox != null)
      {
        _textBox.TextChanged -= OnTextChanged;
        BindingOperations.ClearAllBindings(_textBox);
        _textBox = null;
      }
    }

    public override bool ShouldPropertyGridTryToHandleKeyDown(Key key)
    {
      return false;
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
      ValidateAndColor(_textBox.Text);
    }

    /// <summary>
    /// Try to parse the expression and set foreground color accordingly.
    /// Empty/whitespace is considered valid (no condition = always passes).
    /// </summary>
    private void ValidateAndColor(string text)
    {
      var isValid = string.IsNullOrWhiteSpace(text) || ConditionParser.Parse(text) != null;
      SetForeground(isValid ? "ContentForeground" : "EQStopForegroundBrush");
    }
  }
}
