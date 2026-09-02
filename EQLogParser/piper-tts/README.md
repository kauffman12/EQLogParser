# piper-tts

Piper's native SDK binaries, kept here for the **build**, not shipped in the installer.

| File | What it is |
| --- | --- |
| `piperApi.dll` | the C API EQLogParser.Audio P/Invokes |
| `piper_phonemize.dll` | phonemizer used by the above |
| `espeak-ng.dll` | espeak-ng runtime that `piper_phonemize` links against |
| `onnxruntime.dll`, `onnxruntime_providers_shared.dll` | Microsoft's ONNX inference runtime; keep the same version Kokoro ships (1.22.0), because Windows holds one `onnxruntime.dll` per process |

## What used to be here and is not

`espeak-ng-data\` (19MB) and `voices\` (61MB) were deleted from this repo. They are data, not build inputs, and
keeping them in git meant every clone carried 80MB that only Piper needs. Both now live in the speech runtime pack:
<https://github.com/kauffman12/EQLogParser-TTS> (`piper-tts/espeak-ng-data/`, `piper-tts/voices/`).

At run time Piper reads its data from `%LOCALAPPDATA%\EQLogParser\piper-tts`, which the app downloads on demand from
that repo. That is the only place it looks: nothing under the program folder is adopted, so finding a `piper-tts` in a
build output means nothing at run time. The installer deletes what pre-pack installs left under `{app}`.

## Why keep these five files then

Two consumers need them at build time and neither is the installer:

- `sign.cmd` signs `%RELEASE_DIR%\piper-tts\*.dll`. Signing has to happen before a pack is zipped, because signing
  rewrites the tail of a PE file and any manifest hashed beforehand would disagree with what users download.
- `scripts/Build-TtsPack.ps1 -Sync` copies them out of a Release build into the pack staging area, along with the
  Kokoro support assemblies.

The folder is copied to the build output by the `None Include="piper-tts\**"` item in `EQLogParser.csproj`. No `.iss`
entry installs it.

See `docs/TtsPacks.md` for the whole publish flow.
