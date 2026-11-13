# Release Checklist: Signing & Installer

## What this file is

The release path and nothing else: what the signed installers contain, how the version number moves, and that a user who is
told a new version exists can actually find and download that version. Signing, `EQLogParserInstall.iss`, the version bump,
the website and `README.md` download links, the TTS runtime packs, and who does what in which order: the develop-side bump, the
merge to `master`, the GitHub tag and asset, and the S3 publish.

**Manual feature test steps do not belong here.** Twenty-odd sections of them had accumulated here — "… checks on Windows"
lists for the fight grid, the meter, the overlays, the docking layout — and they are gone. They were never run as a release
gate; they were a place to park a Windows-only worry instead of deciding it. A behaviour that only a live window can show now
gets its rule where every other rule lives, `docs/DesignNotes.md`, written as what must not break plus a plain statement that
no test reaches it. Nothing in the repo may cite a checklist section as the reason something is uncovered, and none is added
back.

## The release is two halves, and only the first is machine-checkable

**Half one is a single commit on `develop`.** Everything a clone can read moves in it: the version files, the release note, the
regenerated RTF, and the signed installer under `Release/`. That commit is the whole deliverable, and "is it ready" is a question a
Linux checkout can answer without a network — see *The readiness check* below.

**Half two is four gestures by hand, after that commit gets the OK**: merge `develop` into `master`, push `master`, create the
release and tag in the GitHub web UI, then rebuild and publish the site from the Windows box (`website/build.py` +
`website/update-aws.cmd`). None of it is scriptable from a clone: the tag and the download asset live in GitHub, the site lives in
S3/CloudFront, and `master` moves only when the operator says so.

The order is not a preference:

- `README.md`, `bottles/*.yml`, the site's generated `download.html` and the in-app `UpdateChecker` all ask GitHub the same
  question — `releases/latest`, or a download URL assembled from a version number. Every one of those links is a 404 from the
  moment the bump commit lands until the release exists, so publishing the site first advertises a file that is not there and
  publishing it after costs nothing.
- The tag belongs on a `master` commit because that is where every existing release tag sits. Creating the release while only
  `develop` is pushed anchors the version to a commit that never reached the branch people download from, and the tag cannot be
  moved after the asset is attached without the URL users already clicked changing under them.

## The readiness check

Ask these of `develop` before giving the OK to merge. All of them run headless.

1. **Every version site says the new version, and nothing else says the old one** —
   `git grep -n "{old}" -- . ':!website/releasenotes.md' ':!EQLogParser/data/releasenotes.rtf'` prints nothing
   (those two are history and keep their old numbers on purpose).
2. **The release note is the top entry in `website/releasenotes.md` and the tracked RTF already carries it.** The RTF is
   `build.py`'s output, never hand-edited; a bump commit that changed the markdown but not `EQLogParser/data/releasenotes.rtf`
   ships an installer whose "what's new" window describes the previous release.
3. **The installer for this version is checked in under `Release/`** as `Release/EQLogParser-install-{version}.exe`. Git shows it
   as a rename-with-binary-change of last release's file — that is the shape of a release commit, not a mistake. It has to be the
   real build: its version resource reads `{version}` and it carries the Authenticode signature. Both are readable on any platform
   from the PE resource and certificate table if in doubt.
4. **`README.md` (version twice) and both `bottles/*.yml`** carry it.
5. **No project or NuGet reference moved since the previous release tag** —
   `git diff {previous-tag}..HEAD -- "*.csproj" "*.props" "*.targets"`. Empty means the `sign.cmd`/`.iss` audit below is not
   triggered. Non-empty, the audit is mandatory, and a bump of `Microsoft.ML.OnnxRuntime` or `KokoroSharp` drags in the Kokoro
   pack rebuild at the end of this file.
