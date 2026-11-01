using FontAwesome5;
using Syncfusion.Windows.PropertyGrid;
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
  internal sealed class WebhookEditor(WebhookDispatchQueue queue, Func<(string Character, string Trigger)> selection,
    Action onSiblingChanged) : BaseTypeEditor
  {
    private Grid _grid;
    private TextBox _text;
    private ImageAwesome _error;
    private ComboBox _aggregation;
    private bool _attaching;
    private bool _active = true;

    public override object Create(PropertyInfo _) => Create();
    public override object Create(PropertyDescriptor _) => Create();
    public override bool ShouldPropertyGridTryToHandleKeyDown(Key _) => false;

    public override void Attach(PropertyViewItem property, PropertyItem info)
    {
      _attaching = true;
      try
      {
        BindingOperations.SetBinding(_text, TextBox.TextProperty, new Binding("Value")
        {
          Source = info,
          Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
          ValidatesOnExceptions = true,
          ValidatesOnDataErrors = true,
          UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        BindingOperations.SetBinding(_aggregation, Selector.SelectedValueProperty,
          SiblingBinding(nameof(Trigger.AggregateWebhooks), info));
        _text.IsReadOnly = !info.CanWrite;
        _aggregation.IsEnabled = info.CanWrite && info.SelectedObject is Trigger;
      }
      finally
      {
        _attaching = false;
      }

      SetActive(_active);
    }

    public override void Detach(PropertyViewItem property)
    {
      queue.EventsStatusChanged -= Refresh;
      if (_text is not null)
      {
        _text.TextChanged -= TextChanged;
        BindingOperations.ClearAllBindings(_text);
      }

      if (_aggregation is not null)
      {
        _aggregation.SelectionChanged -= AggregationChanged;
        BindingOperations.ClearAllBindings(_aggregation);
      }

      _grid = null;
      _text = null;
      _error = null;
      _aggregation = null;
    }

    internal void SetActive(bool active)
    {
      _active = active;
      queue.EventsStatusChanged -= Refresh;
      if (active && _text is not null)
      {
        queue.EventsStatusChanged += Refresh;
        Refresh();
      }
    }

    internal void FocusWebhook()
    {
      _text?.BringIntoView();
      _text?.Focus();
    }

    private static Binding SiblingBinding(string name, PropertyItem info) => new(name)
    {
      Source = info.SelectedObject,
      Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
      UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
      ValidatesOnExceptions = true,
      ValidatesOnDataErrors = true
    };

    private object Create()
    {
      if (_grid is not null) return _grid;
      _grid = new Grid();
      _grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
      _grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
      _grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
      _text = new TextBox
      {
        TextWrapping = TextWrapping.Wrap,
        Padding = new Thickness(0, 2, 0, 2),
        VerticalContentAlignment = VerticalAlignment.Center,
        BorderThickness = new Thickness(0)
      };
      _text.TextChanged += TextChanged;
      _error = new ImageAwesome
      {
        Icon = EFontAwesomeIcon.Solid_ExclamationTriangle,
        Margin = new Thickness(6, 0, 4, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed
      };
      _error.SetResourceReference(ImageAwesome.ForegroundProperty, "EQStopForegroundBrush");
      _error.SetResourceReference(FrameworkElement.HeightProperty, "EQContentSize");
      _error.SetResourceReference(FrameworkElement.WidthProperty, "EQContentSize");
      _aggregation = new ComboBox
      {
        ItemsSource = WebhookAggregation.Options,
        DisplayMemberPath = nameof(WebhookAggregationOption.Text),
        SelectedValuePath = nameof(WebhookAggregationOption.Value),
        SelectedValue = 0,
        BorderThickness = new Thickness(0),
        Margin = new Thickness(6, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "Combine matches from this trigger for the selected time after the first match; Aggregation Off sends each match separately"
      };
      _aggregation.SelectionChanged += AggregationChanged;
      // Reserve every label's width plus arrow space, even while a shorter option is selected.
      foreach (var option in WebhookAggregation.Options)
      {
        var sizingLabel = new TextBlock
        {
          Text = option.Text,
          Visibility = Visibility.Hidden,
          Margin = new Thickness(6, 0, 30, 0)
        };
        foreach (var property in new[] { TextBlock.FontFamilyProperty, TextBlock.FontSizeProperty,
          TextBlock.FontStyleProperty, TextBlock.FontWeightProperty })
        {
          BindingOperations.SetBinding(sizingLabel, property, new Binding(property.Name) { Source = _aggregation });
        }

        Grid.SetColumn(sizingLabel, 2);
        _grid.Children.Add(sizingLabel);
      }

      Grid.SetColumn(_error, 1);
      Grid.SetColumn(_aggregation, 2);
      _grid.Children.Add(_text);
      _grid.Children.Add(_error);
      _grid.Children.Add(_aggregation);
      return _grid;
    }

    private void AggregationChanged(object sender, SelectionChangedEventArgs args)
    {
      if (_attaching) return;
      _aggregation.GetBindingExpression(Selector.SelectedValueProperty)?.UpdateSource();
      onSiblingChanged?.Invoke();
    }

    private void TextChanged(object sender, TextChangedEventArgs args) => Refresh();

    private void Refresh()
    {
      _text?.Dispatcher.InvokeAsync(() =>
      {
        if (_text is null || _error is null) return;
        var (character, trigger) = selection();
        var message = queue.ErrorFor(_text.Text, character, trigger);
        _error.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        _error.ToolTip = message;
        _text.ToolTip = message;
        _text.SetResourceReference(Control.ForegroundProperty, message == null ? "ContentForeground" : "EQStopForegroundBrush");
      });
    }
  }
}