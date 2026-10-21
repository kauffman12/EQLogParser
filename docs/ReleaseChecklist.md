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

- [ ] **Initial Hit Time:** on a cold start the time column is visible; re-dock the pane and change theme/font scale.
  Its width follows the current theme without disappearing or requiring a log reload.
- [ ] **Checkbox combinations:** both checked shows every display row; Inactivity off hides only dividers; Tanking off hides NPC rows
  with no damage received from players. Both off applies both rules. Rechecking restores the rows without reloading the log.
- [ ] **Click after filtering:** with Tanking off, click several remaining rows, including ones below hidden rows. Each highlight and
  the summary must name the same fight. Repeat with both boxes off on a finished log and while following live damage.
- [ ] **Selection across a toggle:** select fights from multiple groups, including a tanking-only NPC. Uncheck Tanking: visible fights stay
  selected, the hidden NPC leaves the selection, and the summary excludes it. If the only selected fight hides, all summaries clear.
  Rechecking must not silently select fights that were hidden.
- [ ] **Group boundaries:** with Inactivity off, Select Group and Unselect Group affect only the pointed-at encounter's visible fights,
  not the whole log or other selected encounters. Repeat with Tanking off too.
- [ ] **Select Group replaces; a modifier on the press makes it add** (2026-10-14): with fights selected in three groups, plain-right-click
  a fight in one group and choose Select Group — **only that group's visible fights stay selected**, everything else is deselected, and the
  boards answer for exactly that pull. Repeat holding **Ctrl** on the right-click, then again holding **Shift**: the group is **added** and the
  other groups survive. Three traps to walk. Release the modifier before clicking the menu item: it still **adds**, because the PRESS decides
  and the hand is expected to have moved by the time the item is clicked. Open the menu from the keyboard with a big selection standing (Apps,
  then Shift+F10) — that is plain, and **Shift's own keystroke must not buy additive mode**. Dismiss a Ctrl menu with Esc, then plain-right-click
  and choose Select Group: it must **replace** — an abandoned or finished Ctrl press may not leak into the next menu. Then turn Inactivity or Tanking off so part of the group is hidden: the replacing select keeps only visible fights, and an
  unmodified Select Group after a Ctrl press still replaces. **Unselect Group is unchanged** — it takes its own encounter out and leaves every
  other row where it was, including over a group nobody had selected (it must spend nothing). One accepted behaviour, so it is not filed as a bug:
  right-clicking an inactivity divider or the empty space below the list still offers Select Group, acting on the group the fallback chain names
  (cursor, then first selected fight) — raised and dropped 2026-10-14, see DesignNotes → "Select Group stands alone".
- [ ] **A press from the closed log may not set this one's mode:** plain-right-click a fight and release outside the grid so no menu appears,
  then open another log (or Clear All) and choose Select Group from a menu. It must replace, as an unmodified gesture does — the remembered
  Ctrl of the abandoned press may not cross captures.
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
- [ ] **A zero-fight load takes the band down** (2026-10-17): open a log (or Clear All into one) whose derive yields no fight
  rows. The *Building the Fight List…* band must go away without another gesture: the read-finished guard runs after ~8 s,
  and an owed cleanup can hold it for up to another ~8 s. Under Debug=True, look for `load band: hidden` and its reason in
  eqlogparser.log. Separately check whether memory growth continues; this guard is not a proven fix for that report.
- [ ] **Frozen-border animation rollout:** load a log and exercise all frozen-column surfaces: summaries, breakdowns,
  Identity Table, spell counts/casts, trigger log, loot/hit/death/event logs, spell-damage/resist/taunt statistics and Quick
  Share history. Scroll horizontally: name/time columns must remain frozen with their boundary intact. Scroll vertically,
  expand/collapse rows, change fights, page/filter Identity Table, and repeat after switching dark/light themes and
  hiding/showing panes. Selection visuals and expander styles must survive new/recycled containers and replacement
  templates. Check for no `Unable to finish the static frozen-column border animation` warning. Compare idle CPU/memory
  behavior separately; the removed temporary clock report's findings live in DesignNotes. This needs realized Windows
  Syncfusion grids; synthetic-template tests cannot establish these integration results.
- [ ] **Character-status GIF visibility:** start with Trigger Manager hidden and enabled characters configured, and load
  a log. Open Trigger Manager and verify visible waiting/active/idle icons animate; hide the pane, collapse character
  folders, disable a character or minimize the main window, then show them again and verify playback resumes. Change reader
  status while hidden so a different GIF/controller is initialized, then repeat. Compare idle CPU/memory with the pane
  hidden and visible; do not interpret legitimate visible-animation allocations as a regression. The helper's Windows
  tests target hidden-controller pause state; the temporary runtime clock-inspection menu has been removed.
  Re-run `VisibleGifAnimationTest` on Windows after switching its off-screen fixture from `Window` to an STA-owned
  `HwndSource`. If a constructor still times out, report its new hwnd/attachment/image/animation/controller diagnostics
  before changing the playback helper.
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

- The progress band moves smoothly rather than sitting at 0 % and jumping to 100 %. At 100 % with a parser backlog, status says
  `Additional Processing` until the initial read's final batch is parsed; only then does `Finished Loading Log File` appear and
  `gc.tidy armed for capture` run. `gc.tidy log loaded` comes later, after `Derive done` and the first board build, on a pool thread,
  never inside the read (see "The load's cleanup pause sits behind the band" under Fight list checks). A tidy line during the bulk
  read means someone moved the pause back into the reader; docs/DesignNotes → "Parallel loading: measured, shipped, and backed out".
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
- **Cleanup stays with its capture:** finish a bulk load, then close or open another log before its first board trio answers.
  Also switch captures while a board completion waits to publish its cleanup notice, or after collection but before its queued completion.
  No old cleanup may start inside the next read, change its status or band, or release its hold. If the replacement load has already
  armed cleanup when the old build finishes, its request must survive and still collect when its own boards answer. There is no backstop timer.
- **Preview Parse stays cleared:** leave it open or hidden during a large board build, then Clear All or switch logs. The
  old parse, available label and selected player set must not return when the worker finishes. A build in the new log
  still publishes normally.

