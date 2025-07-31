﻿using Syncfusion.Windows.PropertyGrid;
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
    private Button _theButton;
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
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(65, GridUnitType.Auto) });

      _theTextBox = new TextBox
      {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(0, 2, 0, 2),
        TextWrapping = TextWrapping.Wrap,
        VerticalContentAlignment = VerticalAlignment.Center,
        BorderThickness = new Thickness(0, 0, 0, 0),
        Text = "Click to Select Custom Color",
        IsReadOnly = true,
        FontStyle = FontStyles.Italic,
        Cursor = Cursors.Hand
      };

      _theTextBox.SetValue(Grid.ColumnProperty, 0);
      _theTextBox.PreviewMouseLeftButtonDown += TheTextBox_PreviewMouseLeftButtonDown;

      _theButton = new Button
      {
        Content = "Reset",
        Padding = new Thickness(8, 2, 8, 2),
        Margin = new Thickness(2, 1, 2, 1)
      };
      _theButton.SetValue(Grid.ColumnProperty, 1);
      _theButton.Click += TheButton_Click;

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
      grid.Children.Add(_theButton);
      return grid;
    }

    private void TheColorPickerBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      if (e.NewValue != null)
      {
        ShowColorPicker();
      }
      else
      {
        HideColorPicker();
      }
    }

    private void TheButton_Click(object sender, RoutedEventArgs e) => HideColorPicker();
    private void TheTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => ShowColorPicker();

    private void HideColorPicker()
    {
      if (_theTextBox != null && _theColorPicker != null && _theTextBox.Visibility != Visibility.Visible)
      {
        _theTextBox.Visibility = Visibility.Visible;
        _theColorPicker.Visibility = Visibility.Collapsed;
        _theColorPicker.Brush = null;
      }
    }

    private void ShowColorPicker()
    {
      if (_theTextBox != null && _theColorPicker != null && _theTextBox.Visibility != Visibility.Collapsed)
      {
        _theTextBox.Visibility = Visibility.Collapsed;
        _theColorPicker.Visibility = Visibility.Visible;
        _theColorPicker.Brush ??= UiUtil.GetBrush(Colors.White);
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
        _theTextBox.PreviewMouseLeftButtonDown -= TheTextBox_PreviewMouseLeftButtonDown;
        BindingOperations.ClearAllBindings(_theTextBox);
        _theTextBox = null;
      }

      if (_theButton != null)
      {
        BindingOperations.ClearAllBindings(_theButton);
        _theButton = null;
      }

      if (_theColorPicker != null)
      {
        BindingOperations.ClearAllBindings(_theColorPicker);
        _theColorPicker.SelectedBrushChanged -= TheColorPickerBrushChanged;
        _theColorPicker?.Dispose();
        _theColorPicker = null;
      }

      ;
    }
  }
}
