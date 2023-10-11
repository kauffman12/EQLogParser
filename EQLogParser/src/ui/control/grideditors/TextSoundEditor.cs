using log4net;
using Syncfusion.Windows.PropertyGrid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Media;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  internal class TextSoundEditor : BaseTypeEditor
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private SoundPlayer SoundPlayer;
    private readonly ObservableCollection<string> FileList;
    private ComboBox TheOptionsCombo;
    private ComboBox TheSoundCombo;
    private TextBox TheFakeTextBox;
    private TextBox TheRealTextBox;

    public TextSoundEditor(ObservableCollection<string> fileList)
    {
      FileList = fileList;
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

      TheRealTextBox.DataContext = property.DataContext;
      BindingOperations.SetBinding(TheRealTextBox, TextBox.TextProperty, binding);
    }

    public override object Create(PropertyInfo propertyInfo) => Create();

    public override object Create(PropertyDescriptor descriptor) => Create();

    private object Create()
    {
      var grid = new Grid();
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200, GridUnitType.Star) });
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

      TheOptionsCombo = new ComboBox();
      TheOptionsCombo.SetValue(Grid.ColumnProperty, 1);
      TheOptionsCombo.ItemsSource = new List<string> { "Text to Speak", "Play Sound" };
      TheOptionsCombo.SelectedIndex = 0;

      TheFakeTextBox = new TextBox
      {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(0, 2, 0, 2),
        TextWrapping = TextWrapping.Wrap,
        VerticalContentAlignment = VerticalAlignment.Center,
        BorderThickness = new Thickness(0, 0, 0, 0)
      };

      TheFakeTextBox.SetValue(Grid.ColumnProperty, 0);
      TheSoundCombo = new ComboBox { Name = "SoundCombo", Visibility = Visibility.Collapsed, Tag = true };
      TheSoundCombo.SetValue(Grid.ColumnProperty, 0);
      TheSoundCombo.SelectedIndex = -1;
      TheRealTextBox = new TextBox { Name = "Real", Visibility = Visibility.Collapsed };
      TheRealTextBox.TextChanged += RealTextBoxTextChanged;
      TheSoundCombo.ItemsSource = FileList;

      grid.Children.Add(TheRealTextBox);
      grid.Children.Add(TheOptionsCombo);
      grid.Children.Add(TheFakeTextBox);
      grid.Children.Add(TheSoundCombo);

      TheFakeTextBox.TextChanged += TextBoxTextChanged;
      TheSoundCombo.SelectionChanged += SoundComboSelectionChanged;
      TheOptionsCombo.SelectionChanged += TypeComboBoxSelectionChanged;
      SoundPlayer ??= new SoundPlayer();

      return grid;
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

        TheOptionsCombo.SelectedIndex = hideText ? 1 : 0;
        TheFakeTextBox.Visibility = hideText ? Visibility.Collapsed : Visibility.Visible;
        TheSoundCombo.Visibility = hideText ? Visibility.Visible : Visibility.Collapsed;

        if (hideText)
        {
          if (TheSoundCombo?.SelectedValue?.ToString() != soundFile)
          {
            TheSoundCombo.Tag = true;
            TheSoundCombo.SelectedValue = soundFile;
          }
        }

        if (!hideText)
        {
          if (TheFakeTextBox.Text != textBox.Text)
          {
            TheFakeTextBox.Text = textBox.Text;
          }
        }
      }
    }

    private void TypeComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (sender is ComboBox { SelectedIndex: > -1 } combo)
      {
        var hideText = combo.SelectedIndex == 0 ? false : true;
        TheFakeTextBox.Visibility = hideText ? Visibility.Collapsed : Visibility.Visible;
        TheSoundCombo.Visibility = hideText ? Visibility.Visible : Visibility.Collapsed;

        if (!hideText)
        {
          var previous = TheFakeTextBox.Text;
          TheFakeTextBox.Text = previous + " ";
          TheFakeTextBox.Text = previous;
        }
        else
        {
          var isSound = TriggerUtil.MatchSoundFile(TheRealTextBox.Text, out var decoded, out var _);
          if (!isSound || !TheSoundCombo.Items.Contains(decoded) || (TheSoundCombo.SelectedValue is string selectedValue &&
            !string.IsNullOrEmpty(selectedValue) && selectedValue != TheRealTextBox.Text))
          {
            TheSoundCombo.Tag = null;
          }

          var previous = (TheSoundCombo.SelectedIndex == -1) ? 0 : TheSoundCombo.SelectedIndex;
          TheSoundCombo.SelectedIndex = -1;
          TheSoundCombo.SelectedIndex = previous;
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
          if (combo.Tag == null && SoundPlayer != null && File.Exists(@"data/sounds/" + selected))
          {
            try
            {
              SoundPlayer.SoundLocation = @"data/sounds/" + selected;
              SoundPlayer.Play();
            }
            catch (Exception ex)
            {
              Log.Error("Error playing sound file.", ex);
            }

            var codedName = "<<" + selected + ">>";
            if (TheRealTextBox.Text != codedName)
            {
              TheRealTextBox.Text = codedName;
            }
          }

          combo.Tag = null;
        }
      }
    }

    private void TextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
      if (sender is TextBox textBox)
      {
        TheRealTextBox.Text = textBox.Text;
      }
    }

    public override bool ShouldPropertyGridTryToHandleKeyDown(Key key)
    {
      return false;
    }

    public override void Detach(PropertyViewItem property)
    {
      if (TheOptionsCombo != null)
      {
        TheOptionsCombo.SelectionChanged -= TypeComboBoxSelectionChanged;
        BindingOperations.ClearAllBindings(TheOptionsCombo);
        TheOptionsCombo = null;
      }

      if (TheSoundCombo != null)
      {
        TheSoundCombo.SelectionChanged -= SoundComboSelectionChanged;
        BindingOperations.ClearAllBindings(TheSoundCombo);
        TheSoundCombo = null;
      }

      if (TheRealTextBox != null)
      {
        TheRealTextBox.TextChanged -= RealTextBoxTextChanged;
        BindingOperations.ClearAllBindings(TheRealTextBox);
        TheRealTextBox = null;
      }

      if (TheFakeTextBox != null)
      {
        TheFakeTextBox.TextChanged -= TextBoxTextChanged;
        BindingOperations.ClearAllBindings(TheFakeTextBox);
        TheFakeTextBox = null;
      }

      SoundPlayer?.Dispose();
      SoundPlayer = null;
    }
  }
}
