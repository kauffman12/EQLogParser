# Speech runtime packs (Piper / Kokoro)

Both optional speech engines need far more than an installer should carry: the Piper runtime plus its voices is ~88 MB
unpacked for a single voice, and the Kokoro runtime is another 93 MB before counting the 156 MB model. Neither is in the
installer. They are published as GitHub release assets from `kauffman12/EQLogParser-TTS` and downloaded into per-user
storage when an engine is enabled.

```
on a user's machine
  %LOCALAPPDATA%\EQLogParser\kokoro\      bin\ native\ voices\ model\ manifest.json
  %LOCALAPPDATA%\EQLogParser\piper-tts\   *.dll espeak-ng-data\ voices\ manifest.json

on GitHub
  kauffman12/EQLogParser-TTS        releases tagged kokoro-<v> and piper-<v>, one zip + one .sha256 each
```

`LocalApplicationData`, not `ApplicationData`: Roaming follows a profile between machines, and re-downloadable binaries
are the worst thing you can put on that path. EQLP's own state (`config\`, `logs\`, `archive\`) stays in Roaming where
it belongs — roam what the user made, download what we ship.

## The packer

`scripts/Build-TtsPack.ps1` lives here so it is reviewed with the code, and gets copied into the TTS repo, where it
expects its data two directories over:

```
Build-TtsPack.ps1
piper-tts\      piperApi.dll piper_phonemize.dll espeak-ng.dll onnxruntime*.dll
    espeak-ng-data\        355 files, keep the tree exactly as shipped
    voices\voices.json     or -GenerateVoicesJson
    voices\<name>\         one folder per voice: *.onnx + *.onnx.json (+ LICENSE)
kokoro\         bin\ native\ voices\ model\
out\            zips and sidecars (generated)
```

```powershell
# 1. in the app repo: build, then sign. Sign BEFORE packing: signing rewrites the tail of a PE file, so a manifest
#    hashed beforehand will not match what users download.
dotnet build EQLogParser.sln -c Release
sign.cmd

# 2. in the TTS repo: look, pull the signed binaries in, pack
powershell -ExecutionPolicy Bypass -File Build-TtsPack.ps1 -Inventory
powershell -ExecutionPolicy Bypass -File Build-TtsPack.ps1 -Sync -AppRelease C:\src\EQLogParser\EQLogParser\bin\Release\net8.0-windows10.0.17763.0
powershell -ExecutionPolicy Bypass -File Build-TtsPack.ps1

# 3. check a pack that came back down from GitHub, not just the one you built
powershell -ExecutionPolicy Bypass -File Build-TtsPack.ps1 -Verify out\piper-1.0.zip
```

Each zip carries `manifest.json` — every file with size and SHA-256, lowercase hex — plus `THIRD-PARTY-NOTICES.txt`.
The app verifies every entry before loading anything, so an incomplete or tampered pack never reaches an engine. The
`.sha256` sidecar covers the zip itself and matches the digest GitHub displays for the asset, which is what makes
`-Verify` meaningful on a downloaded copy.

Three failure modes the script treats as errors rather than warnings, because each one becomes silence on a user's
machine: a required binary missing from a data dir, `voices.json` naming a model or config that is not in the pack, and
(in `-Strict`) a binary we were supposed to sign that is still unsigned. Microsoft-signed files — both `onnxruntime`
copies, `System.Numerics.Tensors` — are left alone instead of re-signed over.

## Publishing rules

- **Never overwrite a published asset.** A released app pins its tag URL. Publish `kokoro-1.1` and ship an app build
  that points at it.
- Keep the version in the tag, the file name and `manifest.json` in step.
- Kokoro model bytes must match the digest pinned in `KokoroTtsEngine` (see DesignNotes → Kokoro model integrity).
  Mirroring the model here is fine — same bytes, same digest, existing users unaffected — and removes someone else's
  uptime from your critical path.

## What goes in git versus releases

GitHub refuses any pushed file over **100 MB** (and warns above 50), and everything ever committed stays in every clone
forever — deleting it later does not shrink the repo, only a history rewrite does. So:

| content | size | where |
|---|---|---|
| script, README, `voices.json`, notices | KB | commit |
| `espeak-ng-data\` | 17 MB / 355 files | commit; changes rarely |
| Piper + Kokoro runtime DLLs | 0.02–66 MB | your call — commit the small ones or `-Sync` them from a build each time |
| Piper `*.onnx` voices | ~25–60 MB each | release assets only |
| `kokoro\model\kokoro-fp16.onnx` | 156 MB | release asset only — cannot be committed without LFS |

Release assets themselves are a different budget: 2 GB per file, no per-download quota on public repos. `scripts/tts-repo-template/`
holds a ready `README.md` and `.gitignore` for the new repo following this split. Git LFS is not worth it here: 1 GB of
free storage and 1 GB/month of bandwidth evaporates on the first voice download spike, and release assets already give a
CDN, per-asset digests and download counts.

## Adding voices

**Piper** — drop `*.onnx` + `*.onnx.json` into `piper-tts\voices\<name>\`, pack with `-GenerateVoicesJson` (it reads
each model's `audio.sample_rate` and `_meta.name`, so there is nothing to hand-edit), publish a new tag. With six voices
the single pack lands around 350–400 MB, which every user who enables Piper downloads in one piece; that is a deliberate
tradeoff over per-voice packs. `-PiperVoices a,b` lets you ship a subset without deleting anything.

**Kokoro** — edit `KokoroVoiceMasks` in the app repo's `Directory.Build.targets` (default `af;am`, American English;
the same property stops KokoroSharp's build target from copying all 79 MB of voices into every build output), rebuild,
`-Sync`, repack. Each `.npy` is ~0.5 MB. Anything beyond English prefixes will not work regardless: MisakiSharp's 66 MB
is English grapheme-to-phoneme data and is the reason the Kokoro pack is mostly one file.

## Current status

The installer and `sign.cmd` are set up for this layout; the loader side — pack paths, assembly and native resolvers,
the download flow in the TTS dialog — is not implemented yet, so today the engines read `{app}\piper-tts`, `{app}\voices`
and `%LOCALAPPDATA%\EQLogParser\kokoro-tts\kokoro-fp16.onnx`.
Until the loader lands, a fresh install has Windows voices only; build the installer with `IncludePiperTTS=1` for a
release that must speak out of the box.

That is also why the app repo still carries ~87 MB under `EQLogParser/piper-tts/`: it feeds the `{app}` fallback, local
dev builds and `IncludePiperTTS` bundles. When the loader lands, that copy can move to this repo for good and
`-Sync -AppRelease` keeps filling in only the binaries.
