# Build-TtsPack.ps1
#
# Builds the GitHub-hosted runtime packs that hold everything the optional speech engines need: the Piper runtime plus
# its voices, and the Kokoro runtime plus its voice embeddings. None of this is in the installer any more (see
# docs/TtsPacks.md), so a pack is the only way an engine becomes usable, and every file inside it is checked against a
# SHA-256 manifest by the app before anything is loaded.
#
# Order matters: sign first, build the pack second. Signing rewrites the tail of a PE file, so a manifest generated
# before signing will not match what users download.
#
# Usage (from anywhere, Windows PowerShell 5.1 or newer):
#   powershell -ExecutionPolicy Bypass -File scripts\Build-TtsPack.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\Build-TtsPack.ps1 -Pack kokoro -IncludeModel
#   powershell -ExecutionPolicy Bypass -File scripts\Build-TtsPack.ps1 -Pack piper -PiperVoices all -SplitVoices
#   powershell -ExecutionPolicy Bypass -File scripts\Build-TtsPack.ps1 -Verify build\tts-packs\piper-1.0.zip
#
# Useful switches:
#   -Pack kokoro|piper|both     which pack to build (default both)
#   -PiperVoices a,b,c|all      which voice folders under piper-tts\voices go into the pack (default all found)
#   -GenerateVoicesJson         rebuild voices.json from the voice folders present (name + sample rate read from each
#                               model's .onnx.json), instead of trusting the checked-in one
#   -SplitVoices                emit a runtime-only pack plus one zip per Piper voice, so users fetch only what they
#                               pick instead of every voice you maintain
#   -IncludeModel               put kokoro-fp16.onnx in the Kokoro pack (looked up in -ModelPath, then LocalAppData)
#   -Strict                     fail if a file we expect to sign is still unsigned
#   -Upload                     run "gh release upload" for each finished pack
#
# Output: one zip + one .sha256 sidecar per pack in -OutDir, each containing manifest.json. Prints the digest, the
# release tag to publish under, and the URL to pin in code.

param(
    [ValidateSet('kokoro', 'piper', 'both')] [string]$Pack = 'both',
    [string]$ReleaseDir = "$PSScriptRoot\..\EQLogParser\bin\Release\net8.0-windows10.0.17763.0",
    [string]$OutDir = "$PSScriptRoot\..\build\tts-packs",
    [string]$PackVersion = '1.0',
    [string]$ModelPath = '',
    [string[]]$PiperVoices = @('all'),
    [switch]$GenerateVoicesJson,
    [switch]$SplitVoices,
    [switch]$IncludeModel,
    [switch]$Strict,
    [switch]$Upload,
    [string]$Repo = 'kauffman12/EQLogParser-TTS',
    [string]$Verify = ''
)

$ErrorActionPreference = 'Stop'
try { Add-Type -AssemblyName System.IO.Compression } catch { }
try { Add-Type -AssemblyName System.IO.Compression.FileSystem } catch { }
if (-not ('System.IO.Compression.ZipFile' -as [type])) { throw 'System.IO.Compression.ZipFile is not available in this PowerShell host' }

# Binaries the pack needs. A file missing from this list lands in the zip unannounced; a file listed and absent fails
# the build, which is the point -- an incomplete pack shows up as silence on someone else's machine.
$KokoroRequired = @(
    'MisakiSharp.dll',
    'NumSharp.dll',
    'System.Numerics.Tensors.dll',
    'OpenTK.Audio.OpenAL.dll',
    'OpenTK.Core.dll',
    'OpenTK.Mathematics.dll'
)
$KokoroNative = @('onnxruntime.dll', 'onnxruntime_providers_shared.dll')
$PiperRequired = @(
    'piperApi.dll',
    'piper_phonemize.dll',
    'espeak-ng.dll',
    'onnxruntime.dll',
    'onnxruntime_providers_shared.dll'
)

# Files we are expected to have signed ourselves. Anything Microsoft ships keeps its own signature.
$MustBeSigned = @(
    'MisakiSharp.dll', 'NumSharp.dll', 'OpenTK.Audio.OpenAL.dll', 'OpenTK.Core.dll', 'OpenTK.Mathematics.dll',
    'piperApi.dll', 'piper_phonemize.dll', 'espeak-ng.dll'
)

$Notices = @'
Third-party notices for EQLogParser speech runtime packs
========================================================

