using Microsoft.WindowsAPICodePack.Dialogs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EQLogParser
{
  public partial class SpritePickerWindow : Window
  {
    public string SelectedValue { get; private set; }
    private List<string> _allSheets = new List<string>();
    private int _currentPage = 0;

    // Windows API for dark title bar
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public SpritePickerWindow()
    {
      InitializeComponent();
      Owner = MainActions.GetOwner();
      
      // Enable dark title bar
      Loaded += (s, e) =>
      {
        var helper = new WindowInteropHelper(this);
        if (helper.Handle != IntPtr.Zero)
        {
          int darkMode = 1;
          DwmSetWindowAttribute(helper.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
        }
      };
      
      LoadDefaultSheets();
    }

    private void LoadDefaultSheets()
    {
      // attempt to find EQ UI folder from default character if any
      var eqDir = EqUtil.GetEqUiFolder();
      if (!string.IsNullOrEmpty(eqDir) && Directory.Exists(eqDir))
      {
        var files = Directory.EnumerateFiles(eqDir, "spells*.tga").Concat(Directory.EnumerateFiles(eqDir, "spells*.png"));
        _allSheets = files.ToList();
        
        // Display friendly names instead of full paths
        var displayNames = _allSheets.Select(f => GetFriendlyName(f)).ToList();
        sheetsList.ItemsSource = displayNames;
        
        UpdatePagination();
      }
    }

    private string GetFriendlyName(string filePath)
    {
      // Convert "spells01.tga" to "Spells 01"
      var fileName = Path.GetFileNameWithoutExtension(filePath);
      if (fileName.StartsWith("spells", StringComparison.OrdinalIgnoreCase) && fileName.Length > 6)
      {
        var number = fileName.Substring(6);
        return $"Spells {number.PadLeft(2, '0')}";
      }
      return fileName;
    }



    private void SheetsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (sheetsList.SelectedIndex >= 0 && sheetsList.SelectedIndex < _allSheets.Count)
      {
        _currentPage = sheetsList.SelectedIndex;
        var path = _allSheets[_currentPage];
        if (File.Exists(path))
        {
          LoadSheet(path);
          UpdatePagination();
        }
      }
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
      if (_currentPage > 0)
      {
        _currentPage--;
        sheetsList.SelectedIndex = _currentPage;
      }
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
      if (_currentPage < _allSheets.Count - 1)
      {
        _currentPage++;
        sheetsList.SelectedIndex = _currentPage;
      }
    }

    private void UpdatePagination()
    {
      int totalPages = _allSheets.Count;
      int currentPageDisplay = _currentPage + 1;
      
      pageText.Text = totalPages > 0 ? $"Page {currentPageDisplay} of {totalPages}" : "Page 0 of 0";
      prevButton.IsEnabled = _currentPage > 0;
      nextButton.IsEnabled = _currentPage < totalPages - 1;
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
        
        // Build grid of 6x6 cells of 40x40 with styled buttons
        gridPanel.Children.Clear();
        gridPanel.Columns = 6;
        
        for (int row = 0; row < 6; row++)
        {
          for (int col = 0; col < 6; col++)
          {
            var button = new Button
            {
              Style = (Style)FindResource("IconSlotStyle"),
              Tag = new { path, col, row }
            };

            var img = new System.Windows.Controls.Image
            {
              Width = 40,
              Height = 40,
              Stretch = System.Windows.Media.Stretch.None
            };

            var rect = new Int32Rect(col * 40, row * 40, 40, 40);
            try
            {
              var cropped = new CroppedBitmap(sheet, rect);
              img.Source = cropped;
            }
            catch { }

            button.Content = img;
            button.Click += IconButton_Click;
            gridPanel.Children.Add(button);
          }
        }
      }
      catch (Exception ex)
      {
        MessageBox.Show("Error loading sprite sheet: " + ex.Message);
      }
    }

    private void IconButton_Click(object sender, RoutedEventArgs e)
    {
      if (sender is Button button && button.Tag is object tag)
      {
        dynamic dynamicTag = tag;
        // Generate eqsprite URI: eqsprite://path/to/sheet.tga/col/row
        SelectedValue = $"eqsprite://{dynamicTag.path}/{dynamicTag.col}/{dynamicTag.row}";
        DialogResult = true;
        Close();
      }
    }
  }
}
