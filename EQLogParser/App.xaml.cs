using AutoMapper;
using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Core;
using log4net.Layout;
using log4net.Repository.Hierarchy;
using Microsoft.Extensions.Caching.Memory;
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
    internal static MemoryCache AppCache = new(new MemoryCacheOptions
    {
      SizeLimit = 1024 * 1024 * 100 // 100 MB
    });

    internal static IMapper AutoMap;
    internal const string ParserHome = "https://eqlogparser.kizant.net";
    internal static double DefaultHeight = SystemParameters.PrimaryScreenHeight * 0.75;
    internal static double DefaultWidth = SystemParameters.PrimaryScreenWidth * 0.85;
    internal static string ReleaseNotesUrl = $"{ParserHome}/releasenotes.html";
    internal static string Version = "";
    internal static WindowState LastWindowState = WindowState.Normal;
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);
    private SplashWindow _splash;

    public App()
    {
      // 30.x
      //SyncfusionLicenseProvider.RegisterLicense("Mzk2NDI3MEAzMzMwMmUzMDJlMzAzYjMzMzAzYkpROWp2Zmh6RkNsazEyc2picm9oM1prRGQ0UHExU0FqZkNPaGx2SXM0T3M9");
      // 31.x
      SyncfusionLicenseProvider.RegisterLicense("Ngo9BigBOggjHTQxAR8/V1JFaF5cXGRCf1FpRmJGdld5fUVHYVZUTXxaS00DNHVRdkdmWXZcd3ZVRGlYVUZ2W0FWYEg=");
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
      base.OnStartup(e);

      try
      {
        // Setup unhandled exception handlers
        DispatcherUnhandledException += AppDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += DomainUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskSchedulerUnobservedTaskException;

        InitializeLogging();

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
          Log.Warn("Windows 10 (build 10240) or newer is required. Make sure you have Windows Compatibility mode turned OFF.");
        }

        // Load splashscreen
        _splash = new SplashWindow();

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
        Version = ResourceAssembly.GetName().Version!.ToString()[..^2];
        Log.Info($"EQLogParser: {Version}, OS: {osVersion.VersionString}, DotNet: {Environment.Version}, RenderMode: {RenderOptions.ProcessRenderMode}");

        // update for release notes URL
        var urlVersion = Version.Replace(".", "-");
        ReleaseNotesUrl = $"{ParserHome}/releasenotes.html#{urlVersion}";

        // show render mode
        ConfigUtil.UpdateStatus($"RenderMode: {RenderOptions.ProcessRenderMode}");

        // load audio voices before window is created
        ConfigUtil.UpdateStatus("Validating Installed Voices");
        await LoadVoicesSafe();

        await ShowMain();
      }
      catch (Exception ex)
      {
        Log.Error("CreateAppError", ex);
        _splash?.SetErrorState();
      }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
      await TriggerManager.Instance.StopAsync();
      await TriggerStateManager.Instance.Dispose();

      AudioManager.Instance.Dispose();
      AppCache.Dispose();
      base.OnExit(e);
    }

    private static async Task LoadVoicesSafe()
    {
      try
      {
        await AudioManager.Instance.LoadValidVoicesAsync();
      }
      catch
      {
        // nothing
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

    private async Task ShowMain()
    {
      var main = new MainWindow
      {
        // DPI and sizing
        Height = ConfigUtil.GetSettingAsDouble("WindowHeight", DefaultHeight),
        Width = ConfigUtil.GetSettingAsDouble("WindowWidth", DefaultWidth),
        Top = ConfigUtil.GetSettingAsDouble("WindowTop", double.NaN),
        Left = ConfigUtil.GetSettingAsDouble("WindowLeft", double.NaN)
      };

      ConfigUtil.UpdateStatus("Checking Window Position");
      CheckWindowPosition(main);

      Log.Info($"Window Pos ({main.Top}, {main.Left}) | Window Size ({main.Width}, {main.Height})");

      // allow time fow window creation
      await Task.Delay(350);

      try
      {
        // Init Trigger Manager
        ConfigUtil.UpdateStatus("Starting Trigger Manager");
        await TriggerManager.Instance.StartAsync();

        var savedState = ConfigUtil.GetSetting("WindowState", "Normal") switch
        {
          "Maximized" => WindowState.Maximized,
          "Minimized" => WindowState.Minimized,
          _ => WindowState.Normal
        };

        if (savedState != WindowState.Minimized || !ConfigUtil.IfSet("HideWindowOnMinimize"))
        {
          main.Show();
        }

        // if start minimized if requested do nothing but update the last state
        if (ConfigUtil.IfSet("StartWithWindowMinimized"))
        {
          if (savedState != WindowState.Minimized)
          {
            LastWindowState = savedState;
          }

          main.WindowState = WindowState.Minimized;
        }
        else
        {
          // use last saved state
          main.WindowState = savedState;
        }

        // window state change event may or may not be
        // received in main window at this point
        main.UpdateWindowBorder();

        // allow time for state change
        await Task.Delay(350);
        MainActions.FireWindowStateChanged(main.WindowState);
        main.ConnectLocationChanged();
        // start archive schedule if configured
        FileUtil.SetArchiveSchedule();
        ConfigUtil.UpdateStatus("Done");

        // complete rest in a new thread
        await Task.Run(async () =>
        {
          // cleanup downloads
          MainActions.Cleanup();

          await MainActions.CheckVersionAsync();
        });
      }
      catch (Exception ex)
      {
        ConfigUtil.UpdateStatus("Done");
        Log.Error($"ShowAppError: {ex.Message}");
        LogDetails(ex);
        _splash?.SetErrorState();
      }
    }

    private static void CheckWindowPosition(MainWindow main)
    {
      var isOffScreen = true;
      var windowRect = new Rect(main.Left, main.Top, main.Width, main.Height);

      foreach (var screen in System.Windows.Forms.Screen.AllScreens)
      {
        var screenRect = new Rect(
          screen.WorkingArea.Left,
          screen.WorkingArea.Top,
          screen.WorkingArea.Width,
          screen.WorkingArea.Height
        );

        if (screenRect.IntersectsWith(windowRect))
        {
          isOffScreen = false;
          break;
        }
      }

      if (isOffScreen)
      {
        // Move the window to the center of the primary screen
        main.Width = App.DefaultWidth;
        main.Height = App.DefaultHeight;
        main.Left = 0;
        main.Top = 0;
        Log.Info($"Window is Offscreen. Changing Window Pos ({main.Top}, {main.Left})");
        Log.Info($"Window is Offscreen. Changing Window Size ({main.Width}, {main.Height})");
      }
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
      if (ex.Exception.InnerException is Exception { } inner)
      {
        if (inner.StackTrace?.Contains("EditControl") == true && inner.Message?.StartsWith("Index", StringComparison.OrdinalIgnoreCase) == true)
        {
          // Ignore EditControl index out of range exceptions
          return;
        }
      }

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
