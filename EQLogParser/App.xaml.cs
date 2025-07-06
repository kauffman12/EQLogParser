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
      SyncfusionLicenseProvider.RegisterLicense("");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
      base.OnStartup(e);

      try
      {
        // Load splashscreen
        _splash = new SplashWindow();

        // Setup unhandled exception handlers
        DispatcherUnhandledException += AppDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += DomainUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskSchedulerUnobservedTaskException;

        InitializeLogging();

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
          Log.Warn("Windows 10 (build 10240) or newer is required. Make sure you have Windows Compatibility mode turned OFF.");
        }

        // Read app settings
        ConfigUtil.Init();

        var wineLoader = Environment.GetEnvironmentVariable("WINELOADER");
        if (!string.IsNullOrEmpty(wineLoader))
        {
          ConfigUtil.SetSetting("HardwareAcceleration", false);
        }
        else
        {
          // hardware acceleration setting
          var hardwareAccel = ConfigUtil.GetSetting("HardwareAcceleration");
          if (hardwareAccel == null)
          {
            ConfigUtil.SetSetting("HardwareAcceleration", true);
          }
        }

        RenderOptions.ProcessRenderMode = ConfigUtil.IfSet("HardwareAcceleration") ? RenderMode.Default : RenderMode.SoftwareOnly;

        if (!ConfigUtil.IfSet("HideSplashScreen"))
        {
          // show splash screen
          _splash.Show();
        }

        // Set Debug level
        SetLoggingLevel();

        AutoMap = new MapperConfiguration(cfg => cfg.AddProfile<MappingProfile>()).CreateMapper();
        var osVersion = Environment.OSVersion;
        Log.Info($"Detected OS Version: {osVersion.VersionString}");
        var version = ResourceAssembly.GetName().Version!.ToString()[..^2];
        Log.Info($"EQLogParser: {version}, DotNet: {Environment.Version}, RenderMode: {RenderOptions.ProcessRenderMode}");

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
          Log.Error($"ShowAppError: {ex.Message}");
          LogDetails(ex);
          _splash?.SetErrorState();
        }
      }, DispatcherPriority.Render));
    }

    private void DomainUnhandledException(object sender, UnhandledExceptionEventArgs ex)
    {
      if (ex.ExceptionObject is Exception exception)
      {
        Log.Error($"DomainUnhandledException: {exception.Message}");
        LogDetails(exception);
      }

      _splash?.SetErrorState();
    }

    private void AppDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs ex)
    {
      Log.Error($"AppDispatcherUnhandledException: {ex.Exception.Message}");
      LogDetails(ex.Exception);
      ex.Handled = true; // Prevents application from closing
      _splash?.SetErrorState();
    }

    private void TaskSchedulerUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs ex)
    {
      Log.Error($"TaskSchedulerUnobservedTaskException: {ex.Exception.Message}");
      LogDetails(ex.Exception);
      ex.SetObserved();
      _splash?.SetErrorState();
    }

    private static void LogDetails(Exception ex)
    {
      Log.Error($"Thread ID: {Environment.CurrentManagedThreadId}");
      Log.Error($"Thread Name: {System.Threading.Thread.CurrentThread.Name}");
      Log.Error(ex.StackTrace);

      var inner = ex.InnerException;
      while (inner != null)
      {
        Log.Error($"Inner Exception: {inner.Message}");
        Log.Error(inner.StackTrace);
        inner = inner.InnerException;
      }
    }

    private void DoNothing(object sender, RoutedEventArgs e)
    {
      // prevent two events from firing
      e.Handled = true;
    }
  }
}
