using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace EQLogParser
{
  public partial class SpellDamageStatsViewer : IDocumentContent
  {
    private readonly ObservableCollection<string> _players = [];
    private readonly ObservableCollection<string> _spells = [];
    private readonly ObservableCollection<string> _types = [];
    public ObservableCollection<SpellDamageRow> SpellDamageData { get; set; }
    private bool _currentShowPlayers = true;
    private string _currentPlayer;
    private string _currentSpell;
    private string _currentType;
    private bool _ready;

    public SpellDamageStatsViewer()
    {
      InitializeComponent();
      SpellDamageData = [];
      typeList.ItemsSource = _types;
      spellList.ItemsSource = _spells;
      playerList.ItemsSource = _players;
      _types.Add("All Types");
      _types.Add(Labels.Dd);
      _types.Add(Labels.Dot);
      _types.Add(Labels.Proc);
      typeList.SelectedIndex = 0;
      DataContext = this;

      // default these columns to descending
      var desc = new[] { "Avg", "Max", "Total", "Hits" };
      dataGrid.SortColumnsChanging += (s, e) => DataGridUtil.SortColumnsChanging(s, e, desc);
      dataGrid.SortColumnsChanged += (s, e) => DataGridUtil.SortColumnsChanged(s, e, desc);
      MainActions.EventsThemeChanged += EventsThemeChanged;
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(dataGrid, titleLabel.Content.ToString());
    private async void CreateImageClick(object sender, RoutedEventArgs e) => await DataGridUtil.CreateImageAsync(dataGrid, titleLabel);
    private void LogLoadingComplete(string _) => Load();
    private void RefreshClick(object sender, RoutedEventArgs e) => Load();
    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);
    private void UpdateTitle() => titleLabel.Content = SpellDamageData.Count == 0 ? "No Spell Data Found" : SpellDamageData.Count + " Spell Entries Found";

    private void SelectionChange(List<Fight> _)
    {
      if (fightOption?.SelectedIndex != 0)
      {
        Load();
      }
    }

    private void Load()
    {
      var selectedSpell = spellList.SelectedItem as string;
      var selectedPlayer = playerList.SelectedItem as string;
      var isPlayerOnly = showPlayers.IsChecked == true;

      _spells.Clear();
      _spells.Add("All Spells");
      _players.Clear();
      _players.Add("All Casters");

      var playerDdTotals = new Dictionary<string, SpellDamageStats>();
      var playerDoTTotals = new Dictionary<string, SpellDamageStats>();
      var playerProcTotals = new Dictionary<string, SpellDamageStats>();
      var uniqueSpells = new Dictionary<string, byte>();
      var uniquePlayers = new Dictionary<string, byte>();
      var fights = MainActions.GetFights(fightOption.SelectedIndex != 0);

      foreach (var fight in fights)
      {
        foreach (var kv in fight.DdDamage)
        {
          if (!isPlayerOnly || PlayerManager.Instance.IsVerifiedPlayer(kv.Value.Caster) || PlayerManager.Instance.IsMerc(kv.Value.Caster))
          {
            if (!playerDdTotals.TryGetValue(kv.Key, out var ddStats))
            {
              ddStats = new SpellDamageStats { Caster = kv.Value.Caster, Spell = kv.Value.Spell };
              playerDdTotals[kv.Key] = ddStats;
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

      var list = new List<SpellDamageRow>();
      foreach (var stats in playerDoTTotals.Values)
      {
        AddRow(list, stats, Labels.Dot);
      }

      foreach (var stats in playerDdTotals.Values)
      {
        AddRow(list, stats, Labels.Dd);
      }

      foreach (var stats in playerProcTotals.Values)
      {
        AddRow(list, stats, Labels.Proc);
      }

      foreach (var key in uniqueSpells.Keys.OrderBy(k => k, StringComparer.Create(new CultureInfo("en-US"), true)))
      {
        _spells.Add(key);
      }

      foreach (var key in uniquePlayers.Keys.OrderBy(k => k, StringComparer.Create(new CultureInfo("en-US"), true)))
      {
        _players.Add(key);
      }

      spellList.SelectedIndex = _spells.IndexOf(selectedSpell) is var s and > -1 ? s : 0;
      playerList.SelectedIndex = _players.IndexOf(selectedPlayer) is var p and > -1 ? p : 0;
      UiUtil.UpdateObservable(list, SpellDamageData);

      if (dataGrid.View != null)
      {
        dataGrid.View.Filter = item =>
        {
          var pass = false;
          if (item is SpellDamageRow row)
          {
            pass = !_currentShowPlayers || PlayerManager.Instance.IsVerifiedPlayer(row.Caster) ||
                   PlayerManager.Instance.IsMerc(row.Caster);
            pass = pass && (_currentType == null || _currentType.Equals(row.Type)) && (_currentSpell == null ||
              _currentSpell.Equals(row.Spell)) && (_currentPlayer == null || _currentPlayer.Equals(row.Caster));
          }
          return pass;
        };

        dataGrid.View.Refresh();
      }

      UpdateTitle();
    }

    private void Refresh()
    {
      dataGrid?.View?.RefreshFilter();
      UpdateTitle();
    }

    private void OptionsChanged(object sender, EventArgs e)
    {
      if (dataGrid is { View: not null })
      {
        _currentType = typeList.SelectedIndex > 0 ? typeList.SelectedItem as string : null;
        _currentSpell = spellList.SelectedIndex > 0 ? spellList.SelectedItem as string : null;
        _currentPlayer = playerList.SelectedIndex > 0 ? playerList.SelectedItem as string : null;
        _currentShowPlayers = showPlayers.IsChecked == true;

        if (ReferenceEquals(sender, fightOption))
        {
          Load();
        }
        else
        {
          Refresh();
        }
      }
    }

    private static void AddRow(ICollection<SpellDamageRow> list, SpellDamageStats stats, string type)
    {
      var row = new SpellDamageRow
      {
        Caster = stats.Caster,
        Spell = stats.Spell,
        Max = stats.Max,
        Hits = stats.Count,
        Avg = stats.Total / stats.Count,
        Total = stats.Total,
        Type = type
      };

      list.Add(row);
    }

    private void ContentLoaded(object sender, RoutedEventArgs e)
    {
      if (VisualParent != null && !_ready)
      {
        MainActions.EventsFightSelectionChanged += SelectionChange;
        MainActions.EventsLogLoadingComplete += LogLoadingComplete;
        Load();
        _ready = true;
      }
    }

    public void HideContent()
    {
      MainActions.EventsFightSelectionChanged -= SelectionChange;
      MainActions.EventsLogLoadingComplete -= LogLoadingComplete;
      SpellDamageData.Clear();
      _ready = false;
    }

    private void AutoGeneratingColumn(object sender, AutoGeneratingColumnArgs e)
    {
      var mapping = e.Column.MappingName;
      if (mapping == "Spell")
      {
        e.Column.Width = MainActions.CurrentSpellWidth;
      }
      else if (mapping is "Avg" or "Max" or "Total" or "Hits")
      {
        var column = new GridNumericColumn
        {
          MappingName = mapping,
          NumberDecimalDigits = 0,
          NumberGroupSizes = [3],
          TextAlignment = TextAlignment.Right
        };

        if (mapping == "Avg")
        {
          column.HeaderText = "Av Hit";
        }
        else if (mapping == "Max")
        {
          column.HeaderText = "Max Hit";
        }
        else if (mapping == "Hits")
        {
          column.HeaderText = "# Hits";
        }

        column.Width = DataGridUtil.CalculateMinGridHeaderWidth(e.Column.HeaderText);
        e.Column = column;
      }
      else
      {
        e.Column.Width = DataGridUtil.CalculateMinGridHeaderWidth(e.Column.HeaderText);
      }
    }
  }

  public class SpellDamageRow
  {
    public string Spell { get; set; }
    public string Caster { get; set; }
    public string Type { get; set; }
    public ulong Avg { get; set; }
    public uint Max { get; set; }
    public ulong Total { get; set; }
    public uint Hits { get; set; }
  }
}
