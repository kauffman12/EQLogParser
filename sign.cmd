@echo off
setlocal

:: Set the path to your Release directory
set RELEASE_DIR=EQLogParser\bin\Release\net8.0-windows10.0.17763.0
set MSI_DIR=EQLogParserMSI\bin\Release
set CERT_PATH=c:\Users\kauff\Documents\signer2.pfx

if exist "%RELEASE_DIR%\EQLogParser.exe" (
    echo Signing executable in Release directory...
    "c:\Program Files (x86)\Windows Kits\10\App Certification Kit\signtool" sign /fd sha256 /f "%CERT_PATH%" /d "John Kauffman" /p C0smos2014 "%RELEASE_DIR%\EQLogParser.exe"
    "c:\Program Files (x86)\Windows Kits\10\App Certification Kit\signtool" sign /fd sha256 /f "%CERT_PATH%" /d "John Kauffman" /p C0smos2014 "%RELEASE_DIR%\EQLogParser.dll"
) else (
    echo Release directory not found, skipping signing...
)

:: Check if any .msi file starting with EQLogParser exists
set "FOUND=FALSE"
for %%f in ("%MSI_DIR%\EQLogParser*.msi") do (
    if exist "%%~f" (
        echo Found MSI: %%~f
        echo Signing MSI
        "c:\Program Files (x86)\Windows Kits\10\App Certification Kit\signtool" sign /fd sha256 /f "%CERT_PATH%" /d "John Kauffman" /p C0smos2014 %%~f
        set "FOUND=TRUE"
        goto :found
    )
)

:found
if "%FOUND%"=="FALSE" (
    echo No matching MSI file found.
)

endlocal


