using log4net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace EQLogParser
{
  public partial class ColumnChart : IDocumentContent
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private readonly List<ColumnData> _columns = [];
    private readonly DispatcherTimer _refresh;
    private bool _ready;

    public ColumnChart()
    {
      InitializeComponent();
      titleLabel.Content = Labels.NoData;
      Loaded += ContentLoaded;
      _refresh = UiUtil.CreateTimer(RefreshTimerTick, 250, false);
      UpdateYAxisMargin();
      DisplayPage();
    }

    private void RefreshTimerTick(object sender, EventArgs e) => DisplayPage();
    private void EventsClearedActiveData(bool cleared) => Reset();
    private async void CreateImageClick(object sender, RoutedEventArgs e) => await UiElementUtil.CreateImage(Dispatcher, mainGrid, titleLabel);

    private void EventsThemeChanged(string _)
    {
      UpdateYAxisMargin();
      DisplayPage();
    }

    private void ContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
      if (_columns.Count > 0)
      {
        _refresh?.Stop();
        _refresh?.Start();
      }
    }

    private void EventsGenerationStatus(StatsGenerationEvent e)
    {
      Dispatcher.InvokeAsync(async () =>
      {
        switch (e.State)
        {
          case "COMPLETED":
            if (e.CombinedStats != null)
            {
              await LoadDataAsync(e.CombinedStats.StatsList);
              titleLabel.Content = "Players vs Top Performer (Percent of Total Damage)";
            }
            else
            {
              titleLabel.Content = Labels.NoData;
            }
            break;
          case "STARTED":
            titleLabel.Content = "Loading...";
            Reset();
            break;
          case "NONPC":
          case "NODATA":
            titleLabel.Content = Labels.NoData;
            Reset();
            break;
        }
      });
    }

    private void Reset()
    {
      if (_columns.Count > 0)
      {
        _columns.Clear();
        DisplayPage();
      }
    }

    private async Task LoadDataAsync(List<PlayerStats> playerList)
    {
      await Task.Run(async () =>
      {
        long baseTotal = 0;
        string theClass = null;
        List<ColumnData> columns = [];
        foreach (var stats in playerList.OrderBy(stats => stats.ClassName).ThenByDescending(stats => stats.Total))
        {
          var isFirst = false;
          var name = stats.OrigName;
          if (!PlayerManager.Instance.IsVerifiedPlayer(name)) continue;

          if (string.IsNullOrEmpty(theClass) || stats.ClassName != theClass)
          {
            isFirst = true;
            baseTotal = stats.Total;
            theClass = string.IsNullOrEmpty(stats.ClassName) ? "Unknown" : stats.ClassName;
          }

          if (baseTotal > 0)
          {
            columns.Add(new ColumnData
            {
              Y = (int)(stats.Total / (double)baseTotal * 100),
              X = name,
              ClassName = theClass,
              ColorBrush = PlayerManager.Instance.GetClassBrush(theClass),
              IsFirst = isFirst,
              HasPets = stats.Name.Contains(" +Pets")
            });
          }
        }

        await Dispatcher.InvokeAsync(() =>
         {
           _columns.Clear();
           _columns.AddRange(columns);
           DisplayPage();
         });
      });
    }

    private void DisplayPage()
    {
      _refresh.Stop();

      // add elements all at once
      List<UIElement> elements = [];

      // add back x axis
      var xAxis = new Rectangle
      {
        Margin = new Thickness(0, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Top,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Height = 1
      };

      xAxis.SetResourceReference(Rectangle.FillProperty, "ContentForeground");
      Grid.SetRow(xAxis, 2);
      elements.Add(xAxis);

      var fontSize = MainActions.CurrentFontSize;
      var largeFontSize = MainActions.CurrentFontSize + 10;
      var columnWidth = 40.0 + ((MainActions.CurrentFontSize - 10) * 5);
      var columnSpacing = 20 + ((MainActions.CurrentFontSize - 10) * 1);
      // markers are placed center within each row so subtract half the top row height
      var halfRowHeight = yAxisGrid.RowDefinitions[0].ActualHeight / 2;
      var yAxisHeight = yAxisRectangle.ActualHeight - halfRowHeight;
      var startX = 10.0;
      foreach (var column in _columns)
      {
        var columnHeight = yAxisHeight * (column.Y / 100.0);

        // column
        var rect = new Rectangle
        {
          Fill = column.ColorBrush,
          Width = columnWidth,
          Height = columnHeight,
          Margin = new Thickness(startX, 0, 0, 0),
          VerticalAlignment = VerticalAlignment.Bottom,
          HorizontalAlignment = HorizontalAlignment.Left
        };

        Grid.SetRow(rect, 0);
        elements.Add(rect);

        // value label
        var labelBlock = new TextBlock
        {
          Text = $"{column.Y}",
          FontSize = fontSize,
          HorizontalAlignment = HorizontalAlignment.Left,
          VerticalAlignment = VerticalAlignment.Top,
        };

        var lWidth = UiElementUtil.CalculateTextBlockWidth(labelBlock);
        var lOffset = (lWidth - columnWidth) / 2;
        labelBlock.Margin = new Thickness(startX - lOffset, yAxisHeight - columnHeight + halfRowHeight, 0, 0);
        Grid.SetRow(labelBlock, 0);
        elements.Add(labelBlock);

        if (column.IsFirst)
        {
          // class label
          var classBlock = new TextBlock
          {
            Text = column.ClassName,
            FontSize = largeFontSize,
            FontWeight = FontWeights.Bold,
            LayoutTransform = new RotateTransform(-90),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
          };

          var cWidth = UiElementUtil.CalculateTextBlockHeight(classBlock);
          var cOffset = (cWidth - columnWidth) / 2;
          classBlock.Margin = new Thickness(startX - cOffset - 4, yAxisHeight - columnHeight + 20, 0, 0);
          Grid.SetRow(classBlock, 0);
          elements.Add(classBlock);
        }

        // name
        var nameBlock = new TextBlock
        {
          Text = column.X,
          FontSize = fontSize,
          HorizontalAlignment = HorizontalAlignment.Left
        };

        var nWidth = UiElementUtil.CalculateTextBlockWidth(nameBlock);
        var nOffset = (nWidth - columnWidth) / 2;
        nameBlock.Margin = new Thickness(startX - nOffset, 5, 0, 0);
        Grid.SetRow(nameBlock, 2);
        elements.Add(nameBlock);

        if (column.HasPets)
        {
          var petBlock = new TextBlock
          {
            Text = "+Pets",
            FontSize = fontSize,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(startX - nOffset, 25, 0, 0)
          };

          Grid.SetRow(petBlock, 2);
          elements.Add(petBlock);
        }

        startX += columnWidth + columnSpacing;
      }

      // spacer at the end
      var spacer = new Rectangle
      {
        Width = 10,
        Height = 0,
        Margin = new Thickness(startX - 25, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Bottom,
        HorizontalAlignment = HorizontalAlignment.Left
      };

      Grid.SetRow(spacer, 0);
      elements.Add(spacer);

      using (Dispatcher.CurrentDispatcher.DisableProcessing())
      {
        content.Children.Clear();

        foreach (var element in elements)
        {
          content.Children.Add(element);
        }
      }
    }

    private void UpdateYAxisMargin()
    {
      foreach (var child in yAxisGrid.Children)
      {
        if (child is TextBlock textBlock)
        {
          if (Grid.GetRow(textBlock) == 0)
          {
            var right = 8 + ((MainActions.CurrentFontSize - 10) * 2);
            textBlock.Margin = new Thickness(0, -2, right, 0);
          }
          else
          {
            var right = 8 + (MainActions.CurrentFontSize - 10);
            textBlock.Margin = new Thickness(0, -1, right, 0);
          }
        }
      }
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e)
    {
      try
      {
        var header = new List<string> { "Class", "Percent", "Name" };

        var data = new List<List<object>>();
        foreach (var column in CollectionsMarshal.AsSpan(_columns))
        {
          var name = column.HasPets ? $"{column.X} +Pets" : column.X;
          data.Add([column.ClassName, column.Y, name]);
        }

        Clipboard.SetDataObject(TextUtils.BuildCsv(header, data, titleLabel.Content as string));
      }
      catch (ExternalException ex)
      {
        Log.Error(ex);
      }
    }

    private void ContentLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
      if (VisualParent != null && !_ready)
      {
        SizeChanged += ContentSizeChanged;
        MainActions.EventsThemeChanged += EventsThemeChanged;
        DataManager.Instance.EventsClearedActiveData += EventsClearedActiveData;
        DamageStatsManager.Instance.EventsGenerationStatus += EventsGenerationStatus;

        // use existing or generate data
        if (DamageStatsManager.Instance.GetLastStats() is { } stats)
        {
          EventsGenerationStatus(stats);
        }
        else
        {
          Task.Run(() => DamageStatsManager.Instance.RebuildTotalStats(new GenerateStatsOptions()));
        }

        _ready = true;
      }
    }

    public void HideContent()
    {
      SizeChanged -= ContentSizeChanged;
      MainActions.EventsThemeChanged -= EventsThemeChanged;
      DataManager.Instance.EventsClearedActiveData -= EventsClearedActiveData;
      DamageStatsManager.Instance.EventsGenerationStatus -= EventsGenerationStatus;
      _ready = false;
    }

    private class ColumnData
    {
      public string X { get; set; }
      public int Y { get; init; }
      public string ClassName { get; init; }
      public Brush ColorBrush { get; init; }
      public bool IsFirst { get; init; }
      public bool HasPets { get; init; }
    }
  }
}
