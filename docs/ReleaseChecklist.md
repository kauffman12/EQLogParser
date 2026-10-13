# Release Checklist: Signing & Installer

## Rule of thumb

`sign.cmd` and `EQLogParserInstall/EQLogParserInstall.iss` are **curated minimum sets**, not "everything in bin". We only install/sign the files the app actually needs to run — the list was built by trimming until it broke, so extra files in `bin\Release` that are missing from these lists are usually intentional.

However: **whenever a new project or NuGet package reference is added, its output dll must be checked into both files.** A missing runtime-critical dll (e.g. a project reference like `EQLogParser.Core.dll`) will not fail the build — the app just crashes at runtime with `FileNotFoundException` on user machines.

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

All of these move together in the release commit; the checks are in `website/build.py`, which refuses a release note it cannot parse.

1. `EQLogParser/Properties/AssemblyInfo.cs` — `AssemblyVersion` and `AssemblyFileVersion`
2. `EQLogParserInstall/EQLogParserInstall.iss` — `MyAppVersion`, which drives `OutputBaseFilename=EQLogParser-install-{version}.exe`
3. `website/releasenotes.md` — a new top entry headed `# {version} | MM/DD/YY`, newest first
4. `python website/build.py` — rebuilds the site **and** regenerates `EQLogParser/data/releasenotes.rtf`, which is the installer's `InfoBeforeFile`; commit the RTF too, it is tracked
5. `README.md` — the "latest Installer" download URL, which carries the version twice (`/releases/download/{version}/EQLogParser-install-{version}.exe`) and is the one everybody forgets
6. `bottles/Games/eqlogparser.yml` and `bottles/index.yml` — the Linux install recipe: description, `file_name`, and the asset URL

Nothing else holds a version: the `<Version>` entries in the `.csproj` files are NuGet package versions, `BackupUtil` carries its own, and `UpdateChecker` finds the installer asset by name at runtime.

## Release steps

1. `dotnet publish` / Release build of `EQLogParser` and `BackupUtil` (target: `net10.0-windows10.0.17763.0`)
2. Run the static + empirical checks above; reconcile any delta against `sign.cmd` and `.iss`. For the empirical check, run `MeasureLoadedAssemblies.ps1` **twice** — once for `EQLogParser.exe`, once with `-ExePath` pointing at BackupUtil — and union the two reports. Skipping the BackupUtil run is the classic way a needed dll ends up missing from the installer (it loads `EQLogParser.dll` reflectively, so nothing in its own build references it).
3. `sign.cmd` — signs all release dlls, BackupUtil, and `EQLogParserMSI\bin\Release\EQLogParser*.msi` (signtool + Sectigo timestamp). It also signs the TTS runtime pack files (listed in its own section) and skips files that already carry a vendor signature rather than overwriting them.
4. Build the Inno Setup installer from `EQLogParserInstall/EQLogParserInstall.iss` (check the `MyReleaseDir`/`BackupUtilDir` paths at the top of the script — adjust per machine)
5. TTS runtime packs (Kokoro / Piper) are **not** in the installer; they are downloaded into `%LOCALAPPDATA%\EQLogParser\<engine>-<version>` — `piper-1.0`, `kokoro-1.0` — when a user enables an engine. When a pack needs changing, build it with `scripts\Build-TtsPack.ps1` — see `docs/TtsPacks.md` (a local working document, deliberately not in the repo) for the sign → manifest → publish order and for adding voices. Check that step 2's `MeasureLoadedAssemblies` report is explained by this: with Kokoro speaking it lists `MisakiSharp.dll`, `NumSharp.dll`, OpenTK and `onnxruntime.dll` as loaded even though they are absent from `{app}`; they come from the pack. Uninstalling removes them (`CurUninstallStepChanged` deletes `%LOCALAPPDATA%\EQLogParser`, which is packs-only: engine folders, `_download`, and any `.staging`/`.retired`/`*.removing` leftovers) because none of it is user-authored and all of it re-downloads on the next enable — if that ever stops being true, that delete has to become a prompt.

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

## Fight list checks on Windows

Use a loaded app with a log containing multiple encounters, inactivity dividers, and NPCs that dealt damage but received none.
These checks require a realized grid; the automated pane tests exercise bound data, not mouse selection or scrolling.

- [ ] **Checkbox combinations:** both checked shows every display row; Inactivity off hides only dividers; Tanking off hides NPC rows
  with no damage received from players. Both off applies both rules. Rechecking restores the rows without reloading the log.
- [ ] **Click after filtering:** with Tanking off, click several remaining rows, including ones below hidden rows. Each highlight and
  the summary must name the same fight. Repeat with both boxes off on a finished log and while following live damage.