## 48-bit amount release checks on Windows

Headless arithmetic, workbook XML and before/after load/board measurements are in DesignNotes →
"Amount widths measured across 44 million hits: 48-bit damage and healing". These are still manual release gates:

- [ ] Load damage/healing lines containing `4294967295`, `5309597698` and `281474976710655`. Check damage/tanking/healing
  grids, spell maxima, grouped hit/DPS logs, charts and FCT. Display abbreviations may round; stored totals must not wrap.
  NPC-to-player Ironbark Hurl belongs to tanking, not outgoing player damage.
- [ ] Open the detailed export in desktop Excel. Both damage sheets and all heal views must contain numeric cells; check
  landed, raw requested and overheal values at the 48-bit maximum. An absent ask stays zero.
- [ ] Compare the same large capture and selections against the pre-widening baseline using the **application process**:
  load peak, settled idle, select-all, one-fight reselection, reopening viewers and old-fight revisits. Healing raw slots
  cost four more bytes; compressed backing and process working set are different measurements. Agree on any memory tradeoff
  before release; the headless results do not establish a stable peak-memory percentage.
- [ ] Follow a log for multiple elapsed hours with regular derives and board refreshes. Append wide damage and healing late,
  verify exact totals, and verify decoded cache occupancy returns after readers release. Accelerated log-time replay does
  not certify an overnight session's memory behavior.
- [ ] Close/open another capture and Clear All with wide amounts present; no old rows, histogram lists or viewer state may
  leak into the replacement capture. WPF tests must execute on Windows, not merely build on Linux.

## Damage overlay checks on Windows

Accounting and display retention live in Core (`DamageOverlayLifecycle`) and are tested with projected facts and actual overlay totals.
These checks still require Windows for popup/hide behavior. See DesignNotes → "Damage overlay: fight accounting and display retention".

- [ ] **The meter abbreviates what the pane spells out** (2026-11): with several mobs on one board, the meter's title bar reads `C(10): <top mob>` while the Damage/Tanking Summary titles read
  `Combined (10): <top mob>`. The digits and the name must agree between them; only the prefix differs. One mob on the meter prints no prefix at all,
  and a zeroed meter over a single encounter must not grow one.
- [ ] **A kill closes it** (2026-11): mode "On Kill", kill a mob (a real death line, not a mob you walked away from) and stop attacking.
  The completed board closes about eight seconds after receipt of the last eligible damage. The death selects the final result but
  does not renew the damage receipt. With no death, On Kill closes about 30 seconds after that receipt.
- [ ] **A lull does NOT reset the numbers** (the case a purely time-based close gets wrong): damage a mob, do nothing for 12-20 s while
  nobody else hits anything, damage the same mob again. One board, one total — both hits in it, duration measured from the first swing.
  If the first hit disappeared, silence was treated as a kill.
- [ ] **A silent board is frozen, then ends in one step** (2026-12): have two or three mobs on the board, then stop all damage and watch
  for a full 30 seconds. No row may leave and no total, rate or percentage may move — membership is measured from the capture's newest
  second, so with nothing arriving the board is static — and then the whole board must close at once when that deadline passes (On Kill:
  ~30 s after receipt of the last accepted record, regardless of its log timestamp). Rows dropping out one by one, or a raid total shrinking while nobody is hitting anything, is the
  old wall-clock pruning coming back.
- [ ] **A one-shot shows its numbers**: kill a low-HP mob in a single hit (or open the overlay a second after a pull you already won).
  The board must appear with the killing blow included, even if no live frame was ever painted. It must not reopen repeatedly for that kill.
- [ ] **The next pull starts fresh before the display hold ends**: kill A and attack B within eight seconds. B's totals/time must exclude A,
  without needing the overlay to hide first. Repeat with A and B sharing a name and with both appearing in one log second.
- [ ] **A survivor keeps its own totals**: damage A and B, then kill A. B retains all of its earlier damage/time. Wait 12 seconds and hit
  B again without a death: it is still the same total. Check the tanking view too; A's incoming-damage contribution must disappear.
- [ ] **An unchanged completed board stays unchanged**: finish a fight longer than eight seconds and reopen the overlay promptly.
  Refreshes must not lose its early hits; the final display disappears after its grace period.
- [ ] **Our own deaths do not close it**: get a party member or your own pet killed while the overlay is open and keep fighting. The
  board must not blank or reset because something on our side died.
- [ ] **Time-based modes still behave**: set 3 s, 5 s and 10 s. Board closes ~that long after receipt of the last accepted combat
  record (including avoidance), kill seen or not;
  a continuous fight never blanks mid-pull. At 3 s, attacks two seconds apart (including a new opponent) must both remain
  counted even when projection briefly lags; then stop attacking and verify that three seconds of real silence hides and resets
  it without extra grace. Known NPC-only noise must not extend that timeout while projection is behind.
- [ ] **Log time and receipt time are independent**: feed identical damage with timestamps behind and ahead of the PC clock.
  Damage, DPS, RDPS and Sec must agree, without waiting for the clock to reach future-dated hits. With a ten-second reset, a line received
  eight seconds late must still buy ten seconds from receipt. During silence, filter/rebuild requests must not renew that deadline.
  Repeat Reset with a new hit in the same log second: only the post-click ordinal counts. Reading a historical file is receipt of data;
  merely reopening or re-deriving an expired capture is not. On a five-second timed opening, feed A damage at log second 0, B damage at 6,
  then an A miss at 10, all received together. The first board must already show only B, with unchanged seconds/DPS on subsequent silent
  ticks. Repeat after Reset and timed expiry, and for Tank. Pause a receipt worker after `BeginRead`, append fresh damage, then resume it:
  expiry must not cut away the appended, unchecked receipt.
