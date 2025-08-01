using System;
using System.Windows;
using System.Windows.Forms;

namespace EQLogParser
{
  internal class WinFormsUtil
  {
    internal static NotifyIcon CreateTrayIcon(MainWindow main)
    {
      var notifyIcon = new NotifyIcon
      {
        Visible = true,
        Text = "EQLogParser",
        ContextMenuStrip = new ContextMenuStrip()
      };

      var iconUri = new Uri("pack://application:,,,/src/ui/main/EQLogParser.ico");
      using (var stream = System.Windows.Application.GetResourceStream(iconUri).Stream)
      {
        notifyIcon.Icon = new System.Drawing.Icon(stream);
      }

      notifyIcon.MouseClick += (s, e) =>
      {
        if (e.Button == MouseButtons.Left)
        {
          RestoreWindow(main);
        }
      };

      var restoreItem = new ToolStripMenuItem("Restore");
      restoreItem.Click += (s, e) => RestoreWindow(main);
      notifyIcon.ContextMenuStrip.Items.Add(restoreItem);
      restoreItem.Visible = true;

      var minimizeItem = new ToolStripMenuItem("Minimize");
      minimizeItem.Click += (s, e) => main.WindowState = WindowState.Minimized;
      notifyIcon.ContextMenuStrip.Items.Add(minimizeItem);
      notifyIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
      minimizeItem.Visible = false;

      var aboutItem = new ToolStripMenuItem("About");
      aboutItem.Click += (s, e) => MainActions.OpenFileWithDefault($"{App.ParserHome}");
      notifyIcon.ContextMenuStrip.Items.Add(aboutItem);
      notifyIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());

      var exitItem = new ToolStripMenuItem("Exit");
      exitItem.Click += (s, e) => main.Close();
      notifyIcon.ContextMenuStrip.Items.Add(exitItem);

      main.StateChanged += (s, e) =>
      {
        restoreItem.Visible = main.WindowState == WindowState.Minimized;
        minimizeItem.Visible = main.WindowState != WindowState.Minimized;
      };

      return notifyIcon;
    }

    private static void RestoreWindow(MainWindow main)
    {
      // disconnect to avoid saving location before it's fully restored
      main.DisconnectLocationChanged();

      if (main.Visibility != Visibility.Visible)
      {
        main.Show();
      }

      main.Activate();

      if (main.WindowState == WindowState.Minimized)
      {
        main.WindowState = App.LastWindowState;
      }

      // reconnect
      main.Dispatcher.Invoke(() =>
      {
        main.ConnectLocationChanged();
      }, System.Windows.Threading.DispatcherPriority.Background);
    }
  }
}
