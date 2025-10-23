using Microsoft.WindowsAPICodePack.Dialogs;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace EQLogParser
{
  public partial class SpritePickerWindow : Window
  {
    public string SelectedValue { get; private set; }

    public SpritePickerWindow()
    {
      InitializeComponent();
      Owner = MainActions.GetOwner();
      LoadDefaultSheets();
    }

    private void LoadDefaultSheets()
    {
      // attempt to find EQ UI folder from default character if any
      var eqDir = EqUtil.GetEqUiFolder();
      if (!string.IsNullOrEmpty(eqDir) && Directory.Exists(eqDir))
      {
        var files = Directory.EnumerateFiles(eqDir, "spells*.tga").Concat(Directory.EnumerateFiles(eqDir, "spells*.png"));
        sheetsList.ItemsSource = files.ToList();
      }
    }

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
      var dialog = new CommonOpenFileDialog { IsFolderPicker = true };
      var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
      if (dialog.ShowDialog(handle) == CommonFileDialogResult.Ok)
      {
        var dir = dialog.FileName;
        var files = Directory.EnumerateFiles(dir, "spells*.tga").Concat(Directory.EnumerateFiles(dir, "spells*.png"));
        sheetsList.ItemsSource = files.ToList();
      }
    }

    private void SheetsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (sheetsList.SelectedItem is string path && File.Exists(path))
      {
        LoadSheet(path);
      }
    }

    private void LoadSheet(string path)
    {
      try
      {
        BitmapSource sheet;
        if (path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
        {
          sheet = TgaLoader.Load(path);
        }
        else
        {
          sheet = new BitmapImage(new Uri(path, UriKind.Absolute));
        }

        sheetImage.Source = sheet;
        // build grid of 6x6 cells of 40x40
        gridPanel.Children.Clear();
        gridPanel.Columns = 6;
        for (int row = 0; row < 6; row++)
        {
          for (int col = 0; col < 6; col++)
          {
            var img = new System.Windows.Controls.Image
            {
              Width = 40,
              Height = 40,
              Margin = new Thickness(2),
              Tag = new { path, col, row }
            };

            var rect = new Int32Rect(col * 40, row * 40, 40, 40);
            try
            {
              var cropped = new CroppedBitmap(sheet, rect);
              img.Source = cropped;
            }
            catch { }

            img.MouseLeftButtonDown += Sprite_Click;
            gridPanel.Children.Add(img);
          }
        }
      }
      catch (Exception ex)
      {
        MessageBox.Show("Error loading sprite sheet: " + ex.Message);
      }
    }

    private void Sprite_Click(object sender, MouseButtonEventArgs e)
    {
      if (sender is System.Windows.Controls.Image img && img.Tag is dynamic tag)
      {
        SelectedValue = $"eqsprite|{tag.path}|{tag.col}|{tag.row}";
        DialogResult = true;
        Close();
      }
    }
  }
}