- [ ] **A blocked counting worker cannot strand the toolbar** (issue #416): use a ten-second reset, paint a board, then
  hold only its worker with a debugger while leaving the dispatcher responsive. After verified activity expires, both
  toolbar and body hide within the next timer tick. Releasing the worker must not flash its expired result back on screen.
  New verified combat must recover the display. A paused dispatcher/GC cannot paint and is not this test.
- [ ] **Paint failure clears both windows**: force one UI apply failure with the debugger after bars have been cleared.
  No standalone toolbar may remain; the failure reaches Error Log and a subsequent successful refresh recovers normally.
  Check zero-total/miss-only rows too: progress values must stay finite.
- [ ] **Close/reconfigure targets its own window**: reset overlay position from the app menu while a live meter is open;
  exactly one setup preview remains and the original meter/toolbar closes. Repeat Gear, Cancel, X, then reconfigure with
  no meter open. A delayed close or Closed callback from the retired instance must not dismiss its replacement.
- [ ] **Excluded output does not renew the quiet clock**: after one included hit, send only an excluded outgoing damage
  type for longer than the reset period. The meter hides despite those lines continuing. Repeat with incoming damage of
  that type: Tank still counts it and the meter remains active. Toggle the exclusion without adding another record and
  verify that cached activity is recomputed. Ordinary healing, casts and taunts must not keep it visible.
  In On Kill, kill A with included damage, then hit/kill B using only excluded damage: A keeps its original final-display
  deadline. Included damage to B must still replace that final display immediately.
- [ ] **Header is one compact, level row**: check fonts 10, 12, 14 and 16, normal/thin bars, damage/tanking, `%` shown and hidden (Setup → STYLE → **columns**,
  since the old *show % damage* checkbox folded into that picker) and light/dark themes. Left controls and DPS/Tank must sit level with Damage/DPS/Sec, with roughly four pixels of visible
  space above the header and before the first player—not a native-window-sized empty band. Check the first opening too:
  header spacing follows the controls' content height even if Windows keeps the companion window taller; its native
  minimum must not become space above the player rows. Content must still fit at every font/DPI.
  Check all six toolbar buttons, including padding around
  their glyphs: clicks/tooltips work, the active view stays orange, and the game does not lose focus. Any transparent
  companion-window area below the controls must let clicks reach the game. Check Setup preview,
  Cancel/Save and the live meter, including mixed-DPI moves.
- [ ] **Toolbar and footer both fit at every supported font size and DPI**: check DPS/Tank at 10, 12, 14 and 16,
  normal/thin bars and both damage/tanking views, including mixed-DPI monitors. The measured toolbar content must neither
  clip its labels nor cover the first row, and the bottom raid-target/totals row must remain fully readable—not just its
  top half. Test an existing saved height without reopening Setup: live compensation must preserve its old row-space
  budget. Hide/show, switch views, resize/re-measure the toolbar by moving across DPI boundaries, and close/reopen several
  times: compensation must not accumulate or shrink the rows. Reconfigure and Save, then reopen again; the runtime reserve
  must not have been baked into OverlayHeight. Compare the actual last row, not only the toolbar's sizing declaration.
- [ ] **On Kill survives a delayed refresh near 30 seconds**: hit once, then again at ~29 s. If the new hit's projection is late,
  the board must stay visible and keep both hits when it catches up. A different opponent or a same-name life after a gap >30 s
  must not inherit the previous life’s totals. Reset while damage is pending must still discard everything before the click.
- [ ] **Timed accumulation matches the fight list**: start a fresh capture with the meter set to 60 seconds. Kill five mobs over
  several minutes without a 60-second quiet gap, then select exactly those five fights. Total damage and DPS must match the meter
  with equivalent filters. Idle polling must not dilute its DPS; only a real reset should discard those fights.
- [ ] **Close with the X and reopen**: the reopened board continues onto the seconds it left off on rather than starting empty, and
  Reset empties it immediately — including not refilling with the last few seconds of damage — while leaving the window usable.
- [ ] **Capture ownership:** switch logs or Clear All while an overlay build is in flight. No old-capture bars may land afterward.
  Reset during a build must likewise prevent that older result from reappearing.

### Incremental damage/tanking meter release gate (2026-11)

Headless equivalence and whole-history worker measurements are in DesignNotes → "Incremental damage meter:
compact accounting, paired publication". These checks are not satisfied by a Linux solution/WPF-test build.

- [ ] **Long live session:** accumulate through many pulls in timed mode, with FCT and summary panes open. Compare
  both DPS and Tank against equivalent fight-list selections. Measure actual refresh latency, UI apply, allocation
  interference and peak retention; do not substitute the headless worker timings for these observations.
- [ ] **Immediate gestures:** switch DPS/Tank while a large initial build/correction is running. Reset and Configure
  must remain responsive, and resetting must clear immediately without old bars returning. Repeat Reset then X-close
  and reopen before the old worker finishes; scalar reset state must survive without the old worker committing it.
- [ ] **Copy is the displayed revision:** copy both views during live growth, including more participants than bars,
  a class filter, pet labels and Special text. Change Parse Preview formatting afterward. New damage in the same log
  second must not change the copied totals. The open summary boards must keep their own answers.
- [ ] **Correction and replacement:** change ownership/classification mid-fight, then switch captures/Clear All with
  outstanding work. Corrected totals must converge; no old-capture rows, toolbar, or queued show callback may return.
  Run the existing WPF suite on Windows, not just its build.
- [ ] **Stress limits:** measure high-add-count On Kill and a large timed accumulation with many distinct lives.
  Historical record reconstruction must not recur each tick, but clock-block sorting, lifecycle scans and auxiliary
  annotations still scale with metadata. The headless 14,101-life THJ append exceeded the proposed 100 ms worker
  target; verify the actual supported workload before calling the long-session release gate complete.

### Six-second RDPS column, checked on Windows (2026-10-20)

The window arithmetic (six labels, divide by six from the first hit, the window riding **log** time and reading no build instant anywhere, pet folding, Tank
attribution) is pinned headless; see DesignNotes → "Six-second RDPS: per-life buckets beside the meter's own amounts". Everything below is rendering or live behavior
no headless run can see. The column now has its own switch (`OverlayShowRdpsColumn`, **off** until somebody turns it on — see "Damage meter column switches, checked
on Windows" below), and a zero prints as `0` (a dash for "no damage in the window" is an open preference, not current behavior).

- [ ] **The header lands over its columns at every font and DPI**: fonts 10, 12, 14 and 16, normal and mini bars, light/dark, damage and
  tanking, mixed-DPI monitors. `RDPS` must sit over its own column, `%`/`Damage`/`DPS` must not have moved from before the column existed,
  and `Sec` stays where it was. The header no longer has hand-tuned per-font margins: it is a grid sharing every row's column widths and right
  insets (`MeterColumnLayout` → `DamageOverlayWindow.ApplyColumnLayout`), which is why this check still decides anything that looks off. Correct a
  drifting word in the layout's inset/width presets, never by moving a value path or nudging one label.
- [ ] **Digits stay put as they grow**: watch one row cross 999 → `1.0K` → `1.0M` in RDPS. Nothing may reflow the table, clip into DPS or
  overlap `Sec`, and the name/icon area must not be covered — including the longest name you can produce (hidden-player rows, `[DoT +n]`
  crit-rate suffix) at a narrow saved width.
- [ ] **A narrow window widens at every font; nothing shrinks**: the minimum is computed per font — numeric block plus a 96-DIP name/icon
  allowance — so with **every column visible** it is **322/358/396/422** DIP at 10/12/14/16 pt (a flat 300 left zero room for names at 14 and
  overflowed at 16); the floor follows the column switches, so turn them all on before measuring these figures. At each font
  size, start from a saved `OverlayWidth` under that figure (the old floor was 250), reopen the overlay, and confirm the window returns at its
  minimum with names still readable left of the class icon. Then widen and narrow by hand: the meter never auto-shrinks, the widened value is
  what gets saved, and neither `OverlayHeight` nor the toolbar reserve changes because a column was added. Report crowding **with the font size**.
- [ ] **Quiet freezes it, like every other column**: hit once, then everyone stops. RDPS keeps showing its last value while Damage, DPS and Sec do the same thing
  (nothing decays on a capture that stopped producing lines), and the whole board retires on the meter's own quiet period — On Kill's engagement gap or the timed mode's
  seconds. Then keep the raid hitting while you stop: your RDPS must fall to 0 within about six seconds as the shared window slides past your last hit, in steps rather
  than a fade. Repeat after a burst of several hits.
- [ ] **Late lines still read full — the honesty gate**: with damage flowing, compare RDPS against what the log says the last six logged seconds
  did, while lines visibly arrive late (chat behind the fight, a burst landing in one clump). The column must read the damage that *exists*, not
  shrink because the file is lagging. The failure to report is a rate about `(6 − lag)/6` of what it should be while hits flow, or one that reaches 0 while damage is
  still landing on screen — either means something reads the PC clock for the window instead of the log's own seconds.
- [ ] **A stalled worker resumes rather than corrupting**: hold the counting worker (debugger) for several seconds and release it. Values sitting where they were *is*
  the freeze law, not a bug; what must happen is that the released worker resumes normally, the first pass after release reads full (no half-windowed rate), and a board
  whose data went far enough back still retires. Report a meter that never recovers or never goes away.
- [ ] **Tank reads incoming, per defender**: check a pet tanking alongside its owner — the pet keeps its own row and its own rate, the owner is
  not charged for it, and switching DPS/Tank shows one rate per view without either number moving the other's accounting.
- [ ] **Pets fold once on the damage side**: with a pet out, RDPS appears on the `X +Pets` row as one value summed before the divide — not a
  rate per pet row and not the visible bars' rates added together. Hide other players and class-filter the panel: the totals row's RDPS must
  not shrink with what is on screen. If any actor shows **two rows**, or a pet's rate sits beside the pet's own name, report it — the rolling
  lane resolves rows through the encounter column's own resolver and has to agree with it.
- [ ] **The window belongs to this pull**: kill A, then pull B within six seconds — A's rolling damage must not join B. In a timed mode, a retained completed fight
  keeps both its totals and its last rolling rate while nothing newer arrives; what it may never do is lend that damage to the next pull's rate.
- [ ] **Nothing else moved**: copied parse contains no RDPS anywhere, the summary boards' DPS/duration/share are unchanged during the same
  raid, and the meter's numbers still agree with an equivalent fight-list selection.

### Damage meter column switches, checked on Windows

`% / Damage / DPS / RDPS` each hide through their own key, driven by the **columns** picker in Damage Meter Setup → STYLE; names, ranking bars and
elapsed seconds always draw. The table, the defaults and the zero-width law are pinned headless (`MeterColumnLayoutTest`) and described in DesignNotes →
"Damage meter column switches: four hideable numbers, zero width when off". What follows is what only a screen can answer.

- [ ] **It looks like it did before you touched anything**: on an install that predates this change (no `OverlayShow*` column keys), the meter shows
  `Damage`, `DPS`, `Sec` and no percent — the pre-RDPS look. Somebody who had RDPS on this branch sees that column disappear and nothing else move; turning it
  back on via the picker must show full numbers immediately, with no six-second warm-up and no half-windowed rate. A `settings.txt` written by an earlier build of this
  branch may hold `OverlayShowRdps` / `OverlayShowDamage` / `OverlayShowDps` — names that never shipped and now read as **nothing**, which is why RDPS returns off rather
  than broken; the next Save writes only the `…Column` keys and those three lines die with it (they are not migrated, and nothing should migrate them).
- [ ] **The `%` reset is the only thing a returning user should notice**: the switch left the OPTIONS page (it was *show % damage*) and lives in the columns picker as
  `OverlayShowPercentColumn`. The old choice deliberately does **not** carry over — `OverlayShowDamagePercent` is read by nothing and vanishes from `settings.txt` at the next
  save — so an install that had it on opens with no percent digits. Confirm all three halves: ticking the box once brings the digits back immediately, that tick survives a restart
  (the new key was written), and nothing crashes or misbehaves because a line they had for years disappeared. Losing it permanently is not expected to matter; if it does, the fix
  is one extra read of the old name in `MeterColumns.Load()`, not a migration pass.
- [ ] **A hidden column reserves nothing**: hide `Damage`, then `%`, then each of the four in turn. No gap may sit where the number was, the remaining numbers must
  not move closer together or further apart, and names must not gain or lose room unevenly. **Check this in thin bars too** — mini mode has its own per-column text
  margins, and a hidden column that keeps one leaves a hole nothing else can explain.
- [ ] **The header goes with its column and lands over its digits**: `%` now prints a literal `%` where the header used to be blank — every header word is spelled by
  `MeterColumns` and applied at layout time, so the markup carries none and a wrong or missing word is corrected in that table, never in this window's XAML. With any
  subset of columns on, each remaining word sits over its own numbers at fonts 10/12/14/16, normal and thin bars, light and dark. A word that no longer has a column
  must not leave a ghost.
- [ ] **A number ending past its cell does not land on the next one**: the right insets are per column and several are negative, so a `%` or `Damage` figure ends outside
  its own cell inside the neighbour's empty space. Hide that neighbour (`%` + `DPS` with `Damage` off at 10 pt is the tightest case; then `DPS` + `RDPS` with everything
  else off) and check the widest digits you can produce still do not touch. Correct it in the inset preset, not by moving a value path.
- [ ] **The setup panel stays clear of a widening stage**: dock Setup to the right of the meter at a narrow saved width, then turn columns **on** one at a time — each
  raise of the minimum pushes the stage's right edge out, and the panel must follow rather than being covered or overlapping. Turn them back off: the panel does not chase
  a narrowing stage. Repeat with the panel docked left (the stage grows away from it and it must not move) and after dragging the panel somewhere of your own choosing.
  Repeat initial opening and width increases on secondary monitors **right and left** of the primary, including different DPI scales: Setup must stay on the chosen
  monitor, never jump back to the primary, and keep its gap until its own work-area edge requires clamping. Drag the pair between those monitors and resize again. Drag the stage down toward the taskbar and back: Setup keeps its bottom edge on
  the monitor the pair is on rather than trailing off screen, and it moves again when the stage comes back up (a clamp that parked it
  would read as a stuck panel).
  Where a work area is too narrow for stage plus panel on **both** sides (a small secondary monitor with a wide meter), the panel pins to that work area's LEFT edge and
  overlaps the stage — reachable beats tidy, and it must never come out off screen. With a debugger, force rectangle-read, work-area-read
  and native-move failures separately: configure stays usable, the failed attempt leaves the panel stationary, and subsequent successful
  moves recover. Repeated failures in one episode produce one WARN in `eqlogparser.log`; after recovery, a new failure may warn again.
  A failed work-area read must never fall through to an unchecked move.
- [ ] **A narrow window still widens, and nothing shrinks**: with only `Sec` left the floor is small (141 DIP at 12 pt), so start from a saved `OverlayWidth` of ~150
  and turn columns **on** — the window grows to the new floor and names stay left of the numbers. Then widen by hand and hide a column: the window must keep the width it has.
  Save persists the width the gesture ended on; Cancel leaves both the saved mask and the saved geometry untouched. The live header may widen a saved stage further
  to accommodate its toolbar; Setup has no toolbar and retains the row-only floor.
- [ ] **The live toolbar never covers the remaining header**: in Setup turn all four columns off, resize to the minimum, Save, and reopen live. `Sec` must sit to the
  right of Setup/Copy/Reset/Close/DPS/Tank, not underneath them, and the toolbar must not extend past the meter. Repeat with each single column, then all columns,
  fonts 10/12/14/16 and mixed-DPI monitor moves. A wider measured toolbar raises the horizontal floor only: no extra row-height reserve or lost footer space.
- [ ] **A long name still clears the numbers at the lowered floor**: hiding columns lowers what you can drag the window to (141 DIP at 12 pt with
  only `Sec` left), so at that narrowest width check the longest name you can produce (masked hidden-player rows, the `[DoT +n]` crit-rate suffix) —
  names are not ellipsised, and nothing may sit *under* the remaining digits. If it collides, the fix is the name allowance, not a new switch.
- [ ] **Tank reads the same words**: switch DPS → Tank with any subset of columns hidden. Headers stay `Damage / DPS / RDPS / Sec` (operator decision: tanking DPS is DPS
  done to you, it did not earn new terms), and hiding a column hides it in both panels at once — the two pools share one layout.
- [ ] **Nothing but pixels moved**: during one fight, toggle every column on and off. Totals, DPS, RDPS, `Sec`, ranking, bar lengths, percentages, the copied parse, the
  selected life and the meter's quiet deadline must all be unmoved; the meter must not rebuild its rows, jump vertically, or re-derive anything. Hiding every hideable column
  still leaves a working meter (names, bars, seconds) — it is not "no data", and it must not close or blank itself.
- [ ] **Picker honesty**: the combo's closed face counts what is on; opening it changes nothing until it closes, and one close previews one layout. Each row's tooltip
  explains its rate (encounter vs the latest six logged seconds) and no row's wording contradicts the header word it sits beside. Cancel from Setup restores
  both the previous column set and the previous window size; Save keeps them and reopens live.
- [ ] **A hostile `settings.txt` is answered the same way twice**: with the app closed, write `OverlayEnableCritRate = 7`, `OverlayFontSize = 13`, `OverlayMaxRows = 22`,
  `OverlayDamageMode = 4000`, `OverlayRankColor = chartreuse-ish`, `OverlayHighlightColor` absent; start, open the meter, then open Setup. Every field in Setup must show
  exactly what the live meter is drawing (crit off, 12 pt, 5 rows, on-kill reset, the default bar colour, highlight following the bar colour) — one reader
  (`DamageMeterConfigState.Load()`) answers for both surfaces, and the constructor used to keep its own copy of these guards with one clamp missing, which is the state this
  gate reproduces. Then Save without touching anything: the file must come back holding those settled values, not the junk.
- [ ] **The bar colour really comes out of the application dictionary**: change bar colour and highlight in Setup and press Save, then switch the app theme (light ↔ dark)
  with a meter open. Rows must repaint through `DamageOverlayProgressBrush` / `DamageOverlayHighlightBrush` — including *your own* row keeping the highlight key — and Cancel
  must leave the reopened meter on the saved colours, not the previewed ones, because the reopened constructor republishes all five meter keys from disk. A row that keeps an old
  colour after a theme switch means something grabbed a `Brush` instead of following its key (see DesignNotes → "Style state lives in the application resource dictionary").

## Tanking Summary pet rows

- [ ] Load `eqlog_Kizant_xegony-tunky.txt` and select all fights with all classes visible. With **Show Pets** enabled,
  verify a separate ``Tunky`s pet`` row totaling **18,818,260** incoming damage. Its owner keeps only the owner's
  own incoming damage, and mercenaries retain their own rows too. The pet may remain hidden from the Identity Table.
- [ ] Toggle **Show Pets** off and on. Pet rows disappear and return without a re-derive; player and merc rows remain.
  Repeat after changing the tanking damage-type and time-window controls. Damage Summary's owner/pet folding is unchanged.

## Healing board credit shapes, checked on Windows (2026-08-03)

The heal parser's credit rules moved at parse time, so the boards that read the heal stream changed on a real capture
(`eqlog_Kizant_xegony-selected.txt`: 134,331 → **163,677** heal facts, 908,155,103 → **979,411,016** landed).

- [ ] **Healing Summary visibility:** Player, Mercenary and Unknown healers appear; Pet, NPC and Spell healers do not.
      Change an Unknown healer to Pet, then take the verdict back: it disappears and returns on the next answer. Repeat
      with Mercenary/Player and verify that class filtering still works independently.
- [ ] A pet's own tick keeps the pet's full name (``Jazrakhan`s pet``), **not** Jazrakhan, in healing-received details,
      the healing log and detailed export. Hiding its summary row must not remove its healing from those surfaces or totals.
