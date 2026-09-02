# EQLogParser Design Notes

Behaviour that is deliberate but not obvious from the code, and the rules that keep it that way.
Read the relevant section before changing import, sharing or migration code so decisions are not
quietly reversed. Style rules live in [CodingStandards.md](CodingStandards.md); this file answers
*"why is it like this?"* and *"what must not change without a decision?"*.

## Trigger and Overlay Import

"Import" means taking an `ExportTriggerNode` tree from some producer and merging it into the tree
stored by `TriggerStateDB`. The producers differ in transport and — importantly — in what identity
the payload carries:

| Producer | Wire form | Network | Identity carried by the payload |
| --- | --- | --- | --- |
| File import `*.tgf.gz` (triggers) | gzip JSON `List<ExportTriggerNode>` | no | none |
| File import `*.ogf.gz` (overlays) | same | no | overlay leaves carry their EQLP `Id` |
| File import `*.gtp` | GINA file export: zip containing XML | no | none — **triggers only** |
| Quick Share `{EQLPT:key}` / `{EQLPO:key}` | key in an EQ chat line; payload fetched by key from the share host (`TriggerUtil.RunQuickShareTaskAsync`) | yes | same as the file exports |
| GINA share `{GINA:key}` | SOAP `DownloadPackageChunk`, up to 100 sequential chunks (`GinaUtil.RunGinaTaskAsync`) | yes | none — **triggers only** |
| NAG migration | local JSON chosen through the NAG directory picker | no | triggers: `OriginalId = nagTriggerId`; overlays: `OverlayData.Source = "nag:{overlayId}"` and **no Id** |

### The overlay tree is flat, the trigger tree is not

- Triggers live under the `Triggers` root, may contain folders, and are enabled per character.
- Overlays live under a single `Overlays` root as a **flat list**. Nothing in the product can create an
  overlay folder: the overlay context menu (`TriggersTreeView.xaml`) has no **Folder** command, and
  drag-and-drop refuses nesting for both trees (`ItemDropping` rejects `DropAsChild` unless the target
  `IsDir()`). The exporter therefore only ever writes overlay leaves directly under the root.
- Consequences to respect:
  - `ImportOverlays` always merges into the `Overlays` root, whatever was selected — the parent picked
    in the file dialog is used for triggers only.
  - Do not add folder-merge semantics, or features that assume grouping, to the overlay tree. Real
    grouping would need a product decision plus a data-model change and migration — not a patch in the
    import branch.
  - Overlays have no per-character enabled state: `ImportOverlays` passes no character ids, so nothing
    is written to `TriggerState.Enabled` for an overlay. Only triggers are enabled per character.

### Identity: what counts as "the same node" on re-import

This is the core contract of importing. Matching lives in `TriggerImportPlanner` (triggers) and in the
overlay branch of `TriggerStateDB.Import`.

**Triggers**
1. If the payload carries `OriginalId` (NAG), match on that. When more than one stored sibling — or
   more than one member of the incoming batch — shares the id, the family is disambiguated by `Name`,
   because one NAG trigger can expand into several siblings (phrase + timer variants, counter resets).
2. Otherwise match on `Name`.

`OriginalId` exists because NAG allows duplicate trigger names and the importer renames collisions
(`X` → `X (2)`), so name alone would break re-import and pile up duplicates. Trigger payloads carry no
node ids, so ids are never a trigger match key.

**Overlays — `Id` if present, `Source` only when there is no `Id`, never the name**
1. Match the sibling with the same EQLP `Id`.
2. If the payload has no `Id`, and `OverlayData.Source` is non-empty, match the sibling with that
   `Source`.
3. Otherwise insert a new overlay.

Rationale:
- The exporter writes `Id` only for overlay leaves, so `Id` is the only handle an EQLP→EQLP share or
  `.ogf.gz` file can offer, and it identifies content exactly. Checking it first is correct.
- `Source` is **not** a cross-user identity. NAG mints overlay ids as random 16-character nanoids per
  install, so `nag:{id}` values are only comparable within one person's own lineage. Its whole purpose
  is that re-running the NAG migration on the same install refreshes the overlays it created earlier
  instead of adding a second copy — which works precisely because NAG payloads carry no `Id`.
