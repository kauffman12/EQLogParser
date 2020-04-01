using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for SpellCastTable.xaml
  /// </summary>
  public partial class SpellCastTable : UserControl
  {
    private bool Running = false;
    private readonly object LockObject = new object();
    private ObservableCollection<dynamic> Records = new ObservableCollection<dynamic>();
    private List<string> CastTypes = new List<string>() { "Cast And Received", "Cast Spells", "Received Spells" };
    private List<string> SpellTypes = new List<string>() { "Any Type", "Beneficial", "Detrimental" };
    private Dictionary<string, byte> UniqueNames = new Dictionary<string, byte>();
    private PlayerStats RaidStats;
    private int CurrentCastType = 0;
    private int CurrentSpellType = 0;
    private bool CurrentShowSelfOnly = false;

    public SpellCastTable(string title, List<PlayerStats> selectedStats, CombinedStats currentStats)
    {
      InitializeComponent();
      titleLabel.Content = title;
      selectedStats.ForEach(stats => UniqueNames[stats.OrigName] = 1);
      RaidStats = currentStats.RaidStats;

      dataGrid.ItemsSource = CollectionViewSource.GetDefaultView(Records);
      BindingOperations.EnableCollectionSynchronization(Records, LockObject);

      castTypes.ItemsSource = CastTypes;
      castTypes.SelectedIndex = 0;
      spellTypes.ItemsSource = SpellTypes;
      spellTypes.SelectedIndex = 0;
      Display();
    }

    internal void Display()
    {
      lock(LockObject)
      {
        if (Running == false)
        {
          Running = true;
          Helpers.SetBusy(true);
          showSelfOnly.IsEnabled = castTypes.IsEnabled = spellTypes.IsEnabled = false;

          Task.Delay(50).ContinueWith(task =>
          {
            Dispatcher.InvokeAsync(() =>
            {
              foreach (var name in UniqueNames.Keys)
              {
                var column = new DataGridTextColumn()
                {
                  Header = name,
                  Width = DataGridLength.Auto,
                  Binding = new Binding(name)
               };

                var columnStyle = new Style(typeof(TextBlock));
                columnStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(name) { Converter = new ReceivedSpellColorConverter() }));
                column.ElementStyle = columnStyle;
                dataGrid.Columns.Add(column);
              }
            });

            var allSpells = new List<ActionBlock>();
            for (int i = 0; i < RaidStats.BeginTimes.Count; i++)
            {
              allSpells.AddRange(DataManager.Instance.GetCastsDuring(RaidStats.BeginTimes[i], RaidStats.LastTimes[i]));
              allSpells.AddRange(DataManager.Instance.GetReceivedSpellsDuring(RaidStats.BeginTimes[i], RaidStats.LastTimes[i]));
            }

            allSpells.Sort((a, b) => a.BeginTime.CompareTo(b.BeginTime));

            var playerSpells = new Dictionary<string, List<string>>();
            var helper = new DictionaryListHelper<string, string>();
            int max = 0;

            double currentTime = double.NaN;
            allSpells.ForEach(block =>
            {
              if (block.Actions.Count > 0)
              {
                int size = 0;
                if ((CurrentCastType == 0 || CurrentCastType == 1) && block.Actions[0] is SpellCast)
                {
                  foreach (var cast in block.Actions.Cast<SpellCast>().Where(cast => IsValid(cast.SpellData, UniqueNames, cast.Caster)))
                  {
                    size = helper.AddToList(playerSpells, cast.Caster, cast.Spell);
                  }
                }
                else if ((CurrentCastType == 0 || CurrentCastType == 2) && block.Actions[0] is ReceivedSpell)
                {
                  foreach (var received in block.Actions.Cast<ReceivedSpell>().Where(received => IsValid(received.SpellData, UniqueNames, received.Receiver)))
                  {
                    size = helper.AddToList(playerSpells, received.Receiver, "Received " + received.SpellData.Name);
                  }
                }

                max = Math.Max(max, size);
              }

              if (!double.IsNaN(currentTime) && block.BeginTime != currentTime)
              {
                AddRow(playerSpells, max, block.BeginTime);
                playerSpells.Clear();
                max = 0;
              }

              currentTime = block.BeginTime;
            });

            if (playerSpells.Count > 0 && max > 0)
            {
              AddRow(playerSpells, max, currentTime);
            }

            Helpers.SetBusy(false);
            Dispatcher.InvokeAsync(() =>
            {
              // only enable for current player
              showSelfOnly.IsEnabled = UniqueNames.ContainsKey(ConfigUtil.PlayerName);
              castTypes.IsEnabled = spellTypes.IsEnabled = true;

              lock (LockObject)
              {
                Running = false;
              }
            });
          }, TaskScheduler.Default);
        }
      }
    }

    private bool IsValid(SpellData data, Dictionary<string, byte> uniqueNames, string player)
    {
      bool valid = data == null || (!data.IsProc && !string.IsNullOrEmpty(player) && uniqueNames.ContainsKey(player));
      valid = valid && (CurrentShowSelfOnly ? true : !string.IsNullOrEmpty(data.LandsOnOther));
      valid = valid && (CurrentSpellType == 0 || CurrentSpellType == 1 && data.IsBeneficial || CurrentSpellType == 2 && !data.IsBeneficial);
      return valid;
    }

    private void AddRow(Dictionary<string, List<string>> playerSpells, int max, double beginTime)
    {
      for (int i = 0; i < max; i++)
      {
        var row = new ExpandoObject() as IDictionary<string, object>;
        row.Add("Time", beginTime);

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

        lock (LockObject)
        {
          Records.Add(row);
        }
      }
    }

    private void OptionsChanged()
    {
      if (Records.Count > 0)
      {
        Records.Clear();

        for (int i = dataGrid.Columns.Count - 1; i > 0; i--)
        {
          dataGrid.Columns.RemoveAt(i);
        }

        CurrentCastType = castTypes.SelectedIndex;
        CurrentSpellType = spellTypes.SelectedIndex;
        CurrentShowSelfOnly = showSelfOnly.IsChecked.Value;
        Display();
      }
    }

    private void Options_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      OptionsChanged();
    }

    private void CheckedOptionsChanged(object sender, RoutedEventArgs e)
    {
      OptionsChanged();
    }

    private void LoadingRow(object sender, DataGridRowEventArgs e)
    {
      // set header count
      if (e.Row != null)
      {
        e.Row.Header = (e.Row.GetIndex() + 1).ToString(CultureInfo.CurrentCulture);
      }
    }
  }
}
