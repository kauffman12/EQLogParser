using log4net;
using Syncfusion.Windows.Tools.Controls;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;

namespace EQLogParser
{
  static class SyncFusionUtil
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    internal static void CloseWindow(DockingManager dockSite, ContentControl window)
    {
      if (window != null)
      {
        var state = (DockingManager.GetState(window) == DockState.Hidden) ? DockState.Dock : DockState.Hidden;
        // delay so windows can be cleaned up before we manually try to do it
        try
        {
          if (state == DockState.Hidden && (window?.Tag as string) != "Hide")
          {
            Dispatcher.CurrentDispatcher.InvokeAsync(() =>
            {
              try
              {
                (window.Content as IDisposable)?.Dispose();

                if (dockSite.Children.Contains(window))
                {
                  dockSite.Children.Remove(window);
                }
                else if (dockSite.DocContainer != null && dockSite.DocContainer.Items.Contains(window))
                {
                  dockSite.DocContainer.Items.Remove(window);
                }
              }
              catch (Exception ex)
              {
                Log.Debug(ex);
              }
            }, DispatcherPriority.Background);
          }
          else
          {
            DockingManager.SetState(window, state);
          }
        }
        catch (Exception e)
        {
          Log.Debug(e);
        }
      }
    }

    internal static bool OpenWindow(DockingManager dockSite, Dictionary<string, ContentControl> opened, out ContentControl window,
      Type type = null, string key = "", string title = "")
    {
      var nowOpen = false;
      window = null;

      if (opened != null && opened.TryGetValue(key, out var control))
      {
        CloseWindow(dockSite, control);
      }
      else if (type != null)
      {
        var instance = Activator.CreateInstance(type);
        window = new ContentControl { Name = key };
        DockingManager.SetHeader(window, title);
        DockingManager.SetState(window, DockState.Document);
        DockingManager.SetCanDock(window, false);
        window.Content = instance;
        dockSite.BeginInit();
        dockSite.Children.Add(window);
        dockSite.EndInit();
        nowOpen = true;
      }

      return nowOpen;
    }

    internal static bool OpenChart(Dictionary<string, ContentControl> opened, DockingManager dockSite, string key, List<string> choices,
      string title, DocumentTabControl tabControl, bool includePets)
    {
      var nowOpen = false;

      if (opened != null && opened.TryGetValue(key, out var control))
      {
        CloseWindow(dockSite, control);
      }
      else
      {
        var chart = new LineChart(choices, includePets);
        var window = new ContentControl { Name = key };
        DockingManager.SetHeader(window, title);
        DockingManager.SetState(window, DockState.Document);
        DockingManager.SetCanDock(window, false);
        window.Content = chart;

        if (dockSite.DocContainer.Items.Count == 0)
        {
          dockSite.BeginInit();
          dockSite.Children.Add(window);
          dockSite.EndInit();
        }
        else if (tabControl == null || tabControl.Items.Count == 0)
        {
          dockSite.CreateHorizontalTabGroup(window);
        }
        else
        {
          tabControl.Container.AddElementToTabGroup(tabControl, window);
        }

        nowOpen = true;
      }

      return nowOpen;
    }
  }
}
