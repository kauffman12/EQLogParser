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
      (Application.Current.MainWindow as MainWindow).GetFightTable().EventsSelectionChange += SpellDamageStatsViewer_EventsSelectionChange;
      dataGrid.Sorting += CustomSorting;
      Load();
    }

    private void SpellDamageStatsViewer_EventsSelectionChange(object sender, System.Collections.IList e)
    {
      if (fightOption.SelectedIndex != 0)
      {
        Load();
      }
    }

    private void SpellDamageStatsViewer_EventsLogLoadingComplete(object sender, bool e) => Load();

    private void RefreshMouseClick(object sender, MouseButtonEventArgs e) => Load();

    private void Load()
    {
      string selectedSpell = spellList.SelectedItem as string;
      string selectedPlayer = playerList.SelectedItem as string;
      bool isPlayerOnly = showPlayers.IsChecked.Value;

      Records.Clear();
      Spells.Clear();
      Spells.Add("All Spells");
      Players.Clear();
      Players.Add("All Casters");

      var playerDDTotals = new Dictionary<string, SpellDamageStats>();
      var playerDoTTotals = new Dictionary<string, SpellDamageStats>();
      var uniqueSpells = new Dictionary<string, byte>();
      var uniquePlayers = new Dictionary<string, byte>();

      var fights = fightOption.SelectedIndex == 0 ? (Application.Current.MainWindow as MainWindow).GetFightTable()?.GetFights() : 
        (Application.Current.MainWindow as MainWindow).GetFightTable()?.GetSelectedFights();

      foreach (var fight in fights)
      {
        foreach (var kv in fight.DDDamage)
        {
          if (!isPlayerOnly || PlayerManager.Instance.IsVerifiedPlayer(kv.Value.Caster))
          {
            if (!playerDDTotals.TryGetValue(kv.Key, out SpellDamageStats ddStats))
            {
              ddStats = new SpellDamageStats { Caster = kv.Value.Caster, Spell = kv.Value.Spell };
              playerDDTotals[kv.Key] = ddStats;
              uniqueSpells[kv.Value.Spell] = 1;
              uniquePlayers[kv.Value.Caster] = 1;
            }

            ddStats.Max = Math.Max(ddStats.Max, kv.Value.Max);
            ddStats.Total += kv.Value.Total;
            ddStats.Count += kv.Value.Count;
          }
        }

        foreach (var kv in fight.DoTDamage)
        {
          if (!isPlayerOnly || PlayerManager.Instance.IsVerifiedPlayer(kv.Value.Caster))
          {
            if (!playerDoTTotals.TryGetValue(kv.Key, out SpellDamageStats dotStats))
            {
              dotStats = new SpellDamageStats { Caster = kv.Value.Caster, Spell = kv.Value.Spell };
              playerDoTTotals[kv.Key] = dotStats;
              uniqueSpells[kv.Value.Spell] = 1;
              uniquePlayers[kv.Value.Caster] = 1;
            }

            dotStats.Max = Math.Max(dotStats.Max, kv.Value.Max);
            dotStats.Total += kv.Value.Total;
            dotStats.Count += kv.Value.Count;
          }
        }
      }

      foreach (var stats in playerDoTTotals.Values)
      {
        AddRow(stats, Labels.DOT);
      }

      foreach (var stats in playerDDTotals.Values)
      {
        AddRow(stats, Labels.DD);
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

    private void AddRow(SpellDamageStats stats, string type)
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

        if (sender == showPlayers || sender == fightOption)
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
        (Application.Current.MainWindow as MainWindow).GetFightTable().EventsSelectionChange -= SpellDamageStatsViewer_EventsSelectionChange;
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
