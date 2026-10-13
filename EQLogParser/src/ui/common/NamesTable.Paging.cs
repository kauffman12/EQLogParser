using Syncfusion.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

// Annotations only; the app builds with Nullable=disable.
#nullable enable annotations

namespace EQLogParser;

public partial class NamesTable
{
  private const int CensusPageSize = NamesTableView.DefaultPageSize;
  private ClassificationReport.Row[] _censusRows = [];
  private long _censusRevision;
  private long _censusWriteStamp;
  private int _censusSessionId;
  private int _censusRequestGeneration;
  private long _viewEpoch;
  private readonly NamesViewBuildLane _viewLane = new();
  private List<NameRow> _ordered = [];
  private Dictionary<string, NameRow> _rowsByName = new(StringComparer.OrdinalIgnoreCase);
  private NamesTableView.Question? _publishedQuestion;
  private long _censusTotal;
  private int _shownCount;
  private int _pageCount;
  private string _query = string.Empty;
  private HashSet<IdentityKind> _typeFilter = [.. IdentityVocabulary.TypeOptions.Select(o => o.Kind)];
  private readonly List<ComboBoxItemDetails> _typeItems = [];
  private DispatcherTimer? _searchDebounce;
  private bool _paneReady;
  private bool _suppressFilterEvents;

  internal sealed record ViewBuild(NamesTableView.BuildStamp Stamp, List<NameRow> Ordered,
    Dictionary<string, NameRow> Index, int Total);

  internal IReadOnlyList<NameRow> RowsForTest => _ordered;
  internal string TitleTextForTest => titleCount.Content as string ?? string.Empty;
  internal string PagerTextForTest => pagerInfo.Text;

  // Standing on a page and walking to another one, without the test reaching into either control.
  internal int PagerPageForTest
  {
    get => idPager.PageIndex;
    set => idPager.MoveToPage(value);
  }

  internal void SelectRowForTest(string name) => namesGrid.SelectedItem = FindRow(name);

  internal int PagerPageCountForTest => idPager.PageCount;

  // These doors use the real build and publish logic inline, without a Task that an STA assertion cannot wait for.
  internal bool AcceptCensus(IReadOnlyList<ClassificationReport.Row> rows)
  {
    StoreCensus(rows, _repaint.Generation, DeriveEngine.Active?.ExplicitRequestGeneration ?? 0);
    return AcceptView(BuildViewForTest());
  }

  internal void PublishCensusForTest(IReadOnlyList<ClassificationReport.Row> rows,
    string column = IdentityRowOrder.DefaultColumn, bool descending = false)
  {
    _sortColumn = column;
    _sortDescending = descending;
    AcceptCensus(rows);
  }

  internal void SetSearchTextForTest(string text)
  {
    _suppressFilterEvents = true;
    nameSearchBox.Text = text;
    _suppressFilterEvents = false;
    _searchDebounce?.Stop();
    _query = NamesTableView.QueryText(text, Resource.NAME_FILTER_TEXT);
    AcceptView(BuildViewForTest());
  }

  internal void SetTypeFilterForTest(params IdentityKind[] kinds)
  {
    foreach (var item in _typeItems)
      item.IsChecked = Enum.TryParse<IdentityKind>(item.Value, out var kind) && kinds.Contains(kind);
    _typeFilter = [.. kinds];
    UiElementUtil.SetComboBoxTitle(typeFilterBox, "Types");
    AcceptView(BuildViewForTest());
  }

  internal ViewBuild BuildViewForTest() => BuildView(_censusRows, SourceStamp());
  internal bool PublishViewForTest(ViewBuild view) => AcceptView(view);

