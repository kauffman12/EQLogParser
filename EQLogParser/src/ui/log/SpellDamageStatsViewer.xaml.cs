using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Dynamic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for SpellDamageStatsViewer.xaml
  /// </summary>
  public partial class SpellDamageStatsViewer : UserControl
  {
    private readonly object LockObject = new object();
    private readonly ObservableCollection<IDictionary<string, object>> Records = new ObservableCollection<IDictionary<string, object>>();
    private readonly ObservableCollection<string> Types = new ObservableCollection<string>();
    private const string NODATA = "No Spell Damage Data Found";

    public SpellDamageStatsViewer()
    {
      InitializeComponent();
      dataGrid.ItemsSource = CollectionViewSource.GetDefaultView(Records);
      BindingOperations.EnableCollectionSynchronization(Records, LockObject);
      BindingOperations.EnableCollectionSynchronization(Types, LockObject);
      typeList.ItemsSource = Types;
      Types.Add("All Types");
      Types.Add(Labels.DD);
      Types.Add(Labels.DOT);
      typeList.SelectedIndex = 0;

      (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete += SpellDamageStatsViewer_EventsLogLoadingComplete;
      dataGrid.Sorting += CustomSorting;
      Load();
    }

    private void SpellDamageStatsViewer_EventsLogLoadingComplete(object sender, bool e) => Load();

    private void RefreshMouseClick(object sender, MouseButtonEventArgs e) => Load();

    private void Load()
    {
      Records.Clear();
      foreach (var stats in DataManager.Instance.GetSpellDoTStats())
      {
        AddRow(stats, Labels.DOT);
      }

      foreach (var stats in DataManager.Instance.GetSpellDDStats())
      {
        AddRow(stats, Labels.DD);
      }

      if (dataGrid.ItemsSource is ListCollectionView view)
      {
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription("Avg", ListSortDirection.Descending));
      }

      titleLabel.Content = Records.Count == 0 ? NODATA : "Spell Damage Stats for " + Records.Count + " Unique Spells";
    }

    private void AddRow(DataManager.SpellDamageStats stats, string type)
    {
      var row = new ExpandoObject() as IDictionary<string, object>;
      row["Name"] = stats.Name;
      row["Max"] = stats.Max;
      row["Hits"] = stats.Count;
      row["Avg"] = stats.Total / stats.Count;
      row["Total"] = stats.Total;
      row["Type"] = type;

      lock (LockObject)
      {
        Records.Add(row);
      }
    }

    private void LoadingRow(object sender, DataGridRowEventArgs e)
    {
      // set header count
      if (e.Row != null)
      {
        e.Row.Header = (e.Row.GetIndex() + 1).ToString(CultureInfo.CurrentCulture);
      }
    }

    private void CustomSorting(object sender, DataGridSortingEventArgs e)
    {
      if (e.Column.Header != null && e.Column.Header.ToString() != "Name" && dataGrid.ItemsSource != null)
      {
        e.Handled = true;
        var direction = ListSortDirection.Descending;
        if (e.Column.SortDirection != null)
        {
          direction = (e.Column.SortDirection == ListSortDirection.Descending) ? ListSortDirection.Ascending : ListSortDirection.Descending;
        }

        if (dataGrid.ItemsSource is ListCollectionView view)
        {
          view.SortDescriptions.Clear();
          view.SortDescriptions.Add(new SortDescription(((e.Column as DataGridTextColumn).Binding as Binding).Path.Path, direction));
        }

        e.Column.SortDirection = direction;
      }
    }

    private void OptionsChanged(object sender, EventArgs e)
    {
      if (dataGrid != null && Records.Count > 0 && dataGrid.ItemsSource is ListCollectionView view)
      {
        int index = typeList.SelectedIndex;
        string type = typeList.SelectedItem as string;
        view.Filter = new Predicate<object>(item => index == 0 || (((IDictionary<string, object>)item)["Type"] is string test && test == type));
      }
    }
  }
}
