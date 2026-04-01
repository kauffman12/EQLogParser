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
      // 33.x
      SyncfusionLicenseProvider.RegisterLicense("");
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
          var hardwareAccel = ConfigUtil.GetSetting("HardwareAcceleration");
          if (hardwareAccel == null)
          {
            ConfigUtil.SetSetting("HardwareAcceleration", true);
          }
        }

        RenderOptions.ProcessRenderMode = ConfigUtil.IfSet("HardwareAcceleration") ? RenderMode.Default : RenderMode.SoftwareOnly;

        if (!ConfigUtil.IfSet("HideSplashScreen"))
        {
          _splash.Show();
        }

        // Set Debug level
        SetLoggingLevel();

        var osVersion = Environment.OSVersion;
        Version = ResourceAssembly.GetName().Version!.ToString()[..^2];
        Log.Info($"EQLogParser: {Version}, OS: {osVersion.VersionString}, DotNet: {Environment.Version}, RenderMode: {RenderOptions.ProcessRenderMode}");

        var urlVersion = Version.Replace(".", "-");
        ReleaseNotesUrl = $"{ParserHome}/releasenotes.html#{urlVersion}";

        ConfigUtil.UpdateStatus($"RenderMode: {RenderOptions.ProcessRenderMode}");

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

      fileAppender.ActivateOptions();
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

    // WPF ValidateTopLeft rejects values outside Int32 range.
    private static bool IsValidTopLeft(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && v >= int.MinValue && v <= int.MaxValue;

    private static (double top, double left) SanitizePosition(double savedTop, double savedLeft)
    {
      var top = IsValidTopLeft(savedTop) ? savedTop : double.NaN;
      var left = IsValidTopLeft(savedLeft) ? savedLeft : double.NaN;
      return (top, left);
    }

    private async Task ShowMain()
    {
      // Read saved values (including possibly-corrupt ones)
      var savedHeight = ConfigUtil.GetSettingAsDouble("WindowHeight", DefaultHeight);
      var savedWidth = ConfigUtil.GetSettingAsDouble("WindowWidth", DefaultWidth);
      var savedTop = ConfigUtil.GetSettingAsDouble("WindowTop", double.NaN);
      var savedLeft = ConfigUtil.GetSettingAsDouble("WindowLeft", double.NaN);

      // Sanitize Top/Left BEFORE assigning them to a Window (prevents WPF ArgumentException)
      var (top, left) = SanitizePosition(savedTop, savedLeft);

      var main = new MainWindow
      {
        Height = savedHeight,
        Width = savedWidth
        // DO NOT set Top/Left here
      };

      // Only assign if safe (otherwise leave as NaN and let CheckWindowPosition reset/center)
      if (!double.IsNaN(top)) main.Top = top;
      if (!double.IsNaN(left)) main.Left = left;

      ConfigUtil.UpdateStatus("Checking Window Position");
      CheckWindowPosition(main);

      Log.Info($"Window Pos ({main.Top}, {main.Left}) | Window Size ({main.Width}, {main.Height})");

      await Task.Delay(350);

      try
      {
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
          main.WindowState = savedState;
        }

        main.UpdateWindowBorder();

        await Task.Delay(350);
        MainActions.FireWindowStateChanged(main.WindowState);
        main.ConnectLocationChanged();

        // Start archive schedule if configured
        FileUtil.SetArchiveSchedule();
        ConfigUtil.UpdateStatus("Done");

        await Task.Run(async () =>
        {
          // Cleanup downloads
          MainActions.Cleanup();

          if (ConfigUtil.IfSet("CheckUpdatesAtStartup"))
          {
            await MainActions.CheckVersionAsync();
          }
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

    private static bool IsUsablePositiveFinite(double v) =>
      !double.IsNaN(v) && !double.IsInfinity(v) && v > 0;

    private static void CheckWindowPosition(MainWindow main)
    {
      // If position or size are not usable, skip intersection math and just reset/center.
      if (!IsUsablePositiveFinite(main.Width) ||
          !IsUsablePositiveFinite(main.Height) ||
          double.IsNaN(main.Left) || double.IsInfinity(main.Left) ||
          double.IsNaN(main.Top) || double.IsInfinity(main.Top))
      {
        ResetAndCenter(main, "Window had invalid position/size (NaN/Infinity/<=0). Resetting.");
        return;
      }

      const double wiggle = 20;            // pixels
      const double minVisiblePercent = 0.25;

      var windowRect = new Rect(
        main.Left - wiggle,
        main.Top - wiggle,
        main.Width + (wiggle * 2),
        main.Height + (wiggle * 2)
      );

      var windowArea = Math.Max(1, windowRect.Width * windowRect.Height);

      foreach (var screen in System.Windows.Forms.Screen.AllScreens)
      {
        var screenRect = new Rect(
          screen.WorkingArea.Left,
          screen.WorkingArea.Top,
          screen.WorkingArea.Width,
          screen.WorkingArea.Height
        );

        var intersection = Rect.Intersect(screenRect, windowRect);

        if (!intersection.IsEmpty)
        {
          var visibleArea = intersection.Width * intersection.Height;
          var visiblePercent = visibleArea / windowArea;

          if (visiblePercent >= minVisiblePercent)
          {
            return; // enough visible
          }
        }
      }

      ResetAndCenter(main, "Window is mostly off-screen? Resetting position and size.");
    }

    private static void ResetAndCenter(MainWindow main, string reason)
    {
      Log.Info(reason);

      main.Width = App.DefaultWidth;
      main.Height = App.DefaultHeight;

      // Center on primary screen's working area
      var primary = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
      main.Left = primary.Left + ((primary.Width - main.Width) / 2);
      main.Top = primary.Top + ((primary.Height - main.Height) / 2);
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
      // Prevents application from closing
      ex.Handled = true;
      _splash?.SetErrorState();
    }

    private void TaskSchedulerUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs ex)
    {
      if (ex.Exception.InnerException is Exception { } inner)
      {
        if (inner.StackTrace?.Contains("EditControl") == true && inner.Message?.StartsWith("Index", StringComparison.OrdinalIgnoreCase) == true)
        {
          // Ignore EditControl index out of range exceptions
          // May be fixed in Syncfusion updates
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
