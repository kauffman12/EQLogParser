using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DeathLogViewer.xaml
  /// </summary>
  public partial class DeathLogViewer : UserControl
  {
    private PlayerStats CurrentPlayer;
    private readonly List<DeathEvent> Deaths = new List<DeathEvent>();

    public DeathLogViewer()
    {
      InitializeComponent();
      (Application.Current.MainWindow as MainWindow).EventsThemeChanged += EventsThemeChanged;
    }

    internal void Init(CombinedStats combined, PlayerStats playerStats)
    {
      var i = 1;
      var list = new List<string>();
      foreach (var death in combined.RaidStats.Deaths.Where(death => death.Killed == playerStats.OrigName))
      {
        list.Add("Death #" + i++);
        Deaths.Add(death);
      }

      titleLabel.Content = combined.ShortTitle;
      deathList.ItemsSource = list;
      deathList.SelectedIndex = 0;
      CurrentPlayer = playerStats;

      DataGridUtil.UpdateTableMargin(dataGrid);
      Display();
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(dataGrid, titleLabel.Content.ToString());
    private void CreateImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(dataGrid, titleLabel);
    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);

    private void Display()
    {
      var death = Deaths[deathList.SelectedIndex];
      var end = death.BeginTime;
      var start = end - 20;
      var allFights = (Application.Current.MainWindow as MainWindow).GetFightTable()?.GetFights();
      var allHeals = DataManager.Instance.GetHealsDuring(start, end + 1);
      var allSpells = DataManager.Instance.GetReceivedSpellsDuring(start, end + 1);
      var damages = new Dictionary<double, List<string>>();
      var heals = new Dictionary<double, List<string>>();
      var spells = new Dictionary<double, List<string>>();
      var times = new Dictionary<double, bool>();

      if (allFights != null)
      {
        foreach (var fight in allFights)
        {
          if (fight.BeginTankingTime <= end && fight.LastTankingTime >= start)
          {
            fight.TankingBlocks.ForEach(block =>
            {
              if (block.BeginTime >= start && block.BeginTime <= end)
              {
                block.Actions.Cast<DamageRecord>().ToList().ForEach(damage =>
                {
                  if (damage.Defender == CurrentPlayer.OrigName && damage.Total > 0)
                  {
                    var value = damage.Attacker + " attacks " + CurrentPlayer.OrigName + " for " + damage.Total + " (" + damage.SubType + ")";
                    if (damages.TryGetValue(block.BeginTime, out var values))
                    {
                      values.Add(value);
                    }
                    else
                    {
                      damages[block.BeginTime] = new List<string> { value };
                    }

                    times[block.BeginTime] = true;
                  }
                });
              }
            });
          }
        }
      }

      allHeals.ForEach(block =>
      {
        block.Actions.Cast<HealRecord>().ToList().ForEach(heal =>
        {
          if (heal.Healed == CurrentPlayer.OrigName && heal.Total > 0)
          {
            var value = heal.Healer + " heals " + CurrentPlayer.OrigName + " for " + heal.Total + " (" + heal.SubType + ")";
            if (heals.TryGetValue(block.BeginTime, out var values))
            {
              values.Add(value);
            }
            else
            {
              heals[block.BeginTime] = new List<string> { value };
            }

            times[block.BeginTime] = true;
          }
        });
      });

      foreach (ref var block in allSpells.ToArray().AsSpan())
      {
        foreach (var spell in block.Actions.Cast<ReceivedSpell>())
        {
          if (spell.Receiver == CurrentPlayer.OrigName && spell.SpellData != null)
          {
            var message = string.IsNullOrEmpty(spell.SpellData.LandsOnYou) ? spell.SpellData.LandsOnOther : spell.SpellData.LandsOnYou;
            if (!string.IsNullOrEmpty(message) && spell.Ambiguity.Count <= 1)
            {
              message += " (" + spell.SpellData.NameAbbrv + ")";
            }

            if (spells.TryGetValue(block.BeginTime, out var values))
            {
              values.Add(message);
            }
            else
            {
              spells[block.BeginTime] = new List<string> { message };
            }

            times[block.BeginTime] = true;
          }
        }
      }

      var list = new List<dynamic>();
      foreach (var time in times.Keys.OrderBy(x => x))
      {
        var sub = new List<dynamic>();
        if (damages.TryGetValue(time, out var damageList))
        {
          damageList.ForEach(damage =>
          {
            var row = new ExpandoObject() as dynamic;
            row.Time = time;
            row.Damage = damage;
            sub.Add(row);
          });
        }

        if (heals.TryGetValue(time, out var healList))
        {
          var i = 0;
          healList.ForEach(heal =>
          {
            if (sub.Count > i)
            {
              sub[i].Healing = heal;
            }
            else
            {
              var row = new ExpandoObject() as dynamic;
              row.Time = time;
              row.Healing = heal;
              sub.Add(row);
            }
          });
        }

        if (spells.TryGetValue(time, out var spellList))
        {
          var i = 0;
          spellList.ForEach(spell =>
          {
            if (sub.Count > i)
            {
              sub[i].Spell = spell;
            }
            else
            {
              var row = new ExpandoObject() as dynamic;
              row.Time = time;
              row.Spell = spell;
              sub.Add(row);
            }
          });
        }

        list.AddRange(sub);
      }

      var combined = death.Message;
      if (!string.IsNullOrEmpty(death.Previous))
      {
        var found = false;
        var split = death.Previous.Split(' ');
        foreach (var value in split)
        {
          if (int.TryParse(value, out var result))
          {
            found = false;
            break;
          }

          if (CurrentPlayer.OrigName == value)
          {
            found = true;
          }
        }

        if (found)
        {
          combined += " (Previous? " + death.Previous + ")";
        }
      }

      AppendMessage(list, combined, end);
      dataGrid.ItemsSource = list;
    }

    private void AppendMessage(List<dynamic> list, string message, double end)
    {
      if (!string.IsNullOrEmpty(message))
      {
        if (list.Count > 0 && list[list.Count - 1].Time == end && string.IsNullOrEmpty(list[list.Count - 1].Damage))
        {
          list[list.Count - 1].Damage = message;
        }
        else
        {
          var row = new ExpandoObject() as dynamic;
          row.Time = end;
          row.Damage = message;
          list.Add(row);
        }
      }
    }

    private void OptionsChanged(object sender, EventArgs e)
    {
      if (CurrentPlayer != null)
      {
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
        dataGrid?.Dispose();
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
