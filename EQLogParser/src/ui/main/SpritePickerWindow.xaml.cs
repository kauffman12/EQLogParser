using log4net;
using Microsoft.WindowsAPICodePack.Dialogs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EQLogParser
{
  public partial class SpritePickerWindow
  {
    public string SelectedValue { get; private set; }

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private List<string> _allSheets = [];
    private int _currentPage;

    internal SpritePickerWindow(string eqUiFolder)
    {
      MainActions.SetCurrentTheme(this);
      InitializeComponent();
      Owner = MainActions.GetOwner();

      if (Directory.Exists(eqUiFolder) || EqUtil.TryGetEqUiFolder(out eqUiFolder))
      {
        LoadDefaultSheets(eqUiFolder);
      }
    }

    private void CancelClick(object sender, RoutedEventArgs e) => Close();

    private void LoadDefaultSheets(string eqUiFolder = "")
    {
      txtFolderPath.Text = eqUiFolder;
      txtFolderPath.FontStyle = FontStyles.Normal;
      var files = Directory.EnumerateFiles(eqUiFolder, "spells*.tga").Concat(Directory.EnumerateFiles(eqUiFolder, "spells*.png"));
      _allSheets = [.. files];

      if (_allSheets.Count > 0)
      {
        // remember last successful folder
        ConfigUtil.SetSetting("EqUiFolder", eqUiFolder);
      }

      gridPanel.Children.Clear();
      // Display items with friendly names instead of full paths
      sheetsList.ItemsSource = _allSheets.Select(f => GetListeItem(f));
      sheetsList.SelectedIndex = _allSheets.Count > 0 ? 0 : -1;

      UpdatePagination();
    }

    private static ListBoxItem GetListeItem(string filePath)
    {
      var listBoxItem = new ListBoxItem();

      // Convert "spells01.tga" to "Spells 01"
      var fileName = Path.GetFileNameWithoutExtension(filePath);
      if (fileName.StartsWith("spells", StringComparison.OrdinalIgnoreCase) && fileName.Length > 6)
      {
        var number = fileName[6..];
        listBoxItem.Content = $"Spells {number.PadLeft(2, '0')}";
      }
      else
      {
        listBoxItem.Content = fileName;
      }

      return listBoxItem;
    }

    private void SheetsListSelectionChanged(object sender, SelectionChangedEventArgs e)
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

    private void PrevPageClick(object sender, RoutedEventArgs e)
    {
      if (_currentPage > 0)
      {
        _currentPage--;
        sheetsList.SelectedIndex = _currentPage;
      }
    }

    private void NextPageClick(object sender, RoutedEventArgs e)
    {
      if (_currentPage < _allSheets.Count - 1)
      {
        _currentPage++;
        sheetsList.SelectedIndex = _currentPage;
      }
    }

    private void UpdatePagination()
    {
      var totalPages = _allSheets.Count;
      var currentPageDisplay = _currentPage + 1;

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

        for (var row = 0; row < 6; row++)
        {
          for (var col = 0; col < 6; col++)
          {
            var button = new Button
            {
              Style = (Style)FindResource("IconSlotStyle"),
              Tag = new { path, col, row }
            };

            var img = new Image
            {
              Stretch = Stretch.None
            };

            var rect = new Int32Rect(col * 40, row * 40, 40, 40);
            try
            {
              var cropped = new CroppedBitmap(sheet, rect);
              var scaled = new TransformedBitmap(cropped, new ScaleTransform(1.45, 1.45));
              img.Source = scaled;
            }
            catch { }

            button.Content = img;
            button.Click += IconButtonClick;
            gridPanel.Children.Add(button);
          }
        }
      }
      catch (Exception ex)
      {
        new MessageWindow("Could not load Sprite Sheet. Check Error Log for Details.", "EQ Icon Picker").ShowDialog();
        Log.Error("Failed to load sprite sheet.", ex);
      }
    }

    private void IconButtonClick(object sender, RoutedEventArgs e)
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

    private void OpenUiFilesClick(object sender, RoutedEventArgs e)
    {
      var dialog = new CommonOpenFileDialog
      {
        IsFolderPicker = true,
        // Set the initial directory
        InitialDirectory = txtFolderPath.Text,
      };

      var handle = new WindowInteropHelper(MainActions.GetOwner()).Handle;
      if (dialog.ShowDialog(handle) == CommonFileDialogResult.Ok)
      {
        LoadDefaultSheets(dialog.FileName);
      }
    }
  }
}
