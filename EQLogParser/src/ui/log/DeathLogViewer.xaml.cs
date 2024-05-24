using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for DeathLogViewer.xaml
  /// </summary>
  public partial class DeathLogViewer : IDisposable
  {
    private PlayerStats _currentPlayer;
    private readonly List<DeathEvent> _deaths = [];

    public DeathLogViewer()
    {
      InitializeComponent();
      MainActions.EventsThemeChanged += EventsThemeChanged;
    }

    internal void Init(CombinedStats combined, PlayerStats playerStats)
    {
      var i = 1;
      var list = new List<string>();
      foreach (var death in combined.RaidStats.Deaths.Where(death => death.Record.Killed == playerStats.OrigName))
      {
        list.Add("Death #" + i++);
        _deaths.Add(death);
      }

      titleLabel.Content = combined.ShortTitle;
      deathList.ItemsSource = list;
      deathList.SelectedIndex = 0;
      _currentPlayer = playerStats;

      DataGridUtil.UpdateTableMargin(dataGrid);
      Display();
    }

    private void CopyCsvClick(object sender, RoutedEventArgs e) => DataGridUtil.CopyCsvFromTable(dataGrid, titleLabel.Content.ToString());
    private void CreateImageClick(object sender, RoutedEventArgs e) => DataGridUtil.CreateImage(dataGrid, titleLabel);
    private void EventsThemeChanged(string _) => DataGridUtil.RefreshTableColumns(dataGrid);

    private void Display()
    {
      var death = _deaths[deathList.SelectedIndex];
      var end = death.BeginTime;
      var start = end - 20;
      var allFights = MainActions.GetFights();
      var allHeals = RecordManager.Instance.GetHealsDuring(start, end + 1);
      var allSpells = RecordManager.Instance.GetSpellsDuring(start, end + 1);
      var damages = new Dictionary<double, List<string>>();
      var heals = new Dictionary<double, List<string>>();
      var spells = new Dictionary<double, List<string>>();
      var times = new Dictionary<double, bool>();

      if (allFights != null)
      {
        foreach (var fight in CollectionsMarshal.AsSpan(allFights))
        {
          if (fight.BeginTankingTime <= end && fight.LastTankingTime >= start)
          {
            fight.TankingBlocks.ForEach(block =>
            {
              if (block.BeginTime >= start && block.BeginTime <= end)
              {
                block.Actions.Cast<DamageRecord>().ToList().ForEach(damage =>
                {
                  if (damage.Defender == _currentPlayer.OrigName && damage.Total > 0)
                  {
                    var value = damage.Attacker + " attacks " + _currentPlayer.OrigName + " for " + damage.Total + " (" + damage.SubType + ")";
                    if (damages.TryGetValue(block.BeginTime, out var values))
                    {
                      values.Add(value);
                    }
                    else
                    {
                      damages[block.BeginTime] = [value];
                    }

                    times[block.BeginTime] = true;
                  }
                });
              }
            });
          }
        }
      }

      foreach (var (beginTime, record) in allHeals)
      {
        if (record.Healed == _currentPlayer.OrigName && record.Total > 0)
        {
          var value = record.Healer + " heals " + _currentPlayer.OrigName + " for " + record.Total + " (" + record.SubType + ")";
          if (heals.TryGetValue(beginTime, out var values))
          {
            values.Add(value);
          }
          else
          {
            heals[beginTime] = [value];
          }

          times[beginTime] = true;
        }
      }

      foreach (var (beginTime, action) in allSpells)
      {
        if (action is ReceivedSpell received)
        {
          if (!received.IsWearOff && received.Receiver == _currentPlayer.OrigName && received.SpellData != null)
          {
            var message = string.IsNullOrEmpty(received.SpellData.LandsOnYou) ? received.SpellData.LandsOnOther : received.SpellData.LandsOnYou;
            if (!string.IsNullOrEmpty(message) && received.Ambiguity.Count <= 1)
            {
              message += " (" + received.SpellData.NameAbbrv + ")";
            }

            if (spells.TryGetValue(beginTime, out var values))
            {
              values.Add(message);
            }
            else
            {
              spells[beginTime] = [message];
            }

            times[beginTime] = true;
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
            row.Healing = null;
            row.Spell = null;
            sub.Add(row);
          });
        }

        if (heals.TryGetValue(time, out var healList))
        {
          const int i = 0;
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
              row.Damage = null;
              row.Spell = null;
              sub.Add(row);
            }
          });
        }

        if (spells.TryGetValue(time, out var spellList))
        {
          const int i = 0;
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
              row.Damage = null;
              row.Healing = null;
              sub.Add(row);
            }
          });
        }

        list.AddRange(sub);
      }

      var combined = death.Record.Message;
      if (!string.IsNullOrEmpty(death.Record.Previous))
      {
        var found = false;
        var split = death.Record.Previous.Split(' ');
        foreach (var value in split)
        {
          if (int.TryParse(value, out _))
          {
            found = false;
            break;
          }

          if (_currentPlayer.OrigName == value)
          {
            found = true;
          }
        }

        if (found)
        {
          combined += " (Previous? " + death.Record.Previous + ")";
        }
      }

      AppendMessage(list, combined, end);
      dataGrid.ItemsSource = list;
    }

    private static void AppendMessage(List<dynamic> list, string message, double end)
    {
      if (!string.IsNullOrEmpty(message))
      {
        if (list.Count > 0 && list[^1].Time == end && string.IsNullOrEmpty(list[^1].Damage))
        {
          list[^1].Damage = message;
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
      if (_currentPlayer != null)
      {
        Display();
      }
    }

    #region IDisposable Support
    private bool _disposedValue; // To detect redundant calls

    protected virtual void Dispose(bool disposing)
    {
      if (!_disposedValue)
      {
        MainActions.EventsThemeChanged -= EventsThemeChanged;
        dataGrid?.Dispose();
        _disposedValue = true;
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