  private void InitializePaging()
  {
    idPager.PageSize = CensusPageSize;
    idPager.PageIndexChanging += PagerPageIndexChanging;
    // The vendor still sorts a page. Give it precisely the whole-index comparison, including tie breaks and Class mapping.
    foreach (var column in ColumnNames)
      namesGrid.SortComparers.Add(new SortComparer
      {
        PropertyName = column,
        Comparer = new PageRowComparer(OrderColumn(column)),
      });
    namesGrid.SortColumnsChanged += SortColumnsChanged;
    namesGrid.SortColumnsChanging += (_, e) => e.CancelScroll = true;

    foreach (var option in IdentityVocabulary.TypeOptions)
      _typeItems.Add(new ComboBoxItemDetails(true, option.Word) { Value = option.Kind.ToString() });
    typeFilterBox.ItemsSource = _typeItems;
    UiElementUtil.SetComboBoxTitle(typeFilterBox, "Types");
    UiElementUtil.SetWatermark(nameSearchBox, Resource.NAME_FILTER_TEXT);
    _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
    _searchDebounce.Tick += (_, _) =>
    {
      _searchDebounce.Stop();
      RequestViewRebuild();
    };
  }

  private NamesTableView.Question CurrentQuestion()
    => NamesTableView.For(_query, _typeFilter, _sortColumn, _sortDescending);

  private NamesTableView.BuildStamp CurrentStamp()
  {
    var session = DeriveEngine.Active;
    return new(_viewEpoch, session?.SessionId ?? 0, _censusRevision, _repaint.Generation, CurrentQuestion(),
      session?.ExplicitRequestGeneration ?? 0);
  }

  private NamesTableView.BuildStamp SourceStamp()
    => CurrentStamp() with
    {
      WriteGeneration = _censusWriteStamp,
      SessionId = _censusSessionId,
      DeriveRequestGeneration = _censusRequestGeneration,
    };

  private bool HasFreshSource() => NamesTableView.IsCurrent(SourceStamp(), CurrentStamp());

  private void StoreCensus(IReadOnlyList<ClassificationReport.Row> rows, long writeStamp, int requestGeneration)
  {
    // Retain the COMPLETE immutable census, not the last question's survivors. A widening filter needs no new derive pass.
    _censusRows = rows.ToArray();
    _censusWriteStamp = writeStamp;
    _censusSessionId = DeriveEngine.Active?.SessionId ?? 0;
    _censusRequestGeneration = requestGeneration;
    _censusRevision++;
  }

  private static ViewBuild BuildView(IReadOnlyList<ClassificationReport.Row> source, NamesTableView.BuildStamp stamp)
  {
    var projected = new List<NameRow>(source.Count);
    foreach (var row in source) projected.Add(RowFrom(row));
    var ordered = NamesTableView.Build(projected, stamp.Question);
    var index = new Dictionary<string, NameRow>(ordered.Count, StringComparer.OrdinalIgnoreCase);
    foreach (var row in ordered) index[row.Name] = row;
    return new ViewBuild(stamp, ordered, index, source.Count);
  }

  private void RequestViewRebuild()
  {
    // An identity write invalidates the source too: no amount of re-filtering can make that old census current.
    if (!HasFreshSource())
    {
      Refresh();
      return;
    }
    if (!_viewLane.TryBegin()) return;
    var stamp = SourceStamp();
    var source = _censusRows;
    _ = Task.Run(() =>
    {
      ViewBuild? built = null;
      try
      {
        // Full-census and question-only asks share this one worker lane; no projection or sort runs on the dispatcher.
        built = BuildView(source, stamp);
      }
      catch (Exception ex)
      {
        Log.Error("Name view build failed", ex);
      }

      Dispatcher.BeginInvoke(() =>
      {
        try
        {
          if (built is not null) AcceptView(built);
        }
        catch (Exception ex)
        {
          Log.Error("Name view publish failed", ex);
        }
        finally
        {
          // A failed build also releases the lane. Newer questions/source revisions pay one coalesced debt.
          if (_viewLane.Complete()) RequestViewRebuild();
        }
      });
    });
  }

  private bool AcceptView(ViewBuild view)
  {
    if (!NamesTableView.IsCurrent(view.Stamp, CurrentStamp())) return false;
    if (Editors.Defer(() => ReleaseView(view))) return false;
    PublishView(view);
    return true;
  }