- [ ] A ward heal still shows under the player who dropped it (`Nymera\`s ward healed …` → row **Nymera**).
- [ ] NPC healers keep their spaced names (`A killer moth`, `The gods`) and spell details in captured records, healing
      received and exports, without appearing as healers in the summary.
- [ ] **Over Heal is never a negative number** anywhere on the healing board, the hit log's OverTotal column, or the
      exported *Heals by you* / *Heals to you* sheets — including rows from the negative HOT print
      (`… over time for -146133 (47344) …`, which counts as 146,133 healed).
- [ ] Nothing new joined the Identity Table as a Player because it was seen healing: mob healers read NPC (mob table), and
      a pet's row is a Pet.

## Summary board rebuilds, checked on Windows

A board's busy state is the one part of a build no headless run can see: an empty `ItemsSource`, a null one and a title swap look
identical to a test. Both halves below are the same seam seen two ways.

- [ ] **A rebuild does not clear the grid** (2026-10-14): on a farm-night log select all rows in Damage, then Tanking, then Healing.
  The previous numbers stay up with only the title changing (*Calculating DPS…* / *…HPS*) and are replaced in one step when the build
  answers. No hourglass appears in the middle of a table at any point — that spinner is SfDataGrid's answer to a null `ItemsSource`,
  which STARTED must never hand it.
