using AutoMapper;
using log4net;
using Syncfusion.Licensing;
using System;
using System.Windows;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for App.xaml
  /// </summary>
  public partial class App : Application
  {
    internal IMapper AutoMap;
    private static readonly ILog LOG = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    public App()
    {
      SyncfusionLicenseProvider.RegisterLicense("LICENSE HERE");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
      base.OnStartup(e);
      AppDomain.CurrentDomain.UnhandledException += DomainUnhandledException;
      AutoMap = new MapperConfiguration(cfg => cfg.AddProfile<MappingProfile>()).CreateMapper();
      LOG.Info($"Using DotNet {Environment.Version}");
    }

    private void DomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
      var exception = e.ExceptionObject as Exception;
      LOG.Error(exception.Message, exception);
    }
  }
}
