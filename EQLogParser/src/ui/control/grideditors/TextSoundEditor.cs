using EQLogParser.Audio;
using Microsoft.Win32;
using Syncfusion.Windows.PropertyGrid;
using Syncfusion.Windows.Tools.Controls;
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
    private readonly ObservableCollection<string> _fileList;
    private ComboBoxAdv _theOptionsCombo;
    private ComboBox _theSoundCombo;
    private TextBox _theTtsBox;
    private TextBox _theRealTextBox;
    private TextBox _theErrorTextBox;
    private TextBox _thePathBox;
    private Button _testButton;
    private StackPanel _buttonContainer;
    private Grid _grid;
    private bool _isSettingOptionsFromText;

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

    public override object Create(PropertyInfo _) => Create();
    public override object Create(PropertyDescriptor _) => Create();

    private object Create()
    {
      if (_grid != null)
        return _grid;

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
        ItemsSource = new List<string> { "Text to Speak", "Play Sound", "Browse for Sound File" },
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

      _thePathBox = new TextBox
      {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(0, 2, 0, 2),
        TextWrapping = TextWrapping.Wrap,
        VerticalContentAlignment = VerticalAlignment.Center,
        BorderThickness = new Thickness(0, 0, 0, 0),
        IsReadOnly = true,
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
      _theErrorTextBox.TextChanged += ErrorBoxTextChanged;
      _theErrorTextBox.SetResourceReference(TextBox.ForegroundProperty, "EQWarnForegroundBrush");
      _testButton.SetResourceReference(Button.HeightProperty, "EQButtonHeight");

      _theTtsBox.SetValue(Grid.ColumnProperty, 0);
      _theErrorTextBox.SetValue(Grid.ColumnProperty, 0);
      _thePathBox.SetValue(Grid.ColumnProperty, 0);
      _theSoundCombo.SetValue(Grid.ColumnProperty, 0);
      _buttonContainer.SetValue(Grid.ColumnProperty, 1);
      _grid.Children.Add(_theRealTextBox);
      _grid.Children.Add(_theTtsBox);
      _grid.Children.Add(_theErrorTextBox);
      _grid.Children.Add(_thePathBox);
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
            AudioManager.Instance.TestSpeakFileAsync(TriggerUtil.ResolveSoundPath(selected), model.Volume);
          }
          else if (_theOptionsCombo.SelectedIndex == 2 && TriggerUtil.MatchSoundFile(_theRealTextBox.Text, out var browsedFile, out _) &&
            File.Exists(TriggerUtil.ResolveSoundPath(browsedFile)))
          {
            AudioManager.Instance.TestSpeakFileAsync(TriggerUtil.ResolveSoundPath(browsedFile), model.Volume);
          }
        }
      }
    }

    private void RealTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
      if (sender is TextBox textBox)
      {
        var isSound = TriggerUtil.MatchSoundFile(textBox.Text, out var soundFile, out _);
        var soundExists = isSound && TriggerUtil.SoundFileExists(soundFile);

        if (isSound)
        {
          var isInFileList = _theSoundCombo.Items.Contains(soundFile);
          if (soundExists && isInFileList)
          {
            _theOptionsCombo.SelectedIndex = 1;
            _theErrorTextBox.Visibility = Visibility.Collapsed;
            _thePathBox.Visibility = Visibility.Collapsed;
            _theTtsBox.Visibility = Visibility.Collapsed;

            if (!Equals(_theSoundCombo.SelectedItem, soundFile))
            {
              _theSoundCombo.Tag = true;
              _theSoundCombo.SelectedItem = soundFile;
            }

            _theSoundCombo.Visibility = Visibility.Visible;
          }
          else if (soundExists && !isInFileList)
          {
            // Custom browsed sound file — show full path, keep "Browse for Sound File" selected
            // Guard against TypeComboBoxSelectionChanged opening the file dialog during programmatic update
            _isSettingOptionsFromText = true;
            _theOptionsCombo.SelectedIndex = 2;
            _isSettingOptionsFromText = false;
            _theErrorTextBox.Visibility = Visibility.Collapsed;
            _theTtsBox.Visibility = Visibility.Collapsed;
            _theSoundCombo.Visibility = Visibility.Collapsed;
            _thePathBox.Text = TriggerUtil.ResolveSoundPath(soundFile);
            _thePathBox.Visibility = Visibility.Visible;
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
          _theOptionsCombo.SelectedIndex == 2 ||
          (_theOptionsCombo.SelectedIndex == 0 && !string.IsNullOrEmpty(textBox.Text));
      }
    }

    private void TypeComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (_isSettingOptionsFromText)
      {
        return;
      }

      if (sender is ComboBoxAdv { SelectedIndex: > -1 } combo)
      {
        // Index 2 = "Browse for Sound File" — open file dialog immediately
        if (combo.SelectedIndex == 2)
        {
          var browseSucceeded = BrowseForSoundFile();
          if (!browseSucceeded)
          {
            // User cancelled or error — reset to previous selection
            combo.SelectedIndex = _theTtsBox.Visibility == Visibility.Visible ? 0 : 1;
          }
          return;
        }

        var hideText = combo.SelectedIndex != 0;
        _theTtsBox.Visibility = hideText ? Visibility.Collapsed : Visibility.Visible;
        _theSoundCombo.Visibility = hideText && combo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        _theErrorTextBox.Visibility = Visibility.Collapsed;
        _thePathBox.Visibility = Visibility.Collapsed;

        if (!hideText)
        {
          var previous = _theTtsBox.Text;
          _theTtsBox.Text = previous + " ";
          _theTtsBox.Text = previous;
        }
        else if (combo.SelectedIndex == 1)
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

    private bool BrowseForSoundFile()
    {
      var dialog = new OpenFileDialog
      {
        Filter = "Audio Files|*.wav;*.mp3",
        Title = "Select a Sound File"
      };

      if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.FileName))
      {
        var selectedPath = dialog.FileName;
        // Store the full path in <<>> encoding — ResolveSoundPath handles absolute paths
        _theRealTextBox.Text = "<<" + selectedPath + ">>";
        return true;
      }

      return false;
    }

    private void SoundComboSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (sender is ComboBox { SelectedValue: string selected } combo)
      {
        if (!string.IsNullOrEmpty(selected))
        {
          // change from real text box being modified
          var path = TriggerUtil.ResolveSoundPath(selected);
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

    private void ErrorBoxTextChanged(object sender, TextChangedEventArgs e)
    {
      // When the user edits a missing sound file path in the error box,
      // propagate the change to the bound _theRealTextBox so the model is
      // updated (enabling save). Only update when the text looks like a
      // complete sound file path to avoid intermediate typing states from
      // triggering RealTextBoxTextChanged's TTS branch.
      if (sender is TextBox textBox &&
        _theOptionsCombo.SelectedIndex == -1 &&
        (textBox.Text.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
         textBox.Text.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)))
      {
        var codedName = "<<" + textBox.Text + ">>";
        if (_theRealTextBox.Text != codedName)
        {
          _theRealTextBox.Text = codedName;
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
        _theErrorTextBox.TextChanged -= ErrorBoxTextChanged;
        BindingOperations.ClearAllBindings(_theErrorTextBox);
        _theErrorTextBox = null;
      }

      if (_thePathBox != null)
      {
        _thePathBox.Text = string.Empty;
        BindingOperations.ClearAllBindings(_thePathBox);
        _thePathBox = null;
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

      if (_grid != null)
      {
        _grid.Children.Clear();
        _grid = null;
      }
    }
  }
}
