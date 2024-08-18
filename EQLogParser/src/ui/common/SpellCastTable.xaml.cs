using Syncfusion.Data;
using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

namespace EQLogParser
{
  public partial class SpellCastTable
  {
    private readonly Dictionary<string, bool> _uniqueNames = [];
    private PlayerStats _raidStats;
    private string _title;

    public SpellCastTable()
    {
      InitializeComponent();
      dataGrid.IsEnabled = false;
      UiElementUtil.SetEnabled(controlPanel.Children, false);
      InitCastTable(dataGrid, titleLabel, selectedCastTypes, selectedSpellRestrictions);
    }

    internal void Init(List<PlayerStats> selectedStats, CombinedStats currentStats)
    {
      _title = currentStats?.ShortTitle ?? "";
      selectedStats?.ForEach(stats => _uniqueNames[stats.OrigName] = true);
      _raidStats = currentStats?.RaidStats;
      Display();
    }

    internal void Display()
    {
      Task.Delay(100).ContinueWith(_ =>
      {
        Dispatcher.InvokeAsync(() =>
        {
          // time column
          dataGrid.Columns.Add(new GridTextColumn
          {
            MappingName = "BeginTime",
            SortMode = DataReflectionMode.Value,
            DisplayBinding = new Binding
            {
              Path = new PropertyPath("BeginTime"),
              Converter = new DateTimeConverter()
            },
            TextAlignment = TextAlignment.Center,
            HeaderText = "Time",
            Width = MainActions.CurrentDateTimeWidth
          });

          // seconds since start
          dataGrid.Columns.Add(new GridTextColumn
          {
            MappingName = "Seconds",
            SortMode = DataReflectionMode.Value,
            TextAlignment = TextAlignment.Right,
            HeaderText = "Sec",
            ColumnSizer = GridLengthUnitType.Auto
          });

          foreach (var name in _uniqueNames.Keys)
          {
            var column = new GridTextColumn
            {
              HeaderText = name,
              MappingName = name,
              CellStyle = DataGridUtil.CreateHighlightForegroundStyle(name, new ReceivedSpellColorConverter()),
              ColumnSizer = GridLengthUnitType.Star
            };

            dataGrid.Columns.Add(column);
          }
        });

        var allSpells = new HashSet<IAction>();
        var spellTimes = new Dictionary<IAction, double>();
        var startTime = SpellCountBuilder.QuerySpells(_raidStats, allSpells, allSpells, spellTimes);
        var playerSpells = new Dictionary<string, List<string>>();
        var max = 0;

        var lastTime = double.NaN;
        var list = new List<IDictionary<string, object>>();
        foreach (var action in allSpells)
        {
          if (spellTimes.TryGetValue(action, out var time))
          {
            if (!double.IsNaN(lastTime) && !time.Equals(lastTime))
            {
              AddRow(list, playerSpells, max, lastTime, startTime);
              playerSpells.Clear();
              max = 0;
            }

            var size = 0;
            if (action is SpellCast { Interrupted: false } cast && !string.IsNullOrEmpty(cast.Caster) && _uniqueNames.ContainsKey(cast.Caster) &&
              PassFilters(cast.SpellData, false))
            {
              size = AddToList(playerSpells, cast.Caster, cast.Spell);
            }

            if (action is ReceivedSpell received && !string.IsNullOrEmpty(received.Receiver) && _uniqueNames.ContainsKey(received.Receiver) &&
              IsValid(received, true, out var replaced) && replaced != null)
            {
              size = AddToList(playerSpells, received.Receiver, "Received " + replaced.NameAbbrv);
            }

            max = Math.Max(max, size);
            lastTime = time;
          }
        }

        if (playerSpells.Count > 0 && max > 0)
        {
          AddRow(list, playerSpells, max, lastTime, startTime);
        }

        Dispatcher.InvokeAsync(() =>
        {
          titleLabel.Content = _title;
          dataGrid.ItemsSource = list;
          dataGrid.IsEnabled = true;
          UiElementUtil.SetEnabled(controlPanel.Children, true);
        });
      });
    }

    private static int AddToList(Dictionary<string, List<string>> dict, string key, string value)
    {
      if (dict.TryGetValue(key, out var list))
      {
        list.Add(value);
      }
      else
      {
        dict[key] = [value];
      }

      return dict[key].Count;
    }

    private bool IsValid(ReceivedSpell spell, bool received, out SpellData replaced)
    {
      var valid = false;
      replaced = spell.SpellData;

      if (!spell.IsWearOff)
      {
        var spellData = spell.SpellData;

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
        row.Add("BeginTime", beginTime);
        row.Add("Seconds", (int)(beginTime - startTime));

        foreach (var player in _uniqueNames.Keys)
        {
          if (playerSpells.TryGetValue(player, out var value) && value.Count > i)
          {
            row.Add(player, value[i]);
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
          UiElementUtil.SetEnabled(controlPanel.Children, true);
          dataGrid.ItemsSource = null;
          dataGrid.Columns.Clear();
          Display();
        }
      }
    }
  }
}
