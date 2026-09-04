# Release Checklist: Signing & Installer

## Rule of thumb

`sign.cmd` and `EQLogParserInstall/EQLogParserInstall.iss` are **curated minimum sets**, not "everything in bin". We only install/sign the files the app actually needs to run — the list was built by trimming until it broke, so extra files in `bin\Release` that are missing from these lists are usually intentional.

However: **whenever a new project or NuGet package reference is added, its output dll must be checked into both files.** A missing runtime-critical dll (e.g. a project reference like `EQLogParser.Core.dll`) will not fail the build — the app just crashes at runtime with `FileNotFoundException` on user machines.

## When to update sign.cmd / EQLogParserInstall.iss

- Adding or renaming a project that produces a dll shipped to `EQLogParser\bin\Release\...`
- Adding a NuGet package that emits an app-local runtime assembly the code can touch
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

All of these move together in the release commit; the checks are in `website/build.py`, which refuses a release note it cannot parse.

1. `EQLogParser/Properties/AssemblyInfo.cs` — `AssemblyVersion` and `AssemblyFileVersion`
2. `EQLogParserInstall/EQLogParserInstall.iss` — `MyAppVersion`, which drives `OutputBaseFilename=EQLogParser-install-{version}.exe`
3. `website/releasenotes.md` — a new top entry headed `# {version} | MM/DD/YY`, newest first
4. `python website/build.py` — rebuilds the site **and** regenerates `EQLogParser/data/releasenotes.rtf`, which is the installer's `InfoBeforeFile`; commit the RTF too, it is tracked
5. `README.md` — the "latest Installer" download URL, which carries the version twice (`/releases/download/{version}/EQLogParser-install-{version}.exe`) and is the one everybody forgets
6. `bottles/Games/eqlogparser.yml` and `bottles/index.yml` — the Linux install recipe: description, `file_name`, and the asset URL

Nothing else holds a version: the `<Version>` entries in the `.csproj` files are NuGet package versions, `BackupUtil` carries its own, and `UpdateChecker` finds the installer asset by name at runtime.

## Release steps

1. `dotnet publish` / Release build of `EQLogParser` and `BackupUtil` (target: `net8.0-windows10.0.17763.0`)
2. Run the static + empirical checks above; reconcile any delta against `sign.cmd` and `.iss`. For the empirical check, run `MeasureLoadedAssemblies.ps1` **twice** — once for `EQLogParser.exe`, once with `-ExePath` pointing at BackupUtil — and union the two reports. Skipping the BackupUtil run is the classic way a needed dll ends up missing from the installer (it loads `EQLogParser.dll` reflectively, so nothing in its own build references it).
3. `sign.cmd` — signs all release dlls, BackupUtil, and `EQLogParserMSI\bin\Release\EQLogParser*.msi` (signtool + Sectigo timestamp). It also signs the TTS runtime pack files (listed in its own section) and skips files that already carry a vendor signature rather than overwriting them.
4. Build the Inno Setup installer from `EQLogParserInstall/EQLogParserInstall.iss` (check the `MyReleaseDir`/`BackupUtilDir` paths at the top of the script — adjust per machine)
5. TTS runtime packs (Kokoro / Piper) are **not** in the installer; they are downloaded into `%LOCALAPPDATA%\EQLogParser\<engine>-<version>` — `piper-1.0`, `kokoro-1.0` — when a user enables an engine. When a pack needs changing, build it with `scripts\Build-TtsPack.ps1` — see `docs/TtsPacks.md` for the sign → manifest → publish order and for adding voices. Check that step 2's `MeasureLoadedAssemblies` report is explained by this: with Kokoro speaking it lists `MisakiSharp.dll`, `NumSharp.dll`, OpenTK and `onnxruntime.dll` as loaded even though they are absent from `{app}`; they come from the pack. Uninstalling removes them (`CurUninstallStepChanged` deletes `%LOCALAPPDATA%\EQLogParser`, which is packs-only: engine folders, `_download`, and any `.staging`/`.retired`/`*.removing` leftovers) because none of it is user-authored and all of it re-downloads on the next enable — if that ever stops being true, that delete has to become a prompt.

   A fresh install therefore ships Windows voices only; Piper and Kokoro appear in the TTS Engine dialog as a download.
   Nothing reads a speech runtime under `{app}` any more — `%LOCALAPPDATA%\EQLogParser\<engine>-<version>` is the only
   place the engines look — but `[InstallDelete]` deliberately does **not** clean up what used to live there.

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
