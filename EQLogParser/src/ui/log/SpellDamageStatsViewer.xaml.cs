using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for SpellDamageStatsViewer.xaml
  /// </summary>
  public partial class SpellDamageStatsViewer : UserControl, IDisposable
  {
    private readonly object LockObject = new object();
    private readonly ObservableCollection<IDictionary<string, object>> Records = new ObservableCollection<IDictionary<string, object>>();
    private readonly ObservableCollection<string> Players = new ObservableCollection<string>();
    private readonly ObservableCollection<string> Spells = new ObservableCollection<string>();
    private readonly ObservableCollection<string> Types = new ObservableCollection<string>();
    private const string NODATA = "No Spell Damage Data Found";

    public SpellDamageStatsViewer()
    {
      InitializeComponent();
      dataGrid.ItemsSource = CollectionViewSource.GetDefaultView(Records);
      BindingOperations.EnableCollectionSynchronization(Records, LockObject);
      BindingOperations.EnableCollectionSynchronization(Players, LockObject);
      BindingOperations.EnableCollectionSynchronization(Spells, LockObject);
      BindingOperations.EnableCollectionSynchronization(Types, LockObject);
      typeList.ItemsSource = Types;
      spellList.ItemsSource = Spells;
      playerList.ItemsSource = Players;
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
      var uniqueSpells = new Dictionary<string, byte>();
      var uniquePlayers = new Dictionary<string, byte>();

      string selectedSpell = spellList.SelectedItem as string;
      string selectedPlayer = playerList.SelectedItem as string;
      bool isPlayerOnly = showPlayers.IsChecked.Value;

      Records.Clear();
      Spells.Clear();
      Spells.Add("All Spells");
      Players.Clear();
      Players.Add("All Casters");

      foreach (var stats in DataManager.Instance.GetSpellDoTStats())
      {
        if (!isPlayerOnly || PlayerManager.Instance.IsVerifiedPlayer(stats.Caster))
        {
          AddRow(stats, Labels.DOT);
          uniqueSpells[stats.Spell] = 1;
          uniquePlayers[stats.Caster] = 1;
        }
      }

      foreach (var stats in DataManager.Instance.GetSpellDDStats())
      {
        if (!isPlayerOnly || PlayerManager.Instance.IsVerifiedPlayer(stats.Caster))
        {
          AddRow(stats, Labels.DD);
          uniqueSpells[stats.Spell] = 1;
          uniquePlayers[stats.Caster] = 1;
        }
      }

      foreach (var key in uniqueSpells.Keys.OrderBy(k => k, StringComparer.Create(new CultureInfo("en-US"), true)))
      {
        Spells.Add(key);
      }

      foreach (var key in uniquePlayers.Keys.OrderBy(k => k, StringComparer.Create(new CultureInfo("en-US"), true)))
      {
        Players.Add(key);
      }

      spellList.SelectedIndex = (Spells.IndexOf(selectedSpell) is int s && s > -1) ? s : 0;
      playerList.SelectedIndex = (Players.IndexOf(selectedPlayer) is int p && p > -1) ? p : 0;

      if (dataGrid.ItemsSource is ListCollectionView view)
      {
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription("Avg", ListSortDirection.Descending));
      }

      titleLabel.Content = Records.Count == 0 ? NODATA : "Spell Damage Stats for " + uniqueSpells.Count + " Unique Spells";
    }

    private void AddRow(DataManager.SpellDamageStats stats, string type)
    {
      var row = new ExpandoObject() as IDictionary<string, object>;
      row["Caster"] = stats.Caster;
      row["Spell"] = stats.Spell;
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
        string type = typeList.SelectedIndex > 0 ? typeList.SelectedItem as string : null;
        string spell = spellList.SelectedIndex > 0 ? spellList.SelectedItem as string : null;
        string player = playerList.SelectedIndex > 0 ? playerList.SelectedItem as string : null;
        bool isPlayerOnly = showPlayers.IsChecked.Value;

        if (sender == showPlayers)
        {
          Load();
        }

        view.Filter = new Predicate<object>(item =>
        {
          bool pass = false;
          if (item is IDictionary<string, object> dict)
          {
            pass = !isPlayerOnly || PlayerManager.Instance.IsVerifiedPlayer(dict["Caster"] as string);
            pass = pass && (type == null || type.Equals(dict["Type"])) && (spell == null || spell.Equals(dict["Spell"])) && (player == null || player.Equals(dict["Caster"]));
          }
          return pass;
        });
      }
    }

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        if (disposing)
        {
          // TODO: dispose managed state (managed objects).
          Records.Clear();
        }

        (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete -= SpellDamageStatsViewer_EventsLogLoadingComplete;
        disposedValue = true;
      }
    }

    // This code added to correctly implement the disposable pattern.
    public void Dispose()
    {
      // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
      Dispose(true);
      // TODO: uncomment the following line if the finalizer is overridden above.
      GC.SuppressFinalize(this);
    }
    #endregion
  }
}
