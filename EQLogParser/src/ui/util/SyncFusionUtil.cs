using log4net;
using Syncfusion.Licensing;
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

    // The one home for the Syncfusion license key. The app (App..ctor) and the test host (Wpf.Test's
    // [AssemblyInitialize]) both call LoadLicense instead of touching SyncfusionLicenseProvider directly, so
    // however this constant is managed (committed or local-only) applies to both identically. An empty key is the
    // vendor's own no-op (RegisterLicense returns on IsNullOrEmpty), so a fresh clone runs keyless everywhere -
    // and Wpf.Test keeps its IsLicenseExceptionShown guard for exactly that state.
    private const string LicenseKey = "";

    internal static void LoadLicense() => SyncfusionLicenseProvider.RegisterLicense(LicenseKey);

    internal static void AddDocument(DockingManager dockSite, Type type, string name, string title, bool show = false)
    {
      var control = _CreateControl(dockSite, type, name, title);
      control.HorizontalAlignment = HorizontalAlignment.Stretch;
      control.VerticalContentAlignment = VerticalAlignment.Stretch;
      DockingManager.SetSideInDockedMode(control, DockSide.Tabbed);
      DockingManager.SetCanResizeHeightInFloatState(control, true);
      DockingManager.SetCanResizeWidthInFloatState(control, true);
      DockingManager.SetCanResizeInFloatState(control, true);

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

    /*
     * Show or hide a window from its menu item. Two properties the old shape got wrong for the edge panes.
     *
     * SHOW WHENEVER IT IS NOT ON SCREEN, not "when its state is Hidden". A window docked in a tab group behind another tab
     * is not visible either, and under the old test its menu item HID it (state was already Dock), so a click on
     * "Pet Owners" while its group showed something else made it disappear. "Not visible" is what the operator means.
     *
     * COME BACK THE WAY IT WENT AWAY. The Document/Dock ladder in ShowStateFor is for windows that live in the middle of the
     * layout; a pane whose markup says SideInDockedMode=Right (the identity strip: Player/NPC Identity) has to
     * return auto-hidden, because SetState(Dock) on it yanks the pane out of its strip and drops it over the tables - the
     * exact relocation an operator never asked for and cannot undo from this menu.
     */
    internal static void ToggleWindow(DockingManager dockSite, string name, bool force = false)
    {
      var opened = GetOpenWindows(dockSite);
      if (opened.TryGetValue(name, out var control))
      {
        if (force || DockingManager.GetState(control) == DockState.Hidden || !control.IsVisible)
        {
          var showState = ShowStateFor(control);
          if (showState == DockState.Hidden)
          {
            Log.Warn("Can not determine ControlControl state for: " + name);
          }
          else
          {
            // Which shape a click chose, per window: docking placement is invisible in the log, and this is the one door that
            // moves a window at all. Rare (a menu click) and it names itself, so it can stay at Info.
            Log.Info($"dock: {name} shown as {showState}");
            DockingManager.SetState(control, showState);
            dockSite.ActivateWindow(name);
          }
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

    /*
     * Which state a hidden window goes back to. Its own method because it is the whole policy of "show or hide, never
     * relocate", and it reads only attached properties, so a test can ask it about a ContentControl without building a
     * docking layout (which needs a realized DockingManager and therefore a live window).
     *
     * A STRIP PANE comes back auto-hidden, and a strip pane is a window that declares BOTH an edge side and that it cannot be
     * a document - which is exactly how the XAML writes the identity strip (SideInDockedMode="Right" plus CanDocument="False",
     * every one of them). The side alone is NOT that declaration: SideInDockedMode is also a property the docking layout itself
     * reports, so a window last sitting against an edge when the layout was saved comes back from dockSite.xml wearing that side,
     * and a chart asked "open in the tab container" would slide out of a strip instead. Document-ness answers first because it is
     * the one property here that only markup sets: AddDocument says true for the middle windows, CanDocument="False" marks the
     * strips, nothing else touches either.
     */
    internal static DockState ShowStateFor(ContentControl window)
    {
      var canDocument = DockingManager.GetCanDocument(window);
      var side = DockingManager.GetSideInDockedMode(window);
      if (!canDocument && side is DockSide.Left or DockSide.Right or DockSide.Top or DockSide.Bottom) return DockState.AutoHidden;
      if (canDocument) return DockState.Document;
      if (DockingManager.GetCanDock(window)) return DockState.Dock;
      if (DockingManager.GetCanFloat(window)) return DockState.Float;

      // Nothing to show: reported back as Hidden so the caller warns instead of putting a window somewhere arbitrary.
      return DockState.Hidden;
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

        if (window.Content is IDisposable content)
        {
          content.Dispose();
        }
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
            Log.Debug("Docking state operation failed", ex);
          }

          disposable.Dispose();
        }
        catch (Exception e)
        {
          Log.Debug("Docking window toggle failed", e);
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
        DockingManager.GetState(cc) == DockState.Document && DockingManager.GetCanDock(cc) is false)
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
        window = _CreateControl(dockSite, type, key, title);
        dockSite.BeginInit();
        dockSite.Children.Add(window);
        dockSite.EndInit();
        nowOpen = true;
      }

      return nowOpen;
    }

    private static ContentControl _CreateControl(DockingManager dockSite, Type type, string name, string title)
    {
      var control = new ContentControl { Name = name };
      control.Content = Activator.CreateInstance(type);
      DockingManager.SetHeader(control, title);
      // Say out loud what the next two lines mean. This is every window the app opens at runtime - the charts, timelines, hit
      // frequency and death log from the summary toolbars, and the whole AddDocument fleet - and it asks for Document while
      // turning CanDock OFF. Left unstated, document-ness is whatever the control defaults to, so both halves of "open this
      // window" could miss: SetState here can be coerced away from a shape the window has no permission for, and a later menu
      // show falls through CanDock to Float. The field report was charts opening as separate popup windows and one docked on
      // the left instead of in the tab container. CanDocument is the property markup answers truthfully about (the XAML panes
      // set it False), unlike SideInDockedMode, which a restored layout rewrites - see ShowStateFor.
      DockingManager.SetCanDocument(control, true);
      DockingManager.SetState(control, DockState.Document);
      DockingManager.SetCanDock(control, false);
      dockSite.Children.Add(control);
      return control;
    }
  }
}
