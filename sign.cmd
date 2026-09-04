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
    "%RELEASE_DIR%\EQLogParser.Core.dll"
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
    "%RELEASE_DIR%\KokoroSharp.dll"
    "%RELEASE_DIR%\Microsoft.ML.OnnxRuntime.dll"
    "%BACKUP_DIR%\BackupUtil.exe"
    "%BACKUP_DIR%\BackupUtil.dll"
) do (
    call :SignFile "%%~F" || goto :fail
)

rem ---------------------------------------------------------------------------
rem TTS runtime packs. These files are no longer in the installer: they are zipped up and published to GitHub, then
rem downloaded into %LOCALAPPDATA%\EQLogParser\<engine> by whoever enables the engine. Sign them anyway: a downloaded
rem DLL carrying our certificate is treated far more calmly by antivirus heuristics than an anonymous one, and it gives
rem users a publisher to look at beyond a hash. Already-signed vendor files (Microsoft's onnxruntime and
rem System.Numerics.Tensors) are skipped rather than overwritten, so their upstream attribution survives.
rem
rem Piper's own binaries are absent from this list on purpose: nothing under {app} is Piper, and EQLogParser no longer
rem carries piperApi/piper_phonemize/espeak-ng at all (see docs/TtsPacks.md). They live in the TTS data repo's staging
rem tree, so a Piper binary bump gets signed there before packing -- Build-TtsPack.ps1 -Verify reports any that are not.
rem
rem IMPORTANT: sign first, then build the SHA-256 manifest the app verifies against. Signing rewrites the tail of the
rem file, so hashes taken before this step will not match what users download.
rem ---------------------------------------------------------------------------
for %%F in (
    "%RELEASE_DIR%\MisakiSharp.dll"
    "%RELEASE_DIR%\NumSharp.dll"
    "%RELEASE_DIR%\System.Numerics.Tensors.dll"
    "%RELEASE_DIR%\OpenTK.Audio.OpenAL.dll"
    "%RELEASE_DIR%\OpenTK.Core.dll"
    "%RELEASE_DIR%\OpenTK.Mathematics.dll"
    "%RELEASE_DIR%\runtimes\win-x64\native\onnxruntime.dll"
    "%RELEASE_DIR%\runtimes\win-x64\native\onnxruntime_providers_shared.dll"
) do (
    call :SignPackFile "%%~F" || goto :fail
)

rem ---------------------------------------------------------------------------
rem The MSVC runtime installed app-local beside EQLogParser.exe (see EQLogParser\redist\README.md). These ship signed
rem by Microsoft and go into the installer as they are, so this only catches the case where someone refreshed the
rem folder from a source that was not signed. Same rule as the pack files: a vendor signature is left alone rather than
rem replaced with ours.
rem ---------------------------------------------------------------------------
for %%F in (
    "%RELEASE_DIR%\redist\msvcp140.dll"
    "%RELEASE_DIR%\redist\msvcp140_1.dll"
    "%RELEASE_DIR%\redist\vcruntime140.dll"
    "%RELEASE_DIR%\redist\vcruntime140_1.dll"
) do (
    call :SignPackFile "%%~F" || goto :fail
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

:SignPackFile
if not exist "%~1" (
    echo Warning: pack file not found, skipping: %~1
    exit /b 0
)

set "SIGSTATE="
for /f "usebackq delims=" %%S in (`powershell -NoProfile -Command "(Get-AuthenticodeSignature -LiteralPath '%~1').Status" 2^>nul`) do set "SIGSTATE=%%S"
if not defined SIGSTATE goto :SignPackFile_Go
if /i "%SIGSTATE%"=="NotSigned" goto :SignPackFile_Go
echo Already signed by its vendor ^(=%SIGSTATE%^), leaving it alone: %~nx1
exit /b 0

:SignPackFile_Go
echo Signing pack file %~1
"%SIGNTOOL%" sign /tr "%TIMESTAMP%" /td sha256 /fd sha256 /a "%~1"
if errorlevel 1 exit /b 1
exit /b 0

:fail
echo Signing failed or was cancelled. Stopping.
exit /b 1