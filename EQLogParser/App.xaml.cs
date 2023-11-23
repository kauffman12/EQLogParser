using AutoMapper;
using log4net;
using Syncfusion.Licensing;
using System;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

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
      SyncfusionLicenseProvider.RegisterLicense("LICENSE HERE");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
      base.OnStartup(e);
      AppDomain.CurrentDomain.UnhandledException += DomainUnhandledException;
      AutoMap = new MapperConfiguration(cfg => cfg.AddProfile<MappingProfile>()).CreateMapper();
      Log.Info($"Using DotNet {Environment.Version}");
      RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
      Log.Info("RenderMode: " + RenderOptions.ProcessRenderMode);
    }

    private void DomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
      var exception = e.ExceptionObject as Exception;
      Log.Error(exception?.Message, exception);
    }
  }
}
