# Build-TtsPack.ps1
#
# Builds the GitHub-hosted runtime packs for EQLogParser's optional speech engines. Everything the Piper and Kokoro
# engines need lives here rather than in the app installer, so this script is the only thing standing between an
# incomplete folder and silence on someone's machine: it refuses to pack when a required file is missing and writes a
# manifest of every file's size and SHA-256 that the app verifies before loading any of it.
#
# Expected layout, with this script at the root of the TTS repo (or anywhere, pointing -DataRoot at it):
#
#   piper-tts\           piperApi.dll piper_phonemize.dll espeak-ng.dll onnxruntime*.dll
#                        espeak-ng-data\**                  <- 355 files, keep the tree exactly as shipped
#                        voices\voices.json                 <- or let -GenerateVoicesJson write it
#                        voices\<name>\*.onnx *.onnx.json   <- one folder per voice
#   kokoro\              bin\MisakiSharp.dll NumSharp.dll System.Numerics.Tensors.dll OpenTK*.dll
#                        native\onnxruntime.dll onnxruntime_providers_shared.dll
#                        voices\af_*.npy am_*.npy LICENSE   <- built by the app repo (KokoroVoiceMasks)
#                        model\kokoro-fp16.onnx             <- optional, -SkipModel leaves it out
#
# Only the binaries come from a build. Everything else is data you keep here: espeak-ng-data, the Piper voices and
# the Kokoro model are no longer in the app repo at all, so -Sync copies binaries (plus whatever data an older app
# checkout still carries) and nothing else (sign them there first -- signing rewrites the tail of a PE file, so a
# manifest built before signing will not match what users download).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File Build-TtsPack.ps1 -Inventory
#   powershell -ExecutionPolicy Bypass -File Build-TtsPack.ps1 -Sync -AppRelease C:\src\EQLogParser\EQLogParser\bin\Release\net8.0-windows10.0.17763.0
#   powershell -ExecutionPolicy Bypass -File Build-TtsPack.ps1                     # both packs
#   powershell -ExecutionPolicy Bypass -File Build-TtsPack.ps1 -Pack piper -GenerateVoicesJson
#   powershell -ExecutionPolicy Bypass -File Build-TtsPack.ps1 -Verify out\piper-1.0.zip
#
# Switches:
#   -Inventory            report what is present, missing and unexpected; pack nothing
#   -Sync                 fill the data dirs from -AppRelease: runtime binaries for both engines and the Kokoro .npy
#                         embeddings, plus espeak-ng-data and Piper voices if that build still carries them;
#                         hash-compared, so re-running is cheap
#   -ModelSource <path>    kokoro-fp16.onnx to stage with -Sync. Default: an installed Kokoro pack in local app data,
#                         then the older %LOCALAPPDATA%\EQLogParser\kokoro-tts copy
#   -Pack kokoro|piper|both
#   -PiperVoices a,b      limit which voice folders go in (default: all found)
#   -GenerateVoicesJson   rebuild voices.json from the voice folders (name and sample rate come from each .onnx.json)
#   -SkipModel            leave kokoro\model\kokoro-fp16.onnx out of the Kokoro pack
#   -Strict               fail if a binary we are expected to sign is unsigned, instead of warning
#   -Upload               run gh release upload for each finished pack

param(
    [ValidateSet('kokoro', 'piper', 'both')] [string]$Pack = 'both',
    [string]$DataRoot = $PSScriptRoot,
    [string]$AppRelease = '',
    [string]$ModelSource = '',
    [string]$OutDir = '',
    [string]$PackVersion = '1.0',
    [string[]]$PiperVoices = @('all'),
    [switch]$GenerateVoicesJson,
    [switch]$SkipModel,
    [switch]$Sync,
    [switch]$Inventory,
    [switch]$Strict,
    [switch]$Upload,
    [string]$Repo = 'kauffman12/EQLogParser-TTS',
    [string]$Verify = ''
)

