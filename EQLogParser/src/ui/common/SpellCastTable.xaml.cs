using Syncfusion.UI.Xaml.Grid;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for SpellCastTable.xaml
  /// </summary>
  public partial class SpellCastTable : UserControl, IDisposable
  {
    private readonly List<string> CastTypes = new List<string>() { "Cast And Received", "Cast Spells", "Received Spells" };
    private readonly List<string> SpellTypes = new List<string>() { "Any Type", "Beneficial", "Detrimental" };
    private readonly Dictionary<string, bool> UniqueNames = new Dictionary<string, bool>();
    private PlayerStats RaidStats;
    private int CurrentCastType = 0;
    private int CurrentSpellType = 0;
    private bool CurrentShowSelfOnly = false;

    public SpellCastTable()
    {
      InitializeComponent();
    }

    internal void Init(List<PlayerStats> selectedStats, CombinedStats currentStats)
    {
      titleLabel.Content = currentStats?.ShortTitle ?? "";
      selectedStats?.ForEach(stats => UniqueNames[stats.OrigName] = true);
      RaidStats = currentStats?.RaidStats;
      castTypes.ItemsSource = CastTypes;
      castTypes.SelectedIndex = 0;
      spellTypes.ItemsSource = SpellTypes;
      spellTypes.SelectedIndex = 0;

      (Application.Current.MainWindow as MainWindow).EventsThemeChanged += EventsThemeChanged;
      Display();
    }

    internal void Display()
    {
      showSelfOnly.IsEnabled = UniqueNames.ContainsKey(ConfigUtil.PlayerName);

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
        if ((CurrentCastType == 0 || CurrentCastType == 1) && action is SpellCast)
        {
          if (action is SpellCast cast && !cast.Interrupted && IsValid(cast, UniqueNames, cast.Caster, out _))
          {
            size = AddToList(playerSpells, cast.Caster, cast.Spell);
          }
        }
        else if ((CurrentCastType == 0 || CurrentCastType == 2) && action is ReceivedSpell)
        {
          SpellData replaced = null;
          if (action is ReceivedSpell received && IsValid(received, UniqueNames, received.Receiver, out replaced))
          {
            if (replaced != null)
            {
              size = AddToList(playerSpells, received.Receiver, "Received " + replaced.NameAbbrv);
            }
          }
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

    private void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(dataGrid, titleLabel.Content.ToString());
    private void CreateImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(dataGrid, titleLabel);
    private void CheckedOptionsChanged(object sender, RoutedEventArgs e) => OptionsChanged();
    private void OptionsChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => OptionsChanged();

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

    private void EventsThemeChanged(object sender, string e)
    {
      // toggle styles to get them to re-render
      foreach (var column in dataGrid.Columns)
      {
        var style = column.CellStyle;
        column.CellStyle = null;
        column.CellStyle = style;
      }
    }

    private bool IsValid(ReceivedSpell spell, Dictionary<string, bool> unique, string player, out SpellData replaced)
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
          valid = spellData.Proc == 0 && (CurrentShowSelfOnly || (spell is SpellCast || !string.IsNullOrEmpty(spellData.LandsOnOther)));
          valid = valid && (CurrentSpellType == 0 || CurrentSpellType == 1 && spellData.IsBeneficial || CurrentSpellType == 2 && !spellData.IsBeneficial);
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

    private void OptionsChanged()
    {
      if (dataGrid?.View != null)
      {
        for (int i = dataGrid.Columns.Count - 1; i > 1; i--)
        {
          dataGrid.Columns.RemoveAt(i);
        }

        CurrentCastType = castTypes.SelectedIndex;
        CurrentSpellType = spellTypes.SelectedIndex;
        CurrentShowSelfOnly = showSelfOnly.IsChecked.Value;
        Display();
      }
    }

    #region IDisposable Support
    private bool disposedValue = false; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        (Application.Current.MainWindow as MainWindow).EventsThemeChanged -= EventsThemeChanged;
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