- Never match overlays by name. NAG's setup wizard creates overlays named things like *Detrimental
  Timers* and *Beneficial Timers*, so two players' same-named overlays are unrelated; a name match
  would silently overwrite someone else's work.
- On a `Source` match the stored overlay is also **renamed** to the incoming name, so content and name
  follow the latest migration. That is reachable only when no `Id` was supplied — i.e. NAG payloads —
  which is why it cannot clobber a shared overlay's local name.
- Accepted consequence: if one person migrates the same NAG database on two machines and then shares
  an overlay between them, they get one extra copy to delete. Do not "fix" this by matching on name or
  by consulting `Source` before `Id`; it would require inventing an identity NAG never had, and the
  alternative is silent clobbering.

**Kind safety (both trees)** — a payload-carrying leaf may only update an existing leaf, and a folder
wrapper may only merge into an existing folder (`MatchesReimportKind`). Node kind is defined by payload
presence (`TriggerNode.TriggerData`/`OverlayData`), there is no kind field. Without this, a folder
wrapper matching a same-named trigger would reach the overwrite branch with null data and erase it.

### Node ids when inserting

- Imported **triggers** always get a store-generated id, whatever the payload contains.
- An imported **overlay** keeps its exported `_id` only while that id is free in the collection; if it
  is taken (importing the same share into a second place — routine) a new id is generated instead.
  `_id` is unique across the whole collection, so trusting an exported id would throw and roll back the
  entire import. This is silent on purpose: it is expected behaviour, not an error (see
  CodingStandards → Logging).

### Threading in the share pipeline

- A share download must never block the dispatcher. Either be genuinely async
  (`TriggerUtil.RunQuickShareTaskAsync` uses `GetAsync`/`ReadAsStreamAsync`/`CopyToAsync`) or make sure
  the continuation resumes off the caller's context (`GinaUtil.RunGinaTaskAsync` uses
  `ConfigureAwait(false)` because its chunk loop is synchronous). The entry points are click handlers,
  so a naive `await` captures the WPF context and resumes the whole transfer on the UI thread.
- Core must not touch WPF. Dialogs and messages go through host hooks (`GinaPlatform`,
  `TriggerStorePlatform`) that marshal to the UI thread; `App.xaml.cs` wires them at startup. The Core
  defaults are no-ops / fail-open so a host that forgets a hook degrades instead of throwing — which
  means **a new hook must be wired in `App.xaml.cs` in the same change**, or it silently does nothing.

### Media validation, badges and fixups during import

- Icon, sound-file and sprite validation runs through `TriggerStorePlatform` hooks (see above).
- The missing-media result must **accumulate** (`hasMissingMedia |= CheckMissingMedia(…)`): the return
  value is what flags the containing folder, so assigning it lets a later clean sibling erase an earlier
  hit and the folder badge disappears.
- A trigger's `SelectedOverlays` references are filtered to overlays that exist in the tree
  (`ValidateOverlays`). Dangling references are dropped, never remapped — do not turn this into a "pick
  something similar" repair.
- `RecentlyMerged` and `MissingMedia` are in-memory session badges surfaced by the view builder and
  cleared from the *Clear Recently Merged* menu. Nothing persists them; don't rely on them across
  restarts, and don't use them as import bookkeeping.
- Imported overlays pass through `SetVerticalAlignment`, which repairs alignment stored by older
  versions at a hard-coded original resolution. That is why an imported overlay can move.

### Names and whitespace

LiteDB trims leading/trailing whitespace on stored strings. Every incoming name is trimmed before any
matching happens (`NormalizeName`), because the NAG dump contains padded names like `" Emollious
colours…"`, and an untrimmed name never matches its trimmed stored twin — each re-import would add a
duplicate instead of updating.

### Keep matching logic out of the store

`TriggerImportPlanner` is pure: it takes the target folder's siblings plus the `OriginalIds` that occur
more than once in the incoming batch and returns an `ImportDecision`; `TriggerStateDB.Import` applies it
to LiteDB. New trigger matching rules belong there, with unit tests under
`EQLogParser.Test/src/store`, so they are covered on any platform rather than only in a WPF session.

