using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Layout;
using System;
using System.IO;
using System.ServiceProcess;
using System.Threading;

namespace EQLogManager
{
  internal static class Program
  {
    private static readonly ManualResetEvent ShutdownEvent = new(false);

    /// <summary>
    /// The main entry point for the application.
    /// </summary>
    public static void Main()
    {
      var appDataRoamingPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
      var logsFolderPath = Path.Combine(appDataRoamingPath, "EQLogParser", "logs");

      // Create a new file appender and set its properties
      var fileAppender = new RollingFileAppender
      {
        Name = "FileAppender",
        File = Path.Combine(logsFolderPath, "EQLogManager.log"),
        AppendToFile = true,
        LockingModel = new FileAppender.MinimalLock(),
        Layout = new PatternLayout("%date [%thread] %level %logger - %message%newline"),
        MaxSizeRollBackups = 5,
        MaximumFileSize = "5MB",
        StaticLogFileName = true
      };

      // Activate the options on the file appender
      fileAppender.ActivateOptions();

      // Set the repository configuration
      BasicConfigurator.Configure(LogManager.GetRepository(), fileAppender);

      /* DEBUG =====
      var service = new EQLogService();
      service.StartDebug();

      Console.WriteLine("Service running... Press Ctrl+C to exit.");
      Console.CancelKeyPress += (sender, eventArgs) =>
      {
        ShutdownEvent.Set();
        eventArgs.Cancel = true;
      };

      ShutdownEvent.WaitOne(); // Wait until the reset event is triggered
      service.StopDebug();
      ===== */

      // Run as service
      var servicesToRun = new ServiceBase[]
      {
        new EQLogService()
      };

      ServiceBase.Run(servicesToRun);
    }
  }
}
