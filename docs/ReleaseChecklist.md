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
- [ ] **A menu opened without a pointer names THIS capture** (2026-10-13): right-click a fight and release outside the grid (so no
  menu appears), then Clear All or open a different log and open the menu from the keyboard (Shift+F10 / Apps key, no mouse press).
  The actions must apply to rows of the log now open, never to the released press's snapshot from the log that closed.
- [ ] **Keyboard after an abandoned right-press:** select fights, right-press and release outside the grid without opening a menu.
  Change the selection with Shift+Down or Ctrl+A, then open a menu with Apps or Shift+F10. The current selection must survive;
  no earlier pointer selection may return. Repeat while staying in the same capture.
- [ ] **One scroll per capture:** open a long log and let it finish reading (status line: "Monitoring Log") — the list lands at its newest row,
  once. Then fight: new rows and cell updates must not move the viewport, and scrolling up to read an old encounter must hold through a whole pull.
  Clear All and reopening the same log get the reveal again (a new load). Nothing about the pointer counts: leave it inside the grid while the log
  reads and the jump still happens. If the fight list sits behind another tab when the read finishes, opening that tab should show the newest row.
- [ ] **The load's cleanup pause sits behind the band, and actually returns memory** (2026-10-13): open a large log (several million lines) and watch the fight list.
  At 100 % the band stays up — *Building the Fight List…* while the first full derive and its boards run — then both the band and the application status line say
  *"Cleaning up.."* for the pause, and the grids appear **after** it. Three things to confirm: (a) no freeze lands on an already-readable grid (that is how the old
  deferred pass read in the field); (b) `PostLoadCleanup` holds the bar only until the pass ends, with an 8 s deadline of its own (sized above the worst pass ever measured,
  3,065 ms) so an unpaid debt cannot park it; (c) the process's memory in Task Manager drops at that moment on a big capture — if `gc.tidy log loaded` reports a near-zero
  heap delta there, the seam is fine and the question has moved to retention (docs/DesignNotes.md → "Garbage collection after loading a log"). Opening a log from end of file
  (the startup auto-monitor, Clear All) reads no lines and must pay no pause at all. With `Debug=True`, `grep "load band\|gc.tidy" eqlogparser.log` says which moment ran,
  which was skipped and whether a band expired while a cleanup still stood.
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
  `Finished Loading Log File`. `gc.tidy armed for capture` appears at that tick, and `gc.tidy log loaded` only later — after `Derive done` and the first board build of
  the load, on a pool thread, never inside the read (see "The load's cleanup pause sits behind the band" under Fight list checks). A tidy line during the bulk read would
  mean someone moved the pause back into the reader; docs/DesignNotes → "Parallel loading: measured, shipped, and backed out".
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
- **A long capture must not quietly stop counting.** The name pool used to throw at 32,767 distinct names on the ingest path, where a
  per-line catch swallowed it — the operator just got boards that read low. Ids are `int` now, so after a long live session or a big file:
  `grep -c "name table exceeded" eqlogparser.log` must be **0**, and a name that arrives late in a huge file (a new raid member, an uncommon
  NPC, a spell nobody cast yet) has to appear on the boards and the identity list rather than being missing without complaint.
- Live tailing is not affected by how the file was opened: new lines reach the boards inside a second.
- Closing the window or opening another log MID-load leaves no wedge and no second capture alive;
  `grep -c "Error Loading File" eqlogparser.log` says whether an abort was recorded as a failed load.
- Clear All re-opens the same file at EOF (a monitor re-open, not a re-read) and every board empties and refills.
- **Cleanup stays with its capture:** finish a bulk load, then close or open another log before its six-second backstop.
  Also switch captures while a board completion has queued cleanup or while the cleanup notice yields for rendering. No
  old cleanup may run inside the next read, stop its fallback timer, or restore the prior capture's status text.
- **Preview Parse stays cleared:** leave it open or hidden during a large board build, then Clear All or switch logs. The
  old parse, available label and selected player set must not return when the worker finishes. A build in the new log
  still publishes normally.

## Damage overlay checks on Windows

The overlay's rules live in Core (`LiveFights`) and are unit-tested headlessly, but the tick that paints and blanks it is WPF: no
Linux run says anything about these. Scenario behind each line: 2.4.x shipped with "reset on kill" unable to close and one-shots
never displaying — see DesignNotes → "When the damage overlay closes".

