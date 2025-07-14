using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EQLogParser
{
  internal static class SelectedParseBuilder
  {
    internal static readonly string[] separator = [" @"];

    internal static StatsSummary Build(string type, CombinedStats stats, List<PlayerStats> players, SummaryOptions opts, string customTitle)
    {
      if (stats == null)
      {
        return new StatsSummary { Title = "", RankedPlayers = "" };
      }

      var details = "";
      var title = "";

      switch (type)
      {
        case Labels.TankParse:
          if (players?.Count > 0)
          {
            details = FormatDetails(
              [.. players.OrderByDescending(p => p.Total)], opts, p =>
              {
                var namePart = opts.RankPlayers ? string.Format(CultureInfo.CurrentCulture, StatsUtil.PlayerRankFormat, p.Rank, p.Name)
                  : string.Format(CultureInfo.CurrentCulture, StatsUtil.PlayerFormat, p.Name);
                var damagePart = string.Format(CultureInfo.CurrentCulture, StatsUtil.TotalOnlyFormat, StatsUtil.FormatTotals(p.Total));
                var line = namePart + damagePart;

                if (opts.ShowSpecial && !string.IsNullOrEmpty(p.Special))
                {
                  line = string.Format(CultureInfo.CurrentCulture, StatsUtil.SpecialFormat, line, p.Special);
                }

                return line;
              });
          }

          title = FormatTitle(stats, opts, customTitle);
          break;

        case Labels.ReceivedHealParse:
          var receivedHeals = players?.FirstOrDefault();
          if (receivedHeals != null && receivedHeals.MoreStats != null)
          {
            var rank = 1;
            long totals = 0;
            details = FormatDetails([.. receivedHeals.MoreStats.SubStats2.OrderByDescending(p => p.Total).Take(10)], opts, p =>
            {
              var namePart = opts.RankPlayers ? string.Format(CultureInfo.CurrentCulture, StatsUtil.PlayerRankFormat, rank, p.Name)
                : string.Format(CultureInfo.CurrentCulture, StatsUtil.PlayerFormat, p.Name);
              var healPart = string.Format(CultureInfo.CurrentCulture, StatsUtil.TotalOnlyFormat, StatsUtil.FormatTotals(p.Total));
              rank++;
              totals += p.Total;
              return namePart + healPart;
            });

            var totalTitle = $"{receivedHeals.Name} Received " + StatsUtil.FormatTotals(totals) + " Healing";
            title = StatsUtil.FormatTitle(customTitle ?? stats.TargetTitle, opts.ShowTime ? stats.TimeTitle : "", totalTitle);
          }
          break;

        case Labels.HealParse:
          if (players?.Count > 0)
          {
            details = FormatDetails(
              [.. players.OrderByDescending(p => p.Total)], opts, p =>
              {
                var namePart = opts.RankPlayers ? string.Format(CultureInfo.CurrentCulture, StatsUtil.PlayerRankFormat, p.Rank, p.Name)
                  : string.Format(CultureInfo.CurrentCulture, StatsUtil.PlayerFormat, p.Name);
                var healPart = string.Format(CultureInfo.CurrentCulture, StatsUtil.TotalOnlyFormat, StatsUtil.FormatTotals(p.Total));
                var line = namePart + healPart;

                if (opts.ShowSpecial && !string.IsNullOrEmpty(p.Special))
                {
                  line = string.Format(CultureInfo.CurrentCulture, StatsUtil.SpecialFormat, line, p.Special);
                }

                return line;
              });
          }

          title = FormatTitle(stats, opts, customTitle);
          break;

        case Labels.TopHealParse:
          var topHeals = players?.FirstOrDefault();

          if (topHeals != null)
          {
            var rank = 1;
            details = FormatDetails([.. topHeals.SubStats.OrderByDescending(p => p.Total).Take(10)], opts, p =>
              {
                var abbr = DataManager.Instance.AbbreviateSpellName(p.Name);
                var namePart = opts.RankPlayers ? string.Format(CultureInfo.CurrentCulture, StatsUtil.PlayerRankFormat, rank, abbr)
                  : string.Format(CultureInfo.CurrentCulture, StatsUtil.PlayerFormat, abbr);
                var healPart = string.Format(CultureInfo.CurrentCulture, StatsUtil.TotalOnlyFormat, StatsUtil.FormatTotals(p.Total));
                rank++;
                return namePart + healPart;
              });

            var totalTitle = $"{topHeals.Name}'s Top Heals";
            title = StatsUtil.FormatTitle(customTitle ?? stats.TargetTitle, opts.ShowTime ? stats.TimeTitle : "", totalTitle);
          }
          break;

        case Labels.DamageParse:
          if (players?.Count > 0)
          {
            details = FormatDetails(
              [.. players.OrderByDescending(p => p.Total)], opts, p =>
              {
                var name = opts.ShowPetLabel ? p.Name : p.Name.Replace(" +Pets", "");
                var namePart = opts.RankPlayers ? string.Format(CultureInfo.CurrentCulture, StatsUtil.PlayerRankFormat, p.Rank, name)
                  : string.Format(CultureInfo.CurrentCulture, StatsUtil.PlayerFormat, name);
                var damagePart = opts.ShowDps ? string.Format(CultureInfo.CurrentCulture, StatsUtil.TotalFormat, StatsUtil.FormatTotals(p.Total), "", StatsUtil.FormatTotals(p.Dps))
                  : string.Format(CultureInfo.CurrentCulture, StatsUtil.TotalOnlyFormat, StatsUtil.FormatTotals(p.Total));
                var line = namePart + damagePart;

                if (opts.ShowTime)
                {
                  line += " " + string.Format(CultureInfo.CurrentCulture, StatsUtil.TimeFormat, p.TotalSeconds);
                }

                if (opts.ShowSpecial && !string.IsNullOrEmpty(p.Special))
                {
                  line = string.Format(CultureInfo.CurrentCulture, StatsUtil.SpecialFormat, line, p.Special);
                }

                return line;
              });
          }

          title = FormatTitle(stats, opts, customTitle);
          break;
      }

      return new StatsSummary { Title = title, RankedPlayers = details };
    }

    private static string FormatDetails(List<PlayerSubStats> statsList, SummaryOptions opts, Func<PlayerSubStats, string> formatFunc)
    {
      if (opts.ListView)
      {
        var result = "";
        foreach (var details in statsList.Select(formatFunc))
        {
          if (!string.IsNullOrEmpty(details))
          {
            result += Environment.NewLine + details;
          }
        }
        return result;
      }
      else
      {
        return ", " + string.Join(" | ", statsList.Select(formatFunc));
      }
    }

    private static string FormatTitle(CombinedStats stats, SummaryOptions opts, string customTitle)
    {
      var timePart = opts.ShowTime ? $"{stats.TimeTitle}" : "";
      if (opts.ShowTotals)
      {
        var totalPart = opts.ShowDps ? stats.TotalTitle : stats.TotalTitle.Split(separator, 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return StatsUtil.FormatTitle(customTitle ?? stats.TargetTitle, timePart, totalPart);
      }
      return StatsUtil.FormatTitle(customTitle ?? stats.TargetTitle, timePart);
    }
  }

  internal class SummaryOptions
  {
    public bool ListView { get; set; }
    public bool ShowDps { get; set; }
    public bool ShowTotals { get; set; }
    public bool RankPlayers { get; set; }
    public bool ShowTime { get; set; }
    public bool ShowPetLabel { get; set; }
    public bool ShowSpecial { get; set; }
  }
}