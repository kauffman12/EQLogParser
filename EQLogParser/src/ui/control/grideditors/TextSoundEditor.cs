using log4net;
using Syncfusion.Windows.PropertyGrid;
using System;
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
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private readonly ObservableCollection<string> _fileList;
    private ComboBox _theOptionsCombo;
    private ComboBox _theSoundCombo;
    private TextBox _theFakeTextBox;
    private TextBox _theRealTextBox;
    private Button _testButton;
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
      _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
      _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

      _theOptionsCombo = new ComboBox();
      _theOptionsCombo.SetValue(Grid.ColumnProperty, 1);
      _theOptionsCombo.ItemsSource = new List<string> { "Text to Speak", "Play Sound" };
      _theOptionsCombo.SelectedIndex = 0;

      _testButton = new Button
      {
        Padding = new Thickness(0, 2, 0, 2),
        Margin = new Thickness(0, 1, 0, 1),
        Content = "Test",
        IsEnabled = false
      };

      _testButton.SetValue(Grid.ColumnProperty, 2);
      _testButton.Click += TestButtonOnClick;

      _theFakeTextBox = new TextBox
      {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(0, 2, 0, 2),
        TextWrapping = TextWrapping.Wrap,
        VerticalContentAlignment = VerticalAlignment.Center,
        BorderThickness = new Thickness(0, 0, 0, 0)
      };

      _theFakeTextBox.SetValue(Grid.ColumnProperty, 0);
      _theSoundCombo = new ComboBox { Name = "SoundCombo", Visibility = Visibility.Collapsed, Tag = true };
      _theSoundCombo.SetValue(Grid.ColumnProperty, 0);
      _theSoundCombo.SelectedIndex = -1;
      _theRealTextBox = new TextBox { Name = "Real", Visibility = Visibility.Collapsed };
      _theRealTextBox.TextChanged += RealTextBoxTextChanged;
      _theSoundCombo.ItemsSource = _fileList;

      _grid.Children.Add(_theRealTextBox);
      _grid.Children.Add(_theOptionsCombo);
      _grid.Children.Add(_theFakeTextBox);
      _grid.Children.Add(_theSoundCombo);
      _grid.Children.Add(_testButton);

      _theFakeTextBox.TextChanged += TextBoxTextChanged;
      _theSoundCombo.SelectionChanged += SoundComboSelectionChanged;
      _theOptionsCombo.SelectionChanged += TypeComboBoxSelectionChanged;

      return _grid;
    }

    private void TestButtonOnClick(object sender, RoutedEventArgs e)
    {
      if (!string.IsNullOrEmpty(_theRealTextBox.Text))
      {
        AudioManager.Instance.TestSpeakTtsAsync(_theRealTextBox.Text);
      }
    }

    private void RealTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
      if (sender is TextBox textBox)
      {
        var hideText = TriggerUtil.MatchSoundFile(textBox.Text, out var soundFile, out _);

        if (hideText)
        {
          if (!File.Exists(@"data/sounds/" + soundFile))
          {
            hideText = false;
            textBox.Text = "";
          }
        }

        _theOptionsCombo.SelectedIndex = hideText ? 1 : 0;
        _theFakeTextBox.Visibility = hideText ? Visibility.Collapsed : Visibility.Visible;
        _theSoundCombo.Visibility = hideText ? Visibility.Visible : Visibility.Collapsed;
        _grid.ColumnDefinitions[2].Width = hideText ? new GridLength(0) : new GridLength(32);

        if (hideText)
        {
          if (_theSoundCombo != null && _theSoundCombo.SelectedValue?.ToString() != soundFile)
          {
            _theSoundCombo.Tag = true;
            _theSoundCombo.SelectedValue = soundFile;
          }
        }

        if (!hideText)
        {
          if (_theFakeTextBox.Text != textBox.Text)
          {
            _theFakeTextBox.Text = textBox.Text;
          }

          _testButton.IsEnabled = !string.IsNullOrEmpty(textBox.Text);
        }
      }
    }

    private void TypeComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (sender is ComboBox { SelectedIndex: > -1 } combo)
      {
        var hideText = combo.SelectedIndex != 0;
        _theFakeTextBox.Visibility = hideText ? Visibility.Collapsed : Visibility.Visible;
        _theSoundCombo.Visibility = hideText ? Visibility.Visible : Visibility.Collapsed;
        _grid.ColumnDefinitions[2].Width = hideText ? new GridLength(0) : new GridLength(32);

        if (!hideText)
        {
          var previous = _theFakeTextBox.Text;
          _theFakeTextBox.Text = previous + " ";
          _theFakeTextBox.Text = previous;
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
          if (combo.Tag == null && File.Exists(@"data/sounds/" + selected))
          {
            var codedName = "<<" + selected + ">>";
            if (_theRealTextBox.Text != codedName)
            {
              _theRealTextBox.Text = codedName;
            }

            var theFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "sounds", selected);
            AudioManager.Instance.TestSpeakFileAsync(theFile);
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

      if (_theFakeTextBox != null)
      {
        _theFakeTextBox.TextChanged -= TextBoxTextChanged;
        BindingOperations.ClearAllBindings(_theFakeTextBox);
        _theFakeTextBox = null;
      }

      if (_testButton != null)
      {
        _testButton.Click -= TestButtonOnClick;
        BindingOperations.ClearAllBindings(_testButton);
        _testButton = null;
      }

      _grid.Children.Clear();
    }
  }
}
