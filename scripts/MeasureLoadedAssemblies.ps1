# MeasureLoadedAssemblies.ps1
#
# Determines the MINIMAL set of files the installer/signer must ship by
# measuring which PE modules the running app actually loads. This replaces
# the manual "remove until it breaks" loop: you run the app once, exercise
# every feature, and get an exact list of what was loaded (lazy-loaded
# assemblies only appear after their code path is touched).
#
# Usage (from repo root or anywhere):
#   powershell -ExecutionPolicy Bypass -File scripts\MeasureLoadedAssemblies.ps1
#
#   Optional:
#     -ExePath "C:\...\EQLogParser\bin\Release\net8.0-windows10.0.17763.0\EQLogParser.exe"
#     -ExePath "C:\...\BackupUtil\bin\Release\net8.0-windows10.0.17763.0\BackupUtil.exe"   # also covers BackupUtil
#
# Run with 64-bit PowerShell (x64). If the app runs elevated, run this
# elevated too or module enumeration will fail.
#
# Output file lists:
#   [LOADED]      - files in the app dir that were loaded during your session
#                   => these are what the installer must ship
#   [NOT LOADED]  - other dll/exe files in the app dir that were never loaded
#                   => candidates for removal from install/sign lists

param(
    [string]$ExePath = "$PSScriptRoot\..\EQLogParser\bin\Release\net8.0-windows10.0.17763.0\EQLogParser.exe",
    [string]$OutputFile = ""
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $ExePath)) { throw "Exe not found: $ExePath" }
$appDir = (Get-Item (Split-Path -Parent $ExePath)).FullName.TrimEnd('\') + '\'
$appName = [IO.Path]::GetFileNameWithoutExtension($ExePath)
if ([string]::IsNullOrEmpty($OutputFile)) { $OutputFile = "$appName.loadedmodules.txt" }

# Files in the app directory (what a minimal installer would ship)
$localFiles = @{}
foreach ($f in (Get-ChildItem -Path $appDir -Recurse -Include *.dll, *.exe)) {
    $rel = $f.FullName.Substring($appDir.Length).ToLower()
    $localFiles[$rel] = $true
}

Write-Host "Starting: $ExePath"
$proc = Start-Process -FilePath $ExePath -WorkingDirectory $appDir -PassThru

Write-Host ""
Write-Host "Now exercise EVERY feature you ship (play logs, all settings pages,"
Write-Host "backup, stats, etc.). Assemblies load lazily - a dll only shows up"
Write-Host "after its code path is actually touched."
Write-Host ""
Write-Host "Watching loaded modules... press Enter when you are DONE using the app."

$loaded = @{}
$moduleError = $false
while (-not [Console]::KeyAvailable) {
    try {
        foreach ($m in $proc.Modules) {
            if ($m.FileName.StartsWith($appDir, [System.StringComparison]::OrdinalIgnoreCase)) {
                $rel = $m.FileName.Substring($appDir.Length).ToLower()
                if ($localFiles.ContainsKey($rel)) { $loaded[$m.ModuleName] = $rel }
            }
        }
    } catch {
        if (-not $moduleError) {
            $moduleError = $true
            Write-Warning "Cannot enumerate modules (32/64-bit mismatch or elevation). Use 64-bit PowerShell, elevated if the app is. Stopping watch."
            break
        }
    }
    Start-Sleep -Seconds 2
}
[Console]::ReadKey() | Out-Null

# Final catch after teardown paths have run
try {
    foreach ($m in $proc.Modules) {
        if ($m.FileName.StartsWith($appDir, [System.StringComparison]::OrdinalIgnoreCase)) {
            $rel = $m.FileName.Substring($appDir.Length).ToLower()
            if ($localFiles.ContainsKey($rel)) { $loaded[$m.ModuleName] = $rel }
        }
    }
} catch { }

$proc.CloseMainWindow() | Out-Null
Start-Sleep -Seconds 2
if (-not $proc.HasExited) {
    Start-Sleep -Seconds 5
    if (-not $proc.HasExited) { Write-Warning "App did not exit, leaving it running (PID $($proc.Id))." }
}

$out = @()
$out += "# $appName - measured loaded modules"
$out += "# Exe: $ExePath"
$out += "# Generated: $(Get-Date -Format 's')"
$out += ""
$out += "## Must ship (loaded during session):"
foreach ($name in ($loaded.Keys | Sort-Object)) { $out += "[LOADED]     $($loaded[$name])" }
$out += ""
$out += "## Present in app dir but never loaded (removal candidates):"
foreach ($rel in ($localFiles.Keys | Where-Object { $loaded.Values -notcontains $_ } | Sort-Object)) {
    $out += "[NOT LOADED] $rel"
}
$out | Out-File -FilePath $OutputFile -Encoding utf8
Write-Host ""
Write-Host "Wrote: $OutputFile"