- [ ] **A build with nothing to show still says "no board"**: leave the selection empty so a pane finishes with no rows. The table shows
  its empty state without a spinner, and selecting rows again repaints normally.
- [ ] **A closed capture cannot repaint the summaries:** start a large selection build, then switch logs or Clear All before
  it finishes. Damage, Healing and Tanking must remain empty until the new capture answers; no old title, rows or busy
  indicator may return. Select fights in the new log while the old work is winding down: a late old empty result must not
  erase the new board, and old healing completions must not change its tanking annotations. Also close without opening
  another log. Run `SummaryPublicationTest` on Windows for the queued-callback regression.

## Identity Table paging checks on Windows

Run `dotnet test EQLogParser.Wpf.Test/EQLogParser.Wpf.Test.csproj` on Windows, then use a realized Identity Table with
more than 1,000 names and a capture that is still growing. A Linux build does not exercise the pager or in-cell ComboBoxes.

- [ ] **Column order and take-back notice:** columns remain Name, Type, Class, Owner, Why. A take-back briefly shows
  `Recalculating…` over the name without moving its row; another click clears the previous notice and the notice clears
  after the answer or its cap. Core timing decisions are tested; this visible presentation is checked here.
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

## Line-chart calculation checks on Windows

- [ ] Run `LineChartSelectionTest`: opening DPS Trends with group rows selected must immediately show their group lines,
  and a subsequent UPDATE must keep them rather than showing No Data. Also exercise this through the actual summary pane.
