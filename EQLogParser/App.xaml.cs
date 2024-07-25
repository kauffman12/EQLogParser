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
  public partial class App
  {
    internal static IMapper AutoMap;
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private SplashWindow _splash;

    public App()
    {
      SyncfusionLicenseProvider.RegisterLicense("SET KEY");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
      base.OnStartup(e);

      try
      {
        // Load splashscreen
        _splash = new SplashWindow();
        _splash.Show();

        // Setup unhandled exception handlers
        DispatcherUnhandledException += AppDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += DomainUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskSchedulerUnobservedTaskException;

        InitializeLogging();

        // Read app settings
        ConfigUtil.Init();

        // Set Debug level
        SetLoggingLevel();

        AutoMap = new MapperConfiguration(cfg => cfg.AddProfile<MappingProfile>()).CreateMapper();
        var version = ResourceAssembly.GetName().Version;
        Log.Info($"EQLogParser: {version}, DotNet: {Environment.Version}");
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        ShowMain();
      }
      catch (Exception ex)
      {
        Log.Error("CreateAppError", ex);
        _splash?.SetErrorState();
      }
    }

    private static void InitializeLogging()
    {
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
        MaximumFileSize = "2MB",
        StaticLogFileName = true,
        RollingStyle = RollingFileAppender.RollingMode.Size,
        PreserveLogFileNameExtension = true,
        CountDirection = -1
      };

      // Activate the options on the file appender
      fileAppender.ActivateOptions();

      // Set the repository configuration
      BasicConfigurator.Configure(LogManager.GetRepository(), fileAppender);
    }

    private static void SetLoggingLevel()
    {
      if (ConfigUtil.IfSet("Debug"))
      {
        ((Hierarchy)LogManager.GetRepository()).Root.Level = Level.Debug;
      }
      else
      {
        ((Hierarchy)LogManager.GetRepository()).Root.Level = Level.Info;
      }
      ((Hierarchy)LogManager.GetRepository()).RaiseConfigurationChanged(EventArgs.Empty);
    }

    private void ShowMain()
    {
      var main = new MainWindow();
      Task.Delay(500).ContinueWith(_ => Dispatcher.Invoke(() =>
      {
        try
        {
          main.Show();
        }
        catch (Exception ex)
        {
          Log.Error("ShowAppError", ex);
          _splash?.SetErrorState();
        }
      }, DispatcherPriority.Render));
    }

    private void TaskSchedulerUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs ex)
    {
      Log.Error("TaskSchedulerUnobservedTaskException", ex.Exception);
      ex.SetObserved();
      _splash?.SetErrorState();
    }

    private void DomainUnhandledException(object sender, UnhandledExceptionEventArgs ex)
    {
      Log.Error("DomainUnhandledException", ex.ExceptionObject as Exception);
      _splash?.SetErrorState();
    }

    private void AppDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs ex)
    {
      Log.Error("AppDispatcherUnhandledException", ex.Exception);
      ex.Handled = true; // Prevents application from closing
      _splash?.SetErrorState();
    }
  }
}
