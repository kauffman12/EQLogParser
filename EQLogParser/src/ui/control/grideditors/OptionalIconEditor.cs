using Microsoft.WindowsAPICodePack.Dialogs;
using Syncfusion.Windows.PropertyGrid;
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
    private TextBox _theTextBox;
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
      if (_theTextBox != null)
      {
        _theTextBox.PreviewMouseLeftButtonDown -= TheTextBoxPreviewMouseLeftButtonDown;
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

    private void TheImagePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => SelectImage();
    private void TheButtonClick(object sender, RoutedEventArgs e) => ShowDefaultText();

    private object Create()
    {
      var grid = new Grid();
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200, GridUnitType.Star) });
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(65, GridUnitType.Auto) });
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(85, GridUnitType.Auto) });

      _theTextBox = new TextBox
      {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(0, 2, 0, 2),
        TextWrapping = TextWrapping.Wrap,
        VerticalContentAlignment = VerticalAlignment.Center,
        BorderThickness = new Thickness(0),
        Text = "Click to Select Timer Bar Icon",
        IsReadOnly = true,
        FontStyle = FontStyles.Italic,
        Cursor = Cursors.Hand
      };

      _theButton = new Button
      {
        Content = "Reset",
        Padding = new Thickness(8, 2, 8, 2),
        Margin = new Thickness(2, 1, 2, 1)
      };

      var spriteBtn = new Button
      {
        Content = "EQ Icons",
        Padding = new Thickness(8, 2, 8, 2),
        Margin = new Thickness(2, 1, 2, 1)
      };
      spriteBtn.SetValue(Grid.ColumnProperty, 2);
      spriteBtn.Click += SpriteBtnClick;

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

      _theTextBox.SetValue(Grid.ColumnProperty, 0);
      _theImage.SetValue(Grid.ColumnProperty, 0);
      _theImagePath.SetValue(Grid.ColumnProperty, 0);
      _theButton.SetValue(Grid.ColumnProperty, 1);

      _theButton.Click += TheButtonClick;
      _theTextBox.PreviewMouseLeftButtonDown += TheTextBoxPreviewMouseLeftButtonDown;
      _theImage.PreviewMouseLeftButtonDown += TheImagePreviewMouseLeftButtonDown;
      _theImagePath.SetResourceReference(TextBox.ForegroundProperty, "EQWarnForegroundBrush");
      _theImagePath.TextChanged += TheImagePathTextChanged;

      grid.Children.Add(_theImage);
      grid.Children.Add(_theTextBox);
      grid.Children.Add(_theImagePath);
      grid.Children.Add(_theButton);
      grid.Children.Add(spriteBtn);
      return grid;
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
        _theImagePath.Text = file;
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

    private void SpriteBtnClick(object sender, RoutedEventArgs e)
    {
      ShowImage();
      var picker = new SpritePickerWindow();
      picker.Owner = MainActions.GetOwner();
      if (picker.ShowDialog() == true)
      {
        var bitmap = UiElementUtil.CreateBitmap(picker.SelectedValue);
        if (bitmap != null && _theImage != null)
        {
          // Force binding update by clearing first (same as Reset does)
          _theImage.Source = null;
          _theImage.Source = bitmap;
        }
      }
    }

    private void TheTextBoxPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
      ShowImage();
      SelectImage();
    }

    private void ShowDefaultText()
    {
      if (_theImagePath != null && _theImage != null && _theTextBox != null)
      {
        _theTextBox.Visibility = Visibility.Visible;
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
      if (_theImagePath != null && _theImage != null && _theTextBox != null)
      {
        var height = UiElementUtil.CalculateTextBoxHeight(_theTextBox);
        _theImage.Height = height + 2;
        _theImage.Width = double.NaN;
        _theTextBox.Visibility = Visibility.Collapsed;
        _theImage.Visibility = Visibility.Visible;
        _theImagePath.Visibility = Visibility.Collapsed;
      }
    }

    private void ShowImagePath()
    {
      if (_theImagePath != null && _theImage != null && _theTextBox != null)
      {
        _theTextBox.Visibility = Visibility.Collapsed;
        _theImage.Visibility = Visibility.Collapsed;
        _theImagePath.Visibility = Visibility.Visible;
      }
    }
  }
}
