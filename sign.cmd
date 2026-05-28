@echo off
setlocal

set "RELEASE_DIR=EQLogParser\bin\Release\net8.0-windows10.0.17763.0"
set "BACKUP_DIR=BackupUtil\bin\Release\net8.0-windows10.0.17763.0"
set "MSI_DIR=EQLogParserMSI\bin\Release"

set "SIGNTOOL=c:\Program Files (x86)\Windows Kits\10\App Certification Kit\signtool"
set "TIMESTAMP=http://timestamp.sectigo.com"

for %%F in (
    "%RELEASE_DIR%\EQLogParser.exe"
    "%RELEASE_DIR%\DotLiquid.dll"
    "%RELEASE_DIR%\EQLogParser.dll"
    "%RELEASE_DIR%\EQLogParser.Audio.dll"
    "%RELEASE_DIR%\EQLogParser.Utils.dll"
    "%RELEASE_DIR%\FontAwesome5.dll"
    "%RELEASE_DIR%\FontAwesome5.Net.dll"
    "%RELEASE_DIR%\LiteDB.dll"
    "%RELEASE_DIR%\log4net.dll"
    "%RELEASE_DIR%\Microsoft.WindowsAPICodePack.dll"
    "%RELEASE_DIR%\Microsoft.WindowsAPICodePack.Shell.dll"
    "%RELEASE_DIR%\Microsoft.Windows.SDK.NET.dll"
    "%RELEASE_DIR%\Microsoft.Extensions.Caching.Abstractions.dll"
    "%RELEASE_DIR%\Microsoft.Extensions.Caching.Memory.dll"
    "%RELEASE_DIR%\Microsoft.Extensions.DependencyInjection.Abstractions.dll"
    "%RELEASE_DIR%\Microsoft.Extensions.Logging.Abstractions.dll"
    "%RELEASE_DIR%\Microsoft.Extensions.ObjectPool.dll"
    "%RELEASE_DIR%\Microsoft.Extensions.Options.dll"
    "%RELEASE_DIR%\Microsoft.Extensions.Primitives.dll"
    "%RELEASE_DIR%\System.Diagnostics.DiagnosticSource.dll"
    "%RELEASE_DIR%\NAudio.dll"
    "%RELEASE_DIR%\NAudio.Core.dll"
    "%RELEASE_DIR%\NAudio.Wasapi.dll"
    "%RELEASE_DIR%\NAudio.WinMM.dll"
    "%RELEASE_DIR%\Riok.Mapperly.Abstractions.dll"
    "%RELEASE_DIR%\SoundTouch.Net.dll"
    "%RELEASE_DIR%\SoundTouch.Net.NAudioSupport.dll"
    "%RELEASE_DIR%\Syncfusion.Compression.Base.dll"
    "%RELEASE_DIR%\Syncfusion.Data.WPF.dll"
    "%RELEASE_DIR%\Syncfusion.DocIO.Base.dll"
    "%RELEASE_DIR%\Syncfusion.Edit.WPF.dll"
    "%RELEASE_DIR%\Syncfusion.GridCommon.WPF.dll"
    "%RELEASE_DIR%\Syncfusion.Licensing.dll"
    "%RELEASE_DIR%\Syncfusion.OfficeChart.Base.dll"
    "%RELEASE_DIR%\Syncfusion.PropertyGrid.WPF.dll"
    "%RELEASE_DIR%\Syncfusion.SfBusyIndicator.WPF.dll"
    "%RELEASE_DIR%\Syncfusion.SfChart.WPF.dll"
    "%RELEASE_DIR%\Syncfusion.SfGrid.WPF.dll"
    "%RELEASE_DIR%\Syncfusion.SfGridCommon.WPF.dll"
    "%RELEASE_DIR%\Syncfusion.SfInput.WPF.dll"
    "%RELEASE_DIR%\Syncfusion.SfProgressBar.WPF.dll"
    "%RELEASE_DIR%\Syncfusion.SfSkinManager.WPF.dll"
    "%RELEASE_DIR%\Syncfusion.SfTreeView.WPF.dll"
    "%RELEASE_DIR%\Syncfusion.Shared.WPF.dll"
    "%RELEASE_DIR%\Syncfusion.Themes.MaterialDark.WPF.dll"
    "%RELEASE_DIR%\Syncfusion.Themes.MaterialDarkCustom.WPF.dll"
    "%RELEASE_DIR%\Syncfusion.Themes.MaterialLight.WPF.dll"
    "%RELEASE_DIR%\Syncfusion.Tools.WPF.Classic.dll"
    "%RELEASE_DIR%\Syncfusion.Tools.WPF.dll"
    "%RELEASE_DIR%\System.Private.ServiceModel.dll"
    "%RELEASE_DIR%\System.ServiceModel.Primitives.dll"
    "%RELEASE_DIR%\System.Drawing.Common.dll"
    "%RELEASE_DIR%\WinRT.Runtime.dll"
    "%RELEASE_DIR%\WpfAnimatedGif.dll"
    "%RELEASE_DIR%\runtimes\win\lib\net8.0\System.Speech.dll"
    "%BACKUP_DIR%\BackupUtil.exe"
    "%BACKUP_DIR%\BackupUtil.dll"
) do (
    call :SignFile "%%~F" || goto :fail
)

for %%F in ("%MSI_DIR%\EQLogParser*.msi") do (
    if exist "%%~fF" (
        call :SignFile "%%~fF" || goto :fail
    )
)

echo Done.
exit /b 0

:SignFile
if not exist "%~1" (
    echo Warning: file not found, skipping: %~1
    exit /b 0
)

echo Signing %~1
"%SIGNTOOL%" sign /tr "%TIMESTAMP%" /td sha256 /fd sha256 /a "%~1"
if errorlevel 1 exit /b 1
exit /b 0

:fail
echo Signing failed or was cancelled. Stopping.
exit /b 1