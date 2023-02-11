using Syncfusion.Data.Extensions;
using Syncfusion.Windows.PropertyGrid;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace EQLogParser
{
  internal class TextSoundEditor : ITypeEditor
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private Grid TextSoundGrid;
    private SoundPlayer SoundPlayer;

    public void Attach(PropertyViewItem property, PropertyItem info)
    {
      Binding binding = new Binding("Value")
      {
        Mode = info.CanWrite ? BindingMode.TwoWay : BindingMode.OneWay,
        Source = info,
        ValidatesOnExceptions = true,
        ValidatesOnDataErrors = true,
        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
      };

      foreach (var child in TextSoundGrid.Children)
      {
        if (child is TextBox textBox && "Real".Equals(textBox.Name))
        {
          BindingOperations.SetBinding(textBox, TextBox.TextProperty, binding);
        }
      }
    }

    public object Create(PropertyInfo propertyInfo) => Create();
    public object Create(PropertyDescriptor descriotor) => Create();

    private object Create()
    {
      var grid = new Grid();
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200, GridUnitType.Star) });
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });

      var combo = new ComboBox();
      combo.SetValue(Grid.ColumnProperty, 1);
      combo.ItemsSource = new List<string> { "Text to Speak", "Play Sound" };
      combo.SelectedIndex = 0;
      var textBox = new TextBox { HorizontalAlignment = HorizontalAlignment.Stretch };
      textBox.SetValue(Grid.ColumnProperty, 0);
      var soundCombo = new ComboBox { Name = "SoundCombo", Visibility = Visibility.Collapsed, Tag = true };
      soundCombo.SetValue(Grid.ColumnProperty, 0);
      soundCombo.SelectedIndex = 0;
      var realTextBox = new TextBox { Name = "Real", Visibility = Visibility.Collapsed };
      realTextBox.TextChanged += RealTextBoxTextChanged;

      try
      {
        soundCombo.ItemsSource = Directory.GetFiles(@"data/sounds").Select(file => Path.GetFileName(file)).ToList();
      }
      catch (Exception)
      {
        // ignore
      }

      grid.Children.Add(combo);
      grid.Children.Add(textBox);
      grid.Children.Add(soundCombo);
      grid.Children.Add(realTextBox);
      TextSoundGrid = grid;

      textBox.TextChanged += TextBoxTextChanged;
      soundCombo.SelectionChanged += SoundComboSelectionChanged;
      combo.SelectionChanged += TypeComboBoxSelectionChanged;

      if (SoundPlayer == null)
      {
        SoundPlayer = new SoundPlayer();
      }

      return grid;
    }

    private void RealTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
      if (sender is TextBox textBox)
      {
        bool hideText = TriggerUtil.MatchSoundFile(textBox.Text, out string soundFile, out _);

        foreach (var child in TextSoundGrid.Children)
        {
          if (child is ComboBox combo)
          {
            if ("SoundCombo".Equals(combo.Name))
            {
              combo.Visibility = hideText ? Visibility.Visible : Visibility.Collapsed;
              if (hideText)
              {
                if (combo?.SelectedValue?.ToString() != soundFile)
                {
                  combo.Tag = true;
                  combo.SelectedValue = soundFile;
                }
              }
            }
            else
            {
              combo.SelectedIndex = hideText ? 1 : 0;
            }
          }
          else if (child is TextBox fakeTextBox && !"Real".Equals(fakeTextBox.Name))
          {
            fakeTextBox.Visibility = hideText ? Visibility.Collapsed : Visibility.Visible;
            if (!hideText)
            {
              if (fakeTextBox.Text != textBox.Text)
              {
                fakeTextBox.Text = textBox.Text;
              }
            }
          }
        }
      }
    }

    private void TypeComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (sender is ComboBox combo && combo.SelectedIndex > -1)
      {
        bool hideText = combo.SelectedIndex == 0 ? false : true;

        foreach (var child in TextSoundGrid.Children)
        {
          if (child is TextBox textBox && !"Real".Equals(textBox.Name))
          {
            textBox.Visibility = hideText ? Visibility.Collapsed : Visibility.Visible;

            if (!hideText)
            {
              var previous = textBox.Text;
              textBox.Text = previous + "-" + previous;
              textBox.Text = previous;
            }
          }
          else if (child is ComboBox soundCombo && "SoundCombo".Equals(soundCombo.Name))
          {
            soundCombo.Visibility = hideText ? Visibility.Visible : Visibility.Collapsed;

            if (hideText)
            {
              var previous = soundCombo.SelectedIndex;
              soundCombo.SelectedIndex = -1;
              soundCombo.Tag = null;
              soundCombo.SelectedIndex = previous;
            }
          }
        }
      }
    }

    private void SoundComboSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (sender is ComboBox combo && combo.SelectedValue is string selected)
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
              LOG.Error("Error playing sound file.", ex);
            }
          }

          combo.Tag = null;
          var codedName = "<<" + selected + ">>";

          foreach (var child in TextSoundGrid.Children)
          {
            if (child is TextBox textBox && "Real".Equals(textBox.Name) && textBox.Text != codedName)
            {
              textBox.Text = codedName;
            }
          }
        }
      }
    }

    private void TextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
      if (sender is TextBox textBox)
      {
        foreach (var child in TextSoundGrid.Children)
        {
          if (child is TextBox realTextBox && "Real".Equals(realTextBox.Name))
          {
            realTextBox.Text = textBox.Text;
          }
        }
      }
    }

    public void Detach(PropertyViewItem property)
    {
      if (TextSoundGrid != null)
      {
        foreach (var child in TextSoundGrid.Children)
        {
          if (child is FrameworkElement elem)
          {
            BindingOperations.ClearAllBindings(elem);

            if (elem is TextBox textBox)
            {
              if ("Real".Equals(textBox.Name))
              {
                textBox.TextChanged -= RealTextBoxTextChanged;
              }
              else
              {
                textBox.TextChanged -= TextBoxTextChanged;
              }
            }
            else if (elem is ComboBox combo)
            {
              if ("SoundCombo".Equals(combo.Name))
              {
                combo.SelectionChanged -= SoundComboSelectionChanged;
              }
              else
              {
                combo.SelectionChanged -= TypeComboBoxSelectionChanged;
              }
            }
          }
        }

        TextSoundGrid.Children.Clear();
        TextSoundGrid = null;
        SoundPlayer?.Dispose();
        SoundPlayer = null;
      }
    }
  }
}
