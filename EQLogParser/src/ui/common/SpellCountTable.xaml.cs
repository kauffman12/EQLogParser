using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for SpellCountTable.xaml
  /// </summary>
  public partial class SpellCountTable : UserControl
  {
    private static readonly log4net.ILog LOG = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private readonly object LockObject = new object();
    private bool Running = false;

    private const string EMPTYICON = "#00ffffff";
    private const string ACTIVEICON = "#5191c1";

    private List<string> PlayerList;
    private SpellCountData TheSpellCounts;
    private ObservableCollection<IDictionary<string, object>> SpellRowsView = new ObservableCollection<IDictionary<string, object>>();
    private DictionaryAddHelper<string, uint> AddHelper = new DictionaryAddHelper<string, uint>();
    private Dictionary<string, byte> HiddenSpells = new Dictionary<string, byte>();
    private List<string> CastTypes = new List<string>() { "Cast And Received", "Cast Spells", "Received Spells" };
    private List<string> CountTypes = new List<string>() { "Show By Count", "Show By Percent" };
    private List<string> MinFreqs = new List<string>() { "Any Freq", "Freq > 1", "Freq > 2", "Freq > 3", "Freq > 4" };
    private List<string> SpellTypes = new List<string>() { "Any Type", "Beneficial", "Detrimental" };
    private int CurrentCastType = 0;
    private int CurrentCountType = 0;
    private int CurrentMinFreqCount = 0;
    private int CurrentSpellType = 0;
    private bool CurrentShowSelfOnly = false;
    private bool CurrentShowProcs = false;

    public SpellCountTable(string title)
    {
      InitializeComponent();
      titleLabel.Content = title;

      dataGrid.Sorting += (s, e2) =>
      {
        if (!string.IsNullOrEmpty(e2.Column.Header as string))
        {
          e2.Column.SortDirection = e2.Column.SortDirection ?? ListSortDirection.Ascending;
        }
      };

      dataGrid.ItemsSource = SpellRowsView;
      castTypes.ItemsSource = CastTypes;
      castTypes.SelectedIndex = 0;
      countTypes.ItemsSource = CountTypes;
      countTypes.SelectedIndex = 0;
      minFreqList.ItemsSource = MinFreqs;
      minFreqList.SelectedIndex = 0;
      spellTypes.ItemsSource = SpellTypes;
      spellTypes.SelectedIndex = 0;
    }

    internal void ShowSpells(List<PlayerStats> selectedStats, CombinedStats currentStats)
    {
      var childStats = currentStats?.Children;
      var raidStats = currentStats?.RaidStats;

      if (selectedStats != null && raidStats != null)
      {
        PlayerList = selectedStats.Select(stats => stats.OrigName).ToList();
        TheSpellCounts = SpellCountBuilder.GetSpellCounts(PlayerList, raidStats);

        if (TheSpellCounts.PlayerCastCounts.Count > 0)
        {
          selectAll.IsEnabled = true;
        }

        Display();
      }
    }

    private void Options_SelectionChanged(object sender, SelectionChangedEventArgs e) => OptionsChanged(true);
    private void CheckedOptionsChanged(object sender, RoutedEventArgs e) => OptionsChanged(true);
    private void SelectAllClick(object sender, RoutedEventArgs e) => DataGridUtils.SelectAll(sender as FrameworkElement);
    private void UnselectAllClick(object sender, RoutedEventArgs e) => DataGridUtils.UnselectAll(sender as FrameworkElement);

    private void Display()
    {
      lock(LockObject)
      {
        if (Running == false)
        {
          Running = true;
          Helpers.SetBusy(true);
          showSelfOnly.IsEnabled = showProcs.IsEnabled = spellTypes.IsEnabled = castTypes.IsEnabled = countTypes.IsEnabled = minFreqList.IsEnabled = false;

          Task.Delay(50).ContinueWith(task =>
          {
            try
            {
              Dispatcher.InvokeAsync(() =>
              {
                var column = new DataGridTextColumn()
                {
                  Header = "",
                  Binding = new Binding("Spell")
                };

                var columnStyle = new Style(typeof(TextBlock));
                columnStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding("Spell") { Converter = new ReceivedSpellColorConverter() }));
                column.ElementStyle = columnStyle;
                dataGrid.Columns.Add(column);
              });

              Dictionary<string, Dictionary<string, uint>> filteredPlayerMap = new Dictionary<string, Dictionary<string, uint>>();
              Dictionary<string, uint> totalCountMap = new Dictionary<string, uint>();
              Dictionary<string, uint> uniqueSpellsMap = new Dictionary<string, uint>();

              uint totalCasts = 0;
              PlayerList.ForEach(player =>
              {
                filteredPlayerMap[player] = new Dictionary<string, uint>();

                if ((CurrentCastType == 0 || CurrentCastType == 1) && TheSpellCounts.PlayerCastCounts.ContainsKey(player))
                {
                  foreach (string id in TheSpellCounts.PlayerCastCounts[player].Keys)
                  {
                    totalCasts = UpdateMaps(id, player, TheSpellCounts.PlayerCastCounts[player][id], TheSpellCounts.MaxCastCounts, totalCountMap, uniqueSpellsMap, filteredPlayerMap, false, totalCasts);
                  }
                }

                if ((CurrentCastType == 0 || CurrentCastType == 2) && TheSpellCounts.PlayerReceivedCounts.ContainsKey(player))
                {
                  foreach (string id in TheSpellCounts.PlayerReceivedCounts[player].Keys)
                  {
                    totalCasts = UpdateMaps(id, player, TheSpellCounts.PlayerReceivedCounts[player][id], TheSpellCounts.MaxReceivedCounts, totalCountMap, uniqueSpellsMap, filteredPlayerMap, true, totalCasts);
                  }
                }
              });

              List<string> sortedPlayers = totalCountMap.Keys.OrderByDescending(key => totalCountMap[key]).ToList();
              List<string> sortedSpellList = uniqueSpellsMap.Keys.OrderByDescending(key => uniqueSpellsMap[key]).ToList();

              int colCount = 0;
              foreach (string name in sortedPlayers)
              {
                string colBinding = name; // dont use colCount directly since it will change during Dispatch
                double total = totalCountMap.ContainsKey(name) ? totalCountMap[name] : 0;
                string header = name + " = " + ((CurrentCountType == 0) ? total.ToString(CultureInfo.CurrentCulture) : Math.Round(total / totalCasts * 100, 2).ToString(CultureInfo.CurrentCulture));

                Dispatcher.InvokeAsync(() =>
                {
                  DataGridTextColumn col = new DataGridTextColumn() { Header = header, Binding = new Binding(colBinding) };
                  col.CellStyle = Application.Current.Resources["SpellGridDataCellStyle"] as Style;
                  col.HeaderStyle = Application.Current.Resources["BrightCenterGridHeaderStyle"] as Style;
                  dataGrid.Columns.Add(col);
                });

                Thread.Sleep(5);
                colCount++;
              }

              string totalHeader = CurrentCountType == 0 ? "Totals = " + totalCasts : "Totals = 100";
              Dispatcher.InvokeAsync(() =>
              {
                DataGridTextColumn col = new DataGridTextColumn() { Header = totalHeader, Binding = new Binding("totalColumn") };
                col.CellStyle = Application.Current.Resources["SpellGridDataCellStyle"] as Style;
                col.HeaderStyle = Application.Current.Resources["BrightCenterGridHeaderStyle"] as Style;
                dataGrid.Columns.Add(col);
              });

              int existingIndex = 0;
              foreach (var spell in sortedSpellList)
              {
                var row = ((SpellRowsView.Count > existingIndex) ? SpellRowsView[existingIndex] : new ExpandoObject()) as IDictionary<string, object>;

                row.Add("Spell", spell);
                //row.IsReceived = spell.StartsWith("Received", StringComparison.Ordinal);
                row.Add("IconColor", ACTIVEICON);

                int i;
                double[] values = new double[sortedPlayers.Count + 1];
                for (i = 0; i < sortedPlayers.Count; i++)
                {
                  if (filteredPlayerMap.ContainsKey(sortedPlayers[i]))
                  {
                    if (filteredPlayerMap[sortedPlayers[i]].ContainsKey(spell))
                    {
                      if (CurrentCountType == 0)
                      {
                        row.Add(sortedPlayers[i], filteredPlayerMap[sortedPlayers[i]][spell]);
                      }
                      else
                      {
                        row.Add(sortedPlayers[i], Math.Round((double)filteredPlayerMap[sortedPlayers[i]][spell] / totalCountMap[sortedPlayers[i]] * 100, 2));
                      }
                    }
                    else
                    {
                      row.Add(sortedPlayers[i], CurrentCountType == 0 ? 0 : 0.0);
                    }
                  }
                }

                row.Add("totalColumn", CurrentCountType == 0 ? uniqueSpellsMap[spell] : Math.Round((double)uniqueSpellsMap[spell] / totalCasts * 100, 2));

                if ((SpellRowsView.Count <= existingIndex))
                {
                  Dispatcher.InvokeAsync(() => SpellRowsView.Add(row));
                }

                existingIndex++;
                Thread.Sleep(5);
              }
            }
            catch (Exception ex)
            {
              LOG.Error(ex);
              throw;
            }
            finally
            {
              Helpers.SetBusy(false);
              Dispatcher.InvokeAsync(() =>
              {
                showProcs.IsEnabled = spellTypes.IsEnabled = castTypes.IsEnabled = countTypes.IsEnabled = minFreqList.IsEnabled = true;
                showSelfOnly.IsEnabled = PlayerList.Contains(ConfigUtil.PlayerName);
                exportClick.IsEnabled = copyOptions.IsEnabled = removeRowClick.IsEnabled = SpellRowsView.Count > 0;

                lock (LockObject)
                {
                  Running = false;
                }
              });
            }
          }, TaskScheduler.Default);
        }
      }
    }

    private uint UpdateMaps(string id, string player, uint playerCount, Dictionary<string, uint> maxCounts, Dictionary<string, uint> totalCountMap,
      Dictionary<string, uint> uniqueSpellsMap, Dictionary<string, Dictionary<string, uint>> filteredPlayerMap, bool received, uint totalCasts)
    {
      var spellData = TheSpellCounts.UniqueSpells[id];

      var spellTypeCheck = CurrentSpellType == 0 || (CurrentSpellType == 1 && spellData.IsBeneficial) || (CurrentSpellType == 2 && !spellData.IsBeneficial);
      var selfOnlyCheck = !received || CurrentShowSelfOnly == true || !string.IsNullOrEmpty(spellData.LandsOnOther);
      var procCheck = received || CurrentShowProcs == true || !spellData.IsProc;

      if (spellTypeCheck && selfOnlyCheck && procCheck)
      {
        string name = spellData.NameAbbrv;

        if (received)
        {
          name = "Received " + name;
        }

        if (!HiddenSpells.ContainsKey(name) && maxCounts[id] > CurrentMinFreqCount)
        {
          AddHelper.Add(totalCountMap, player, playerCount);
          AddHelper.Add(uniqueSpellsMap, name, playerCount);
          AddHelper.Add(filteredPlayerMap[player], name, playerCount);
          totalCasts += playerCount;
        }
      }

      return totalCasts;
    }

    private void OptionsChanged(bool clear = false)
    {
      if (SpellRowsView.Count > 0)
      {
        if (clear)
        {
          SpellRowsView.Clear();
        }

        for (int i = dataGrid.Columns.Count - 1; i > 0; i--)
        {
          dataGrid.Columns.RemoveAt(i);
        }

        CurrentCastType = castTypes.SelectedIndex;
        CurrentCountType = countTypes.SelectedIndex;
        CurrentMinFreqCount = minFreqList.SelectedIndex;
        CurrentSpellType = spellTypes.SelectedIndex;
        CurrentShowSelfOnly = showSelfOnly.IsChecked.Value;
        CurrentShowProcs = showProcs.IsChecked.Value;
        Display();
      }
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      // adds a delay where a drag-select doesn't keep sending events

      DataGrid callingDataGrid = sender as DataGrid;
      selectAll.IsEnabled = (callingDataGrid.SelectedItems.Count < callingDataGrid.Items.Count) && callingDataGrid.Items.Count > 0;
      unselectAll.IsEnabled = callingDataGrid.SelectedItems.Count > 0 && callingDataGrid.Items.Count > 0;
    }

    private void CreateImageClick(object sender, RoutedEventArgs e)
    {
      // lame workaround to toggle scrollbar to fix UI
      dataGrid.IsEnabled = false;
      dataGrid.SelectedItems.Clear();
      dataGrid.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
      dataGrid.HorizontalScrollBarVisibility = ScrollBarVisibility.Visible;

      Task.Delay(50).ContinueWith((bleh) =>
      {
        Dispatcher.InvokeAsync(() =>
        {
          dataGrid.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
          dataGrid.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
          SpellRowsView.ToList().ForEach(spr => spr["IconColor"] = EMPTYICON);
          dataGrid.Items.Refresh();

          Task.Delay(50).ContinueWith((bleh2) => Dispatcher.InvokeAsync(() => CreateImage()), TaskScheduler.Default);
        });
      }, TaskScheduler.Default);
    }

    private void CreateImage()
    {
      try
      {
        const int labelHeight = 16;
        const int margin = 4;

        var details = DataGridUtils.GetRowDetails (dataGrid);
        var totalRowHeight = details.Item1 * details.Item2 + details.Item1 + 2; // add extra for header row and a little for the bottom border
        var totalColumnWidth = dataGrid.Columns.ToList().Sum(column => column.ActualWidth);
        var realTableHeight = dataGrid.ActualHeight < totalRowHeight ? dataGrid.ActualHeight : totalRowHeight;
        var realColumnWidth = dataGrid.ActualWidth < totalColumnWidth ? dataGrid.ActualWidth : totalColumnWidth;

        var dpiScale = VisualTreeHelper.GetDpi(dataGrid);
        RenderTargetBitmap rtb = new RenderTargetBitmap((int)realColumnWidth, (int)(realTableHeight + labelHeight + margin), dpiScale.PixelsPerInchX, dpiScale.PixelsPerInchY, PixelFormats.Pbgra32);

        DrawingVisual dv = new DrawingVisual();
        using (DrawingContext ctx = dv.RenderOpen())
        {
          var brush = new VisualBrush(titleLabel);
          ctx.DrawRectangle(brush, null, new Rect(new Point(4, margin / 2), new Size(titleLabel.ActualWidth, labelHeight)));

          brush = new VisualBrush(dataGrid);
          ctx.DrawRectangle(brush, null, new Rect(new Point(0, labelHeight + margin), new Size(dataGrid.ActualWidth, dataGrid.ActualHeight + SystemParameters.HorizontalScrollBarHeight)));
        }

        rtb.Render(dv);
        Clipboard.SetImage(rtb);

        SpellRowsView.ToList().ForEach(spr => spr["IconColor"] = ACTIVEICON);
        dataGrid.Items.Refresh();
      }
#pragma warning disable CA1031 // Do not catch general exception types
      catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
      {
        if (ex is ExternalException || ex is ThreadStateException || ex is ArgumentNullException || ex is NullReferenceException)
        {
          LOG.Error("Could not Copy Image", ex);
        }
      }
      finally
      {
        dataGrid.IsEnabled = true;
      }
    }

    private void ReloadClick(object sender, RoutedEventArgs e)
    {
      HiddenSpells.Clear();
      OptionsChanged(true);
    }

    private void ImportClick(object sender, RoutedEventArgs e)
    {
      try
      {
        // WPF doesn't have its own file chooser so use Win32 Version
        OpenFileDialog dialog = new OpenFileDialog
        {
          // filter to txt files
          DefaultExt = ".scf.gz",
          Filter = "Spell Count File (*.scf.gz) | *.scf.gz"
        };

        // show dialog and read result
        if (dialog.ShowDialog().Value)
        {
          FileInfo gzipFileName = new FileInfo(dialog.FileName);

          GZipStream decompressionStream = new GZipStream(gzipFileName.OpenRead(), CompressionMode.Decompress);
          var reader = new StreamReader(decompressionStream);
          string json = reader?.ReadToEnd();
          reader?.Close();

          var data = JsonConvert.DeserializeObject<SpellCountsSerialized>(json);

          // copy data
          PlayerList = PlayerList.Union(data.PlayerNames).ToList();

          foreach (var player in data.TheSpellData.PlayerCastCounts.Keys)
          {
            TheSpellCounts.PlayerCastCounts[player] = data.TheSpellData.PlayerCastCounts[player];
          }

          foreach (var player in data.TheSpellData.PlayerReceivedCounts.Keys)
          {
            TheSpellCounts.PlayerReceivedCounts[player] = data.TheSpellData.PlayerReceivedCounts[player];
          }

          foreach (var spellId in data.TheSpellData.MaxCastCounts.Keys)
          {
            if (!TheSpellCounts.MaxCastCounts.ContainsKey(spellId) || TheSpellCounts.MaxCastCounts[spellId] < data.TheSpellData.MaxCastCounts[spellId])
            {
              TheSpellCounts.MaxCastCounts[spellId] = data.TheSpellData.MaxCastCounts[spellId];
            }
          }

          foreach (var spellId in data.TheSpellData.MaxReceivedCounts.Keys)
          {
            if (!TheSpellCounts.MaxReceivedCounts.ContainsKey(spellId) || TheSpellCounts.MaxReceivedCounts[spellId] < data.TheSpellData.MaxReceivedCounts[spellId])
            {
              TheSpellCounts.MaxReceivedCounts[spellId] = data.TheSpellData.MaxReceivedCounts[spellId];
            }
          }

          foreach (var spellData in data.TheSpellData.UniqueSpells.Keys)
          {
            if (!TheSpellCounts.UniqueSpells.ContainsKey(spellData))
            {
              TheSpellCounts.UniqueSpells[spellData] = data.TheSpellData.UniqueSpells[spellData];
            }
          }

          OptionsChanged(true);
        }
      }
      catch (IOException ex)
      {
        LOG.Error(ex);
      }
      catch (UnauthorizedAccessException uax)
      {
        LOG.Error(uax);
      }
      catch (SecurityException se)
      {
        LOG.Error(se);
      }
    }

    private void ExportClick(object sender, RoutedEventArgs e)
    {
      try
      {
        var data = new SpellCountsSerialized { TheSpellData = TheSpellCounts };
        data.PlayerNames.AddRange(PlayerList);

        var result = JsonConvert.SerializeObject(data);
        SaveFileDialog saveFileDialog = new SaveFileDialog();
        string filter = "Spell Count File (*.scf.gz)|*.scf.gz";
        saveFileDialog.Filter = filter;
        if (saveFileDialog.ShowDialog().Value)
        {
          FileInfo gzipFileName = new FileInfo(saveFileDialog.FileName);
          FileStream gzipTargetAsStream = gzipFileName.Create();
          GZipStream gzipStream = new GZipStream(gzipTargetAsStream, CompressionMode.Compress);
          var writer = new StreamWriter(gzipStream);
          writer?.Write(result);
          writer?.Close();
        }
      }
      catch (IOException ex)
      {
        LOG.Error(ex);
      }
      catch (UnauthorizedAccessException uax)
      {
        LOG.Error(uax);
      }
      catch (SecurityException se)
      {
        LOG.Error(se);
      }
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e)
    {
      try
      {
        var export = DataGridUtils.BuildExportData(dataGrid);
        string result = TextFormatUtils.BuildCsv(export.Item1, export.Item2, titleLabel.Content as string);
        Clipboard.SetDataObject(result);
      }
      catch (ArgumentNullException ane)
      {
        Clipboard.SetDataObject("EQ Log Parser Error: Failed to create BBCode\r\n");
        LOG.Error(ane);
      }
      catch (ExternalException ex)
      {
        LOG.Error(ex);
      }
    }

    private void CopyBBCodeClick(object sender, RoutedEventArgs e)
    {
      try
      {
        var export = DataGridUtils.BuildExportData(dataGrid);
        string result = TextFormatUtils.BuildBBCodeTable(export.Item1, export.Item2, titleLabel.Content as string);
        Clipboard.SetDataObject(result);
      }
      catch (ArgumentNullException ane)
      {
        Clipboard.SetDataObject("EQ Log Parser Error: Failed to create BBCode\r\n");
        LOG.Error(ane);
      }
      catch (ExternalException ex)
      {
        LOG.Error(ex);
      }
    }

    private void CopyGamparseClick(object sender, RoutedEventArgs e)
    {
      try
      {
        var export = DataGridUtils.BuildExportData(dataGrid);
        string result = TextFormatUtils.BuildGamparseList(export.Item1, export.Item2, titleLabel.Content as string);
        Clipboard.SetDataObject(result);
      }
      catch (ArgumentNullException ane)
      {
        Clipboard.SetDataObject("EQ Log Parser Error: Failed to create BBCode\r\n");
        LOG.Error(ane);
      }
      catch (ExternalException ex)
      {
        LOG.Error(ex);
      }
    }

    private void RemoveSelectedRowsClick(object sender, RoutedEventArgs e)
    {
      // Don't allow if the previous operation hasn't finished
      // this probably needs to be better...
      if (!Running)
      {
        var modified = false;
        while (dataGrid.SelectedItems.Count > 0)
        {
          if (dataGrid.SelectedItem is IDictionary<string, object> spr)
          {
            HiddenSpells[spr["Spell"] as string] = 1;
            SpellRowsView.Remove(spr);
            modified = true;
          }
        }

        if (modified)
        {
          OptionsChanged();
        }
      }
    }

    private void RemoveSpellMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
      var cell = sender as DataGridCell;

      // Don't allow if the previous operation hasn't finished
      // this probably needs to be better...
      if (!Running && cell.DataContext is IDictionary<string, object> spr)
      {
        HiddenSpells[spr["Spell"] as string] = 1;
        SpellRowsView.Remove(spr);
        OptionsChanged();
      }
    }

    private void GridSizeChanged(object sender, SizeChangedEventArgs e)
    {
      var settingsLoc = settingsPanel.PointToScreen(new Point(0, 0));
      var titleLoc = titlePanel.PointToScreen(new Point(0, 0));

      if ((titleLoc.X + titlePanel.ActualWidth) > (settingsLoc.X + 10))
      {
        titlePanel.Visibility = Visibility.Hidden;
      }
      else
      {
        titlePanel.Visibility = Visibility.Visible;
      }
    }
  }
}
