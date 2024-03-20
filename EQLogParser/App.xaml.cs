using AutoMapper;
using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Core;
using log4net.Layout;
using log4net.Repository.Hierarchy;
using Syncfusion.Licensing;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for App.xaml
  /// </summary>
  public partial class App : Application
  {
    internal static IMapper AutoMap;
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    public App()
    {
      SyncfusionLicenseProvider.RegisterLicense("SEY KEY HERE");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
      base.OnStartup(e);
      // unhandled exceptions
      DispatcherUnhandledException += AppDispatcherUnhandledException;
      AppDomain.CurrentDomain.UnhandledException += DomainUnhandledException;
      TaskScheduler.UnobservedTaskException += TaskSchedulerUnobservedTaskException;

      var appDataRoamingPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
      var logsFolderPath = Path.Combine(appDataRoamingPath, "EQLogParser", "logs");

      // Create a new file appender and set its properties
      var fileAppender = new RollingFileAppender
      {
        Name = "FileAppender",
        File = Path.Combine(logsFolderPath, "EQLogParser.log"),
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

      if (ConfigUtil.IfSet("Debug"))
      {
        ((Hierarchy)LogManager.GetRepository()).Root.Level = Level.Debug;
      }
      else
      {
        ((Hierarchy)LogManager.GetRepository()).Root.Level = Level.Info;
      }

      ((Hierarchy)LogManager.GetRepository()).RaiseConfigurationChanged(EventArgs.Empty);

      AutoMap = new MapperConfiguration(cfg => cfg.AddProfile<MappingProfile>()).CreateMapper();
      Log.Info($"Using DotNet {Environment.Version}");
      RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
      Log.Info("RenderMode: " + RenderOptions.ProcessRenderMode);
    }

    private static void TaskSchedulerUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
    {
      Log.Error("TaskSchedulerUnobservedTaskException");
      Log.Error(e.Exception?.Message, e.Exception);
      e.SetObserved();
    }

    private static void DomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
      var exception = e.ExceptionObject as Exception;
      Log.Error("DomainUnhandledException");
      Log.Error(exception?.Message, exception);
    }

    private static void AppDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
      Log.Error("AppDispatcherUnhandledException");
      Log.Error(e.Exception?.Message, e.Exception);
      e.Handled = true; // Prevents application from closing
    }
  }
}
