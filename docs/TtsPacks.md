# Speech runtime packs (Piper / Kokoro)

Both optional speech engines need far more than an installer should carry: the Piper runtime plus its voices is 88 MB
unpacked and Kokoro's runtime is another 93 MB, before counting the 156 MB model. They are downloaded on demand into
per-user storage and published as GitHub release assets by `scripts/Build-TtsPack.ps1`.

```
where things live on a user's machine
  %LOCALAPPDATA%\EQLogParser\kokoro\      bin\ native\ voices\ model\ manifest.json
  %LOCALAPPDATA%\EQLogParser\piper-tts\   *.dll espeak-ng-data\ voices\ manifest.json

where things live on GitHub
  https://github.com/kauffman12/EQLogParser-TTS   releases tagged kokoro-<v>, piper-<v>, piper-voice-<name>-<v>
```

`LocalApplicationData`, not `ApplicationData`: Roaming follows a profile across machines, and re-downloadable binaries
are the worst thing to put on that path. EQLP's own state (`config\`, `logs\`, `archive\`) stays in Roaming.

## Building a pack

Run a Release build first — the script stages from `EQLogParser\bin\Release\net8.0-windows10.0.17763.0`, which is
where NuGet puts the runtime assemblies and where `Directory.Build.targets` puts the Kokoro voice embeddings.

```powershell
# 1. sign the binaries that carry no vendor signature (skips Microsoft's)
sign.cmd

# 2. stage + manifest + zip + digest
powershell -ExecutionPolicy Bypass -File scripts\Build-TtsPack.ps1 -Pack kokoro -IncludeModel
powershell -ExecutionPolicy Bypass -File scripts\Build-TtsPack.ps1 -Pack piper  -GenerateVoicesJson

# 3. publish (prints the exact gh command per pack)
# 4. sanity-check what came back down
powershell -ExecutionPolicy Bypass -File scripts\Build-TtsPack.ps1 -Verify build\tts-packs\piper-1.0.zip
```

Order is not negotiable: **sign, then manifest, then upload.** Signing rewrites the tail of a PE file, so hashes taken
before signing will not match what users download. `-Strict` makes an unsigned binary fail the build instead of warning,
which is what you want once `sign.cmd` is expected to have covered everything.

Each zip contains `manifest.json` — every file with its size and SHA-256 — plus `THIRD-PARTY-NOTICES.txt`. The app
verifies every entry before it loads anything, so an incomplete or tampered pack never reaches the engine. Alongside the
zip there is a `.sha256` sidecar for the zip itself, which is also the digest GitHub prints on the asset, so both sides
can be checked.

## Publishing rules

- **Never overwrite a published asset.** A released app pins its tag URL. Fix mistakes by publishing `kokoro-1.1` and
  shipping a build that pins it.
- Keep the pack version in the tag and in `manifest.json` in step, and bump both together.
- Kokoro's model bytes must stay identical to whatever digest is pinned in `KokoroTtsEngine` (see DesignNotes → Kokoro
  model integrity). Mirroring the file into your own release is fine — same bytes, same digest, existing users unaffected.

## Adding a Piper voice

1. Drop the model and its `.onnx.json` into `EQLogParser\piper-tts\voices\<folder>\`. The Release build copies them to
   the output directory, which is what the script stages from.
2. Let `voices.json` be rebuilt with `-GenerateVoicesJson`: it reads each model's `audio.sample_rate` and `_meta.name`,
   so no hand-editing. Without that switch it uses the checked-in `voices.json` and fails if a listed model or config is
   missing from the pack — a typo in that file becomes silence on a user's machine, so prefer letting the script write it.
3. Build and publish. Prefer `-SplitVoices` once there is more than one voice: you get a runtime pack (~25 MB with
   `espeak-ng-data`) plus one zip per voice, so enabling speech does not mean downloading every voice you maintain.

## Adding a Kokoro voice

Edit `KokoroVoiceMasks` in `Directory.Build.targets` (default `af;am` — American English; the same property also prunes
the copy that KokoroSharp's build target would otherwise drop into every build output). Rebuild, repack, publish.
Each `.npy` is ~0.5 MB, so voices are cheap; MisakiSharp's 66 MB of English dictionaries is what dominates the pack and
it only covers `a`/`b` (American/British English) — other language prefixes need their own G2P data and are not covered
by this pack.

## Current status

The installer and `sign.cmd` are set up for this layout. The loader side — pack paths, resolvers, the download flow in
the TTS dialog — is not implemented yet, so today the engines read `{app}\piper-tts`, `{app}\voices` and
`%LOCALAPPDATA%\EQLogParser\kokoro-tts\kokoro-fp16.onnx`. Until it lands, a fresh install has Windows voices only;
build with `IncludePiperTTS=1` for a release that must speak out of the box.