- [ ] On a short known fight, compare player-only, player-plus-pets, raid and group charts: the first occupied second has
  nonzero aggregate DPS, missed attacks do not dilute average hit/crit rate, and a single-member group agrees with its player.
- [ ] **Rolling troughs:** two six-million bursts ten log seconds apart must show one million RDPS at each burst and zero
  from the first burst's second +6 through +9. Check a shorter gap with a genuinely nonzero low too: it must not be forced
  to zero. Verify Damage, Tanking and Healing, player-plus-pets, group and raid views, and rolling amount as well as rate.
- [ ] **Startup and quiet tails:** the first 600-point bucket answers 100 rolling DPS/HPS, not 600. When another player
  continues, a stopped player's line reaches zero at its last amount's second +6. When everyone stops, the plotted range
  stops at the last captured second; waiting or changing the PC clock must not extend it.
- [ ] **Mode switches and CSV:** run `LineChartSelectionTest` on Windows. Switching between rolling and aggregate modes
  must preserve original aggregate totals and buckets. Rolling CSV includes the quiet transition points shown on the chart.
  Across a gap over 30 seconds, instantaneous lines retain their existing zero markers and aggregate damage never drops to zero.
- [ ] **Rendering cost:** zoom in on quiet transitions and check responsiveness on a long multi-fight selection. Rolling
  modes add transition/endpoints, not every empty second; aggregate/instantaneous point counts are unchanged. Linux tests
  cannot verify Syncfusion rendering performance.

## Chat archive range checks on Windows

*"Playing past midnight and the chats are stuck on yesterday"* had two causes; this is the first (docs/DesignNotes.md →
"A chat range is whole days, and an untouched end dial rides the clock"). It cannot be exercised headlessly — it lives in a docked
pane's date dials — so it is worth ten minutes before a release goes out.