6. **The build and test gates pass**: the zero-warning command in `AGENTS.md` prints `0`, and
   `dotnet test EQLogParser.Test/EQLogParser.Test.csproj` reports no failures. The `EQLP_EMU`-gated real-log tests skip without
   `EQLP_EMU=1` — a skip is not a failure. `EQLogParser.Wpf.Test` does not run here at all, so its absence from the tally proves
   nothing either way.

What this check cannot reach, and nobody should pretend otherwise: whether `MeasureLoadedAssemblies.ps1` was run against the
Release bin, whether every shipped dll was signed, and whether the installer installs cleanly. Those belong to the Windows half.

## Rule of thumb

`sign.cmd` and `EQLogParserInstall/EQLogParserInstall.iss` are **curated minimum sets**, not "everything in bin". We only install/sign the files the app actually needs to run — the list was built by trimming until it broke, so extra files in `bin\Release` that are missing from these lists are usually intentional.

However: **whenever a project or NuGet package reference changes, audit its runtime outputs against both lists.** Add each assembly the installed app needs; test-only dependencies and files supplied by the shared framework or optional packs do not automatically belong in the installer. A missing runtime-critical dll (e.g. `EQLogParser.Core.dll`) will not fail the build — the app crashes at runtime with `FileNotFoundException`.

## When to update sign.cmd / EQLogParserInstall.iss

- Adding or renaming a project that produces a dll shipped to `EQLogParser\bin\Release\...`
- Adding a NuGet package that emits an app-local runtime assembly the code can touch
- **Removing** a project or NuGet package: take its dll out of both files **and** add the old names to `[InstallDelete]`. Inno only removes what it installed itself, so a dll an earlier version left in `{app}` rides through every upgrade forever. Done for `Microsoft.WindowsAPICodePack.dll` and `Microsoft.WindowsAPICodePack.Shell.dll` when the choosers moved onto the dialogs that ship inside .NET (`FileDialogUtil`).
- Adding a new sub-project that BackupUtil depends on (BackupUtil references the whole `EQLogParser` project and also loads `EQLogParser.dll` **reflectively** at runtime — `Assembly.Load("EQLogParser")` in `BackupUtil/Program.cs` — so everything that assembly needs must be present next to it too)

## How to verify the list is complete (smarter than trial-and-error)

### 1. Static check — what could the app load?

Compute the transitive compile-time reference closure of the project dlls (`EQLogParser.dll`, `EQLogParser.Core.dll`, `EQLogParser.Audio.dll`, `EQLogParser.Utils.dll`), excluding shared-framework assemblies, and resolve against the release bin output:

