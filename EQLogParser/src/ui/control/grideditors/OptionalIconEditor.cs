using Microsoft.WindowsAPICodePack.Dialogs;
using Syncfusion.Windows.PropertyGrid;
using Syncfusion.Windows.Tools.Controls;
using System.Collections.Generic;
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
    private TextBox _theImagePath;
    private ComboBoxAdv _theImageTypeBox;
    private Button _theButton;
    private Image _theImage;

    public override void Attach(PropertyViewItem property, PropertyItem info)
    {

      var binding = new Binding("Value")
      {
        Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
        Source = info,
        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        ValidatesOnExceptions = true,
        ValidatesOnDataErrors = true
      };

      BindingOperations.SetBinding(_theImagePath, TextBox.TextProperty, binding);
    }

    public override object Create(PropertyInfo propertyInfo) => Create();
    public override object Create(PropertyDescriptor descriotor) => Create();
    public override bool ShouldPropertyGridTryToHandleKeyDown(Key key) => false;

    public override void Detach(PropertyViewItem property)
    {
      if (_theImageTypeBox != null)
      {
        BindingOperations.ClearAllBindings(_theImageTypeBox);
        _theImageTypeBox.SelectionChanged -= ImageTypeBoxSelectionChanged;
        _theImageTypeBox = null;
      }

      if (_theButton != null)
      {
        BindingOperations.ClearAllBindings(_theButton);
        _theButton = null;
      }

      if (_theImage != null)
      {
        BindingOperations.ClearAllBindings(_theImage);
        _theImage.PreviewMouseLeftButtonDown -= TheImagePreviewMouseLeftButtonDown;
        _theImage = null;
      }

      if (_theImagePath != null)
      {
        BindingOperations.ClearAllBindings(_theImagePath);
        _theImagePath.TextChanged -= TheImagePathTextChanged;
        _theImagePath = null;
      }
    }

    private void TheButtonClick(object sender, RoutedEventArgs e) => ShowDefaultText();

    private object Create()
    {
      var grid = new Grid();
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200, GridUnitType.Star) });
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(65, GridUnitType.Auto) });

      _theImageTypeBox = new ComboBoxAdv
      {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Padding = new Thickness(0, 2, 0, 2),
        Margin = new Thickness(2, 0, 0, 0),
        VerticalContentAlignment = VerticalAlignment.Center,
        BorderThickness = new Thickness(0),
        DefaultText = "Click to Select Timer Bar Icon",
        IsReadOnly = true
      };

      _theButton = new Button
      {
        Content = "Reset",
        Padding = new Thickness(8, 2, 8, 2),
        Margin = new Thickness(2, 1, 2, 1)
      };

      _theImage = new Image
      {
        Visibility = Visibility.Collapsed,
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(4, 2, 0, 2)
      };

      _theImagePath = new TextBox
      {
        Visibility = Visibility.Collapsed,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(0, 2, 0, 2),
        TextWrapping = TextWrapping.Wrap,
        VerticalContentAlignment = VerticalAlignment.Center,
        BorderThickness = new Thickness(0),
        IsReadOnly = true,
        Cursor = Cursors.Arrow,
      };

      _theImageTypeBox.ItemsSource = new List<string>() { "Select from EQ Icons (Everquest/uifiles)", "Select from Image File (png, jpeg)" };
      _theImageTypeBox.SetValue(Grid.ColumnProperty, 0);
      _theImage.SetValue(Grid.ColumnProperty, 0);
      _theImagePath.SetValue(Grid.ColumnProperty, 0);
      _theButton.SetValue(Grid.ColumnProperty, 1);

      _theButton.Click += TheButtonClick;
      _theButton.SetResourceReference(Button.HeightProperty, "EQButtonHeight");
      _theImageTypeBox.SelectionChanged += ImageTypeBoxSelectionChanged;
      _theImage.PreviewMouseLeftButtonDown += TheImagePreviewMouseLeftButtonDown;
      _theImagePath.SetResourceReference(TextBox.ForegroundProperty, "EQWarnForegroundBrush");
      _theImagePath.TextChanged += TheImagePathTextChanged;

      grid.Children.Add(_theImage);
      grid.Children.Add(_theImageTypeBox);
      grid.Children.Add(_theImagePath);
      grid.Children.Add(_theButton);
      return grid;
    }

    private void TheImagePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
      if (_theImagePath?.Text?.StartsWith("eqsprite://", System.StringComparison.OrdinalIgnoreCase) == true)
      {
        SelectSprite();
        return;
      }

      SelectImageFile();
    }

    private void ImageTypeBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (_theImageTypeBox?.SelectedIndex == 0)
      {
        SelectSprite();
      }
      else if (_theImageTypeBox?.SelectedIndex == 1)
      {
        SelectImageFile();
      }
    }

    private void SelectImageFile()
    {
      ShowImage();

      var dialog = new CommonOpenFileDialog
      {
        // Set to false because we're opening a file, not selecting a folder
        IsFolderPicker = false,
        // Set the initial directory
        InitialDirectory = FileUtil.GetDirFromPath(_theImagePath?.Text) ?? string.Empty
      };

      // Show dialog and read result
      dialog.Filters.Add(new CommonFileDialogFilter("Images", "*.png;*.jpg;*.jpeg"));

      var handle = new WindowInteropHelper(MainActions.GetOwner()).Handle;
      if (dialog.ShowDialog(handle) == CommonFileDialogResult.Ok)
      {
        _theImagePath.Text = dialog.FileName;
      }
      else if (_theImage.Source == null)
      {
        ShowDefaultText();
      }
    }

    private void SelectSprite()
    {
      ShowImage();

      // use folder of existing sprite if possible
      var picker = new SpritePickerWindow(EqUtil.GetUiFolderFromSpritePath(_theImagePath.Text));
      if (picker.ShowDialog() == true)
      {
        _theImagePath.Text = picker.SelectedValue;
      }
      else if (_theImage.Source == null)
      {
        ShowDefaultText();
      }
    }

    private void TheImagePathTextChanged(object sender, TextChangedEventArgs e)
    {
      if (_theImage != null)
      {
        var path = _theImagePath.Text;
        _theImage.Source = UiElementUtil.CreateBitmap(path);

        // error case
        if (!string.IsNullOrEmpty(path) && _theImage.Source == null)
        {
          ShowImagePath();
        }
        else if (_theImage.Source != null)
        {
          ShowImage();
        }
        else
        {
          ShowDefaultText();
        }
      }
    }

    private void ShowDefaultText()
    {
      if (_theImagePath != null && _theImage != null && _theImageTypeBox != null)
      {
        _theImageTypeBox.SelectedIndex = -1;
        _theImageTypeBox.Visibility = Visibility.Visible;
        _theImage.Visibility = Visibility.Collapsed;
        _theImagePath.Visibility = Visibility.Collapsed;

        if (!string.IsNullOrEmpty(_theImagePath.Text))
        {
          _theImagePath.Text = null;
          _theImage.Source = null;
        }
      }
    }

    private void ShowImage()
    {
      if (_theImagePath != null && _theImage != null && _theImageTypeBox != null)
      {
        var height = UiElementUtil.CalculateTextBoxHeight(_theImagePath);
        _theImage.Height = height + 2;
        _theImage.Width = double.NaN;
        _theImageTypeBox.Visibility = Visibility.Collapsed;
        _theImage.Visibility = Visibility.Visible;
        _theImagePath.Visibility = Visibility.Collapsed;
      }
    }

    private void ShowImagePath()
    {
      if (_theImagePath != null && _theImage != null && _theImageTypeBox != null)
      {
        _theImageTypeBox.Visibility = Visibility.Collapsed;
        _theImage.Visibility = Visibility.Collapsed;
        _theImagePath.Visibility = Visibility.Visible;
      }
    }
  }
}
