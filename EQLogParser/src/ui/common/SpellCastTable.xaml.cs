using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Threading.Tasks;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for SpellCastTable.xaml
  /// </summary>
  public partial class SpellCastTable : CastTable
  {
    private readonly Dictionary<string, bool> UniqueNames = new();
    private PlayerStats RaidStats;
    private string Title;

    public SpellCastTable()
    {
      InitializeComponent();
      dataGrid.IsEnabled = false;
      UIElementUtil.SetEnabled(controlPanel.Children, false);
      InitCastTable(dataGrid, titleLabel, selectedCastTypes, selectedSpellRestrictions);
    }

    internal void Init(List<PlayerStats> selectedStats, CombinedStats currentStats)
    {
      Title = currentStats?.ShortTitle ?? "";
      selectedStats?.ForEach(stats => UniqueNames[stats.OrigName] = true);
      RaidStats = currentStats?.RaidStats;
      Display();
    }

    internal void Display()
    {
      Task.Delay(100).ContinueWith(task =>
      {
        Dispatcher.InvokeAsync(() =>
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
        });

        var allSpells = new HashSet<TimedAction>();
        var startTime = SpellCountBuilder.QuerySpellBlocks(RaidStats, allSpells);
        var playerSpells = new Dictionary<string, List<string>>();
        var max = 0;

        var lastTime = double.NaN;
        var list = new List<IDictionary<string, object>>();
        foreach (var action in allSpells.OrderBy(action => action.BeginTime).ThenBy(action => (action is ReceivedSpell) ? 1 : -1))
        {
          if (!double.IsNaN(lastTime) && action.BeginTime != lastTime)
          {
            AddRow(list, playerSpells, max, lastTime, startTime);
            playerSpells.Clear();
            max = 0;
          }

          var size = 0;
          if (action is SpellCast cast && !cast.Interrupted && IsValid(cast, UniqueNames, cast.Caster, false, out _))
          {
            size = AddToList(playerSpells, cast.Caster, cast.Spell);
          }

          if (action is ReceivedSpell received && IsValid(received, UniqueNames, received.Receiver, true, out var replaced) && replaced != null)
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

        Dispatcher.InvokeAsync(() =>
        {
          titleLabel.Content = Title;
          dataGrid.ItemsSource = list;
          dataGrid.IsEnabled = true;
          UIElementUtil.SetEnabled(controlPanel.Children, true);
        });
      });
    }

    private int AddToList(Dictionary<string, List<string>> dict, string key, string value)
    {
      if (dict.TryGetValue(key, out var list))
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
      var valid = false;
      replaced = spell.SpellData;

      if (!string.IsNullOrEmpty(player) && unique.ContainsKey(player))
      {
        var spellData = spell.SpellData ?? null;

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
      for (var i = 0; i < max; i++)
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
        if (UpdateSelectedCastTypes(selectedCastTypes) || UpdateSelectedRestrictions(selectedSpellRestrictions))
        {
          titleLabel.Content = "Loading...";
          dataGrid.IsEnabled = true;
          UIElementUtil.SetEnabled(controlPanel.Children, true);
          dataGrid.ItemsSource = null;

          for (var i = dataGrid.Columns.Count - 1; i > 1; i--)
          {
            dataGrid.Columns.RemoveAt(i);
          }

          Display();
        }
      }
    }
  }
}