- [ ] **Chat pane across a real midnight.** Open the Chat pane with a player's archive loaded, leave EQLogParser running past
  00:00 (or advance the system clock an hour over midnight), then press Refresh. Today's messages must appear with **no** change
  to the date pickers and no restart, and the end-date box should have moved to today by itself. The shipped bug showed the
  newest line belonging to yesterday with a picker nobody had touched.
- [ ] **A picked end date still wins.** Pick an end date of yesterday in the same session, Refresh: today must NOT appear. Then
  move the picker to today and confirm today's messages return — that separates "follows the clock" from "ignores the dial".
- [ ] **First day of a month / year.** On the 1st (or with the clock rolled over one), refresh the Chat pane and confirm the new
  `Chat-MM.zip` — and on Jan 1, the new year folder — is read at all. The old bound skipped the whole archive, not a few lines.

## Live log rollover checks on Windows

The second cause of *"playing past midnight and the chats are stuck on yesterday"* — a monitored tail that cannot see its file
renamed away (docs/DesignNotes.md → "Following the live log across a midnight rollover"). The Windows-only
`LogReaderRolloverTest` exercises a synthetic rename; these checks still owe an actual app/game run.

- [ ] **A monitored log follows a rename.** Monitor `eqlog_<Char>_<Server>.txt`, then rename that file away while the game (or an
  append against a freshly created file of the same name) keeps writing to the NEW file. Within about a second the fight list and
  boards must carry on with the new file's lines instead of freezing on the last line of the renamed-away one. Nothing is printed
  either way — a stuck tail is silent too — so watch a board, not `eqlogparser.log`.
- [ ] **The head of the new file arrives.** Those first lines used to be skipped by a seek-to-end during the reopen window: put a
  distinctive line (a /say right after the rename) at the very start of the new file and confirm it shows up in the Chat archive
  and the boards.
- [ ] **Copy-and-replace does not double count.** With a monitor running and damage on the boards, rename the current file
  away and place an identical copy under the same name, with one new damage line appended. The old totals must stay put and
  the new line must count exactly once. A new creation stamp alone does not make the copied lines new evidence.
- [ ] **Old tail is not lost.** Append a distinctive /say to the OLD file immediately before renaming it; create the new file
  with a different /say as its first line. Both must enter the Chat archive exactly once, in old-then-new order.
- [ ] **Recovery reopen does not double count.** A same-file recovery with no new lines must not make board totals jump;
  a genuine truncate/rewrite starts from the new content, not from a buffered piece of the old file.
- [ ] **Log Management still archives.** Trigger archive-on-activity (a file over the size/age thresholds): the move + recreate
  must leave the monitor following the new empty file rather than stalling on a name that briefly does not exist.
- [ ] **A long raid does not lose the watcher's folder.** The watcher is narrowed to `FileName | DirectoryName` with a 64 KB buffer
  precisely so append floods cannot overflow it; if `eqlogparser.log` ever prints `Directory watcher faulted`, say so — rollover
  then rides on the 200 ms poll alone, which still works but loses its promptness.

## Log Management Save and reopen checks on Windows

Use a disposable log/configuration for archive and write-failure checks. The headless settings round-trips cannot prove
real window events or filesystem permissions; `LogManagementWindowTest` also needs a Windows run.

- [ ] **Each field sticks.** Change the folder, size, age, compression, organization, mode, day, hour, minute and AM/PM;
  Save and reopen, then restart the app and reopen again. Repeat with an existing drive root, a UNC share, and a folder
  containing `=`. Scheduled mode with both **Any Size / Any Age** must retain both, not the previous thresholds.
- [ ] **Reverting is not an unsaved change.** Change compression and restore it, or select the existing folder again:
  Save returns to disabled and Archive Now must not warn about unsaved edits. A real pending edit must still block Archive Now.
- [ ] **Enable transitions revalidate.** In enabled Activity mode choose both Any thresholds: Save is disabled. Disable
  management: Save becomes available. Re-enable it: Save is disabled again. Repeat with no folder selected; saving while
  disabled must not store the literal folder hint.
- [ ] **A failed write is not a successful Save.** Make the test settings file unwritable, edit a value and Save: the
  dialog remains open with the draft and a warning, the Error Log contains the failure, and the active settings/schedule
  remain unchanged. Restore write access and retry without another edit: Save succeeds and reopen retains the draft.

## Bot table (`mercs.txt`) checks on Windows

