using Syncfusion.Windows.PropertyGrid;
using Syncfusion.Windows.Tools.Controls;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  internal class TextSoundEditor : BaseTypeEditor
  {
    private readonly ObservableCollection<string> _fileList;
    private ComboBoxAdv _theOptionsCombo;
    private ComboBox _theSoundCombo;
    private TextBox _theTtsBox;
    private TextBox _theRealTextBox;
    private TextBox _theErrorTextBox;
    private Button _testButton;
    private StackPanel _buttonContainer;
    private Grid _grid;

    public TextSoundEditor(ObservableCollection<string> fileList)
    {
      _fileList = fileList;
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

      _theRealTextBox.DataContext = property.DataContext;
      BindingOperations.SetBinding(_theRealTextBox, TextBox.TextProperty, binding);
    }

    public override object Create(PropertyInfo propertyInfo) => Create();

    public override object Create(PropertyDescriptor descriptor) => Create();

    private object Create()
    {
      _grid = new Grid();
      _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200, GridUnitType.Star) });
      _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100, GridUnitType.Auto) });

      _buttonContainer = new StackPanel
      {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Stretch
      };

      _theOptionsCombo = new ComboBoxAdv
      {
        ItemsSource = new List<string> { "Text to Speak", "Play Sound" },
        SelectedIndex = 0,
        BorderThickness = new Thickness(0),
        IsReadOnly = true
      };

      _testButton = new Button
      {
        Padding = new Thickness(8, 2, 8, 2),
        Margin = new Thickness(2, 1, 2, 1),
        Content = "Test",
        IsEnabled = false
      };

      _theSoundCombo = new ComboBox
      {
        Name = "SoundCombo",
        Visibility = Visibility.Collapsed,
        Tag = true,
        BorderThickness = new Thickness(0)
      };

      _theTtsBox = new TextBox
      {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(0, 2, 0, 2),
        TextWrapping = TextWrapping.Wrap,
        VerticalContentAlignment = VerticalAlignment.Center,
        BorderThickness = new Thickness(0, 0, 0, 0)
      };

      _theErrorTextBox = new TextBox
      {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(0, 2, 0, 2),
        TextWrapping = TextWrapping.Wrap,
        VerticalContentAlignment = VerticalAlignment.Center,
        BorderThickness = new Thickness(0, 0, 0, 0),
        Visibility = Visibility.Collapsed
      };

      _theRealTextBox = new TextBox
      {
        Name = "Real",
        Visibility = Visibility.Collapsed
      };

      _buttonContainer.Children.Add(_theOptionsCombo);
      _buttonContainer.Children.Add(_testButton);
      _theSoundCombo.SelectedIndex = -1;
      _theSoundCombo.ItemsSource = _fileList;
      _theRealTextBox.TextChanged += RealTextBoxTextChanged;
      _theErrorTextBox.SetResourceReference(TextBox.ForegroundProperty, "EQWarnForegroundBrush");
      _testButton.SetResourceReference(Button.HeightProperty, "EQButtonHeight");

      _theTtsBox.SetValue(Grid.ColumnProperty, 0);
      _theErrorTextBox.SetValue(Grid.ColumnProperty, 0);
      _theSoundCombo.SetValue(Grid.ColumnProperty, 0);
      _buttonContainer.SetValue(Grid.ColumnProperty, 1);
      _grid.Children.Add(_theRealTextBox);
      _grid.Children.Add(_theTtsBox);
      _grid.Children.Add(_theErrorTextBox);
      _grid.Children.Add(_theSoundCombo);
      _grid.Children.Add(_buttonContainer);

      _testButton.Click += TestButtonOnClick;
      _theTtsBox.TextChanged += TextBoxTextChanged;
      _theSoundCombo.SelectionChanged += SoundComboSelectionChanged;
      _theOptionsCombo.SelectionChanged += TypeComboBoxSelectionChanged;
      return _grid;
    }

    private void TestButtonOnClick(object sender, RoutedEventArgs e)
    {
      if (sender is Button { DataContext: PropertyItem { SelectedObject: TriggerPropertyModel model } })
      {
        if (model.DataContext is TriggersTreeView view)
        {
          if (_theOptionsCombo.SelectedIndex == 0 && !string.IsNullOrEmpty(_theRealTextBox.Text))
          {
            view.PlayTts(_theRealTextBox.Text, model.VoiceRate, model.Volume);
          }
          else if (_theOptionsCombo.SelectedIndex == 1 && _theSoundCombo.SelectedValue is string selected && !string.IsNullOrEmpty(selected))
          {
            AudioManager.Instance.TestSpeakFileAsync(@"data/sounds/" + selected, model.Volume);
          }
        }
      }
    }

    private void RealTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
      if (sender is TextBox textBox)
      {
        var isSound = TriggerUtil.MatchSoundFile(textBox.Text, out var soundFile, out _);
        var soundExists = isSound && File.Exists(@"data/sounds/" + soundFile);

        if (isSound)
        {
          if (soundExists)
          {
            _theOptionsCombo.SelectedIndex = 1;
            _theErrorTextBox.Visibility = Visibility.Collapsed;
            _theTtsBox.Visibility = Visibility.Collapsed;
            _theSoundCombo.Visibility = Visibility.Visible;
          }
          else
          {
            _theOptionsCombo.SelectedIndex = -1;
            _theOptionsCombo.DefaultText = "Click for Options";
            _theErrorTextBox.Text = soundFile;
            _theTtsBox.Visibility = Visibility.Collapsed;
            _theSoundCombo.Visibility = Visibility.Collapsed;
            _theErrorTextBox.Visibility = Visibility.Visible;
          }
        }
        else
        {
          _theOptionsCombo.SelectedIndex = 0;
          _theErrorTextBox.Visibility = Visibility.Collapsed;
          _theSoundCombo.Visibility = Visibility.Collapsed;
          _theTtsBox.Visibility = Visibility.Visible;

          if (_theTtsBox.Text != textBox.Text)
          {
            _theTtsBox.Text = textBox.Text;
          }
        }

        _testButton.IsEnabled = _theOptionsCombo.SelectedIndex == 1 ||
          (_theOptionsCombo.SelectedIndex == 0 && !string.IsNullOrEmpty(textBox.Text));
      }
    }

    private void TypeComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (sender is ComboBoxAdv { SelectedIndex: > -1 } combo)
      {
        var hideText = combo.SelectedIndex != 0;
        _theTtsBox.Visibility = hideText ? Visibility.Collapsed : Visibility.Visible;
        _theSoundCombo.Visibility = hideText ? Visibility.Visible : Visibility.Collapsed;
        _theErrorTextBox.Visibility = Visibility.Collapsed;

        if (!hideText)
        {
          var previous = _theTtsBox.Text;
          _theTtsBox.Text = previous + " ";
          _theTtsBox.Text = previous;
        }
        else
        {
          var isSound = TriggerUtil.MatchSoundFile(_theRealTextBox.Text, out var decoded, out var _);
          if (!isSound || !_theSoundCombo.Items.Contains(decoded) || (_theSoundCombo.SelectedValue is string selectedValue &&
            !string.IsNullOrEmpty(selectedValue) && selectedValue != _theRealTextBox.Text))
          {
            _theSoundCombo.Tag = null;
          }

          var previous = (_theSoundCombo.SelectedIndex == -1) ? 0 : _theSoundCombo.SelectedIndex;
          _theSoundCombo.SelectedIndex = -1;
          _theSoundCombo.SelectedIndex = previous;
        }
      }
    }

    private void SoundComboSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (sender is ComboBox { SelectedValue: string selected } combo)
      {
        if (!string.IsNullOrEmpty(selected))
        {
          // change from real text box being modified
          var path = @"data/sounds/" + selected;
          if (combo.Tag == null && File.Exists(path))
          {
            var codedName = "<<" + selected + ">>";
            if (_theRealTextBox.Text != codedName)
            {
              _theRealTextBox.Text = codedName;
            }

            AudioManager.Instance.TestSpeakFileAsync(path);
          }
          combo.Tag = null;
        }
      }
    }

    private void TextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
      if (sender is TextBox textBox)
      {
        _theRealTextBox.Text = textBox.Text;
      }
    }

    public override bool ShouldPropertyGridTryToHandleKeyDown(Key key)
    {
      return false;
    }

    public override void Detach(PropertyViewItem property)
    {
      if (_theOptionsCombo != null)
      {
        _theOptionsCombo.SelectionChanged -= TypeComboBoxSelectionChanged;
        BindingOperations.ClearAllBindings(_theOptionsCombo);
        _theOptionsCombo = null;
      }

      if (_theSoundCombo != null)
      {
        _theSoundCombo.SelectionChanged -= SoundComboSelectionChanged;
        BindingOperations.ClearAllBindings(_theSoundCombo);
        _theSoundCombo = null;
      }

      if (_theRealTextBox != null)
      {
        _theRealTextBox.TextChanged -= RealTextBoxTextChanged;
        BindingOperations.ClearAllBindings(_theRealTextBox);
        _theRealTextBox = null;
      }

      if (_theTtsBox != null)
      {
        _theTtsBox.TextChanged -= TextBoxTextChanged;
        BindingOperations.ClearAllBindings(_theTtsBox);
        _theTtsBox = null;
      }

      if (_theErrorTextBox != null)
      {
        BindingOperations.ClearAllBindings(_theErrorTextBox);
        _theErrorTextBox = null;
      }

      if (_testButton != null)
      {
        _testButton.Click -= TestButtonOnClick;
        BindingOperations.ClearAllBindings(_testButton);
        _testButton = null;
      }

      if (_buttonContainer != null)
      {
        _buttonContainer.Children.Clear();
        _buttonContainer = null;
      }

      _grid.Children.Clear();
    }
  }
}