Kokoro pack
  KokoroSharp            MIT                     https://github.com/Lyrcaxis/KokoroSharp
  MisakiSharp            Apache-2.0              https://github.com/Larysak/MisakiSharp
  NumSharp               Apache-2.0              https://github.com/WenqingDai/NumSharp
  OpenTK                 MIT                     https://github.com/opentk/opentk
  System.Numerics.Tensors MIT                    https://github.com/dotnet/machinelearning
  Microsoft.ML.OnnxRuntime MIT                   https://github.com/microsoft/onnxruntime
  Kokoro model + voices  Apache-2.0              https://huggingface.co/hexgrad/Kokoro-82M

Piper pack
  Piper (piperApi)       MIT                     https://github.com/rhasspy/piper
  piper_phonemize        MIT                     https://github.com/espeak-ng/espeak-ng-phomemize
  eSpeak / eSpeak-NG     GPL-3.0                 https://github.com/espeak-ng/espeak-ng
  Microsoft.ML.OnnxRuntime MIT                   https://github.com/microsoft/onnxruntime

Voice models carry their own licenses; keep the LICENSE files that came with them next to the model.
The full license text for each component is available from the project pages above.
'@

function Get-Sha256Hex([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLower()
}

function Copy-StageFile([string]$Source, [string]$Stage, [string]$Relative) {
    if (-not (Test-Path -LiteralPath $Source)) { throw "missing input: $Source" }
    $target = Join-Path $Stage ($Relative -replace '/', '\')
    $dir = Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Copy-Item -LiteralPath $Source -Destination $target -Force
    return Get-Item -LiteralPath $target
}

function Copy-StageTree([string]$Source, [string]$Stage, [string]$RelativePrefix) {
    if (-not (Test-Path -LiteralPath $Source)) { throw "missing input tree: $Source" }
    $root = (Get-Item -LiteralPath $Source).FullName.TrimEnd('\')
    foreach ($f in Get-ChildItem -LiteralPath $Source -Recurse -File) {
        $rel = $RelativePrefix.TrimEnd('/') + '/' + $f.FullName.Substring($root.Length).TrimStart('\').Replace('\', '/')
        Copy-StageFile $f.FullName $Stage $rel | Out-Null
    }
}

function Write-Manifest([string]$Stage, [string]$Engine, [object[]]$Extra) {
    $files = @()
    foreach ($f in Get-ChildItem -LiteralPath $Stage -Recurse -File | Where-Object { $_.Name -ne 'manifest.json' }) {
        $rel = $f.FullName.Substring((Get-Item -LiteralPath $Stage).FullName.Length + 1).Replace('\', '/')
        $files += [pscustomobject]@{
            path   = $rel
            size   = [int64]$f.Length
            sha256 = (Get-Sha256Hex $f.FullName)
        }
    }
    $files = @($files | Sort-Object path)
    $manifest = [ordered]@{
        engine      = $Engine
        packVersion = $PackVersion
        generated   = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        fileCount   = $files.Count
        totalBytes  = ($files | Measure-Object -Property size -Sum).Sum
        files       = $files
    }
    if ($Extra) { foreach ($k in $Extra.Keys) { $manifest[$k] = $Extra[$k] } }
    # No BOM: the app reads these with System.Text.Json.
    [IO.File]::WriteAllText((Join-Path $Stage 'manifest.json'), ($manifest | ConvertTo-Json -Depth 6))
    return $files
}

function New-PackZip([string]$Stage, [string]$ZipPath) {
    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    $zip = [System.IO.Compression.ZipFile]::Open($ZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in Get-ChildItem -LiteralPath $Stage -Recurse -File) {
            $rel = $f.FullName.Substring((Get-Item -LiteralPath $Stage).FullName.Length + 1).Replace('\', '/')
            $entry = $zip.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
            $es = $entry.Open()
            try {
                $fs = [IO.File]::OpenRead($f.FullName)
                try { $fs.CopyTo($es) } finally { $fs.Dispose() }
            } finally { $es.Dispose() }
        }
    } finally { $zip.Dispose() }

    $hex = Get-Sha256Hex $ZipPath
    $name = Split-Path -Leaf $ZipPath
    [IO.File]::WriteAllText("$ZipPath.sha256", "$hex  $name`n")
    return $hex
}

function Report-Signatures([string]$Stage) {
    $problems = @()
    foreach ($f in Get-ChildItem -LiteralPath $Stage -Recurse -File |
            Where-Object { $_.Extension -eq '.dll' -or $_.Extension -eq '.exe' }) {
        if ($MustBeSigned -notcontains $f.Name) { continue }
        $sig = Get-AuthenticodeSignature -LiteralPath $f.FullName
        if ($sig.Status -eq 'NotSigned') {
            $problems += $f.Name
            Write-Warning "$($f.Name) is unsigned. Run sign.cmd first -- users get a publisher-less DLL."
        } else {
            $who = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { 'unknown' }
            Write-Host ("  signed: {0,-32} {1}" -f $f.Name, $who)
        }
    }
    if ($problems.Count -gt 0 -and $Strict) {
        throw "unsigned pack files: $($problems -join ', ') (rerun without -Strict to build anyway)"
    }
}

function Find-KokoroModel() {
    if ($ModelPath) { return $ModelPath }
    $candidates = @(
        "$env:LOCALAPPDATA\EQLogParser\kokoro-tts\kokoro-fp16.onnx",
        (Join-Path $ReleaseDir 'kokoro-fp16.onnx')
    )
    foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { return $c } }
    return ''
}

function Get-PiperVoiceFolders() {
    $root = Join-Path $ReleaseDir 'piper-tts\voices'
    if (-not (Test-Path -LiteralPath $root)) { throw "no piper voices at $root" }
    $all = @(Get-ChildItem -LiteralPath $root -Directory | Where-Object { Get-ChildItem -LiteralPath $_.FullName -Filter *.onnx -File | Select-Object -First 1 })
    if ($all.Count -eq 0) { throw "no voice folders containing a .onnx model under $root" }
    if ($PiperVoices.Count -eq 1 -and $PiperVoices[0] -eq 'all') { return , $all }
    $picked = @()
    foreach ($name in $PiperVoices) {
        $d = $all | Where-Object { $_.Name -ieq $name } | Select-Object -First 1
        if (-not $d) { throw "voice '$name' not found under $root (have: $($all.Name -join ', '))" }
        $picked += $d
    }
    return , $picked
}

function New-PiperVoicesJson([string]$Stage, [object[]]$VoiceDirs) {
    $entries = @()
    foreach ($d in $VoiceDirs) {
        foreach ($model in Get-ChildItem -LiteralPath $d.FullName -Filter *.onnx -File) {
            $jsonPath = "$($model.FullName).json"
            if (-not (Test-Path -LiteralPath $jsonPath)) {
                Write-Warning "$($d.Name): $($model.Name) has no .onnx.json beside it, skipping"
                continue
            }
            $sample = 22050
            $name = $d.Name
            try {
                $j = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
                if ($j.audio.sample_rate) { $sample = [int]$j.audio.sample_rate }
                if ($j._meta.name) { $name = [string]$j._meta.name }
            } catch { Write-Warning "$($d.Name): could not read $($model.Name).json ($_)" }
            $entries += [ordered]@{
                Name   = $name
                Config = "$($d.Name)/$([IO.Path]::GetFileName($jsonPath))"
                Model  = "$($d.Name)/$($model.Name)"
                Sample = $sample
            }
        }
    }
    if ($entries.Count -eq 0) { throw 'voices.json would be empty' }
    $doc = [ordered]@{ Voices = $entries }
    [IO.File]::WriteAllText((Join-Path $Stage 'voices\voices.json'), ($doc | ConvertTo-Json -Depth 5))
    return $entries
}

function Assert-VoicesJsonComplete([string]$Stage) {
    $path = Join-Path $Stage 'voices\voices.json'
    if (-not (Test-Path -LiteralPath $path)) { throw "no voices.json in $Stage\voices" }
    $doc = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    foreach ($v in $doc.Voices) {
        foreach ($rel in @($v.Model, $v.Config)) {
            if (-not (Test-Path -LiteralPath (Join-Path $Stage ($rel -replace '/', '\')))) {
                throw "voices.json lists '$($v.Name)' -> $rel but that file is not in the pack"
            }
        }
    }
    return $doc.Voices.Count
}

function Write-PackSummary([string]$Label, [string]$ZipPath, [string]$Hex, [int]$FileCount) {
    $mb = [math]::Round((Get-Item -LiteralPath $ZipPath).Length / 1MB, 1)
    $tag = "$Label-$PackVersion"
    Write-Host ''
    Write-Host "=== $Label ==="
    Write-Host ("  {0}  ({1} MB, {2} files)" -f (Split-Path -Leaf $ZipPath), $mb, $FileCount)
    Write-Host "  sha256 $Hex"
    Write-Host "  release tag   : $tag"
    Write-Host "  asset name    : $(Split-Path -Leaf $ZipPath)"
    Write-Host "  url to pin    : https://github.com/$Repo/releases/download/$tag/$(Split-Path -Leaf $ZipPath)"
    if ($Upload) {
        Write-Host '  uploading...'
        $zipArgs = @($ZipPath, "$ZipPath.sha256")
        & gh release upload $tag $zipArgs --repo $Repo
        if ($LASTEXITCODE -ne 0) { throw "gh release upload failed for $tag" }
    } else {
        Write-Host "  publish with  : gh release create $tag `"$ZipPath`" `"$ZipPath.sha256`" --repo $Repo --title `"$Label $PackVersion`""
    }
}

# ---------------------------------------------------------------- verify mode
if ($Verify) {
    if (-not (Test-Path -LiteralPath $Verify)) { throw "no such zip: $Verify" }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("ttsverify_" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($Verify, $tmp)
        $manifestPath = Join-Path $tmp 'manifest.json'
        if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'zip has no manifest.json' }
        $m = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $bad = 0
        foreach ($e in $m.files) {
            $p = Join-Path $tmp ($e.path -replace '/', '\')
            if (-not (Test-Path -LiteralPath $p)) { Write-Host "MISSING  $($e.path)"; $bad++; continue }
            if ((Get-Item -LiteralPath $p).Length -ne $e.size) { Write-Host "SIZE     $($e.path)"; $bad++; continue }
            if ((Get-Sha256Hex $p) -ne $e.sha256) { Write-Host "HASH     $($e.path)"; $bad++; continue }
        }
        Write-Host ''
        Write-Host ("verified {0}: {1} files, engine={2} packVersion={3}" -f (Split-Path -Leaf $Verify), $m.files.Count, $m.engine, $m.packVersion)
        if ($bad -gt 0) { throw "$bad file(s) did not match the manifest" }
        Write-Host 'all files match'
        if (Test-Path -LiteralPath "$Verify.sha256") {
            $expected = (Get-Content -LiteralPath "$Verify.sha256" -TotalCount 1).Split(' ')[0].ToLower()
            $actual = Get-Sha256Hex $Verify
            if ($expected -ne $actual) { throw "sidecar digest mismatch: $expected vs $actual" }
            Write-Host 'sidecar digest matches'
        } else {
            Write-Warning 'no .sha256 sidecar beside this zip'
        }
    } finally { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
    return
}

# ---------------------------------------------------------------- build mode
if (-not (Test-Path -LiteralPath $ReleaseDir)) { throw "release output not found: $ReleaseDir (build Release first)" }
$ReleaseDir = (Get-Item -LiteralPath $ReleaseDir).FullName
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$OutDir = (Get-Item -LiteralPath $OutDir).FullName
Write-Host "source: $ReleaseDir"
Write-Host "output: $OutDir"

if ($Pack -eq 'kokoro' -or $Pack -eq 'both') {
    $stage = Join-Path $OutDir "stage-kokoro"
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage | Out-Null

    foreach ($dll in $KokoroRequired) { Copy-StageFile (Join-Path $ReleaseDir $dll) $stage "bin/$dll" | Out-Null }
    foreach ($nat in $KokoroNative) {
        Copy-StageFile (Join-Path $ReleaseDir "runtimes\win-x64\native\$nat") $stage "native/$nat" | Out-Null
    }

    $voiceSrc = Join-Path $ReleaseDir 'voices'
    if (-not (Test-Path -LiteralPath $voiceSrc)) { throw "no kokoro voices at $voiceSrc (check KokoroVoiceMasks in Directory.Build.targets)" }
    foreach ($v in Get-ChildItem -LiteralPath $voiceSrc -Recurse -File |
            Where-Object { $_.FullName -notmatch '\\voices-zh(\\|$)' }) {
        if (Test-Path -LiteralPath (Join-Path $stage "voices\$($v.Name)")) {
            throw "duplicate kokoro voice file name: $($v.Name)"
        }
        Copy-StageFile $v.FullName $stage ("voices/" + $v.Name) | Out-Null
    }
    $npy = @(Get-ChildItem -LiteralPath (Join-Path $stage 'voices') -Filter *.npy -File)
    if ($npy.Count -eq 0) { throw 'no .npy voice embeddings staged' }

    $model = Find-KokoroModel
    if ($IncludeModel) {
        if (-not $model) { throw '-IncludeModel given but kokoro-fp16.onnx was not found (pass -ModelPath)' }
        Copy-StageFile $model $stage 'model/kokoro-fp16.onnx' | Out-Null
        Write-Host "  model: $model"
    } elseif ($model) {
        Write-Host "  model left out ($model); add -IncludeModel to ship it inside the pack"
    }

    [IO.File]::WriteAllText((Join-Path $stage 'THIRD-PARTY-NOTICES.txt'), $Notices)
    Report-Signatures $stage
    $files = Write-Manifest $stage 'kokoro' @{ modelIncluded = [bool]$IncludeModel }
    $zip = Join-Path $OutDir "kokoro-$PackVersion.zip"
    $hex = New-PackZip $stage $zip
    Write-PackSummary 'kokoro' $zip $hex $files.Count
    Remove-Item -LiteralPath $stage -Recurse -Force
}

if ($Pack -eq 'piper' -or $Pack -eq 'both') {
    $voices = Get-PiperVoiceFolders
    Write-Host ''
    Write-Host 'piper voices found:'
    $totalBytes = 0
    foreach ($v in $voices) {
        $bytes = (Get-ChildItem -LiteralPath $v.FullName -Recurse -File | Measure-Object -Property Length -Sum).Sum
        $totalBytes += $bytes
        Write-Host ("  {0,-24} {1,7:N1} MB" -f $v.Name, ($bytes / 1MB))
    }
    Write-Host ("  {0,-24} {1,7:N1} MB in {2} voice(s)" -f 'total', ($totalBytes / 1MB), $voices.Count)
    if (-not $SplitVoices -and $totalBytes -gt 150MB) {
        Write-Warning ("this pack carries {0:N0} MB of voices; every Piper user downloads all of them. Consider -SplitVoices." -f ($totalBytes / 1MB))
    }

    $runtimeStage = Join-Path $OutDir 'stage-piper'
    if (Test-Path -LiteralPath $runtimeStage) { Remove-Item -LiteralPath $runtimeStage -Recurse -Force }
    New-Item -ItemType Directory -Path $runtimeStage | Out-Null

    foreach ($dll in $PiperRequired) { Copy-StageFile (Join-Path $ReleaseDir "piper-tts\$dll") $runtimeStage $dll | Out-Null }
    Copy-StageTree (Join-Path $ReleaseDir 'piper-tts\espeak-ng-data') $runtimeStage 'espeak-ng-data'

    foreach ($v in $voices) { Copy-StageTree $v.FullName $runtimeStage "voices/$($v.Name)" }
    if ($GenerateVoicesJson) {
        New-PiperVoicesJson $runtimeStage $voices | Out-Null
        Write-Host "  voices.json generated from $($voices.Count) voice folder(s)"
    } else {
        $vj = Join-Path $ReleaseDir 'piper-tts\voices\voices.json'
        if (-not (Test-Path -LiteralPath $vj)) { throw "no voices.json at $vj (or use -GenerateVoicesJson)" }
        Copy-StageFile $vj $runtimeStage 'voices/voices.json' | Out-Null
    }
    $voiceCount = Assert-VoicesJsonComplete $runtimeStage
    [IO.File]::WriteAllText((Join-Path $runtimeStage 'THIRD-PARTY-NOTICES.txt'), $Notices)

    if ($SplitVoices) {
        # One zip per voice plus a runtime pack without any models, so enabling Piper costs ~25 MB and each extra voice is
        # its own download. voices.json in the runtime pack lists only what it can actually speak; the app adds entries as
        # voice packs land.
        foreach ($v in $voices) {
            $vstage = Join-Path $OutDir "stage-piper-voice-$($v.Name)"
            if (Test-Path -LiteralPath $vstage) { Remove-Item -LiteralPath $vstage -Recurse -Force }
            New-Item -ItemType Directory -Path $vstage | Out-Null
            Copy-StageTree $v.FullName $vstage "voices/$($v.Name)"
            $vfiles = Write-Manifest $vstage "piper-voice-$($v.Name)" $null
            $vzip = Join-Path $OutDir "piper-voice-$($v.Name)-$PackVersion.zip"
            $vhex = New-PackZip $vstage $vzip
            Write-PackSummary "piper-voice-$($v.Name)" $vzip $vhex $vfiles.Count
            Remove-Item -LiteralPath $vstage -Recurse -Force
        }
        foreach ($v in $voices) {
            Get-ChildItem -LiteralPath (Join-Path $runtimeStage "voices\$($v.Name)") -Recurse -File |
                Remove-Item -Force
        }
    }

    Report-Signatures $runtimeStage
    $files = Write-Manifest $runtimeStage 'piper' @{ voices = $voiceCount; splitVoices = [bool]$SplitVoices }
    $zip = Join-Path $OutDir "piper-$PackVersion.zip"
    $hex = New-PackZip $runtimeStage $zip
    Write-PackSummary 'piper' $zip $hex $files.Count
    Remove-Item -LiteralPath $runtimeStage -Recurse -Force
}

Write-Host ''
Write-Host 'done. Rules for publishing:'
Write-Host '  - the .sha256 sidecar goes up next to the zip; users can compare it against the digest GitHub prints'
Write-Host '  - never overwrite a published asset: an installed app pins its tag, so fix mistakes with a new version'
Write-Host '  - check a downloaded pack with: -Verify <zip>'
