using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for SpellDamageStatsViewer.xaml
  /// </summary>
  public partial class SpellDamageStatsViewer : UserControl, IDisposable
  {
    private readonly ObservableCollection<string> Players = new ObservableCollection<string>();
    private readonly ObservableCollection<string> Spells = new ObservableCollection<string>();
    private readonly ObservableCollection<string> Types = new ObservableCollection<string>();
    private bool CurrentShowPlayers = true;
    private string CurrentPlayer = null;
    private string CurrentSpell = null;
    private string CurrentType = null;

    public SpellDamageStatsViewer()
    {
      InitializeComponent();
      typeList.ItemsSource = Types;
      spellList.ItemsSource = Spells;
      playerList.ItemsSource = Players;
      Types.Add("All Types");
      Types.Add(Labels.DD);
      Types.Add(Labels.DOT);
      Types.Add(Labels.PROC);
      typeList.SelectedIndex = 0;

      (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete += LogLoadingComplete;
      (Application.Current.MainWindow as MainWindow).GetFightTable().EventsSelectionChange += SelectionChange;

      // default these columns to descending
      var desc = new string[] { "Avg", "Max", "Total", "Hits" };
      dataGrid.SortColumnsChanging += (object s, GridSortColumnsChangingEventArgs e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      dataGrid.SortColumnsChanged += (object s, GridSortColumnsChangedEventArgs e) => DataGridUtil.SortColumnsChanged(s, e, desc);

      DataGridUtil.UpdateTableMargin(dataGrid);
      (Application.Current.MainWindow as MainWindow).EventsThemeChanged += EventsThemeChanged;
      Load();
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(dataGrid, titleLabel.Content.ToString());
    private void CreateImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(dataGrid, titleLabel);
    private void LogLoadingComplete(string _) => Load();
    private void RefreshClick(object sender, RoutedEventArgs e) => Load();
    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);

    private void SelectionChange(object sender, System.Collections.IList e)
    {
      if (fightOption.SelectedIndex != 0)
      {
        Load();
      }
    }

    private void Load()
    {
      var selectedSpell = spellList.SelectedItem as string;
      var selectedPlayer = playerList.SelectedItem as string;
      var isPlayerOnly = showPlayers.IsChecked.Value;

      Spells.Clear();
      Spells.Add("All Spells");
      Players.Clear();
      Players.Add("All Casters");

      var playerDDTotals = new Dictionary<string, SpellDamageStats>();
      var playerDoTTotals = new Dictionary<string, SpellDamageStats>();
      var playerProcTotals = new Dictionary<string, SpellDamageStats>();
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
            if (!playerDDTotals.TryGetValue(kv.Key, out var ddStats))
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
            if (!playerDoTTotals.TryGetValue(kv.Key, out var dotStats))
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

        foreach (var kv in fight.ProcDamage)
        {
          if (!isPlayerOnly || PlayerManager.Instance.IsVerifiedPlayer(kv.Value.Caster) || PlayerManager.Instance.IsMerc(kv.Value.Caster))
          {
            if (!playerProcTotals.TryGetValue(kv.Key, out var procStats))
            {
              procStats = new SpellDamageStats { Caster = kv.Value.Caster, Spell = kv.Value.Spell };
              playerProcTotals[kv.Key] = procStats;
              uniqueSpells[kv.Value.Spell] = 1;
              uniquePlayers[kv.Value.Caster] = 1;
            }

            procStats.Max = Math.Max(procStats.Max, kv.Value.Max);
            procStats.Total += kv.Value.Total;
            procStats.Count += kv.Value.Count;
          }
        }
      }

      var list = new List<IDictionary<string, object>>();
      foreach (ref var stats in playerDoTTotals.Values.ToArray().AsSpan())
      {
        AddRow(list, stats, Labels.DOT);
      }

      foreach (ref var stats in playerDDTotals.Values.ToArray().AsSpan())
      {
        AddRow(list, stats, Labels.DD);
      }

      foreach (ref var stats in playerProcTotals.Values.ToArray().AsSpan())
      {
        AddRow(list, stats, Labels.PROC);
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
      dataGrid.ItemsSource = list;
    }

    private void AddRow(List<IDictionary<string, object>> list, SpellDamageStats stats, string type)
    {
      var row = new ExpandoObject() as IDictionary<string, object>;
      row["Caster"] = stats.Caster;
      row["Spell"] = stats.Spell;
      row["Max"] = stats.Max;
      row["Hits"] = stats.Count;
      row["Avg"] = stats.Total / stats.Count;
      row["Total"] = stats.Total;
      row["Type"] = type;
      list.Add(row);
    }

    private void ItemsSourceChanged(object sender, GridItemsSourceChangedEventArgs e)
    {
      if (dataGrid.View != null)
      {
        dataGrid.View.Filter = (item) =>
        {
          var pass = false;
          if (item is IDictionary<string, object> dict)
          {
            pass = !CurrentShowPlayers || PlayerManager.Instance.IsVerifiedPlayer(dict["Caster"] as string) ||
              PlayerManager.Instance.IsMerc(dict["Caster"] as string);
            pass = pass && (CurrentType == null || CurrentType.Equals(dict["Type"])) && (CurrentSpell == null ||
              CurrentSpell.Equals(dict["Spell"])) && (CurrentPlayer == null || CurrentPlayer.Equals(dict["Caster"]));
          }
          return pass;
        };

        UpdateTitle();
      }
    }

    private void UpdateTitle()
    {
      dataGrid?.View?.RefreshFilter();
      var count = (dataGrid?.View != null) ? dataGrid.View.Records.Count : 0;
      titleLabel.Content = count == 0 ? "No Spell Data Found" : count + " Spell Entries Found";
    }

    private void OptionsChanged(object sender, EventArgs e)
    {
      if (dataGrid != null && dataGrid.View != null)
      {
        CurrentType = typeList.SelectedIndex > 0 ? typeList.SelectedItem as string : null;
        CurrentSpell = spellList.SelectedIndex > 0 ? spellList.SelectedItem as string : null;
        CurrentPlayer = playerList.SelectedIndex > 0 ? playerList.SelectedItem as string : null;
        CurrentShowPlayers = showPlayers.IsChecked.Value;

        if (sender == fightOption)
        {
          Load();
        }
        else
        {
          UpdateTitle();
        }
      }
    }

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        (Application.Current.MainWindow as MainWindow).EventsThemeChanged -= EventsThemeChanged;
        (Application.Current.MainWindow as MainWindow).EventsLogLoadingComplete -= LogLoadingComplete;
        (Application.Current.MainWindow as MainWindow).GetFightTable().EventsSelectionChange -= SelectionChange;
        dataGrid.Dispose();
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
