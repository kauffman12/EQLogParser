using Syncfusion.UI.Xaml.Grid;
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
      dataGrid.ItemsSource = Records;
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

      (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete += LogLoadingComplete;
      (Application.Current.MainWindow as MainWindow).GetFightTable().EventsSelectionChange += SelectionChange;

      // default these columns to descending
      string[] desc = new string[] { "Avg", "Max", "Total", "Hits" };
      dataGrid.SortColumnsChanging += (object s, GridSortColumnsChangingEventArgs e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      dataGrid.SortColumnsChanged += (object s, GridSortColumnsChangedEventArgs e) => DataGridUtil.SortColumnsChanged(s, e, desc);
      Load();
    }

    private void LogLoadingComplete(object sender, bool e) => Load();

    private void MenuItemRefresh(object sender, RoutedEventArgs e) => Load();

    private void SelectionChange(object sender, System.Collections.IList e)
    {
      if (fightOption.SelectedIndex != 0)
      {
        Load();
      }
    }

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
          if (!isPlayerOnly || PlayerManager.Instance.IsVerifiedPlayer(kv.Value.Caster) || PlayerManager.Instance.IsMerc(kv.Value.Caster))
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
          if (!isPlayerOnly || PlayerManager.Instance.IsVerifiedPlayer(kv.Value.Caster) || PlayerManager.Instance.IsMerc(kv.Value.Caster))
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

      foreach (ref var stats in playerDoTTotals.Values.ToArray().AsSpan())
      {
        AddRow(stats, Labels.DOT);
      }

      foreach (ref var stats in playerDDTotals.Values.ToArray().AsSpan())
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

      dataGrid.SortColumnDescriptions.Clear();
      dataGrid.SortColumnDescriptions.Add(new SortColumnDescription { ColumnName = "Avg", SortDirection = ListSortDirection.Descending });
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
      row["Empty"] = "";
      Records.Add(row);
    }

    private void OptionsChanged(object sender, EventArgs e)
    {
      if (dataGrid != null && dataGrid.View != null)
      {
        string type = typeList.SelectedIndex > 0 ? typeList.SelectedItem as string : null;
        string spell = spellList.SelectedIndex > 0 ? spellList.SelectedItem as string : null;
        string player = playerList.SelectedIndex > 0 ? playerList.SelectedItem as string : null;
        bool isPlayerOnly = showPlayers.IsChecked.Value;

        if (sender == fightOption)
        {
          Load();
        }

        dataGrid.View.Filter = new Predicate<object>(item =>
        {
          bool pass = false;
          if (item is IDictionary<string, object> dict)
          {
            pass = !isPlayerOnly || PlayerManager.Instance.IsVerifiedPlayer(dict["Caster"] as string) || PlayerManager.Instance.IsMerc(dict["Caster"] as string);
            pass = pass && (type == null || type.Equals(dict["Type"])) && (spell == null || spell.Equals(dict["Spell"])) && (player == null || player.Equals(dict["Caster"]));
          }
          return pass;
        });

        dataGrid.View.RefreshFilter();
      }
    }

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete -= LogLoadingComplete;
        (Application.Current.MainWindow as MainWindow).GetFightTable().EventsSelectionChange -= SelectionChange;
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
