using Syncfusion.Windows.PropertyGrid;
using Syncfusion.Windows.Shared;
using System;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  /*
   * "Repeated Reset Time (s)" with its anchor policy sitting beside it, in the same shape PatternEditor uses for a pattern and its
   * "Use Regex" box: one grid, a stretching numeric column and a fixed 110 px checkbox column, so the boxes line up with the regex checkbox
   * on the rows above. The checkbox binds to the SIBLING property (Trigger.RepeatedResetSlides) on info.SelectedObject rather than to this
   * PropertyItem's Value, exactly as UseRegex/PreviousUseRegex/UseCloseRegex do.
   *
   * What the choice means is RepeatedWindowRule's (Core, tested): unchecked pins the window to the first match of the epoch, so counting
   * restarts every N seconds however often it fires; checked lets every fire push the deadline out, which is how GINA counted and what makes
   * the same number an idle timeout. The grid only moves the two values — it decides nothing, so nothing here can drift from the rule.
   */
  internal class RepeatedResetEditor : BaseTypeEditor
  {
    private DoubleTextBox _theDoubleTextBox;
    private CheckBox _theCheckBox;
    private Grid _grid;
    private readonly Action _onSiblingChanged;

    public RepeatedResetEditor(Action onSiblingChanged)
    {
      _onSiblingChanged = onSiblingChanged;
    }

    public void SetForeground(string foreground)
    {
      // Same single-reference limitation as PatternEditor: this editor is registered once for one property.
      _theDoubleTextBox?.SetResourceReference(EditorBase.PositiveForegroundProperty, foreground);
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

      BindingOperations.SetBinding(_theDoubleTextBox, DoubleTextBox.ValueProperty, binding);

      binding = new Binding(nameof(Trigger.RepeatedResetSlides))
      {
        Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
        Source = info.SelectedObject,
        ValidatesOnExceptions = true,
        ValidatesOnDataErrors = true,
      };

      BindingOperations.SetBinding(_theCheckBox, ToggleButton.IsCheckedProperty, binding);
    }

    public override object Create(PropertyInfo _) => Create();
    public override object Create(PropertyDescriptor _) => Create();

    private object Create()
    {
      if (_grid != null)
        return _grid;

      _grid = new Grid();
      _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200, GridUnitType.Star) });
      _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

      // The same numeric box RangeEditor builds for this property (0 to 99999, spin button on, no border), so a window typed in by hand is
      // bounded and formatted exactly as it was before the checkbox arrived beside it.
      _theDoubleTextBox = new DoubleTextBox
      {
        ApplyZeroColor = false,
        ShowSpinButton = true,
        ScrollInterval = 0.1,
        BorderThickness = new Thickness(0),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        MinValue = 0,
        MaxValue = 99999,
      };

      _theDoubleTextBox.SetResourceReference(EditorBase.PositiveForegroundProperty, "ContentForeground");
      _theDoubleTextBox.SetValue(Grid.ColumnProperty, 0);

      _theCheckBox = new CheckBox
      {
        Content = "Sliding Reset",
        Template = (ControlTemplate)Application.Current.Resources["CustomCheckBoxTemplate"]
      };

      _theCheckBox.SetValue(Grid.ColumnProperty, 1);
      _theCheckBox.Checked += TheCheckBoxToggled;
      _theCheckBox.Unchecked += TheCheckBoxToggled;

      _grid.Children.Add(_theDoubleTextBox);
      _grid.Children.Add(_theCheckBox);
      return _grid;
    }

    /*
     * Telling the pane there is unsaved work. The checkbox's binding writes RepeatedResetSlides - a sibling of the property this row tracks -
     * so the grid raises no ValueChanged and Save would never enable; see TriggersView.OnEditorSiblingChanged for why this does not nudge the
     * number the way PatternEditor nudges its text.
     *
     * The CustomCheckBoxTemplate contains its own ToggleButton-ish visual with no Content, which surfaces as OriginalSource on a real click;
     * PatternEditor ignores those, and so does this, or one click counts twice.
     */
    private void TheCheckBoxToggled(object sender, RoutedEventArgs e)
    {
      if (e.OriginalSource is CheckBox box && box.Content != null)
      {
        _onSiblingChanged?.Invoke();
      }
    }

    public override bool ShouldPropertyGridTryToHandleKeyDown(Key key)
    {
      return false;
    }

    public override void Detach(PropertyViewItem property)
    {
      if (_theDoubleTextBox != null)
      {
        BindingOperations.ClearAllBindings(_theDoubleTextBox);
        _theDoubleTextBox.Dispose();
        _theDoubleTextBox = null;
      }

      if (_theCheckBox != null)
      {
        _theCheckBox.Checked -= TheCheckBoxToggled;
        _theCheckBox.Unchecked -= TheCheckBoxToggled;
        BindingOperations.ClearAllBindings(_theCheckBox);
        _theCheckBox = null;
      }

      if (_grid != null)
      {
        _grid.Children.Clear();
        _grid = null;
      }
    }
  }
}