- Every file in that closure must appear in `.iss` (installed) — otherwise the code path that touches it crashes lazily.
- Caveat: the closure is a *superset* (unused references don't load), and it misses reflectively loaded assemblies and pack-URI WPF resources (e.g. Syncfusion themes load via resource URI with no static reference) — so "installed but not in closure" does **not** mean removable.
- A Python/dnfile one-off script can do this; the important part is walking `AssemblyRef` metadata tables transitively.

### 2. Empirical check — what did the app actually load? (authoritative)

`scripts/MeasureLoadedAssemblies.ps1` launches the built app, watches loaded PE modules while you exercise every feature, and reports:

- `[LOADED]` — files that must ship
- `[NOT LOADED]` — removal candidates

```
powershell -ExecutionPolicy Bypass -File scripts\MeasureLoadedAssemblies.ps1
# run again with -ExePath pointing at BackupUtil.exe; union the two reports
```

This is the modern replacement for "remove until it breaks": one non-breaking session gives the provable minimum set. Run it on a Release build on Windows (x64 PowerShell; elevated if the app is elevated).

## Version bump

All of these move together in the release commit **on `develop`**; the checks are in `website/build.py`, which refuses a release
note it cannot parse.

1. `EQLogParser/Properties/AssemblyInfo.cs` — `AssemblyVersion` and `AssemblyFileVersion`
2. `EQLogParserInstall/EQLogParserInstall.iss` — `MyAppVersion`, which drives `OutputBaseFilename=EQLogParser-install-{version}.exe`
3. `website/releasenotes.md` — a new top entry headed `# {version} | MM/DD/YY`, newest first
4. `python website/build.py` — rebuilds the site **and** regenerates `EQLogParser/data/releasenotes.rtf`, which is the installer's `InfoBeforeFile`; commit the RTF too, it is tracked
5. `README.md` — the "latest Installer" download URL, which carries the version twice (`/releases/download/{version}/EQLogParser-install-{version}.exe`) and is the one everybody forgets
6. `bottles/Games/eqlogparser.yml` and `bottles/index.yml` — the Linux install recipe: description, `file_name`, and the asset URL
7. `Release/EQLogParser-install-{version}.exe` — the signed installer itself, tracked, produced by the steps below

Nothing else holds a version: the `<Version>` entries in the `.csproj` files are NuGet package versions, `BackupUtil` carries its own, and `UpdateChecker` finds the installer asset by name at runtime.

## Half one on Windows: build, sign, installer

These run **before** the bump commit, because the commit contains the installer they produce. `MyAppVersion` must already read the
new version — ISCC takes the output file name from it.

1. `dotnet publish` / Release build of `EQLogParser` and `BackupUtil` (target: `net10.0-windows10.0.17763.0`)
2. Run the static + empirical checks above; reconcile any delta against `sign.cmd` and `.iss`. For the empirical check, run `MeasureLoadedAssemblies.ps1` **twice** — once for `EQLogParser.exe`, once with `-ExePath` pointing at BackupUtil — and union the two reports. Skipping the BackupUtil run is the classic way a needed dll ends up missing from the installer (it loads `EQLogParser.dll` reflectively, so nothing in its own build references it).
3. `sign.cmd` — signs all release dlls and BackupUtil (signtool + Sectigo timestamp). It also signs the TTS runtime pack files (listed in its own section) and the app-local MSVC copies, and skips files that already carry a vendor signature rather than overwriting them. There is no MSI to sign: the WiX project left the repo, and `sign.cmd` no longer mentions it.
4. Build the Inno Setup installer from `EQLogParserInstall/EQLogParserInstall.iss` (check the `MyReleaseDir`/`BackupUtilDir` paths at the top of the script — adjust per machine), then copy `Output\EQLogParser-install-{version}.exe` over `Release/EQLogParser-install-{version}.exe` so it rides in the bump commit
5. TTS runtime packs (Kokoro / Piper) are **not** in the installer; enabling an engine downloads it into `%LOCALAPPDATA%\EQLogParser\kokoro\` or `%LOCALAPPDATA%\EQLogParser\piper-tts\`. Versioned names such as `kokoro-1.0` and `piper-1.0` identify release tags/assets, not install directories. When a pack needs changing, build it with `scripts\Build-TtsPack.ps1` — see the tracked `docs/TtsPacks.md` for the sign → manifest → publish order and for adding voices. Check that step 2's `MeasureLoadedAssemblies` report is explained by this: with Kokoro speaking it lists `MisakiSharp.dll`, `NumSharp.dll`, OpenTK and `onnxruntime.dll` as loaded even though they are absent from `{app}`; they come from the pack. Uninstalling removes them (`CurUninstallStepChanged` deletes `%LOCALAPPDATA%\EQLogParser`, which is packs-only: engine folders, `_download`, and any `.staging`/`.retired`/`*.removing` leftovers) because none of it is user-authored and all of it re-downloads on the next enable — if that ever stops being true, that delete has to become a prompt.

   A fresh install therefore ships Windows voices only; Piper and Kokoro appear in the TTS Engine dialog as a download.
   Nothing reads a speech runtime under `{app}` any more — the `kokoro` and `piper-tts` folders under `%LOCALAPPDATA%\EQLogParser`
   are the only places the engines look — but `[InstallDelete]` deliberately does **not** clean up what used to live there.

   The exception in `[Files]` is the app-local MSVC runtime (`{app}\msvcp140*.dll`, `vcruntime140*.dll`, taken from
   `EQLogParser\redist`). Expect those four in the report loaded **from `{app}`** even on a machine that has the
   redistributable installed: the program folder is searched before System32, and that precedence is the feature. They
   belong to the whole process, so an ONNX Runtime loaded from a pack resolving against them is the pairing this exists
   to create — see `EQLogParser\redist\README.md` for why those copies have to stay current. Releases
   through 2.3.61 put Piper at `{app}\piper-tts` with voices underneath, and users put their own voice models in that
   folder; leaving a few hundred inert MB is better than deleting files somebody added. The uninstaller still clears
   `{app}`. So `[InstallDelete]` stays what it always was — libraries this project dropped years ago (`ActiproSoftware.*`,
   `LiveCharts*`, `Hardcodet.*`, `NumericUpDownLib`, `Syncfusion.SfRichTextBoxAdv`, `WPFTextBoxAutoComplete`) and
   superseded docs under `{app}\data` — never a user's files, and never a name the current `[Files]` section installs.

   If this release bumps `Microsoft.ML.OnnxRuntime` or `KokoroSharp`, the published Kokoro pack has to be rebuilt against it and `TtsPackManager` re-pointed at the new tag: the installed managed ONNX wrapper and the pack's native `onnxruntime.dll` must be the same version, and KokoroSharp runs on top of everything in the pack.


## Half two, after the OK: merge, tag, publish

Four gestures, in this order, by hand. `master` is the branch people download from, and the tag is what every "is there a new
version" question in the world reads.

1. **Merge `develop` into `master`**: `git switch master && git merge --ff-only develop`. A fast-forward is the expected case —
   `develop` carries everything `master` has, and the bump commit adds nothing that only `master` owns. If it will not
   fast-forward, stop and fix the branch topology; do not merge the release commit sideways to make the number move.
2. **Push `master`** (`git push origin master`, plus `git push origin develop` if develop has new work). This has to precede the
   release: the web UI tags a commit that exists on the remote.
3. **Create the release and its tag in the GitHub UI, targeting `master`.** Both strings are load-bearing, not cosmetic:
   - **Tag: exactly `{version}`** (e.g. `2.4.12`). `UpdateChecker` does `Version.TryParse(tag_name, out …)` and compares
     Major/Minor/Build, so a `v` prefix or any suffix parses as nothing and the app reports "no update available" forever, with no
     error to find.
   - **Asset: exactly `EQLogParser-install-{version}.exe`** — the same bytes as `Release/EQLogParser-install-{version}.exe`.
     `UpdateChecker` builds its in-app download path as `{path}\EQLogParser-install-{tagName}.exe`, so an asset renamed for
     tidiness is an update that downloads nothing.
   - Release body: the bullets of the top release-notes entry, so the tag page and the site agree.
4. **Confirm it from anywhere, then publish the site from the Windows box.** `releases/latest` must redirect to `{version}` and the
   `README.md` download URL must return 200 — two `curl -I` calls, and together they are the proof that the app, the README, the
   bottles recipe and the site all now resolve. Then `python website/build.py` (the venv is `website\.venv`; `sitecheck.py` runs
   inside the build and a non-zero exit means do not deploy) and `website\update-aws.cmd`, which pushes `dist/` to
   `s3://eqlogparser.kizant.net` with the per-kind cache headers each upload label sets.

`dist/*.html`, `sitemap.xml` and `feed.xml` are gitignored, so the site that players get is the one on S3, not the one in the
clone — a build that was never uploaded is an unshipped change, and there is no diff to remind anyone. `download.html` is generated
from `MyAppVersion` in the `.iss`, which is the whole reason step 4 sits after step 3: publishing first advertises an installer that
returns 404. The tracked half of that same `build.py` run is the RTF, and it must already be in the bump commit — running it again
here feeds `dist/`, not the installer. After swapping an image or a vendored script under `img/`/`assets/`, follow the invalidation
note in `update-aws.cmd`; the HTML and the versioned stylesheet do not need one.