R27-mercdb claims every name in `data/mercs.txt` as **Mercenary**, above an NPC target frame but below positive player
proof. Merc is already player-side, so no board carries a merc filter and none should be added (docs/DesignNotes.md → "The bot table in
`data/mercs.txt`"). Everything below is a grid read, not a log line.

- [ ] **A bot is never a fight.** Open a capture with a bot in it (`a bloodbone fighter`, `a gingerbread soother`, a
  `Candlefolk …`) and confirm the fight list has NO row named for it — not even a one-row engagement from its own swings.
- [ ] **…and its number is on the boards.** The same bot must appear as a damage row like any player, and its hits landing
  from a mob must reach the tanking board as damage received. If it has no row anywhere, the name did not match the file:
  check the spelling (matching ignores case, so casing is never the reason).
- [ ] **A healer bot heals on the heal board.** A soother that only ever appears in heal lines still gets its row and its
  spell sub-rows — it is claimed from the shared name pool, not from damage facts. Healing received by a listed merc and
  its self-healing must also count without any group-join or registry sighting.
- [ ] **The Identity Table says who told it.** Type column **Mercenary**, Why column **Mercenary DB**, hover **In the
  Mercenary DB**, one clause on the head line. No filename anywhere.
- [ ] **A wrong row is correctable both ways.** Typing NPC over a bot sticks for that capture; the take-back icon returns it
  to Mercenary on the next pass (the table claims it again, which is what the shipped files say). Deleting the row from
  `mercs.txt` must make it read like any other name of its shape — an article-shaped one comes back as NPC.
- [ ] **An NPC target frame does not make a bot hostile.** Target a listed merc so the log writes `Targeted (NPC):`:
  it stays Mercenary, has no fight-list row of its own, and its damage and damage received still reach the boards.
- [ ] **Positive player proof outranks the file.** A `Targeted (Player):` line on a listed name makes it read Player for
  the rest of the capture, and the hover says so rather than arguing.

## Text and timer overlay positions on Windows

Use equal display scaling initially, to isolate desktop coordinates from DPI behavior. Repeat each placement check for
both kinds of trigger overlay (DesignNotes → "Trigger overlay positions are signed desktop coordinates").

- [ ] **Left/above monitors retain negative coordinates.** Drag a preview onto a monitor left of the primary, save in the
  preview, change an unrelated font/color option in the properties pane and save there too. Close and reopen the preview,
  then fire a real trigger: both windows must stay at the saved location. Repeat with a monitor above the primary and a
  monitor above-left; the Top/Left editors must show the negative values rather than zero. Restart the app and fire again.
- [ ] **A wide desktop is not cut off at 9999.** If the desktop extends far enough, position an overlay beyond that Left
  value and repeat the preview/properties saves and live trigger. The numeric editor must preserve the whole coordinate.
- [ ] **Another preview cannot move the selected overlay.** Keep overlay A selected in the properties pane with an unsaved
  non-geometry change. Move and save preview B, then save A's properties. A must keep its own geometry and pending change;
  B must keep its new position. Repeat with text/timer roles exchanged, and while a trigger (not an overlay) is selected.
- [ ] **A geometry refresh is not a new edit.** Saving a preview while its properties pane is selected must refresh the
  geometry without enabling Save/Cancel on an otherwise clean pane. If other edits were already pending, those buttons
  must stay enabled and the pending values must survive.

## Detailed .xlsx export checks on Windows

File ▸ Save ▸ **Selected Fight to E_xcel** writes ECMA-376 SpreadsheetML by hand (`EQLogParser.Core/src/export/`,
DesignNotes → "The detailed export is an Excel workbook, written by hand"). The structure is asserted headless; what only
Excel can answer is whether it opens and behaves. No new dll ships with this feature, so the installer lists stay as they are.

- [ ] **It opens as a workbook, not a repair prompt.** Select one fight, export, double-click the file. Eight tabs in order:
  Player DPS, NPC DPS, Spells cast, All heals, Heals by you, Heals to you, Players, Event Info — header row bold and frozen, filter
  buttons present on every sheet.
- [ ] **Numbers are numbers.** In Player DPS, sort the Damage column descending: a text column sorts 1, 10, 20, 9. Insert a
  pivot over the range and drag Damage into Values — it sums without "Number Stored as Text" warnings.
- [ ] **The pet rows name their player.** Owner is filled on possessive and custom-named pets. Player repeats a player's
  own name and groups pet damage under its owner; damage Class follows that player. Check a charm before/during/after its
  window. A charmed player moves to NPC DPS only during the charm; a charmed NPC moves in the opposite direction.
  A mercenary reads Mercenary in Players, opens no fight-list row, and still has its damage rows.
- [ ] **Relative time matches the captured event.** The pull's own second is `1`, one second early is `-1`, and five seconds
  early is `-5`; the scale has no zero. Log Time is the timestamp from the log, not the PC clock at export time.
  Event Info states the window, the totals and which rule the heal sheets used.
- [ ] **The Players tab reflects this event's personas.** Capture-time class wins over saved/latest class, with the saved
  class as fallback when this log has no evidence. A multi-persona selection keeps separate class rows with first/last seen
  times. Pet names appear in Pets and Pet Of; a caster with no DPS is still listed.
- [ ] **Spell names group across ranks.** Spell/Skill is clean, Rank is separate, and Full preserves the original text.
  Damage Type carries the melee verb where available; Category retains the broader damage classification.
- [ ] **The over-heal column is the healing that was not needed.** On a `for 3461 (60581)` line the sheet says Heal 3461,
  Over Heal 57120. The gross asked-for number under that header is the bug this check exists for.
- [ ] **The menu itself opens.** File ▸ Save shows the Excel entry with its glyph and MainWindow loads: an icon name that
  is not a member of `FontAwesome5.EFontAwesomeIcon` builds clean and throws at window load (headless: `XamlIconNameTest`).
- [ ] **It opens outside desktop Excel too** — Google Sheets and LibreOffice both, since the users this feature is for are
  the ones without Office. A repair prompt in any of the three is a package bug, not a quirk.
- [ ] **Selection is membership, not an envelope.** Select two non-adjacent fights and leave an encounter between them
  unselected. Its damage must be absent, as must simultaneous damage against another unselected NPC. Casts/heals use the
  union of lifetimes plus each lead-in; overlaps do not duplicate rows. Event Info Seconds excludes gaps and lead-in.
  Select a charm-closed encounter with a linked hidden post-charm row: both halves' damage must export even though the
  hidden half falls outside the visible encounter's time window.
- [ ] **All heals reconciles.** Other players' and NPC heals appear there once; summed Heal equals Event Info Healing.
  Direct/HOT Type and Requested Raw survive. The two local-player sheets remain views, not a total to add together.
- [ ] **Live export is internally consistent.** While the log grows, export an active fight. Row sums/counts agree with
  Event Info. Change capture during saving: no replacement-capture identity or class may leak into the workbook.
  If the capture changes before preparation, a cancellation message explains why nothing was saved and the old target
  remains untouched; after preparation, the frozen export may finish normally.
- [ ] **Custom title is honored.** Enter a title in Parse Preview, export, and check Event Info Event. With no custom
  title the selected NPC names supply it. Every data sheet still starts with exactly one table header.
- [ ] **A whole-night export finishes with the progress dialog open**, on a capture with hundreds of thousands of damage
  rows, and the resulting file opens (this is the case the row-ceiling split exists for).
- [ ] **Refusals are words, not files.** No log open, and a log open with nothing selected, each say so in the save menu
  dialog and write no file. An existing target file is overwritten only through the save dialog's own confirm.
- [ ] **Failed exports release their files**: induce a write failure (for example a full test volume). The old target stays
  intact, the message directs the operator to Error Log, and the exception appears there. The temporary file is not held
  open by the app; saving again must work after recovery.