- [ ] **Selection across a toggle:** select fights from multiple groups, including a tanking-only NPC. Uncheck Tanking: visible fights stay
  selected, the hidden NPC leaves the selection, and the summary excludes it. If the only selected fight hides, all summaries clear.
  Rechecking must not silently select fights that were hidden.
- [ ] **Group boundaries:** with Inactivity off, Select Group and Unselect Group affect only the pointed-at encounter's visible fights,
  not the whole log or other selected encounters. Repeat with Tanking off too.
- [ ] **Sorted search and restoration:** sort by name or damage so the order differs from time order. Search with Enter and Shift+Enter:
  the matching row must be highlighted and scrolled into view in displayed order. Toggle either box and repeat. A re-derive must preserve
  selected visible fights even while sorted.
- [ ] **Live changes:** with Tanking off, an NPC becomes visible when it receives its first player damage. Existing visible selections stay
  on the same fights through updates; a verdict change that hides a selected NPC removes it from the selection and updates the summary.
- [ ] **One scroll per capture:** open a long log and let it finish reading (status line: "Monitoring Log") — the list lands at its newest row,
  once. Then fight: new rows and cell updates must not move the viewport, and scrolling up to read an old encounter must hold through a whole pull.
  Clear All and reopening the same log get the reveal again (a new load). Nothing about the pointer counts: leave it inside the grid while the log
  reads and the jump still happens. If the fight list sits behind another tab when the read finishes, opening that tab should show the newest row.
- [ ] **An ordinary log stays ordinary during a live fight** (2026-11): with boards open and damage flowing, `eqlogparser.log` (no `Debug=True`) gains
  roughly nothing. Two commands after a raid night, both expecting `0` or near it: `grep -c "other stats build(s) were running" eqlogparser.log`
  (overlap now speaks ONCE per episode plus one closing count, so a night of collisions reads as a handful of lines, and the closing line points at
  Debug for the doors) and `grep -c "timer overlay render loop failed" eqlogparser.log` (closing the timer overlay used to print that with a
  TaskCanceledException stack, which is a window shutting down, not a failure). If overlaps genuinely pile up the closing line still says so.
  A third, from the same audit: `grep -c "parse failed" eqlogparser.log` — a parser fault that repeats per log line now prints ONCE (`RepeatCap`),
  with the offending line quoted on that single line; more than one such line means genuinely different failures, which is what the key is for.
  Scrolling a search result into view still works — that is a gesture, not a follow. A new fight appearing mid-pull must not move the list at
  all (no jump to the top when a row lands), and with a column sorted a pass must keep the reading position too.

## Big-file load checks on Windows

A whole-file open reads **one stream in file order** — one reader, one capture, no cut in the middle (LogReader /
docs/DesignNotes → "Parallel loading: measured, shipped, and backed out"). Linux cannot run the app, so these are
operator checks with `Debug=True`, on a capture of several hundred MB or more:

- The progress band moves smoothly rather than sitting at 0 % and jumping to 100 %, and it reaches 100 % in step with
  `Finished Loading Log File`. `gc.tidy log loaded` appears later still — the tidy is armed at 100 % and lands on the
  first contentful board build (or its one-shot backstop), never inside the read.
- The fight list shows its band while lines are arriving and hands over rows without clearing them; an empty snapshot
  mid-read must not settle it (`FightTableLoadBandTest` covers this headless for the WPF side).
- Spot-check what a cross-line carry produces, since these are the fields a mid-file cut used to corrupt: a fight with
  critical hits reports a non-zero crit split (and the overlay marks crits), deaths show their last sentence where the
  log has one, and a Magic Die roll in the log appears on the boards exactly once.
- Totals match a re-open of the same file (same process or not): damage, healing, and fight count identical. Same-file
  determinism is the property to defend here; there is no second read path to compare against any more.
- Live tailing works right after: lines appended while the load ran appear, and the boards are not a monitor that missed
  its history. Lines now reach the parser **in batches**, so the thing to watch is latency rather than correctness — a line
  written while watching must land within about a second, not wait for 5,000 more.
- The Trigger Tester's non-realtime run still speaks its sample lines one at a time at their own pace (same seam, now
  carrying batches; a bundle treated as one event would fire every trigger at once).
- With `Debug=True`, a big open prints exactly one `gc.mode batch ON for a bulk read (N MB), from Interactive` and one
  `gc.mode batch OFF, back to Interactive` when the file runs out — and **neither** line for a small file or for a
  follow-from-end-of-file open (a monitor over a 1.2 GB capture reads nothing, so it must not change a process-wide policy).
  If a load ends without the OFF line while nothing else is reading, a scope leaked: note it as a bug, do not shrug it off.
