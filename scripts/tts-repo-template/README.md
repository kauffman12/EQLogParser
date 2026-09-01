# EQLogParser TTS runtime packs

Piper and Kokoro are optional speech engines for [EQLogParser](https://github.com/kauffman12/EQLogParser). Neither is
part of the app installer: the app downloads a pack from this repo's releases into `%LOCALAPPDATA%\EQLogParser\` when a
user enables the engine, and verifies every file in it against `manifest.json` before loading anything.

App releases stay in the EQLogParser repo. This repo holds only runtime data, so a voice can be added or a dictionary
fixed without implying an app release.

## Layout

```
Build-TtsPack.ps1        the packer
piper-tts\               piperApi.dll piper_phonemize.dll espeak-ng.dll onnxruntime*.dll
    espeak-ng-data\      355 files, keep the tree exactly as shipped
    voices\voices.json   or let the script generate it
    voices\<name>\       one folder per voice: *.onnx + *.onnx.json (+ its LICENSE)
kokoro\
    bin\                 MisakiSharp.dll NumSharp.dll System.Numerics.Tensors.dll OpenTK*.dll
    native\              onnxruntime.dll onnxruntime_providers_shared.dll
    voices\              af_*.npy am_*.npy + LICENSE (produced by the app build)
    model\               kokoro-fp16.onnx (optional; the app can also fetch it from upstream)
out\                     zips and .sha256 sidecars (generated, not committed)
```

## Building and publishing

```powershell
# in the EQLogParser repo: build, then sign the binaries (signing rewrites a PE file's tail, so sign before hashing)
dotnet build EQLogParser.sln -c Release
sign.cmd

# here
powershell -ExecutionPolicy Bypass -File Build-TtsPack.ps1 -Inventory
powershell -ExecutionPolicy Bypass -File Build-TtsPack.ps1 -Sync `
    -AppRelease C:\src\EQLogParser\EQLogParser\bin\Release\net8.0-windows10.0.17763.0
powershell -ExecutionPolicy Bypass -File Build-TtsPack.ps1
```

Each pack prints its SHA-256, the tag to publish under and the `gh release create` line. Tags are `kokoro-<version>`
and `piper-<version>`, and the app pins one of them, so **never overwrite a published asset** — publish `piper-1.1` and
ship an app build that points at it instead.

Useful switches: `-Pack kokoro|piper|both`, `-PiperVoices a,b` to leave voices out, `-GenerateVoicesJson` to build
`voices.json` from the voice folders (name and sample rate are read from each model's `.onnx.json`), `-SkipModel`,
`-Strict` (fail on unsigned binaries), `-Upload`, and `-Verify out\piper-1.0.zip` to re-check a zip against its own
manifest — worth running on a copy downloaded from GitHub, not just the one you built.

## What belongs in git

GitHub rejects any pushed file over 100 MB, and everything ever committed stays in every clone forever, so the packs
themselves only live in releases:

| what | size | where |
|---|---|---|
| script, README, `voices.json`, notices | KB | commit |
| `espeak-ng-data\` | 17 MB | commit (changes rarely) |
| Piper/Kokoro runtime DLLs | 0.02–66 MB | your call — commit the small ones, or `-Sync` them in per build |
| `*.onnx` voice models | ~25–60 MB each | release assets only |
| `kokoro\model\kokoro-fp16.onnx` | 156 MB | release asset only — it cannot be committed at all |

The `.gitignore` here follows that split; adjust it if you would rather keep the repo metadata-only and populate both
data directories locally before packing.

## Voice notes

- Kokoro voices come from the app build: `KokoroVoiceMasks` in its `Directory.Build.targets` picks the prefixes
  (default `af;am`, American English). Each `.npy` is about 0.5 MB. Non-English prefixes are not supported — the 66 MB
  MisakiSharp payload is English grapheme-to-phoneme data.
- Kokoro's model bytes must match the SHA-256 pinned in the app. Mirroring it here is fine: same bytes, same digest,
  users who already have it are unaffected.
