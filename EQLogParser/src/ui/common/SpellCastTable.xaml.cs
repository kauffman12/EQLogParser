using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for SpellCastTable.xaml
  /// </summary>
  public partial class SpellCastTable : CastTable
  {
    private readonly Dictionary<string, bool> UniqueNames = new Dictionary<string, bool>();
    private PlayerStats RaidStats;

    public SpellCastTable()
    {
      InitializeComponent();
      InitCastTable(dataGrid, titleLabel, selectedCastTypes, selectedSpellRestrictions);
    }

    internal void Init(List<PlayerStats> selectedStats, CombinedStats currentStats)
    {
      titleLabel.Content = currentStats?.ShortTitle ?? "";
      selectedStats?.ForEach(stats => UniqueNames[stats.OrigName] = true);
      RaidStats = currentStats?.RaidStats;
      Display();
    }

    internal void Display()
    {
      foreach (var name in UniqueNames.Keys)
      {
        var column = new GridTextColumn
        {
          HeaderText = name,
          MappingName = name,
          CellStyle = DataGridUtil.CreateHighlightForegroundStyle(name, new ReceivedSpellColorConverter())
        };

        dataGrid.Columns.Add(column);
      }

      var allSpells = new HashSet<TimedAction>();
      var startTime = SpellCountBuilder.QuerySpellBlocks(RaidStats, allSpells);
      var playerSpells = new Dictionary<string, List<string>>();
      int max = 0;

      double lastTime = double.NaN;
      var list = new List<IDictionary<string, object>>();
      foreach (var action in allSpells.OrderBy(action => action.BeginTime).ThenBy(action => (action is ReceivedSpell) ? 1 : -1))
      {
        if (!double.IsNaN(lastTime) && action.BeginTime != lastTime)
        {
          AddRow(list, playerSpells, max, lastTime, startTime);
          playerSpells.Clear();
          max = 0;
        }

        int size = 0;
        if (action is SpellCast cast && !cast.Interrupted && IsValid(cast, UniqueNames, cast.Caster, false, out _))
        {
          size = AddToList(playerSpells, cast.Caster, cast.Spell);
        }

        SpellData replaced = null;
        if (action is ReceivedSpell received && IsValid(received, UniqueNames, received.Receiver, true, out replaced) && replaced != null)
        {
          size = AddToList(playerSpells, received.Receiver, "Received " + replaced.NameAbbrv);
        }

        max = Math.Max(max, size);
        lastTime = action.BeginTime;
      }

      if (playerSpells.Count > 0 && max > 0)
      {
        AddRow(list, playerSpells, max, lastTime, startTime);
      }

      dataGrid.ItemsSource = list;
    }

    private int AddToList(Dictionary<string, List<string>> dict, string key, string value)
    {
      if (dict.TryGetValue(key, out List<string> list))
      {
        list.Add(value);
      }
      else
      {
        dict[key] = new List<string> { value };
      }

      return dict[key].Count;
    }

    private bool IsValid(ReceivedSpell spell, Dictionary<string, bool> unique, string player, bool received, out SpellData replaced)
    {
      bool valid = false;
      replaced = spell.SpellData;

      if (!string.IsNullOrEmpty(player) && unique.ContainsKey(player))
      {
        SpellData spellData = spell.SpellData ?? null;

        if (spellData == null && spell.Ambiguity.Count > 0 && DataManager.ResolveSpellAmbiguity(spell, out replaced))
        {
          spellData = replaced;
        }

        if (spellData != null)
        {
          valid = PassFilters(spellData, received);
        }
      }

      return valid;
    }

    private void AddRow(List<IDictionary<string, object>> list, Dictionary<string, List<string>> playerSpells, int max, double beginTime, double startTime)
    {
      for (int i = 0; i < max; i++)
      {
        var row = new ExpandoObject() as IDictionary<string, object>;
        row.Add("Time", beginTime);
        row.Add("Seconds", (int)(beginTime - startTime));

        foreach (var player in UniqueNames.Keys)
        {
          if (playerSpells.ContainsKey(player) && playerSpells[player].Count > i)
          {
            row.Add(player, playerSpells[player][i]);
          }
          else
          {
            row.Add(player, "");
          }
        }

        list.Add(row);
      }
    }

    private void CastTypesChanged(object sender, EventArgs e)
    {
      if (dataGrid?.View != null && selectedCastTypes?.Items != null)
      {
        for (int i = dataGrid.Columns.Count - 1; i > 1; i--)
        {
          dataGrid.Columns.RemoveAt(i);
        }

        UpdateSelectedCastTypes(selectedCastTypes);
        UpdateSelectedRestrictions(selectedSpellRestrictions);
        Display();
      }
    }
  }
}
