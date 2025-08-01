﻿using Microsoft.WindowsAPICodePack.Dialogs;
using Syncfusion.Windows.PropertyGrid;
using System;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;

namespace EQLogParser
{
  internal class OptionalIconEditor : BaseTypeEditor
  {
    private TextBox _theTextBox;
    private Button _theButton;
    private Image _theImage;
    private DependencyPropertyDescriptor _theImageDpd;

    public override void Attach(PropertyViewItem property, PropertyItem info)
    {
      var binding = new Binding("Value")
      {
        Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
        Source = info,
        ValidatesOnExceptions = true,
        ValidatesOnDataErrors = true
      };

      BindingOperations.SetBinding(_theImage, Image.SourceProperty, binding);
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
        Text = "Click to Select Timer Bar Icon",
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

      _theImage = new Image
      {
        Visibility = Visibility.Collapsed,
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(4, 2, 0, 2)
      };

      _theImage.SetValue(Grid.ColumnProperty, 0);
      _theImage.PreviewMouseLeftButtonDown += TheImage_PreviewMouseLeftButtonDown;
      _theImageDpd = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
      _theImageDpd?.AddValueChanged(_theImage, TheImage_SourceChanged);

      grid.Children.Add(_theImage);
      grid.Children.Add(_theTextBox);
      grid.Children.Add(_theButton);
      return grid;
    }

    private void TheImage_SourceChanged(object sender, EventArgs e)
    {
      if (_theImage?.Source != null)
      {
        ShowImage();
      }
      else
      {
        HideImage();
      }
    }

    private void TheImage_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
      SelectImage();
    }

    private void SelectImage()
    {
      var dialog = new CommonOpenFileDialog
      {
        // Set to false because we're opening a file, not selecting a folder
        IsFolderPicker = false,
        // Set the initial directory
        InitialDirectory = "",
      };

      // Show dialog and read result
      dialog.Filters.Add(new CommonFileDialogFilter("Images", "*.png;*.jpg;*.jpeg"));

      var handle = new WindowInteropHelper(MainActions.GetOwner()).Handle;
      if (dialog.ShowDialog(handle) == CommonFileDialogResult.Ok)
      {
        var file = dialog.FileName; // Get the selected file name
        _theImage.Source = UiElementUtil.CreateBitmap(file);
      }
    }

    private void TheButton_Click(object sender, RoutedEventArgs e) => HideImage();
    private void TheTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
      ShowImage();
      SelectImage();
    }

    private void HideImage()
    {
      if (_theImage != null && _theTextBox != null && _theTextBox.Visibility != Visibility.Visible)
      {
        _theTextBox.Visibility = Visibility.Visible;
        _theImage.Visibility = Visibility.Collapsed;
        _theImage.Source = null;
      }
    }

    private void ShowImage()
    {
      if (_theImage != null && _theTextBox != null)
      {
        var height = UiElementUtil.CalculateTextBoxHeight(_theTextBox);
        _theImage.Height = height + 2;
        _theImage.Width = double.NaN;
        _theTextBox.Visibility = Visibility.Collapsed;
        _theImage.Visibility = Visibility.Visible;
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

      if (_theImage != null)
      {
        BindingOperations.ClearAllBindings(_theImage);
        _theImageDpd.RemoveValueChanged(_theImage, TheImage_SourceChanged);
        _theImage.PreviewMouseLeftButtonDown -= TheImage_PreviewMouseLeftButtonDown;
        _theImage = null;
      }
    }
  }
}
