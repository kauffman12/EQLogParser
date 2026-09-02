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

## What the app pins

`EQLogParser.Audio/src/TtsPackManager.cs` holds one entry per engine: tag, asset name, archive SHA-256, install folder.
Changing a pack means editing this table and shipping an app build that carries it.

The digest lives in compiled code on purpose. A hash downloaded from the same server as the payload proves nothing --
whoever can replace the zip can replace the file describing it -- so the only anchor worth checking is one that arrives
inside the signed installer and moves only when the app itself is released. The sidecar next to an asset exists for
humans and for `-Verify`, not as the app's reference.

| engine | tag / asset | archive SHA-256 |
|---|---|---|
| Piper | `piper-1.0` → `piper-1.0.zip` | `dc24d7f9673b28b9e18a0801f0492107ad8c2b6e6ba6645ca67488b703f76451` |
| Kokoro | `kokoro-1.0` → `kokoro-1.0.zip` | `b1070b9e231dd0d08203fc89f6540c6de3d13de479bd506f63f6902194241788` |

## Publishing rules

- **Never overwrite a published asset.** A released app pins its tag URL and its archive digest. Publish `kokoro-1.1`
  and ship an app build whose `TtsPackManager` points at it. The one exception is before any app build that pins the
  pack has shipped: nothing out there can then be left pointing at bytes that no longer exist, so replacing a pack under
  its own tag is fine as long as the pin and this table move with it.
- The digest to pin is GitHub's own: `GET /repos/kauffman12/EQLogParser-TTS/releases/assets/{id}` reports
  `"digest": "sha256:..."`, and the `.zip.sha256` sidecar should agree with it. An app whose pin and asset disagree
  fails closed with "checksum mismatch. The download was discarded", which is the pack working as intended, not loading
- Keep the version in the tag, the file name and `manifest.json` in step.
- Kokoro model bytes must match the digest pinned in `KokoroTtsEngine` (see DesignNotes → Kokoro model integrity).
  Mirroring the model here is fine — same bytes, same digest, existing users unaffected — and removes someone else's
  uptime from your critical path.
- **Bumping `Microsoft.ML.OnnxRuntime` in the app means republishing the Kokoro pack.** The managed wrapper installs
  with the app and the native `onnxruntime.dll` comes from the pack; ONNX Runtime requires the two to be the same
  version. Same reasoning for KokoroSharp itself: it installs with the app and runs against everything in the pack.
  A mismatch shows up as Kokoro refusing to start, in the log, not as a crash.

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

**Kokoro** — edit `KokoroVoicePrefixes` in the app repo's `Directory.Build.targets` (default `af;am;bf;bm`, the
English voices both sides of the Atlantic; the same property stops KokoroSharp's build target from copying all 79 MB of
voices into every build output — only the app's own output gets the folder, since that is the one `-Sync` reads, and a
stray `voices\` in another output is deleted), rebuild, `-Sync`, repack. Each `.npy` is ~0.5 MB, so the eight British
voices cost 4 MB on a 228 MB download. Anything beyond English prefixes will not work regardless: MisakiSharp's 66 MB is
English grapheme-to-phoneme data and is the reason the Kokoro pack is mostly one file.

### More voices in a pack that is already published

The voice embeddings and the model are data, and nothing signs an archive: a signature covers the bytes of one PE
file, so unpacking a published pack, adding `.npy` files and rezipping leaves every signature inside it exactly as it
was. What does change is the digest the app pins. A pack's `manifest.json` is self describing — size and SHA-256 of
every file in it, paths sorted, voices-relative with forward slashes — and the app verifies against that file rather
than against a copy of it, so regenerating the manifest from the directory keeps per file verification honest. Three
things then have to move together:

1. `manifest.json` regenerated from the pack directory (everything else in it untouched, byte for byte).
2. The zip rehashed and its `.sha256` sidecar rewritten, so GitHub's asset digest and the sidecar still agree.
3. `TtsPackManager.Packs` given the new zip digest and size — that pin is what an installed app checks the download
   against, which is why replacing an asset under its existing tag is only open while no shipped build pins the old
   digest (see the comment on that table).

A pack rebuilt this way needs no signing pass. `-Verify` still works on it: it checks entries against the manifest and
the sidecar against the archive, neither of which knows or cares that the directory was assembled by hand.

## Installing at run time

The TTS Engine dialog is the whole UI: it lists all three engines and puts one button on whatever comes next —
**Download Piper (348 MB)** / **Download Kokoro (228 MB)** for an engine with nothing on disk, **Use Piper** to start an
installed one, **In use** when it is already speaking. Looking at a row applies nothing; switching is the button, and it
takes effect without a restart. A finished download does switch on its own, since that is plainly why it was fetched.
While something is downloading a **Cancel** button appears next to Close; closing the window cancels as well.

**Remove Files** asks first — it deletes a couple of hundred megabytes and the way back is to download them again. It
applies to an engine that is not the one in use: that one holds its native libraries mapped until EQLogParser closes,
so its directory cannot be deleted cleanly. This is why browsing does not switch: when selecting a row applied it, every
row on screen was by definition the active one, and the button could never become available for anything. An engine used
earlier in the same session has the same problem and says so: the libraries are still mapped, so removing it needs a
restart rather than hunting for another running copy.

`TtsPackManager.InstallAsync` does the rest, and each step is there because the alternative is worse:

1. stream the zip to `%LOCALAPPDATA%\EQLogParser\_download\<asset>.tmp`, reporting progress — nothing is touched in the
   engine directory yet, so a dropped connection costs a retry and nothing else.
2. hash the archive and compare it to the pin. A CDN, proxy or DNS that hands back other bytes is discarded here.
3. extract into `<engine>.staging` and verify every `manifest.json` entry (path, size, SHA-256). Entries that resolve
   outside the target directory abort the install — archive paths are treated as hostile even from a digest-matched zip.
4. move any existing install aside to `<engine>.retired`, promote staging, write `.pack-ready` (`<tag> <digest>`), then
   delete the retired copy. If promoting fails the retired copy is moved back, so a pack that worked this morning still
   starts tomorrow.

**Cancel** covers all four steps and not just the transfer: the byte loop, the archive hash, each extracted entry and
each verified file watch the same token, which matters because steps 2 and 3 are tens of seconds on their own after a
fast download. A cancelled or failed install leaves whatever was installed exactly as it was — staging and the temp
archive go on the way out.

Two free-space checks rather than one: room for the archive before anybody's bandwidth is spent, and — once the zip is in
hand and its central directory can say what it will really occupy — room for the extracted tree before anything lands.
An unreadable drive or an unmeasurable archive counts as room enough; these exist to refuse a job that cannot finish, not
to argue with a disk, and a disk that fills up regardless reports its own numbers in the error log.

Startup does no hashing: an engine counts as installed when its directory holds what the engine needs (`voices\
voices.json` + `piperApi.dll`, or `model\kokoro-fp16.onnx` + at least one `.npy`). Kokoro additionally re-checks its
own model hash, cached in a sidecar marker.

The app repo carries neither engine's data. What remains under `EQLogParser/piper-tts/` is five native SDK binaries
(10 MB) for `sign.cmd` and `-Sync` to read; see `EQLogParser/piper-tts/README.md`. The app never reads a speech runtime
from under the program folder, so an old build directory that still holds `espeak-ng-data\` and `voices\` cannot pass
itself off as an installed pack — it is dead weight until that directory is cleaned, and the installer deletes what
pre-pack installs left in `{app}`.
