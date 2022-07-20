using System.Windows;

namespace EQLogParser
{
  /// <summary>
  /// Interaction logic for App.xaml
  /// </summary>
  public partial class App : Application
  {
    public App()
    {
      Syncfusion.Licensing.SyncfusionLicenseProvider.RegisterLicense("key");
    }

    private void CloseOverlay_MouseClick(object sender, RoutedEventArgs e) => OverlayUtil.ResetOverlay(Dispatcher);
  }
}