- [ ] **A kill closes it** (2026-11): mode "On Kill", kill a mob (a real death line, not a mob you walked away from) and stop attacking.
  The board must stay readable for a few seconds, then clear roughly 8-10 s after the last damage — not linger the rest of the session,
  and not vanish before the total can be read. Past ~30 s it must also close on a mob that simply never died.
- [ ] **A lull does NOT reset the numbers** (the case a purely time-based close gets wrong): damage a mob, do nothing for 12-20 s while
  nobody else hits anything, damage the same mob again. One board, one total — both hits in it, duration measured from the first swing.
  If the first hit disappeared, silence was treated as a kill.
- [ ] **A one-shot shows its numbers**: kill a low-HP mob in a single hit (or open the overlay a second after a pull you already won).
  The board must appear with that damage on it. An empty overlay here means the window opened *after* the facts, which was the fault.
- [ ] **It stays up while things keep dying**: kill one mob and immediately attack another. No blank in between; the second pull's
  numbers appear (and after the second death, the same ~8 s close).
- [ ] **Our own deaths do not close it**: get a party member or your own pet killed while the overlay is open and keep fighting. The
  board must not blank or reset because something on our side died.
- [ ] **Time-based modes still behave**: set 5 s (and 10 s). Board closes ~that long after the last damage, kill seen or not; a
  continuous fight never blanks mid-pull.
- [ ] **Close with the X and reopen**: the reopened board continues onto the seconds it left off on rather than starting empty, and
  Reset empties it immediately — including not refilling with the last few seconds of damage — while leaving the window usable.

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
- [ ] **No summon-word rows** (2026-10-13): `Kizant`s pet`, `X`s warder`, `Y`s familiar` appear nowhere in the table and searching for one
  finds nothing — while its **owner** does appear, a pet with a player-chosen name still shows its Owner pencil, and `…s Ward of
  Destruction` shapes are unaffected. Write any verdict onto a hidden name (another pane or an existing override) and its row comes back,
  because a click that left no row could never be taken back. The strip's total counts only listed names: it must not promise the hidden ones.
- [ ] **An eye follows its master** (2026-10-13; conditional, and three captures measured here contain no eye row at all — an eye is refused as a
  combatant before its name is interned, so check this only if one ever appears). While its master reads Player there is no `Eye of <master>` row;
  setting that master NPC from any board's menu brings the row back on the next census. An eye whose master reads NPC or Unknown, one that is not in
  the census at all, and `Eye of Veeshan`/`Despair`/`Mother` are never hidden.
- [ ] **Cache invalidation:** visit several pages, return to one and change a verdict from another pane. Both that page and
  previously visited pages show the new word after a census; the current page reloads even when its page number did not change.
- [ ] **Editing and races:** on a later page open each cell editor, let a census arrive, then pick/escape/page away. The editor
  is not torn out mid-pick, closes cleanly, and the newest held result lands once. Change filters or write from another pane
  during the hold: an older answer never overwrites the newer question/verdict. Take-back retains its Unknown row in All Types.
- [ ] **Blank class pick:** set a saved class on a Player row, choose blank, and confirm that the cell returns to what
  this capture says (or blank when the capture learned none). It must not keep the saved override or strand the editor hold.
- [ ] **Viewport and selection:** select a row, scroll elsewhere within its page and allow several censuses. The viewport stays
  put. A deliberate filter/sort can follow the surviving selection to its new page; shrinking results clamps to an existing page.
- [ ] **The pager states where it is** (2026-10-13): with more than 1,000 names the line beside the arrows reads
  `Page 1 of 3` and changes when an arrow is clicked — including on the LAST page, where the strip's range must end at the list's
  real end ("Showing 2,001 to 2,500 of 2,500"), not at a round number. Then select a row on the current page and leave the pane
  open through several censuses in a live capture: **the page must not change** (it used to be pulled back to the selected row
  every beat), while typing in the Name Filter must land wherever that answer lives. A narrowed answer reads both numbers —
  "… of 37 Identities, 12,406 total" — and an answer of nothing reads "No matching Identities, 12,406 total", never "Showing 0".
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