  private void ReleaseView(ViewBuild view)
  {
    // The exact same door at release: a write, question, census or capture may have changed during the hold.
    if (NamesTableView.IsCurrent(view.Stamp, CurrentStamp())) PublishView(view);
    else if (IsVisible && view.Stamp.Epoch == _viewEpoch)
    {
      if (HasFreshSource()) RequestViewRebuild();
      else Refresh();
    }
  }

  private void PublishView(ViewBuild view)
  {
    var selectedName = namesGrid.SelectedItem is NameRow selected ? selected.Name : null;
    var selectedIndex = selectedName is null ? -1 : view.Ordered.FindIndex(row =>
      string.Equals(row.Name, selectedName, StringComparison.OrdinalIgnoreCase));
    var questionMoved = view.Stamp.Question != _publishedQuestion;

    // A NEW QUESTION may follow the selection (the reader asked for that name); an ordinary census may not — it clamps only.
    var decision = NamesTableView.DecidePage(view.Ordered.Count, CensusPageSize, idPager.PageIndex, selectedIndex, questionMoved);

    _ordered = view.Ordered;
    _rowsByName = view.Index;
    _publishedQuestion = view.Stamp.Question;
    if (_recalcName is not null) FindRow(_recalcName)?.Recalculating = true;

    // On-demand mode never assigns Source. ResetCache preserves the current page, so evict that page explicitly too.
    if (idPager.PagedSource is PagedCollectionView paged)
    {
      paged.ResetCache();
      paged.ResetCacheForPage(paged.PageIndex);
      paged.MaxItemsCount = decision.PageCount * CensusPageSize;
    }
    idPager.PageCount = decision.PageCount;
    if (decision.PageCount > 0) idPager.MoveToPage(decision.PageIndex); // reload even when the page number is unchanged
    else if (idPager.PagedSource is PagedCollectionView empty) empty.MoveToPage(0);

    namesGrid.SelectedItem = selectedName is not null ? FindRow(selectedName) : null;
    // JumpToSelection is true only on a question change that moved the pager, so an ordinary census neither moves the page
    // nor scrolls within it: selection comes back, attention does not.
    if (decision.JumpToSelection) ShowRowAt(selectedIndex % CensusPageSize);

    _censusTotal = view.Total;
    _shownCount = view.Ordered.Count;
    _pageCount = decision.PageCount;
    UpdateCountText(decision.PageIndex);
  }

  /*
   * Both numbers come off the same arithmetic (NamesTableView owns it), and both are re-stamped on every page move: the
   * count names the RANGE the reader is looking at, so it would lie within seconds if only PublishView refreshed it.
   */
  private void UpdateCountText(int pageIndex)
  {
    var narrowed = _query.Length > 0 || _typeFilter.Count < IdentityVocabulary.TypeOptions.Length;
    titleCount.Content = NamesTableView.CountText(_censusTotal, _shownCount, CensusPageSize, pageIndex, narrowed,
      Resource.IDENTITIES);
    pagerInfo.Text = NamesTableView.PageText(_pageCount, pageIndex);
  }

  // The arrows moved the pager by themselves: the range and the page number are the pager's answer, not the build's.
  private void PagerPageIndexChanged(object sender, EventArgs e) => UpdateCountText(idPager.PageIndex);

  private void PagerPageIndexChanging(object? sender, Syncfusion.UI.Xaml.Controls.DataPager.PageIndexChangingEventArgs e)
  {
    EndAllCellEdits();
    // Closing an editor can release a newer filtered index, so clamp the gesture against that index, not the old page count.
    var decision = NamesTableView.DecidePage(_ordered.Count, CensusPageSize, e.NewPageIndex, -1, false);
    if (decision.PageCount == 0) e.Cancel = true;
    else e.NewPageIndex = decision.PageIndex;
  }

  private void PagerOnDemandLoading(object sender, Syncfusion.UI.Xaml.Controls.DataPager.OnDemandLoadingEventArgs e)
  {
    if (sender is not Syncfusion.UI.Xaml.Controls.DataPager.SfDataPager pager) return;
    var start = Math.Max(0, e.StartIndex);
    var count = Math.Max(0, Math.Min(e.PageSize, _ordered.Count - start));
    var items = new List<NameRow>(count);
    for (var i = 0; i < count; i++) items.Add(_ordered[start + i]);
    pager.LoadDynamicItems(start, items);
  }