$ErrorActionPreference = 'Stop'
try { Add-Type -AssemblyName System.IO.Compression } catch { }
try { Add-Type -AssemblyName System.IO.Compression.FileSystem } catch { }
if (-not ('System.IO.Compression.ZipFile' -as [type])) { throw 'System.IO.Compression.ZipFile is unavailable in this PowerShell host' }

$KokoroBin = @(
    'MisakiSharp.dll',
    'NumSharp.dll',
    'System.Numerics.Tensors.dll',
    'OpenTK.Audio.OpenAL.dll',
    'OpenTK.Core.dll',
    'OpenTK.Mathematics.dll'
)
$KokoroNative = @('onnxruntime.dll', 'onnxruntime_providers_shared.dll')
$PiperBin = @('piperApi.dll', 'piper_phonemize.dll', 'espeak-ng.dll', 'onnxruntime.dll', 'onnxruntime_providers_shared.dll')

# Files we are expected to have signed ourselves. Microsoft's onnxruntime and System.Numerics.Tensors keep their own
# signatures; re-signing them would replace upstream attribution with ours for no benefit.
$MustBeSigned = @(
    'MisakiSharp.dll', 'NumSharp.dll', 'OpenTK.Audio.OpenAL.dll', 'OpenTK.Core.dll', 'OpenTK.Mathematics.dll',
    'piperApi.dll', 'piper_phonemize.dll', 'espeak-ng.dll'
)

$Notices = @'
Third-party notices for EQLogParser speech runtime packs
========================================================

Kokoro pack
  KokoroSharp                 MIT             https://github.com/Lyrcaxis/KokoroSharp
  MisakiSharp                 Apache-2.0      https://github.com/Larysak/MisakiSharp
  NumSharp                    Apache-2.0      https://github.com/WenqingDai/NumSharp
  OpenTK                      MIT             https://github.com/opentk/opentk
  System.Numerics.Tensors     MIT             https://github.com/dotnet/machinelearning
  Microsoft.ML.OnnxRuntime    MIT             https://github.com/microsoft/onnxruntime
  Kokoro model + voice data   Apache-2.0      https://huggingface.co/hexgrad/Kokoro-82M

Piper pack
  Piper (piperApi)            MIT             https://github.com/rhasspy/piper
  piper_phonemize             MIT             https://github.com/espeak-ng/phomemize
  eSpeak / eSpeak-NG          GPL-3.0         https://github.com/espeak-ng/espeak-ng
  Microsoft.ML.OnnxRuntime    MIT             https://github.com/microsoft/onnxruntime

Individual voice models carry their own licenses; keep the LICENSE file that came with each one beside it.
Full license text is available from the project pages above.
'@

