using log4net;
using Syncfusion.Windows.Tools.Controls;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace EQLogParser
{
  internal static class SyncFusionUtil
  {
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    internal static void AddDocument(DockingManager dockSite, Type type, string name, string title, bool show = false)
    {
      var control = new ContentControl
      {
        Name = name,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
      };

      var instance = Activator.CreateInstance(type);
      control.Content = instance;
      DockingManager.SetHeader(control, title);
      DockingManager.SetState(control, DockState.Document);
      DockingManager.SetSideInDockedMode(control, DockSide.Tabbed);
      DockingManager.SetCanDock(control, false);
      DockingManager.SetCanResizeHeightInFloatState(control, true);
      DockingManager.SetCanResizeWidthInFloatState(control, true);
      DockingManager.SetCanResizeInFloatState(control, true);
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

    internal static void SetDesiredHeight(string resource, double size, ContentControl window)
    {
      Application.Current.Resources[resource] = size;
      DockingManager.SetDesiredHeightInDockedMode(window, size);
      DockingManager.SetDesiredMinHeightInFloatingMode(window, size);
    }

    internal static void SetDesiredWidth(string resource, double size, ContentControl window)
    {
      Application.Current.Resources[resource] = size;
      DockingManager.SetDesiredWidthInDockedMode(window, size);
      DockingManager.SetDesiredWidthInFloatingMode(window, size);
    }

    internal static void ToggleWindow(DockingManager dockSite, string name, bool force = false)
    {
      var opened = GetOpenWindows(dockSite);
      if (opened.TryGetValue(name, out var control))
      {
        if (DockingManager.GetState(control) == DockState.Hidden || force)
        {
          if (DockingManager.GetCanDocument(control))
          {
            DockingManager.SetState(control, DockState.Document);
          }
          else if (DockingManager.GetCanDock(control))
          {
            DockingManager.SetState(control, DockState.Dock);
          }
          else if (DockingManager.GetCanFloat(control))
          {
            DockingManager.SetState(control, DockState.Float);
          }
          else
          {
            Log.Warn("Can not determine ControlControl state for: " + name);
          }

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

    // This is where closing summary tables and line charts will get disposed
    internal static void CloseTab(DockingManager dockSite, ContentControl window, List<bool> logWindows)
    {
      if (window.Content is EqLogViewer)
      {
        if (DockingManager.GetHeader(window) is string title)
        {
          var last = title.LastIndexOf(' ');
          if (last > -1)
          {
            var value = title[last..];
            if (int.TryParse(value, out var result) && result > 0 && logWindows.Count >= result)
            {
              logWindows[result - 1] = false;
            }
          }
        }

        (window.Content as IDisposable)?.Dispose();
      }
      else
      {
        CloseWindow(dockSite, window);
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
        DockingManager.SetState(window, DockState.Hidden);
      }
    }

    internal static void DockSiteSaveActiveWindow(DockingManager dockSite)
    {
      // save active window
      if (dockSite.ActiveWindow is ContentControl cc && !string.IsNullOrEmpty(cc.Name) &&
        DockingManager.GetState(cc) == DockState.Document && DockingManager.GetCanDock(cc) == false)
      {
        ConfigUtil.SetSetting("ActiveWindow", cc.Name);
      }
    }

    internal static bool OpenWindow(out ContentControl window, Type type = null, string key = "", string title = "")
    {
      var nowOpen = false;
      window = null;

      var dockSite = MainActions.GetDockSite();
      if (type != null)
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
