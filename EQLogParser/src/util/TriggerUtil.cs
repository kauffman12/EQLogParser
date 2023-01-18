using Syncfusion.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace EQLogParser
{
  internal class TriggerUtil
  {
    internal static void AddTreeNodes(List<TriggerNode> nodes, TriggerTreeViewNode treeNode)
    {
      if (nodes != null)
      {
        foreach (var node in nodes)
        {
          var child = new TriggerTreeViewNode { Content = node.Name, SerializedData = node };
          if (node.TriggerData != null)
          {
            child.IsTrigger = true;
            child.IsChecked = node.IsEnabled;
            treeNode.ChildNodes.Add(child);
          }
          else
          {
            child.IsChecked = node.IsEnabled;
            child.IsExpanded = node.IsExpanded;
            child.IsTrigger = false;
            treeNode.ChildNodes.Add(child);
            AddTreeNodes(node.Nodes, child);
          }
        }
      }
    }

    internal static void Copy(Trigger to, Trigger from)
    {
      to.Comments = from.Comments;
      to.DurationSeconds = from.DurationSeconds;
      to.EnableTimer = from.EnableTimer;
      to.EndEarlyPattern = from.EndEarlyPattern;
      to.EndTextToSpeak = from.EndTextToSpeak;
      to.EndUseRegex = from.EndUseRegex;
      to.Errors = "None";
      to.LongestEvalTime = -1;
      to.Pattern = from.Pattern;
      to.Priority = from.Priority;
      to.TextToSpeak = from.TextToSpeak;
      to.TriggerAgainOption= from.TriggerAgainOption;
      to.UseRegex = from.UseRegex;
      to.WarningSeconds = from.WarningSeconds;
      to.WarningTextToSpeak = from.WarningTextToSpeak;
    }

    internal static void MergeNodes(List<TriggerNode> newNodes, TriggerNode parent)
    {
      if (newNodes != null)
      {
        if (parent.Nodes == null)
        {
          parent.Nodes = newNodes;
        }
        else
        {
          var needsSort = new List<TriggerNode>();
          foreach (var newNode in newNodes)
          {
            var found = parent.Nodes.Find(node => node.Name == newNode.Name);

            if (found != null)
            {
              if (newNode.TriggerData != null && found.TriggerData != null)
              {
                Copy(found.TriggerData, newNode.TriggerData);
              }
              else
              {
                MergeNodes(newNode.Nodes, found);
              }
            }
            else
            {
              parent.Nodes.Add(newNode);
              needsSort.Add(parent);
            }
          }

          needsSort.ForEach(parent => parent.Nodes = parent.Nodes.OrderBy(node => node.Name).ToList());
        }
      }
    }

    internal static string UpdatePattern(bool useRegex, string playerName, string pattern)
    {
      pattern = pattern.Replace("{c}", playerName, StringComparison.OrdinalIgnoreCase);

      if (useRegex && Regex.Matches(pattern, @"{(s\d?)}", RegexOptions.IgnoreCase) is MatchCollection matches && matches.Count > 0)
      {
        matches.ForEach(match =>
        {
          if (match.Groups.Count > 1)
          {
            pattern = pattern.Replace(match.Value, "(?<" + match.Groups[1].Value + ">.+)");
          }
        });
      }

      return pattern;
    }
  }
}