- While a big file loads the window may feel slightly stiffer (fewer, longer collections are the deal being struck); the
  boards, the overlay and live tailing must behave normally once it finishes.
- Closing the window or opening another log MID-load leaves no wedge and no second capture alive;
  `grep -c "Error Loading File" eqlogparser.log` says whether an abort was recorded as a failed load.
- Clear All re-opens the same file at EOF (a monitor re-open, not a re-read) and every board empties and refills.

## Identity Table paging checks on Windows

Run `dotnet test EQLogParser.Wpf.Test/EQLogParser.Wpf.Test.csproj` on Windows, then use a realized Identity Table with
more than 1,000 names and a capture that is still growing. A Linux build does not exercise the pager or in-cell ComboBoxes.

- [ ] **Startup and empty states:** the grid follows the pager when its template creates PagedSource. A capture with no names,
  zero search matches and no types checked shows no stale rows or indefinite spinner. Clearing the dials restores names.
- [ ] **Whole-census lookup:** find a name originally on a later page; clear the query and recover the full list without a
  new derive. Switch NPC-only to Player/Pet, then All Types: unplaced and Taken Back rows are included only in All Types.
- [ ] **Cue and captions:** focus/blur the empty Name Filter, press Escape after typing, and close/reopen the capture. The cue
  never becomes a query. Toggle checkboxes without selecting a different ComboBox row: the closed caption updates every time.
  In Chat, To/From cues appear, clear on focus and return on empty blur without leaving autocomplete suggestions behind.
- [ ] **Global headers:** click Name, Type, Class, Owner and Why both ways; check page boundaries and equal-word name ties.
  Class really sorts class words, not names; the arrow matches the entire list, not just the current slice.
- [ ] **Cache invalidation:** visit several pages, return to one and change a verdict from another pane. Both that page and
  previously visited pages show the new word after a census; the current page reloads even when its page number did not change.
- [ ] **Editing and races:** on a later page open each cell editor, let a census arrive, then pick/escape/page away. The editor
  is not torn out mid-pick, closes cleanly, and the newest held result lands once. Change filters or write from another pane
  during the hold: an older answer never overwrites the newer question/verdict. Take-back retains its Unknown row in All Types.
- [ ] **Viewport and selection:** select a row, scroll elsewhere within its page and allow several censuses. The viewport stays
  put. A deliberate filter/sort can follow the surviving selection to its new page; shrinking results clamps to an existing page.
- [ ] **Capture isolation:** hide the pane during a build, switch logs, then show it: only the new capture appears and both
  filters are cleared. Hide/show the same capture preserves dials and sort. Repeat close/reopen while an editor holds an answer.
- [ ] **Narrow pane and scale:** count, dials and PreviousNext strip fit the docked width at supported font sizes. With a
  name-heavy capture (up to 25,000 ledger entries), record responsiveness and per-census UI cost, including on a slower machine.
  These measurements are still a release gate, not an inferred result of a passing build.

## Docking states a unit test cannot reach

Two behaviours depend on state a headless test cannot build; both have shipped broken once.

- [ ] **Show a pane that is hidden behind another tab.** Click the Pet Owners menu item while Player/NPC Identity is the visible
  tab of the right-hand strip: it must PEEK in the strip, not move into the middle of the layout, and pressing the item again hides
  it where it was. (`ToggleWindow` shows rather than relocates; a floated pane cannot share a tab strip at all.)
- [ ] **Restart with an OLD `dockSite.xml`.** Launch on a layout saved by a previous build: the window state must survive. The
  retired Verified Players / Verified Pets windows are empty hidden shells for exactly this reason — `LoadDockState` throws on a
  name it cannot resolve and the catch calls `ResetState()`, which wipes every pane, size and position.
- [ ] **Charts open as tabs in the main container.** Reported from a Windows run (one opened as an undocked popup, one docked on
  the left): open DPS Trends / Healing Trends from the View menu **and** a Hit Frequency or ADPS Timeline from a summary toolbar —
  each must land in the tab container, not floating, not against an edge. Fix: the runtime-opened windows now declare
  `CanDocument=true` beside the `SetState(Document)`/`CanDock=false` they already asked for, and `ShowStateFor` consults document-ness
  *before* `SideInDockedMode` (the side getter does not return what markup set — a `Tabbed` pane reads back as an edge — so it cannot
  decide the shape of a document). `eqlogparser.log` prints `dock: <window> shown as Document` for every menu-opened window; toolbar
  windows have no line (different door, `SyncFusionUtil.OpenWindow`). If one still opens wrong, record WHICH window and whether
  **Reset Window State** (drops `dockSite.xml`) changes it — that separates the code path from a saved layout.
