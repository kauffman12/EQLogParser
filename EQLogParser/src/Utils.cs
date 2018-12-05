using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EQLogParser
{
  class Utils
  {
    internal static void OpenWindow(ActiproSoftware.Windows.Controls.Docking.DockingWindow window)
    {
      if (!window.IsOpen)
      {
        window.IsOpen = true;
      }
      else
      {
        window.Focus();
      }

      if (!window.IsActive)
      {
        window.Activate();
      }
    }

    internal static string FormatTimeSpan(TimeSpan diff)
    {
      string result = "Inactivity > ";
      if (diff.Days >= 1)
      {
        result += diff.Days + " days";
      }
      else if (diff.Hours >= 1)
      {
        result += diff.Hours + " hours";
      }
      else
      {
        result += diff.Minutes + " minutes";
      }

      return result;
    }

    internal static string FormatDamage(long total)
    {
      string result;

      if (total < 1000)
      {
        result = total.ToString();
      }
      else if (total < 1000000)
      {
        result = Math.Round((decimal)total / 1000, 1) + "K";
      }
      else
      {
        result = Math.Round((decimal)total / 1000 / 1000, 1) + "M";
      }

      return result;
    }
  }

  internal static class TextBoxBehavior
  {
    public static readonly DependencyProperty TripleClickSelectAllProperty = DependencyProperty.RegisterAttached("TripleClickSelectAll", typeof(bool), typeof(TextBoxBehavior), new PropertyMetadata(false, OnPropertyChanged));

    private static void OnPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      var tb = d as TextBox;
      if (tb != null)
      {
        var enable = (bool)e.NewValue;
        if (enable)
        {
          tb.PreviewMouseLeftButtonDown += OnTextBoxMouseDown;
        }
        else
        {
          tb.PreviewMouseLeftButtonDown -= OnTextBoxMouseDown;
        }
      }
    }

    private static void OnTextBoxMouseDown(object sender, MouseButtonEventArgs e)
    {
      if (e.ClickCount == 3)
      {
        ((TextBox)sender).SelectAll();
      }
    }

    internal static void SetTripleClickSelectAll(DependencyObject element, bool value)
    {
      element.SetValue(TripleClickSelectAllProperty, value);
    }

    internal static bool GetTripleClickSelectAll(DependencyObject element)
    {
      return (bool)element.GetValue(TripleClickSelectAllProperty);
    }
  }
}