  private void AbandonView()
  {
    _viewEpoch++;
    _viewLane.Abandon();
    _searchDebounce?.Stop();
  }

  private void ResetPaging()
  {
    _suppressFilterEvents = true;
    _query = string.Empty;
    UiElementUtil.RestoreWatermark(nameSearchBox, Resource.NAME_FILTER_TEXT);
    _suppressFilterEvents = false;
    foreach (var item in _typeItems) item.IsChecked = true;
    _typeFilter = [.. IdentityVocabulary.TypeOptions.Select(o => o.Kind)];
    UiElementUtil.SetComboBoxTitle(typeFilterBox, "Types");
    StoreCensus([], _repaint.Generation, DeriveEngine.Active?.ExplicitRequestGeneration ?? 0);
    PublishView(BuildView(_censusRows, SourceStamp()));
  }

  private void NameSearchBoxTextChanged(object? sender, TextChangedEventArgs e)
  {
    if (!_paneReady || _suppressFilterEvents) return;
    var query = NamesTableView.QueryText(nameSearchBox.Text, Resource.NAME_FILTER_TEXT);
    if (query == _query) return;
    // Update the ticket immediately; debounce only the cost, not stale-answer rejection.
    _query = query;
    _searchDebounce?.Stop();
    _searchDebounce?.Start();
  }

  private void NameSearchBoxPreviewKeyDown(object sender, KeyEventArgs e)
  {
    if (!_paneReady || e.Key != Key.Escape) return;
    e.Handled = true;
    nameSearchBox.Text = string.Empty;
    _query = string.Empty;
    _searchDebounce?.Stop();
    RequestViewRebuild();
    namesGrid.Focus();
  }

  private void TypeFilterDropDownClosed(object sender, EventArgs e)
  {
    if (!_paneReady) return;
    var kinds = new HashSet<IdentityKind>();
    foreach (var item in _typeItems)
      if (item.IsChecked && Enum.TryParse<IdentityKind>(item.Value, out var kind)) kinds.Add(kind);
    // SelectedText has no notifier: reselect through the shared helper even if only a checkbox changed.
    UiElementUtil.SetComboBoxTitle(typeFilterBox, "Types");
    if (kinds.SetEquals(_typeFilter)) return;
    _typeFilter = kinds;
    RequestViewRebuild();
  }

  private void SortColumnsChanged(object sender, Syncfusion.UI.Xaml.Grid.GridSortColumnsChangedEventArgs e)
  {
    if (!_paneReady) return;
    var descriptor = namesGrid.SortColumnDescriptions.FirstOrDefault();
    _sortColumn = OrderColumn(descriptor?.ColumnName ?? IdentityRowOrder.DefaultColumn);
    _sortDescending = descriptor?.SortDirection == System.ComponentModel.ListSortDirection.Descending;
    RequestViewRebuild();
  }

  private static string OrderColumn(string column)
    => column == "PlayerClass" ? IdentityRowOrder.ClassColumn : column;

  private sealed class PageRowComparer(string column) : IComparer<object>
  {
    private readonly Comparison<NameRow> _order = IdentityRowOrder.For<NameRow>(column);
    public int Compare(object? x, object? y) => x is NameRow a && y is NameRow b ? _order(a, b) : 0;
  }

  private NameRow? FindRow(string name) => _rowsByName.GetValueOrDefault(name);

  private void ShowRowAt(int pageLocalIndex)
  {
    if (namesGrid.View is null) return;
    var firstRecord = 0;
    for (var i = 0; i < 8; i++)
      if (Syncfusion.UI.Xaml.Grid.GridIndexResolver.ResolveToRecordIndex(namesGrid, i) == 0)
      {
        firstRecord = i;
        break;
      }
    namesGrid.ScrollInView(new Syncfusion.UI.Xaml.ScrollAxis.RowColumnIndex(firstRecord + pageLocalIndex, 0));
  }
}
