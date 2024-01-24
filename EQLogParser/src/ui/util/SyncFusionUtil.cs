using log4net;
using Syncfusion.Windows.Tools.Controls;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Controls;

namespace EQLogParser
{
  static class SyncFusionUtil
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    internal static void AddDocument(DockingManager dockSite, Type type, string name, string title, bool show = false)
    {
      var control = new ContentControl { Name = name };
      DockingManager.SetHeader(control, title);
      DockingManager.SetState(control, DockState.Document);
      DockingManager.SetSideInDockedMode(control, DockSide.Tabbed);
      DockingManager.SetCanDock(control, false);
      var instance = Activator.CreateInstance(type);
      control.Content = instance;
      dockSite.Children.Add(control);

      if (!show)
      {
        DockingManager.SetState(control, DockState.Hidden);
      }
    }

    internal static Dictionary<string, ContentControl> GetOpenWindows(DockingManager dockSite)
    {
      var opened = new Dictionary<string, ContentControl>();
      foreach (var child in dockSite.Children)
      {
        if (child is ContentControl control)
        {
          opened[control.Name] = control;
        }
      }

      return opened;
    }

    internal static void ToggleWindow(DockingManager dockSite, string name)
    {
      var opened = GetOpenWindows(dockSite);
      if (opened.TryGetValue(name, out var control))
      {
        if (DockingManager.GetState(control) == DockState.Hidden)
        {
          dockSite.ActivateWindow(name);
        }
        else
        {
          if (control.Content is IDocumentContent doc)
          {
            doc.HideContent();
          }

          DockingManager.SetState(control, DockState.Hidden);
        }
      }
    }

    internal static void CloseWindow(DockingManager dockSite, ContentControl window)
    {
      // don't really remove the window unless it is disposable and not just a simple Grid like in MainWindow.xaml
      // right-click windows fall in this case
      if (window?.Content is IDisposable disposable and UserControl)
      {
        // delay so windows can be cleaned up before we manually try to do it
        try
        {
          try
          {
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

          disposable.Dispose();
        }
        catch (Exception e)
        {
          Log.Debug(e);
        }
      }
      else if (window?.Content is IDocumentContent doc)
      {
        doc.HideContent();
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
  }
}