# ------------------------------------------------------------------- helpers
function Get-Sha256Hex([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLower() }

function Get-RelativePath([string]$Root, [string]$Full) {
    $root = (Get-Item -LiteralPath $Root).FullName.TrimEnd('\')
    return $Full.Substring($root.Length).TrimStart('\').Replace('\', '/')
}

function Copy-FileTo([string]$Source, [string]$TargetDir, [string]$Relative) {
    if (-not (Test-Path -LiteralPath $Source)) { throw "missing input: $Source" }
    $target = Join-Path $TargetDir ($Relative -replace '/', '\')
    $dir = Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Copy-Item -LiteralPath $Source -Destination $target -Force
}

function Copy-TreeTo([string]$Source, [string]$TargetDir, [string]$Prefix) {
    if (-not (Test-Path -LiteralPath $Source)) { throw "missing input tree: $Source" }
    foreach ($f in Get-ChildItem -LiteralPath $Source -Recurse -File) {
        Copy-FileTo $f.FullName $TargetDir ($Prefix.TrimEnd('/') + '/' + (Get-RelativePath $Source $f.FullName))
    }
}

function New-ManifestIn([string]$Dir, [string]$Engine, [hashtable]$Extra) {
    $files = @()
    foreach ($f in Get-ChildItem -LiteralPath $Dir -Recurse -File | Where-Object { $_.Name -ne 'manifest.json' }) {
        $files += [pscustomobject]@{ path = (Get-RelativePath $Dir $f.FullName); size = [int64]$f.Length; sha256 = (Get-Sha256Hex $f.FullName) }
    }
    $files = @($files | Sort-Object path)
    if ($files.Count -eq 0) { throw "nothing to manifest in $Dir" }
    $manifest = [ordered]@{
        engine      = $Engine
        packVersion = $PackVersion
        generated   = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        fileCount   = $files.Count
        totalBytes  = ($files | Measure-Object -Property size -Sum).Sum
        files       = $files
    }
    if ($Extra) { foreach ($k in $Extra.Keys) { $manifest[$k] = $Extra[$k] } }
    # Written without a BOM: the app parses this with System.Text.Json.
    [IO.File]::WriteAllText((Join-Path $Dir 'manifest.json'), ($manifest | ConvertTo-Json -Depth 6))
    return $files
}

function New-PackZip([string]$Dir, [string]$ZipPath) {
    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    $zip = [System.IO.Compression.ZipFile]::Open($ZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in Get-ChildItem -LiteralPath $Dir -Recurse -File) {
            $entry = $zip.CreateEntry((Get-RelativePath $Dir $f.FullName), [System.IO.Compression.CompressionLevel]::Optimal)
            $es = $entry.Open()
            try { $fs = [IO.File]::OpenRead($f.FullName); try { $fs.CopyTo($es) } finally { $fs.Dispose() } }
            finally { $es.Dispose() }
        }
    } finally { $zip.Dispose() }
    $hex = Get-Sha256Hex $ZipPath
    [IO.File]::WriteAllText("$ZipPath.sha256", "$hex  $(Split-Path -Leaf $ZipPath)`n")
    return $hex
}

function Test-PackSignatures([string]$Dir) {
    $unsigned = @()
    foreach ($f in Get-ChildItem -LiteralPath $Dir -Recurse -File |
            Where-Object { $_.Extension -eq '.dll' -and $MustBeSigned -contains $_.Name }) {
        $status = (Get-AuthenticodeSignature -LiteralPath $f.FullName).Status
        $sig = Get-AuthenticodeSignature -LiteralPath $f.FullName
        if ($sig.Status -eq 'NotSigned') {
            $unsigned += $f.Name
            Write-Warning "$(Get-RelativePath $Dir $f.FullName) is unsigned; sign it in the app repo (sign.cmd) and run -Sync again"
        } else {
            $who = 'unknown'
            if ($sig.SignerCertificate) { $who = $sig.SignerCertificate.Subject }
            Write-Host ("  signed {0,-32} {1}" -f $f.Name, $who)
        }
    }
    if ($unsigned.Count -gt 0 -and $Strict) { throw "unsigned pack files: $($unsigned -join ', ')" }
}

function Get-VoiceDirs([string]$VoicesRoot) {
    if (-not (Test-Path -LiteralPath $VoicesRoot)) { return @() }
    $found = @(Get-ChildItem -LiteralPath $VoicesRoot -Directory |
        Where-Object { @(Get-ChildItem -LiteralPath $_.FullName -Filter *.onnx -File).Count -gt 0 })
    if ($PiperVoices.Count -eq 1 -and $PiperVoices[0] -eq 'all') { return $found }
    $picked = @()
    foreach ($name in $PiperVoices) {
        $d = $found | Where-Object { $_.Name -ieq $name } | Select-Object -First 1
        if (-not $d) { throw "voice '$name' not under $VoicesRoot (have: $($found.Name -join ', '))" }
        $picked += $d
    }
    return $picked
}

function Get-DirBytes([string]$Path) {
    $sum = 0
    foreach ($f in Get-ChildItem -LiteralPath $Path -Recurse -File) { $sum += $f.Length }
    return $sum
}

function Write-PackResult([string]$Label, [string]$ZipPath, [string]$Hex, [int]$FileCount) {
    $leaf = Split-Path -Leaf $ZipPath
    $mb = [math]::Round((Get-Item -LiteralPath $ZipPath).Length / 1MB, 1)
    $tag = "$Label-$PackVersion"
    Write-Host ''
    Write-Host "=== $Label ==="
    Write-Host ("  {0}  ({1} MB, {2} payload files + manifest.json)" -f $leaf, $mb, $FileCount)
    Write-Host "  sha256        $Hex"
    Write-Host "  release tag   $tag"
    Write-Host "  url to pin    https://github.com/$Repo/releases/download/$tag/$leaf"
    if ($Upload) {
        Write-Host '  uploading...'
        & gh release upload $tag $ZipPath "$ZipPath.sha256" --repo $Repo
        if ($LASTEXITCODE -ne 0) { throw "gh release upload failed for $tag" }
    } else {
        Write-Host "  publish with  gh release create $tag `"$ZipPath`" `"$ZipPath.sha256`" --repo $Repo --title `"$Label $PackVersion`""
    }
}

# ------------------------------------------------------------------- sources
$DataRoot = (Resolve-Path -LiteralPath $DataRoot).Path
$PiperDir = Join-Path $DataRoot 'piper-tts'
$KokoroDir = Join-Path $DataRoot 'kokoro'
if (-not $OutDir) { $OutDir = Join-Path $DataRoot 'out' }

function Sync-FromBuild {
    if ($AppRelease -and -not (Test-Path -LiteralPath $AppRelease)) { throw "no such build output: $AppRelease" }
    if (-not (Test-Path -LiteralPath $AppRelease)) { throw "no such build output: $AppRelease" }
    $AppRelease = (Resolve-Path -LiteralPath $AppRelease).Path
    $copied = 0
    foreach ($n in $PiperBin) {
        $s = Join-Path $AppRelease "piper-tts\$n"
        if (Test-Path -LiteralPath $s) { Copy-IfChanged $s (Join-Path $PiperDir $n); $copied++ }
    }
    # espeak-ng-data and the voice folders used to be in the app repo, which is where the old installer picked them
    # up. They are this repo's data now, so a current build contributes nothing here; an older checkout still does.
    $copied += Sync-Tree (Join-Path $AppRelease 'piper-tts\espeak-ng-data') (Join-Path $PiperDir 'espeak-ng-data')
    $srcVoices = Join-Path $AppRelease 'piper-tts\voices'
    if (Test-Path -LiteralPath $srcVoices) {
        foreach ($d in Get-ChildItem -LiteralPath $srcVoices -Directory) {
            $copied += Sync-Tree $d.FullName (Join-Path $PiperDir "voices\$($d.Name)")
        }
    }
    foreach ($n in $KokoroBin) {
        $s = Join-Path $AppRelease $n
        if (Test-Path -LiteralPath $s) { Copy-IfChanged $s (Join-Path $KokoroDir "bin\$n"); $copied++ }
    }
    foreach ($n in $KokoroNative) {
        $s = Join-Path $AppRelease "runtimes\win-x64\native\$n"
        if (Test-Path -LiteralPath $s) { Copy-IfChanged $s (Join-Path $KokoroDir "native\$n"); $copied++ }
    }
    # Kokoro voice embeddings are produced by the app build; KokoroVoiceMasks in Directory.Build.targets chooses them.
    $vs = Join-Path $AppRelease 'voices'
    if (Test-Path -LiteralPath $vs) {
        foreach ($f in Get-ChildItem -LiteralPath $vs -File | Where-Object { $_.Extension -eq '.npy' -or $_.Name -ieq 'LICENSE' }) {
            Copy-IfChanged $f.FullName (Join-Path $KokoroDir "voices\$($f.Name)")
            $copied++
        }
    }
    # The graph is not in the build output. Whoever last ran Kokoro has it: an installed runtime pack, or the local
    # app data copy written before packs existed. Neither exists on a machine that never spoke Kokoro, hence -ModelSource.
    $modelSrc = $ModelSource
    if (-not $modelSrc) {
        foreach ($guess in @(
            (Join-Path $env:LOCALAPPDATA 'EQLogParser\kokoro\model\kokoro-fp16.onnx'),
            (Join-Path $env:LOCALAPPDATA 'EQLogParser\kokoro-tts\kokoro-fp16.onnx'))) {
            if (Test-Path -LiteralPath $guess) { $modelSrc = $guess; break }
        }
    }
    if ($modelSrc -and (Test-Path -LiteralPath $modelSrc)) {
        if (Copy-IfChanged $modelSrc (Join-Path $KokoroDir 'model\kokoro-fp16.onnx')) { $copied++ }
    } else {
        Write-Host "  no kokoro model found ($modelSrc); pass -ModelSource <path> to stage one"
    }
    Write-Host "sync: $copied file(s) new or changed"
}

function Sync-Tree([string]$SourceDir, [string]$TargetDir) {
    if (-not (Test-Path -LiteralPath $SourceDir)) { return 0 }
    $count = 0
    foreach ($f in Get-ChildItem -LiteralPath $SourceDir -Recurse -File) {
        $rel = Get-RelativePath $SourceDir $f.FullName
        if (Copy-IfChanged $f.FullName (Join-Path $TargetDir ($rel -replace '/', '\')) -Quiet) { $count++ }
    }
    if ($count -gt 0) { Write-Host ("  {0}: {1} file(s) new or changed" -f (Split-Path -Leaf $SourceDir), $count) }
    return $count
}

# Checked before anything is staged, so a half-populated data dir produces one readable list instead of an exception from
# three functions deep -- after the other pack has already been built.
function Get-KokoroMissing {
    $m = @()
    foreach ($n in $KokoroBin) { if (-not (Test-Path -LiteralPath (Join-Path $KokoroDir "bin\$n"))) { $m += "kokoro/bin/$n" } }
    foreach ($n in $KokoroNative) { if (-not (Test-Path -LiteralPath (Join-Path $KokoroDir "native\$n"))) { $m += "kokoro/native/$n" } }
    $v = Join-Path $KokoroDir 'voices'
    if (@(Get-ChildItem -LiteralPath $v -Filter *.npy -File -ErrorAction SilentlyContinue).Count -eq 0) { $m += 'kokoro/voices/*.npy' }
    return $m
}

function Get-PiperMissing {
    $m = @()
    foreach ($n in $PiperBin) { if (-not (Test-Path -LiteralPath (Join-Path $PiperDir $n))) { $m += "piper-tts/$n" } }
    if (-not (Test-Path -LiteralPath (Join-Path $PiperDir 'espeak-ng-data'))) { $m += 'piper-tts/espeak-ng-data/  (355 files: phoneme tables and language data Piper needs to speak at all)' }
    $vd = Join-Path $PiperDir 'voices'
    if (@(Get-VoiceDirs $vd).Count -eq 0) { $m += 'piper-tts/voices/<name>/*.onnx  (at least one voice folder holding a model)' }
    if (-not $GenerateVoicesJson -and -not (Test-Path -LiteralPath (Join-Path $vd 'voices.json'))) { $m += "piper-tts/voices/voices.json  (or pass -GenerateVoicesJson to build it from the voice folders)" }
    return $m
}

function Assert-PackInputs([string]$Engine, [object[]]$Missing) {
    if ($Missing.Count -eq 0) { Write-Host "$Engine inputs: complete"; return }
    $lines = @("$Engine pack is missing input(s):")
    foreach ($x in $Missing) { $lines += "  $x" }
    $lines += 'fix by copying them into the data dirs, or run:'
    $lines += "  .\Build-TtsPack.ps1 -Sync -AppRelease <EQLogParser bin\Release\net8.0-windows10.0.17763.0>"
    $lines += 'then: .\Build-TtsPack.ps1 -Inventory'
    throw ($lines -join [Environment]::NewLine)
}

function Copy-IfChanged([string]$Source, [string]$Target, [switch]$Quiet) {
    if ((Test-Path -LiteralPath $Target) -and (Get-Sha256Hex $Target) -eq (Get-Sha256Hex $Source)) { return $false }
    Copy-FileTo $Source (Split-Path -Parent $Target) (Split-Path -Leaf $Target)
    if (-not $Quiet) { Write-Host "  copied $(Split-Path -Leaf $Source)" }
    return $true
}

# ------------------------------------------------------------------- verify
if ($Verify) {
    if (-not (Test-Path -LiteralPath $Verify)) { throw "no such zip: $Verify" }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('ttsverify_' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($Verify, $tmp)
        $mp = Join-Path $tmp 'manifest.json'
        if (-not (Test-Path -LiteralPath $mp)) { throw 'zip contains no manifest.json' }
        $m = Get-Content -LiteralPath $mp -Raw | ConvertFrom-Json
        $bad = 0
        foreach ($e in $m.files) {
            $p = Join-Path $tmp ($e.path -replace '/', '\')
            if (-not (Test-Path -LiteralPath $p)) { Write-Host "MISSING $($e.path)"; $bad++; continue }
            if ((Get-Item -LiteralPath $p).Length -ne $e.size) { Write-Host "SIZE    $($e.path)"; $bad++; continue }
            if ((Get-Sha256Hex $p) -ne $e.sha256.ToLower()) { Write-Host "HASH    $($e.path)"; $bad++ }
        }
        Write-Host ("verified {0}: engine={1} packVersion={2} files={3}" -f (Split-Path -Leaf $Verify), $m.engine, $m.packVersion, $m.files.Count)
        if ($bad -gt 0) { throw "$bad file(s) did not match the manifest" }
        Write-Host 'all files match'
        if (Test-Path -LiteralPath "$Verify.sha256") {
            $want = (Get-Content -LiteralPath "$Verify.sha256" -TotalCount 1).Split(' ')[0].ToLower()
            $have = Get-Sha256Hex $Verify
            if ($want -ne $have) { throw "sidecar mismatch: sidecar=$want zip=$have" }
            Write-Host "sidecar matches  $have"
        } else { Write-Warning 'no .sha256 sidecar beside this zip' }
    } finally { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
    return
}

# ------------------------------------------------------------------- inventory
if ($Inventory -or $Sync) { Sync-FromBuild }

if ($Inventory) {
    Write-Host "data root: $DataRoot"
    Write-Host ''
    Write-Host 'piper voices:'
    foreach ($v in Get-VoiceDirs (Join-Path $PiperDir 'voices')) {
        Write-Host ("  {0,-26} {1,7:N1} MB" -f $v.Name, ((Get-DirBytes $v.FullName) / 1MB))
    }
    $kv = Join-Path $KokoroDir 'voices'
    $npy = @(Get-ChildItem -LiteralPath $kv -Filter *.npy -File -ErrorAction SilentlyContinue)
    if ($npy.Count -gt 0) {
        Write-Host ("kokoro voices: {0} .npy, {1:N1} MB" -f $npy.Count, ((Get-DirBytes $kv) / 1MB))
    } else { Write-Host 'kokoro voices: none' }
    $model = Join-Path $KokoroDir 'model\kokoro-fp16.onnx'
    if (Test-Path -LiteralPath $model) {
        Write-Host ("kokoro model : {0:N1} MB  sha256 {1}" -f ((Get-Item $model).Length / 1MB), (Get-Sha256Hex $model))
    } else { Write-Host 'kokoro model : not present (the app downloads it from upstream instead)' }
    Write-Host ''
    $missing = @()
    if ($Pack -eq 'piper' -or $Pack -eq 'both') { $missing += Get-PiperMissing }
    if ($Pack -eq 'kokoro' -or $Pack -eq 'both') { $missing += Get-KokoroMissing }
    if ($missing.Count -eq 0) { Write-Host "inventory: complete for $Pack" }
    else {
        Write-Host "inventory: missing for $Pack"
        $missing | ForEach-Object { Write-Host "  $_" }
    }
    return
}

# ------------------------------------------------------------------- packing
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$OutDir = (Get-Item -LiteralPath $OutDir).FullName
Write-Host "data root: $DataRoot"
Write-Host "output    : $OutDir"

function New-StagedDir([string]$Name) {
    $s = Join-Path $env:TEMP ('ttsstage_' + $Name + '_' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $s | Out-Null
    return $s
}

if ($Pack -eq 'kokoro' -or $Pack -eq 'both') {
    Assert-PackInputs 'kokoro' (Get-KokoroMissing)
    $stage = New-StagedDir 'kokoro'
    try {
        foreach ($n in $KokoroBin) { Copy-FileTo (Join-Path $KokoroDir "bin\$n") $stage "bin/$n" }
        foreach ($n in $KokoroNative) { Copy-FileTo (Join-Path $KokoroDir "native\$n") $stage "native/$n" }

        $voiceSrc = Join-Path $KokoroDir 'voices'
        $npyCount = 0
        foreach ($f in Get-ChildItem -LiteralPath $voiceSrc -Recurse -File | Where-Object { $_.FullName -notmatch '\\voices-zh(\\|$)' }) {
            if ($f.Extension -ne '.npy' -and $f.Name -inotmatch 'LICENSE|COPYING|README') { continue }
            Copy-FileTo $f.FullName $stage "voices/$($f.Name)"
            if ($f.Extension -eq '.npy') { $npyCount++ }
        }
        if ($npyCount -eq 0) { throw "no kokoro .npy voices under $voiceSrc" }

        $model = Join-Path $KokoroDir 'model\kokoro-fp16.onnx'
        $haveModel = $false
        if (Test-Path -LiteralPath $model) {
            if ($SkipModel) { Write-Host "  kokoro model left out ($(Split-Path -Leaf $model))" }
            else { Copy-FileTo $model $stage 'model/kokoro-fp16.onnx'; $haveModel = $true }
        }

        [IO.File]::WriteAllText((Join-Path $stage 'THIRD-PARTY-NOTICES.txt'), $Notices)
        Write-Host ''
        Write-Host "kokoro: $npyCount voices, model $(if ($haveModel) { 'included' } else { 'not included' })"
        Test-PackSignatures $stage
        $files = New-ManifestIn $stage 'kokoro' @{ modelIncluded = [bool]$haveModel; voiceCount = $npyCount }
        $zip = Join-Path $OutDir "kokoro-$PackVersion.zip"
        Write-PackResult 'kokoro' $zip (New-PackZip $stage $zip) $files.Count
    } finally { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($Pack -eq 'piper' -or $Pack -eq 'both') {
    Assert-PackInputs 'piper' (Get-PiperMissing)
    $voices = Get-VoiceDirs (Join-Path $PiperDir 'voices')

    $stage = New-StagedDir 'piper'
    try {
        foreach ($n in $PiperBin) { Copy-FileTo (Join-Path $PiperDir $n) $stage $n }
        Copy-TreeTo (Join-Path $PiperDir 'espeak-ng-data') $stage 'espeak-ng-data'
        $voiceBytes = 0
        foreach ($v in $voices) {
            Copy-TreeTo $v.FullName $stage "voices/$($v.Name)"
            $vb = Get-DirBytes $v.FullName
            $voiceBytes += $vb
            Write-Host ("  voice {0,-26} {1,7:N1} MB" -f $v.Name, ($vb / 1MB))
        }

        if ($GenerateVoicesJson) {
            $entries = @()
            foreach ($v in $voices) {
                foreach ($model in Get-ChildItem -LiteralPath $v.FullName -Filter *.onnx -File) {
                    $json = "$($model.FullName).json"
                    if (-not (Test-Path -LiteralPath $json)) { Write-Warning "$($v.Name): $($model.Name) has no .onnx.json beside it, skipping"; continue }
                    $sample = 22050
                    $name = $v.Name
                    try {
                        $j = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
                        if ($j.audio.sample_rate) { $sample = [int]$j.audio.sample_rate }
                        if ($j._meta.name) { $name = [string]$j._meta.name }
                    } catch { Write-Warning "$($v.Name): unreadable $($model.Name).json" }
                    $entries += [ordered]@{
                        Name   = $name
                        Config = "$($v.Name)/$(Split-Path -Leaf $json)"
                        Model  = "$($v.Name)/$($model.Name)"
                        Sample = $sample
                    }
                }
            }
            if ($entries.Count -eq 0) { throw 'generated voices.json would list nothing' }
            # Names come from each model's _meta.name; paths stay voices-relative with forward slashes, matching the
            # format PiperTtsEngine expects.
            $text = (@{ Voices = $entries } | ConvertTo-Json -Depth 5)
            [IO.File]::WriteAllText((Join-Path $stage 'voices\voices.json'), $text)
            # Keep the data dir in step with the pack, so the mapping that ships is the one in git.
            [IO.File]::WriteAllText((Join-Path $PiperDir 'voices\voices.json'), $text)
            Write-Host "  voices.json generated for $($entries.Count) model(s) and written to piper-tts\voices\"
        } else {
            $vj = Join-Path $PiperDir 'voices\voices.json'
            if (-not (Test-Path -LiteralPath $vj)) { throw "no voices.json at $vj (or use -GenerateVoicesJson)" }
            Copy-FileTo $vj $stage 'voices/voices.json'
        }

        # A name in voices.json whose files are not in the pack is silence on a user's machine, so it fails the build --
        # and says what is actually on disk, because the usual cause is a voice folder whose files are named differently
        # than the entry claims. -GenerateVoicesJson builds the mapping from the files themselves and cannot drift.
        $doc = Get-Content -LiteralPath (Join-Path $stage 'voices\voices.json') -Raw | ConvertFrom-Json
        foreach ($entry in $doc.Voices) {
            foreach ($rel in @($entry.Model, $entry.Config)) {
                # voices.json paths are relative to the voices directory (that is how PiperTtsEngine resolves them), so
                # they land under voices/ inside the pack -- not at the pack root.
                $inPack = Join-Path $stage ('voices\' + ($rel -replace '/', '\'))
                if (Test-Path -LiteralPath $inPack) { continue }

                $folder = Split-Path -Parent $rel
                $srcFolder = Join-Path $PiperDir ('voices\' + ($folder -replace '/', '\'))
                $lines = @("voices.json lists '$($entry.Name)' -> $rel which is not in the pack")
                if (-not (Test-Path -LiteralPath $srcFolder)) {
                    $have = @(Get-VoiceDirs (Join-Path $PiperDir 'voices')).Name -join ', '
                    $lines += "  there is no voice folder '$folder' under piper-tts\voices (found: $have)"
                } else {
                    $listing = @()
                    foreach ($f in Get-ChildItem -LiteralPath $srcFolder -File | Select-Object -First 6) { $listing += $f.Name }
                    $lines += "  piper-tts\voices\$folder actually contains: $($listing -join ', ')"
                    $wantExt = [IO.Path]::GetExtension($rel)
                    $key = [IO.Path]::GetFileNameWithoutExtension($rel)
                    $near = Get-ChildItem -LiteralPath $srcFolder -File |
                        Where-Object { $_.Name -like "*$key*" -or $_.Extension -eq $wantExt } |
                        Select-Object -First 3
                    foreach ($c in $near) {
                        if ($c.Extension -eq $wantExt -and $c.Name -ne [IO.Path]::GetFileName($rel)) {
                            $lines += "    did you mean: $folder/$($c.Name)"
                        }
                    }
                }
                $lines += 'fix the entry, or run with -GenerateVoicesJson to build voices.json from the files on disk'
                throw ($lines -join [Environment]::NewLine)
            }
        }

        [IO.File]::WriteAllText((Join-Path $stage 'THIRD-PARTY-NOTICES.txt'), $Notices)
        Write-Host ''
        Write-Host ("piper: {0} voice(s), {1:N0} MB of voice data" -f $voices.Count, ($voiceBytes / 1MB))
        Test-PackSignatures $stage
        $files = New-ManifestIn $stage 'piper' @{ voiceCount = @($doc.Voices).Count }
        $zip = Join-Path $OutDir "piper-$PackVersion.zip"
        Write-PackResult 'piper' $zip (New-PackZip $stage $zip) $files.Count
    } finally { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host 'publishing rules:'
Write-Host '  - upload the .sha256 sidecar next to each zip; it is also the digest GitHub shows for the asset'
Write-Host '  - never overwrite a published asset: released apps pin the tag, so fix mistakes with a new version'
Write-Host '  - re-check what came back down with: -Verify <zip>'