### Known warts, deliberately unfixed

Recorded so they are not mistaken for open bugs — each was reviewed and left alone:
- The overlay **Import**/**Export** tooltips mention "the Selected Folder" / "Selected Folders" although
  overlay import ignores the selection and always merges into the `Overlays` root. Text copied from the
  trigger menu; cosmetic.
- The generic walker in `TriggerStateDB.Import` will still create a directory node in the overlay tree
  if a hand-edited or foreign payload contains a node with neither payload type. EQLP's exporter cannot
  produce one, so nothing validates against it today.
- `GinaUtil` builds its SOAP envelope by concatenating the session id taken from the chat line, and sets
  `Content-Length` by hand to a character count. The GINA service is effectively offline, so this path
  fails fast; if it ever matters again, build the request with a real XML writer instead of appending.

### Decisions that were reconsidered and rejected

- Matching overlays by name (clobbers unrelated players' overlays).
- Consulting `Source` before `Id`, or always loading all siblings to find a `Source` match.
- Logging overlay id collisions, re-imports, or other expected outcomes of normal sharing.
- Adding folder-merge semantics to overlay import without a product decision.

## Speech synthesis and TTS engines

The audio subsystem can speak trigger callouts with one of three engines: the Windows speech API, Piper, or
Kokoro. One speaks at a time. Which one is a user setting (`TtsEngine`), applied at startup and switchable while the
app runs.

### One engine behind ITtsEngine

Each engine implements `ITtsEngine` (`EQLogParser.Audio/src`) and owns its own per player voice state. A factory walks
the configured preference (`Tools > Select TTS engine...`, settings key `TtsEngine`) and falls back to Piper and then
the Windows voices, so a missing model or voice pack is a silent downgrade rather than an error dialog.

Before this seam `AudioManager` carried `_usePiper` / `_useKokoro` booleans consulted at a dozen sites — voice listing,
default voice, per player voice binding, synthesis, sample rates, shutdown — and kept a synth object per engine inside
its player records. Every engine therefore touched every other engine's code, and none of it could be exercised
without the native library behind it.

### Synthesis threading and cache

`SpeakTtsAsync`, `TestSpeakTtsAsync`, `SpeakOrSaveTtsAsync` and `TestSpeakFileAsync` are fire-and-forget for their
callers, so they hand the work to the thread pool and never resume on the caller's context. Synthesis happens inside
the engine behind `Task.Run`, because ONNX Runtime and SAPI block: a Kokoro sentence costs a few hundred milliseconds
and used to run on the UI thread, freezing the window mid callout. One `SemaphoreSlim` still serializes synthesis —
the neural engines are CPU-bound and overlapping calls only slow every caller down.

Synthesized PCM is cached in the same memory cache used for audio files, keyed by engine, voice and a hash of the text
(60 minute sliding expiry, sized in bytes so the existing 100 MB budget accounts for it). A line like `Got the level
90` plays dozens of times a raid, so only the first occurrence pays for inference. The text is hashed rather than used
verbatim so a long custom callout cannot produce an unbounded key. Even the cache lookup runs under the gate: doing it
outside would mean reading `_tts` without the lock that keeps synthesis and engine swaps apart.

### Switching engines while running

`AudioManager.SwitchEngineAsync` builds the requested engine, lets it discover its voices, re-binds every player, swaps
it in and disposes the one it replaced. `Tools > Select TTS engine...` calls it on selection change and after a Kokoro
download finishes, so picking an engine takes effect on the next callout instead of on the next start. The saved
setting still decides what a fresh launch uses, and a switch that cannot be honored (model missing, native library
refusing to load) leaves the current engine speaking rather than leaving the app without speech.

Two things make swapping safe rather than merely convenient:

- The whole switch runs under the synthesis `SemaphoreSlim`, so an engine is created and destroyed while nothing can
  be speaking through it. Synthesis re-reads `_tts` *after* acquiring that gate, which is why a callout that arrives
  mid switch cannot end up using a retired engine, nor cache PCM under the wrong engine's key.
- A voice name from one engine means nothing to another, so `AudioManager` remembers what the host asked each player
  to speak with (`_requestedVoices`) and replays those names to the new engine. The engine binds the names it has and
  drops the rest, which sends that player back to the engine's default voice. Kokoro deliberately refuses to remember
  a name it does not have: a stale name would otherwise cling to a player for the rest of its life and be spoken
  quietly as a different voice.

### Windows voices are proven, not assumed

Windows is the only engine with no files to check: the voices live in the operating system, so "is it available?" has no
answer short of asking one to speak. Historically the code answered *yes* unconditionally and swallowed whatever came
back, which reads as silence with nothing in the log.

Now `LoadVoicesAsync` records the verdict (`WindowsTtsEngine.IsAvailable`) and everything downstream — engine choice at
startup, what the picker lets you click, whether a switch is honored — reads it:

- **Wine is answered before anything has to fail.** `ntdll.dll` exports `wine_get_version` and real Windows never has,
  so asking for that export is not a heuristic about build numbers or registry keys a service pack can move. The check
  is worth its cost because the two errors are not equally bad: wrongly concluding "this is Wine" switches off the only
  engine a machine has, while a wrong answer in the other direction just leaves the runtime probe below to catch it.
  Loading `ntdll.dll` pinned to System32 keeps that from being spoofable by a planted copy next to the executable, and
  the result is cached. Whisky, Bottles and CrossOver are all Wine underneath and land here too; a real Windows install
  in a VM on Linux keeps its voices, which is the correct answer.
- **Unknown counts as available.** The probe only runs for the engine that actually starts, so an unprobed engine must
  not be hidden. This is the last engine standing; hiding it on a guess would silence someone who is fine.
- **False from the runtime probe means both APIs came back empty.** WinRT `SpeechSynthesizer` and legacy SAPI are
  checked independently and either one is enough: a machine with only legacy voices installed stays available. Windows
  images with the speech runtime removed produce nothing from both, which is the case this catches that Wine does not.
- **An engine with no voices is not a switch target.** `SwitchEngineAsync` asks the new engine for its voice list after
  `LoadVoicesAsync` and refuses it if empty, staying on the current engine instead of reporting a successful switch
  into silence.
- If nothing at startup turns out to be usable, `LoadValidVoicesAsync` logs it. That line is the difference between a
  bug report saying "no audio" and one that says what to fix.

The picker greys an engine out only when there is neither a way to use it nor a way to get it: Piper and Kokoro stay
clickable while not installed because clicking them is how they get downloaded, and Windows goes grey when it has been
caught having no voices. `GetEngineDescription` in the dialog says so in words too — the Windows voices come from the
OS, which is why a Wine or Linux session usually has none.

### What installs and what downloads

The installer carries the app plus two small assemblies that `EQLogParser.Audio.dll` is compiled against
(`KokoroSharp.dll`, `Microsoft.ML.OnnxRuntime.dll`) so the seam types resolve and an engine reports itself unavailable
rather than failing. Everything heavy is fetched into per-user storage on demand:

```
%LOCALAPPDATA%\EQLogParser\kokoro\   bin\ (MisakiSharp, NumSharp, OpenTK, Numerics.Tensors)
                                     native\ (onnxruntime.dll, providers_shared)
                                     voices\ (*.npy + LICENSE)
                                     model\kokoro-fp16.onnx
%LOCALAPPDATA%\EQLogParser\piper-tts\  piperApi.dll and friends, voices\, espeak-ng-data\
```

`LocalApplicationData`, not `ApplicationData`: the Roaming folder is copied at logon and logoff on profile-redirected
machines, and 230 MB of re-downloadable binaries is the worst possible thing to put on that path. EQLP's own state
(`config\`, `logs\`, `archive\`) stays in Roaming where it belongs — roam what the user made, download what we ship.
`TtsPackManager` owns those directories: it resolves them, downloads and verifies packs, and deletes them. Publish order
is fixed by one fact — signing rewrites a file's tail, so the SHA-256 manifest the app verifies has to be generated from
the signed bytes (`sign.cmd`, then manifest, then upload).

Nothing here needs to load at startup: .NET resolves assembly references on first use, which is what makes hosting the
engines remotely possible at all. Two hooks cover a pack once it exists:

- `AssemblyLoadContext.Default.Resolving` answers `MisakiSharp`, `NumSharp`, `OpenTK*` and `System.Numerics.Tensors`
  from `<kokoro>\bin`, using `Assembly.LoadFrom` on the default context rather than a private `AssemblyLoadContext`.
  A second copy of a shared dependency in another context would not bind to the KokoroSharp that installs beside the
  executable, and type identity across the seam matters more than isolation here.
- `ResolvingUnmanagedDll` answers `onnxruntime.dll` and its provider stub from `<kokoro>\native`, which is what
  `Microsoft.ML.OnnxRuntime`'s own P/Invoke stubs ask for. Piper needs neither: its import resolver loads
  `piperApi.dll` by full path and the OS takes the dependencies sitting beside it.

Both return "not mine" (null / `IntPtr.Zero`) for anything they do not have, so unrelated loads are untouched.
The hooks are registered once, before any engine is constructed.

There is no fallback to a copy beside the executable, and there deliberately is not one. Reading `{app}\piper-tts` when it
was complete -- which earlier releases did, so that upgrading did not cost a re-download -- meant an engine could be
running off files the dialog cannot update, cannot remove, and cannot match against a pinned digest, and worse: those
files are still sitting in old build outputs, so development runs reported a working Piper nobody had downloaded. One
location per engine, `%LOCALAPPDATA%\EQLogParser\<engine>`, owned end to end by `TtsPackManager`. `[InstallDelete]` now
deletes what installs before packs left under `{app}`; the files are inert whether or not they are removed, so that entry
is about reclaiming space, not about behavior.

### Kokoro model integrity

The Kokoro graph (156 MB) is not part of the installer. It arrives over HTTPS inside the Kokoro runtime pack the first
time a user opts in, which means the file the app later executes with its own privileges came off the network rather
than out of a signed package.

- `TtsPackManager` pins the SHA-256 of the archive and verifies every file in it against the pack's `manifest.json`
  before promoting it into place, and `KokoroTtsEngine` independently pins the graph itself
  (`ModelSha256`) and re-checks it before handing the path to onnxruntime. Two independent pins: a pack that was
  built wrong, or changed after install, still does not get to run.
- A verified model gets a `kokoro-fp16.onnx.sha256` marker beside it, so the hash pass costs nothing at every
  start. A hand-placed or previously downloaded model pays for it once, then writes the marker.
- We do not delete a model that fails verification, and we do not re-hash on every load: a mismatch is reported in
  the log once, the engine reports itself unavailable, and the existing fallback chain picks up. Deleting a user's
  156 MB download over a checksum we could have mispinned is the worse failure.
- Changing `ModelFileName` (for example back to the fp32 graph) means updating `ModelSha256` in the same commit.
  The two constants are the pin.

### Piper native lookup

`piperApi.dll` lives in the Piper runtime pack (`%LOCALAPPDATA%\EQLogParser\piper-tts`) and nowhere else, so it needs a
search path of its own. The
first implementation called `SetDllDirectory`, which is process-global and single-slot: it applied to every later
native load by anyone in the process, silently replaced any other caller's directory, and was the cause of a real
bug where listing Windows voices initialized Piper as a side effect.

`PiperTtsEngine` now registers a `NativeLibrary` import resolver that answers exactly one library name, `piperApi.dll`,
from whichever pack directory is in play (the engine's own copy is captured when it is built, so downloading a pack
while an older Piper is alive cannot move files out from under it — and `initialize()` re-runs against the new
espeak-ng data when the directory changes). Everything else returns `IntPtr.Zero` and resolves normally. Piper's own
dependencies (`onnxruntime.dll`, `espeak-ng.dll`, `piper_phonemize.dll`) sit beside `piperApi.dll`, which the altered
search path used by `NativeLibrary.Load` covers.
