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
the configured preference (settings key `TtsEngine`, set from the TTS Engine item of the **Tools** dropdown on the
Triggers toolbar, alongside Dictionary and Quick Shares) and
falls back through the remaining engines in order - a Piper whose pack is missing or will not load reaches an installed
Kokoro before the Windows voices, which are the last resort and need no files at all - so a missing model or voice pack
is a silent downgrade rather than an error dialog. Engine names are matched case insensitively at every boundary
(`TtsEngineFactory.Normalize`) because the setting is plain text somebody can edit.

The session's first engine is built on the thread pool from the `AudioManager` constructor, not inline: a Kokoro
session over the 156 MB model graph takes seconds, and that constructor runs on the UI thread the moment startup
touches `Instance`. `LoadValidVoicesAsync` is the single point that waits for the build - startup awaits it before the
main window shows, players register only after it - so the engine field can be null only in that window.

Before this seam `AudioManager` carried `_usePiper` / `_useKokoro` booleans consulted at a dozen sites — voice listing,
default voice, per player voice binding, synthesis, sample rates, shutdown — and kept a synth object per engine inside
its player records. Every engine therefore touched every other engine's code, and none of it could be exercised
without the native library behind it.

### What a voice is called

`GetVoices()` hands out ids - `af_nicole`, `en_US-lessac-medium`, `Microsoft David Desktop` - and those exact strings are
what a character's config stores and what an engine is told to speak. The pickers show something shorter through
`ITtsEngine.GetVoiceDisplayName`, reached from the view layer by one converter (`VoiceNameConverter`) on the three voice
dropdowns, so the item itself stays the id: whatever saves, matches or speaks a voice never sees the label, and no stored
value changed.

Where the label comes from is each engine's business. Kokoro reads the accent straight out of its own naming convention,
`[locale][gender]_name`, which turns `af_nicole` into "Nicole (US)" and `bf_emma` into "Emma (GB)" - worth showing now
that the pack carries British voices too. Piper labels a voice with the locale its model declares in `language.code`
(`voices.json` may say so outright), settled once when the engine starts because it costs a small file read per voice;
a name that cannot be located goes without a suffix. Windows answers with the name it was handed: "Microsoft David
Desktop" needs no improvement, and the `(Legacy) ` marking on a System.Speech voice is information rather than noise.

A third form exists because one place says a voice's name out loud. Picking a row in a voice dropdown previews it by
speaking that name, so `ITtsEngine.GetVoiceSpokenName` returns the name with neither the identifier's leading letters nor
the locale tag — "Nicole" from `af_nicole`, "Cori" from a Piper pack whose display label reads "Cori (GB)". Handing the
stored value to a synthesizer instead spells it: Kokoro reads `af_heart` as letters, so choosing Heart was answered by
somebody reciting "ay eff heart".

### Synthesis threading and cache

`SpeakTtsAsync`, `TestSpeakTtsAsync`, `SpeakOrSaveTtsAsync` and `TestSpeakFileAsync` are fire-and-forget for their
callers, so they hand the work to the thread pool and never resume on the caller's context. Synthesis happens inside
the engine behind `Task.Run`, because ONNX Runtime and SAPI block: a Kokoro sentence costs a few hundred milliseconds
and used to run on the UI thread, freezing the window mid callout. One `SemaphoreSlim` still serializes synthesis —
the neural engines are CPU-bound and overlapping calls only slow every caller down.

Synthesized PCM is cached in the same memory cache used for audio files, keyed by engine, voice and a hash of the text
(60 minute sliding expiry, sized in bytes so the existing 100 MB budget accounts for it). A line like `Got the level
90` plays dozens of times a raid, so only the first occurrence pays for inference. The text is hashed rather than used
verbatim so a long custom callout cannot produce an unbounded key.

A cached phrase needs neither the gate nor an engine: those bytes belong to an engine, a voice and a text, not to
whichever engine happens to be speaking, so the first lookup answers from the cache alone. That is what keeps twenty
familiar callouts from queueing behind one new sentence being synthesized. Everything that can miss runs under the gate,
where the engine cannot change underneath it.

Synthesis is not the only thing that touches an engine, and the gate alone was not enough: `SetVoice`, `RemoveVoice`,
`GetVoices` and `GetVoice` are called by the UI, synchronously, and one of them arriving while a switch releases an
engine's native state is a use-after-free in Piper's voice table rather than a caught exception. They now run under a
separate `_engineLock`, which the swap and the retirement also take. It is deliberately not held across synthesis — a
callout must never wait behind a dropdown, and a dropdown must never wait behind a model.

### Switching engines while running

`AudioManager.SwitchEngineAsync` builds the requested engine, lets it discover its voices, re-binds every player, swaps
it in, disposes the one it replaced, and then warms the voices that are still bound. The **TTS Engine** dialog calls it
when you press **Use**, and after a runtime pack finishes downloading, so switching takes effect on the next callout
instead of on the next start. The saved
setting still decides what a fresh launch uses, and a switch that cannot be honored (model missing, native library
refusing to load) leaves the current engine speaking rather than leaving the app without speech.

Two things make swapping safe rather than merely convenient:

- The whole switch runs under the synthesis `SemaphoreSlim`, so an engine is created and destroyed while nothing can be
  speaking through it. Synthesis re-reads `_tts` *after* acquiring that gate, which is why a callout that arrives mid
  switch cannot end up using a retired engine, nor cache PCM under the wrong engine's key. The swap itself and the
  disposal of the retired engine additionally run under `_engineLock`, so the quick UI-side engine calls described above
  cannot straddle them either. An engine that was built but never became active is disposed at the same point, which is
  why a switch that fails part way through does not leak a 300 MB inference session.
- Warm-up happens *after* the switch returns the gate, never inside it. Warm-up enters that gate only when it is free,
  so asking from a method that already holds it means every warm-up after a switch quietly gives up.
- A voice name from one engine means nothing to another, so `AudioManager` remembers what the host asked each player
  to speak with (`_requestedVoices`) and replays those names to the new engine. The engine binds the names it has and
  drops the rest, which sends that player back to the engine's default voice. Kokoro deliberately refuses to remember
  a name it does not have: a stale name would otherwise cling to a player for the rest of its life and be spoken
  quietly as a different voice.

### Warming a voice before anything has to wait on it

Speaking costs time in three separate places, and only some of it is the model: building a Piper voice is an ONNX
session over the voice model plus the espeak-ng dictionaries; *any* engine's first synthesis warms the runtime behind it
(arena sizing, kernel selection, the phonemizer's tables); and the audio device opens lazily on first playback. Cached
phrases skip all of it, so a trigger that says the same dozen sentences pays once per session — the delay is on the
first thing said with a voice, which is usually the trigger someone is waiting for.

`AudioManager.WarmUpVoice` covers the parts that can be paid in advance:

- **When:** a player is registered (`Add`), its voice changes (`SetVoice` — this is what the voice dropdown does, both
  when you pick one and when an engine switch repopulates the list and selects a default) and after an engine switch,
  for each distinct voice still bound to a player.
- **What:** `ITtsEngine.WarmUpVoiceAsync`. Piper builds the native voice; Kokoro and Windows have nothing per voice to
  build — every embedding arrives with the session made at engine creation — so they speak one short word into memory
  and throw it away, which is what warms inference.
- **Never audible and never in the way:** warm-up enters the synthesis gate only when it is free, retries briefly if
  something is talking, and gives up rather than standing in line. Choosing a voice also speaks an audible preview, and
  waiting behind a warm-up for that would be worse than staying cold. Whatever the retry window misses costs speed and
  nothing else.
- **One worker, one voice at a time:** requests queue rather than each starting a task of its own, deduped by engine and
  voice, because registering thirty characters at a zone-in is thirty voices asking for CPU at the same moment. A voice
  counts as prepared only when it actually got through; recording one that gave up would suppress the next attempt and
  leave a player cold for the rest of the evening.

Piper holds **one** prepared voice outside the players, under a reserved id in the native table. Previews used to build
a voice, speak and destroy it, so every different text paid for the model again; now `ResolveAdHocSpeaker` keeps it,
and takes the previous one out when the selection moves — which is the whole point, since otherwise an afternoon of
trying voices leaks a hundred megabyte model per change. Two further rules keep memory flat: a preview of a voice that
some player already speaks uses *that player's* session rather than building a second copy, and rebinding a player to a
different voice removes the old native voice first, because replacing a table entry is not documented as releasing it.

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
- **What this engine holds per player is two synthesizer objects, not a lock.** `_lock` covers the player table only;
  nothing in this class serializes an utterance, on either API. Concurrency belongs to `AudioManager`, which lets one
  synthesis run at a time across all engines — worth knowing before adding a second gate here, and before assuming a
  second callout can be synthesized concurrently through `System.Speech`, which it cannot.

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

Neither hook is a guarantee, and that distinction turned out to matter: they are asked **after** the default search has
failed to find a name. `onnxruntime.dll` is a case where something else may answer first — see
[Which onnxruntime.dll wins](#which-onnxruntimedll-wins) — which is why the runtime is claimed rather than resolved.

There is no fallback to a copy beside the executable, and there deliberately is not one. Reading `{app}\piper-tts` when it
was complete -- which earlier releases did, so that upgrading did not cost a re-download -- meant an engine could be
running off files the dialog cannot update, cannot remove, and cannot match against a pinned digest, and worse: those
files are still sitting in old build outputs, so development runs reported a working Piper nobody had downloaded. One
location per engine, `%LOCALAPPDATA%\EQLogParser\<engine>`, owned end to end by `TtsPackManager`. `[InstallDelete]` now
deletes what installs before packs left under `{app}`; the files are inert whether or not they are removed, so that entry
is about reclaiming space, not about behavior.

### Downloading a pack, and getting out of one

An install is four jobs wearing one progress bar, and the bar is divided the way the work is: nine tenths for bytes off
the network, then slices for hashing the archive, unpacking it, and checking every extracted file against
`manifest.json`. Nothing sits at 90 % for a minute while the app works, which is the part people could not otherwise
distinguish from a freeze — reading a few hundred megabytes back takes tens of seconds after a fast download.

Every one of those loops watches the Cancel button through one `CancellationToken`: the transfer, the archive digest,
each extracted entry, each verified file. `ZipArchiveEntry.ExtractToFile` is deliberately not used, because it neither
reports bytes nor aborts before an entry finishes and one entry here is the 156 MB model. Cancellation is worth this much
because of where the work happens: unpacking and verification run in `<engine>.staging`, an existing pack moves to
`<engine>.retired` only once the new copy is complete and verified, and a promotion that fails puts the old copy back.
Give up halfway and the only trace is a deleted temp archive; a pack that spoke this morning still speaks.

Two free-space checks guard the job rather than one. Room for the archive is checked before the download begins — that
check exists to save somebody's bandwidth, so it refuses only the case that cannot possibly work. Room for the extracted
tree is checked afterwards from the zip's own central directory, which states the uncompressed size of every entry, so it
is a measured number rather than a guessed compression ratio and it fires before files start landing. An unreadable drive
or an unmeasurable archive counts as room enough in both: this is not the place where a disk gets argued with, and a
disk that fills up anyway reports the real figure itself.

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

### Which onnxruntime.dll wins

Both speech engines load `onnxruntime.dll`, and Windows keeps **one native module per base name** for the life of a
process: whoever maps it first holds that name, and every later request — an import from `piperApi.dll`, a P/Invoke from
`Microsoft.ML.OnnxRuntime.dll`, even a load by absolute path to a different file — is answered out of the module list.
So one ONNX Runtime serves the whole session, and the question is which file that is.

The answer cannot be left to resolution order, because an old copy is easy to hit. `onnxruntime.dll` is not a Windows
system DLL, but other programs install it there anyway: a machine with a 2021-vintage `C:\Windows\System32\
onnxruntime.dll` (1.7.x) answers `DllImport("onnxruntime")` from System32 and refuses Kokoro's graph with something like
"Unsupported model IR version" about a download that is perfectly fine. Two facts make that fatal rather than merely
untidy:

- The default search — the host's deps.json native assets, then `LoadLibraryEx` over the usual directories including
  System32 — runs **before** .NET asks anyone's resolver. A search that *succeeds* with somebody else's file never asks
  `ResolvingUnmanagedDll`, and a resolver registered for `Microsoft.ML.OnnxRuntime` never runs either.
- `EQLogParser.deps.json` lists `runtimes/win-x64/native/onnxruntime.dll`, which the installer deliberately does not
  ship (that is ~12 MB of a ~20 MB installer, and the packs carry it). A declared native asset that is not on disk is
  simply not found, so a clean install falls through to the operating system while a development run — whose build
  output does have the file — resolves it correctly. That asymmetry is why this bug showed up on a VM and not on the
  machine that reproduced everything else.

What works instead is being **resident first**, which is `TtsPackManager.PreferMatchingOnnxRuntime()`: it loads EQLP's
own copy by absolute path, so from then on every request for the name is answered with ours. Candidate order is
`<kokoro>\native`, then `<piper-tts>`: Kokoro's copy leads because it is published together with the managed wrapper
installed beside the executable and repacked whenever that wrapper moves, while Piper's pack carries the same build today
and either serves. It runs once, from the thread pool, before the session's first engine is built (`AudioManager`) — not
from the constructor, because mapping a 12 MB runtime is not startup work for the UI thread — and again from both engines
for a pack downloaded mid-session. Whichever engine gets there first is fine; that is the point of keeping the choice in
one place rather than in an engine.

Three supports around the claim, each covering a case the others cannot:

- `EnsureOnnxRuntimeImportResolver` pins `Microsoft.ML.OnnxRuntime`'s own imports to the approved folder. It cannot beat
  System32 — nothing registered from managed code can, once the default search finds a file — so its job is the mirror
  failure: when EQLP's copy is **missing** it throws instead of returning `IntPtr.Zero`, because handing the name back
  is exactly how a foreign runtime gets in. Fail loudly on a decision we own.
- After claiming, `IsForeignOnnxRuntimeResident()` asks which file is actually mapped — an absolute-path load returns the
  already-resident module, so success alone does not prove it is ours — and Kokoro refuses to start when the answer is a
  path outside its packs and program folder. The alternative is 156 MB of "re-download your model" advice aimed at a
  file that is fine.
- `WarnOnRuntimeDrift` compares the mapped module's version with the wrapper installed beside the executable, since the
  pack publishes the two together and a mismatch means one of them moved alone. Warn, not refuse: major.minor is not the
  whole contract and an engine that speaks beats a tidy log.

### The MSVC runtime the same way

`onnxruntime.dll` imports `msvcp140.dll`, `msvcp140_1.dll`, `vcruntime140.dll` and `vcruntime140_1.dll`. Those four
install app-local beside `EQLogParser.exe` (`EQLogParser\redist`, Microsoft-signed and left that way) so a machine with no
Visual C++ redistributable can still speak — historically that was exactly where Kokoro failed on Wine while Piper worked,
and the difference was never the ONNX build.

They are claimed by name in the same call, before ONNX Runtime is mapped, and that claim is the load-bearing part. A
runtime loaded from `<kokoro>\native` is mapped with an altered search path — its own directory, then the system
Directories — so the program folder is **not** in that list, and four DLLs beside `EQLogParser.exe` would do nothing for
it unless those names were already resident. Claiming them first means ONNX's imports are answered from the module list,
which is also why the claim runs before the runtime and not after.

The consequence of installing them flat in `{app}` is worth stating plainly: the search order puts the program folder
ahead of System32, so these four are what this process maps even on a machine that has a newer redistributable. That is
Microsoft's *local deployment* of the CRT and it holds under one condition — **the checked-in copies stay current**
(`EQLogParser\redist\README.md` says how to refresh them). The CRT serves older binaries forward, so an up-to-date
app-local copy is a safe floor for everything in the process: Syncfusion's natives, NAudio's, ONNX Runtime's. Which file
answered each name is visible twice over — in `scripts\MeasureLoadedAssemblies.ps1` output, and in the Debug lines
`ClaimVisualCppRuntimes` writes.

Whether that makes Bottles' `vcredist2022` dependency redundant is a separate question with a fresh-prefix test in front
of it (`bottles/Games/eqlogparser.yml` keeps it until someone runs one).

## Floating Combat Text

`View → Floating Combat Text` shows the player's own combat numbers from live log records. The rendering choice is settled and recorded in
`docs/NagFctReference.md` (SkiaSharp beat a WPF vector path roughly 100 fps to 30 at ×10 raid scale), and the loser has since been deleted rather
than kept as a reference - see "Shared policy, one renderer" below. This section covers why the *plumbing* is shaped the way it is, because that is
the part a later change is most likely to undo by accident.

### One queue, drained on the render tick

`FctManager` does not raise an event per record. It pushes `FctHitCommand`s onto a `ConcurrentQueue` and the
overlay drains it from the canvas's `EventsFrame` callback — once per painted frame, at most 60 times a second.

The earlier shape posted one dispatcher item per record, which is the worst of both worlds: a raid AoE window
of ~200 hits/s became 200 cross-thread posts and 200 layout invalidations, and a UI stall replayed the whole
burst seconds after it stopped mattering. Draining on the frame clock batches for free (the frame is the batch)
and makes staleness cheap to reason about: anything older than `FctManager.MaxQueueAgeMs` is dropped and counted
rather than drawn. A combat number that arrives half a second late describes a swing the player has already
reacted to; showing it is worse than losing it, because it lies about what is on global right now.

Two counters make overload visible instead of mysterious: `FctManager.DroppedCount` (queue lost the UI could not
draw) and `IFctDiagnostics.DroppedCount` on the canvas (hits the lane caps refused). The header shows their sum
as "N dropped" only when it is non-zero, so a healthy overlay stays quiet.

### Parsing costs nothing when nobody is looking

`FctManager.Enabled` is a volatile flag gated at the top of both handlers, and it is set from the overlay's
`IsVisibleChanged`. Hiding the overlay therefore stops the feed at the parser: no player-name comparisons, no
pet-owner registry lookups, no command allocation. `DamageLineParser`/`HealingLineParser` additionally hoist
their static event delegate before raising, so with no subscriber even the `*ProcessedEvent` wrapper is not
allocated — FCT is the only reason those events exist, and it should not tax a user who never opens it.

A window owns exactly one manager: `FctManager.Create()` on construction, `Dispose()` (which unsubscribes from
both parsers) on close. Without that pairing a closed overlay keeps a live handler chain feeding a queue nobody
drains, which is invisible until the next session shows yesterday's fight.

### Shared policy, one renderer

The two canvases used to carry near-copies of the same layout and motion code, and they drifted within days. That is why `FctHitState` is plain data
and the decisions live in one place - and why there is now one renderer instead of two behind an interface. The second backend survived its own A/B
verdict and stayed "for reference", during which the frame pump existed twice; when the configure-mode demo was added, `FctSkiaCanvas` learned to
ask for frames on its behalf and `FctSimCanvas` did not, and the loop ran at about two frames a second - numbers frozen between cues, then jumping.
Nothing was expensive; half the code that asks for drawing had never heard of the thing animating. A seam with one implementation is not a seam, it
is a second copy of a rule:

- `FctIngest` — fold a repeat into the number already showing that exact hit, spawn, take a full lane's slot from a less
  significant number, or count a drop.
- `FctLayout` — which band of the canvas a lane lives in, spawn position, travel, the protected middle.
- `FctPlacement` — for travelling text, throwing that spawn several times and keeping the one whose flight least crosses the
  numbers already in flight (§"Travelling numbers pick a gap to go through").
- `FctMotion` — position, scale and opacity as pure functions of `(hit, age)`, plus the rule for the main line (one hit's
  face value, plus how many identical hits it stands for).
- `FctStyle` — lane → font size/color as `0xAARRGGBB` ints, so no renderer owns a palette copy.
- `FctLifeController` — adaptive lifetime.

What is left in `FctSkiaCanvas` is what genuinely belongs to a renderer: substrate resources (`SKFont`/`SKPaint`/halo sprites), the frame pump and
the blit. That split is also what makes the animation unit-testable (`EQLogParser.Wpf.Test/src/ui/control/Fct*Test.cs`) without a window, dispatcher
or GPU — worth keeping in mind before moving maths back into a canvas.

`FctMotion.RefreshText` having the text rule is deliberate and fixes a real bug: zero-damage records (Dodge,
Parry, Invulnerable) carry `Value == 0`, and a renderer that recomputed the numeric string each frame overwrote
the label with "0" — which reads as a legal absorb rather than an obvious mistake. A hit with `FixedText` set now
can only ever draw that text.

**A fold counts, it never adds.** Folding is for EQ's habit of repeating exact values — every DoT tick, every fixed-damage
proc — and what it produces is one number that says how many: `2,040 ×2`, `412 ×5`. The key is lane + side + proc-or-direct +
periodic-or-direct + ability name + **the value as it is drawn**, so a fold can only ever combine hits the player cannot tell
apart. Same-ability-but-different is not close enough: matching on lane alone once let an Immolation tick grow a number
labelled "Spinning Attack", which is a wrong total wearing a true label, and summing identical hits was the same mistake at a
smaller scale — 4,080 is an amount no hit landed for, it makes the player divide to find out what happened, and eight routine
ticks wearing the face value of a big one is precisely the confusion the fold exists to remove.

The lane cap decides who owns a slot rather than whether the information survives. The fold is tried before the cap and does
not care about occupancy, so a stream of identical hits never consumes slots at all — 20 seconds of the same 900 at eleven a
second measured two live numbers and zero drops. What cannot fold goes next to eviction: take the least significant number on
screen, but only if the newcomer clearly outranks it, where significance is what a number *stands for* — face value times its
count, with a proc discounted because one is subordinate by design. Only then is a drop counted. Healing direct casts are
excluded from folding at every occupancy — players read heals one cast at a time, and two identical heals are still two
casts of two different targets — which is why they need eviction: without it, "no folding" would mean "the thirteenth heal in
a raid-wide panic is silently invisible", and the point of showing healing at all is that a missed one matters.

### Raster at most 60 times a second, and never on a beat pattern

`CompositionTarget.Rendering` fires at display refresh, so on a 144 Hz monitor an animated canvas would raster a
full surface 144 times a second for text nobody can read faster. The cap lives in `FctFramePacer`, kept out of the canvas so the rule is testable
against synthetic tick streams; the tick still fires `EventsFrame` (the simulation paces its record schedule off it) but the surface memset + draw +
blit does not run. On an 800×560 overlay at 150% scaling, each skipped raster is ~2.3 MB of pixel work avoided.

**The pump asks about every list that moves.** It invalidates while there is animated content, and the configure-mode demo is animated content that
deliberately does not live in the canvas's own hit list - keeping it out is what protects the counters. A pump keyed on that one list repaints twice a
second during configuring. `FctDemo.Animated` is the demo's answer to the question, and `Animated_DemandsAFrameForEveryFlight` counts frames rather
than trusting intent: over 90 % of ticks across the busy part of a cycle must have something in flight.

**The cap counts whole ticks, and that detail is the difference between smooth and juddery.** The first version asked
"has `TargetFrameMs` (16.67 ms) elapsed since the last paint?" — a threshold tuned to 60 Hz sitting on top of a 60 Hz
stream. Real frames arrive at 16.4, 16.9, 16.6…; whenever one lands a hair early it is skipped, the next frame is two
refreshes later, and the cadence settles into an alternating one-frame/two-frames pattern. Positions are exact and the
average fps looks perfect while the text visibly stutters, most on the display where the cap is not skipping at all in
principle. `FctFramePacer` measures the refresh interval from the tick spacing itself (light EWMA, samples outside
0.5–200 ms discarded so a tab-out or a GC pause cannot retune it) and then paints on a whole number of ticks nearest
the target ratio: every tick at 60 Hz, every second at 120 Hz, every second at 144 Hz (72 fps, not the 48 that a time
threshold produced), every fourth at 240 Hz. Never faster than the display, never a beat pattern, and it re-derives
itself when the window moves to another monitor mid-fight.

Because smoothness lives in the tail and not the mean, `IFctDiagnostics` reports `MaxFrameMs` (worst frame in the stats
window) alongside the average, plus `DisplayHz` so painted fps can be read against the real refresh rate: 72 fps under a
144 Hz display is pacing, 60 fps under a 60 Hz display with a 40 ms max frame is overload. The simulation window prints
both; the gameplay overlay deliberately does not, because a stats line that moves every second is a distraction in a pull.
`FctFramePacerTest` feeds synthetic tick streams — including a jittered 60 Hz one, where it asserts the old time-threshold
rule really did skip frames — so the cadence is pinned without a monitor.

The destination bitmap and its copy buffer are allocated once per size and reused. Allocating a `WriteableBitmap`
plus a fresh `byte[w*h*4]` every frame put ~5 MB/frame on the large-object heap and forced a new GPU texture
upload each time instead of updating the existing one; both are now per-resize, not per-frame. The surface is
explicitly `Bgra8888`/`Premul`, byte-identical to WPF's `Bgra32`, so `ReadPixels` is a memcpy with no conversion —
and it fails closed (skip the frame) rather than blitting wrong bytes if Skia ever hands back another format.

The remaining copy (surface → snapshot → pinned array → back buffer) is the known next step: `D3DImage` with a
shared Skia surface would remove it. It is not taken here because it needs a real GPU to validate against, and
three memcpys are not what limits this renderer today.

### One region scheme, and why direction went vertical

The overlay cannot know where the player's target ring, cast bar or spell gems sit in the game window, so it can only
promise that a band across its middle stays clear. The first scheme spent that promise horizontally: incoming lanes
on the left half, outgoing on the right, `FctLayout.CenterClearance` reserving the column between them and
`FctMotion.ArcedX` clamping with half the *scaled* text width so even a crit at full blowout cannot slide over the
middle. It worked for numbers and failed for everything else — left/right is a convention that has to be learned and
remembered, and it has no analogue anywhere in EQ's own UI.

The default makes direction vertical, which is how nearly every game with floating text does
it: text about my target rises out of the top of the overlay, text about my body sinks out of the bottom, and both
travel *away* from the protected strip between them (`GapTopFrac`/`GapBottomFrac`). Three things came out of that for
free:

- The empty middle is empty **by construction** rather than by clamping traffic out of it. Diverging travel cannot
  cross the gap it started outside, where the old scheme needed a per-frame clamp to keep two opposing fountains apart.
- Direction has two independent carriers (band and direction of motion) instead of one, so it survives a glance,
  peripheral vision and a colorblind player.
- The x axis stopped meaning *who* and went back to meaning *what*: damage sits toward the middle of its band,
  healing out wide, crits and labels centered. Each band carries two categories, so lane slots are still needed —
  they were never the problem; overloading one axis with two meanings was.

There is one region scheme, deliberately. The original left/right split (`FctLayoutMode.Halves`, a checkbox on the header,
`FctOverlayLayout` in settings.ini) stayed as a fallback long after bands became the default, and every style added
afterwards had to be told about it. The cell grid measures its columns against the canvas width, so in halves the outer
columns fell under the protected centre column and the clamp collapsed several "distinct" cells onto one place — measured:
4 overlapping numbers in 8. Taking the grid away there was worse: a pulse hit has no travel to separate it from its
neighbours, so plain placement gave 11-22 overlapping pairs on the same burst. Halves also offered no vertical band for the
choreographed styles to fall inside, which is why their fall had a mode-specific special case. Two geometry systems that
each satisfy half the styles cost more than one that satisfies all of them, and the fallback was not earning its keep, so it
was removed rather than fixed; a stale `FctOverlayLayout` entry in an existing settings.ini is simply unread now. The
configure row labels the direction of travel and nothing else — `↑ yours   ↓ on you` — because that is the one thing the geometry
cannot say for itself. The sentence-long hint it replaced was read once and then became clutter.

A fountain's choreography — travel, then accelerate under gravity while shrinking — points *down* on its way out, and an
incoming band is the last thing before the bottom of the screen. A literal fall there parks the number against its own
band edge for half its life, which reads as stuck rather than as physics, so `FctIngest.AssignLifetime` **mirrors** it:
`FallDist` is signed screen-relative (positive falls toward the bottom, negative back up toward the gap) and an incoming
hit gets half its sink distance back on the way out. Both sides then have the same overshoot-and-settle
shape in opposite signs, which is what "one animation, two directions" was supposed to mean, and the return cannot reach
the protected strip because it is a fraction of travel already spent below it.

The layout keeps its promises at any window size: a band that a short window would invert falls back to "inside the edges"
instead of throwing — an inverted clamp band used to crash every frame on a small overlay. `FctLayoutTest` pins it at
100–240 px, across the whole motion and at crit scale.

### Four motion styles, and the one thing none of them may change

The fountain began as a checkbox, which was honest while there were two choices and became a lie as soon as players
wanted text that stays put or fans out. `FctMotionStyle` is that axis now: **hold** (travel away from the strip, stop,
be read, fade — the default), **fountain** (overshoot, then fall; mirrored upward on the lower band), **pulse** (no
travel at all — it swells where it appeared) and **spray** (a random cone out of the lane slot, then a short fall).
Where a hit goes, how it moves and how numbers stack are three orthogonal decisions, and cramming two of them into one
boolean was how "fountain" came to mean several things at once.

Three rules keep four styles from becoming four behaviours:

- **Motion is presentation, never information.** Band and direction of travel still say who acted whichever style is
  running, which is what makes "try each during the next pull" a safe thing to offer. The combo therefore applies to
  hits spawned *after* the change rather than restyling what is already on screen.
- **A hit keeps the style it was born with** (`FctHitState.Style`, snapshotted by `FctIngest.Accept`). If the renderer
  read one live setting, switching fountain → hold mid-flight would hand every parabola in progress a different
  velocity for its remaining frames. Snapshotting an enum per hit is what makes switching free.
- **The protected strip stays clear by construction, for all four.** Pulse's cells are laid out inside a margin off the
  strip's edge and every one is measured against it (`FctCellGrid`); spray falls
  back by a fixed share of the distance it already travelled (`SprayFallFrac`), mirrored upward on the lower band; hold
  never passes its clamp. Outgoing fountain is the one that still falls a share of *window* height — legitimate, since
  the strip is behind it and the bottom edge is clamped. A test sweeps each style across its whole curve asserting none
  enters the strip or leaves the window, so a future fifth style has to pass the same bar.

Spray needed one constant that measurement forced: `SprayReachFactor`, roughly twice the depth of a band, with height
capped at what the band offers. Given only the band's own travel budget (`usable * sin(theta)`), spray's horizontal
coverage came out **identical to hold's** — hold already adds 12% of width in arc plus a lane-slot jitter, so a shallow
cone sat entirely inside noise that was already there. A reach longer than the band lets the wide angles of the cone
actually move sideways while the steep ones simply top out against the clamp.

Widening the cone then exposed the real reason spray and fountain looked alike: **both axes ran on one ease curve**. When x
and y advance by the same fraction of their totals, every trajectory is a straight line from origin to apex — the fan
existed only in where numbers ended up, never in how they got there, so mid-flight a wide spray was a slanted fountain.
`FctMotion.LateralProgress` puts spray's sideways travel on an ease-out while its vertical keeps the smootherstep climb:
shrapnel keeps moving sideways while gravity handles the vertical, so the path bends over into an arc by itself. It eases
to zero slope at the apex instead of running linearly because a number that stops dead sideways at the moment it begins to
fall has a kink you can see. Hold and fountain deliberately keep one curve between them — a straight climb is what a thing
with no gravity does. A test pins the difference rather than trusting the eye: hold's drawn point never leaves the line
between its origin and its apex, spray's must leave it by more than 20 px and be nearly spread out by the time it peaks.

Two smaller changes went the same way. The cone opened from ±38° to ±49°, with `SprayMaxLateralFrac` moving from 0.30 to
0.34 of width — that cap has to travel with the angle or every wide draw stops at the same wall and the fan comes out flat
topped — and spray's choreography got its own tempo, `SprayMotionWindowMs` (1700 ms against fountain's 2000), because
shared flight time was the other half of the resemblance: two 2 second arcs read as one effect whatever path they draw, and
shrapnel is supposed to look quick. Measured at 980×640 with these numbers, spray covers 519 px of x mid-flight where hold
covers 246 and fountain 242.

Pulse then failed for a different reason, which in-game use showed and no amount of reasoning about single numbers would
have: **static text overlaps**. Free-floating placement is fine for numbers that move, because each is only in a spot for a
moment; a number that stays is read for its whole life, and two of them sharing a place is mush. That is what pulse was,
because it inherited the lane-slot jitter designed for travelling text.

Every combat log UI lands on the same fix — slot allocation — so `FctCellGrid` does: one cell per hit, held for the hit's
life, oldest taken when the block fills. WoW's anti-stagger number mode spreads simultaneous values across fixed positions
with a row limit (and the scrolling-text addons went further into explicit grids), FFXIV stacks a capped number of rows in
one place, GW2 groups into fixed areas. Deterministic positions are the point: the player stops hunting for numbers.

- **Cells belong to a band, not a lane.** The scarce thing is space inside a band; separate pools per lane would put the
  damage block on top of the healing block, which is the bug. Colour says what a number is, position only has to say "not
  on top of another number".
- **The outer row belongs to procs**, smaller cells kept out of the block being read so item spam can never push into it.
  Outward means away from the protected strip on both sides, which also keeps the strip's neighbour free of the most
  frequent text on screen.
- **Rows fill nearest the strip first, centre-out within a row**, and every number starts from one point in the band and
  slides into its cell (`FctMotion.PulseSlideMs`, 220 ms; zero puts numbers straight into their cells for anyone who finds
  the slide busy). A burst therefore reads as one event expanding rather than several unrelated appearances.
- **A full block takes its oldest cell, but a crit is not up for grabs.** If everything left is a crit and the newcomer is
  smaller, the newcomer is dropped and counted. Losing a routine number beats erasing the biggest one on screen.
- **Geometry depends only on band and canvas size, never on the hit being placed.** This one is load-bearing, and the first
  version got it wrong: sizing each row from that hit's own text reserve made two numbers in the same row disagree about
  where the row was, and they landed on each other — the exact bug the grid exists to kill. Row slots are shared; only the
  centring inside a slot is per-hit. The cost is that a crit's pop can crowd a neighbour by a few pixels, which every grid
  layout pays: rows sized for the loudest possible number would halve how many numbers fit.
- **An overlay too small for a grid keeps its numbers.** `HasRoom` is checked first; when no row plus margins fits, the hit
  keeps the plain static placement `FctLayout` gave it instead of being dropped. Losing layout is fine, losing damage is not.

Pulse is also the nearest thing here to the reduced-motion option the design document asks for: nothing translates, so
the information arrives without movement. It is not labelled that way yet — an explicit reduced-motion setting should
pick pulse and shorten lifetimes rather than invent a fifth style.

What was deliberately **not** built is anchored-follow (a number tracking its own mob across the screen). EQ's log
never contains actor positions, only names, so there is nothing to anchor to; that absence is the whole reason the
design leans on bands and an empty strip instead of "numbers above your target".

### Travelling numbers pick a gap to go through

Choosing a lane slot and throwing a small random jitter at it — about an eighth of the band's depth, which is all the layout used
to do — turned out not to be enough. Measured on a 980×640 overlay with a number arriving every 700 ms, **58% of fountain pairs**
shared a spot somewhere in their overlapping lives; six held numbers in one band, **71% of pairs**. Two numbers climbing nearly
the same path are unreadable for the whole flight, and they did it while most of the band around them sat empty. That is a
legibility defect wearing the clothes of a polish item.

So travelling text asks where it would actually go from a grid of legal launch points across its band, and keeps the one with the
least crowding (`FctPlacement`). Three things keep this from becoming a second layout:

- **Every candidate comes out of `FctLayout.Spawn`**, asked for an origin instead of a dice roll. The band, its reserve against
  the protected strip and the window edges apply to a requested launch point exactly as they do to a random one, so no candidate
  can sit somewhere the layout would forbid.
- **Cost is measured along the flight, not at the origin**, because that is what the player watches: fountain text falls back
  through the band it climbed, so two spawns that start apart can still collide on the way down. Samples are wall-clock aligned,
  so an older number is compared at where it really is rather than at the same phase of its animation.
- **The first candidate is the layout's own throw and pays nothing**, and the rest pay a small penalty for having searched plus a
  graded one for drifting from where the lane puts things. An uncrowded overlay therefore draws exactly what the layout alone
  would have drawn; the search is only paid for when it buys space.

Probing is systematic rather than random, which took learning twice: twelve random throws at a crowded band found far less of the
room that was there than walking the band does. The grid is three columns by six rows — six rows because that is about how many
rows of text a band's depth allows, three columns because that is about how many numbers can sit side by side in one column — and
every launch point is nudged off its lattice position afterwards so nothing marches in lockstep.

Nothing is refused: if every launch point crowds, the least crowded is used anyway. Capacity belongs to the lane cap and the life
shortener, and losing numbers belongs to nobody.

Pulse text does not come through here — it has cells, which is the right answer for text that stays still. The evidence for that
split is old: the deleted left/right preset's "floating" checkbox was free-float placement with nothing moving, measured at **85%
of pairs overlapping**.

**Depth is free and sideways is not, so the two axes are searched differently, and that is a learned lesson too.** The first
version widened both by the same factor — five times the layout's jitter — which is how a hit came to start at the far left border
of the overlay and sway inland on the way up. The damage column sits at 0.42 of the width, so a ±0.45 throw went off the left edge,
the clamp pinned it to the wall, and the search scored that wall as an empty gap: measured afterwards, numbers averaged 22% of the
overlay away from their own column and 7% launched flush against an edge. Healing had the same trap waiting on the right. So depth
is searched across the whole band, while sideways reach is measured **in widths of the number's own text** — the question "could a
neighbour sit beside this one?" is about how wide the text is, not how wide the window is — and capped at 0.22 of the overlay
width. Numbers now average 10–15% from their column, none start at an edge, and overlap is still cut by a factor of four to six.

Final figures for the same measurement, on 980×640: three fountains 58% → **9.2%** of pairs colliding, six held numbers 71% →
**16.5%**, eight spray numbers 40% → **~10%**, at about 1.5 µs per placement with a full lane live. Held numbers are the worst case
because nothing about them moves to help: six in one band genuinely do not fit without touching, and that remainder is what the cap
and the life shortener are for, not what placement can solve.

### Two states: numbers only, or configuring

Locked is not a setting — it is what the overlay *is*. It opens locked, it is played with locked, and it has no header: while locked
the controls row is hidden, the panel background and border are transparent, the resize bands are gone, and clicks pass through to
EverQuest. What is on the screen is damage numbers and nothing else.

That last part was a defect hiding inside a feature. The header (`FCT`, motion combo, a "lock (click-through)" checkbox, a
sentence-long hint, stats) and a dark rounded panel used to paint in both states, over the middle of the game view — furniture nobody
asked to look at while fighting, on a window whose whole job is to be out of the way. Making it numbers-only was not a cosmetics pass.
The row that remains got read for clutter at the same time: the hint sentence and the `motion` label in front of a combo that can only
be a motion combo are gone, and so is the checkbox — see below.

**Configuration is entered deliberately from the app menu** (View → Floating Combat Text → Setup). Not from a button on the overlay: this
window lives where the player is looking, and the Damage Meter gets away with an on-window toolbar because you park that in a corner.
The hidden header keeps its height rather than collapsing, so entering configure mode cannot move a single number — the moment you are
positioning them is precisely when the layout must not shift.

**Save is what writes**, and **Cancel is the way out without writing**. Both sit at the right end of the configure row, stacked because there is no
width to spare. Leaving without saving is a normal way to finish a look around — you came to see what the dials do, not to change them — and for a while
the only way to do that was a sentence printed on the panel explaining the Esc key, which described the ordinary exit as the absence of an action. A
button that says "Cancel" beside one that says "Save" removes the sentence and the reading. Esc still does the same thing for a hand already on it; it
just no longer has to be documented mid-screen. Setup clicked again from the menu also backs out, and every one of those paths puts back whatever was
saved before, because abandoning a configuration session is not the same gesture as approving one. The motion combo previews live (the next numbers use
the new style) and writes nothing, so trying a style costs a click and un-trying it costs nothing.

That replaces a "lock (click-through)" checkbox, which was never about locking. It was the only way out of configure mode wearing a
side effect as its label, so the player who clicked it to finish got their mouse taken away and no way to notice they had also not
saved anything. A button that says what happens is worth more than a toggle that guesses. Placement is the one thing saved without
being asked: geometry is written when a drag or resize is released, because where you left the window is never ambiguous.

Lock state is deliberately **not persisted**. A saved "unlocked" is a state that outlives the session it was meant for: next launch,
the overlay is an invisible rectangle eating clicks over the game, and the player's only diagnosis is that the UI feels haunted. Same
reasoning retired `FctOverlayLocked` outright rather than defaulting it to true — inert in existing settings.ini files, like every
other retired key here.

### Two dials and a short loop: what configuring is for

A feature whose range a player cannot adjust has whatever opinion the implementer happened to hold, shipped as theirs. So the configure row carries two
dials — **size** and **speed** — each stepped at 5 %, so a setting is a place you can park rather than a value you have to hit by eye. Each dial is
three lines of its own: what it is, the track with a bold `-` and `+` either end, and where it landed *underneath*. That shape is about room. The row
started as one line with each number read out beside its track, which worked until it didn't: this panel is going to acquire more settings, and a layout
that grows sideways runs out of window at some width somebody chose — so the numbers moved below, where they cost nothing, and another dial can be added
beside them. The marks carry the size rather than the labels because they are what you consult while a thumb is moving. Everything wraps: a narrow
overlay drops the speed dial under the size dial, never a button off the edge.

**The second dial is speed, not time — it used to be called time, and it ran backwards.** A control labelled "time" whose right-hand end makes numbers
appear and clear sooner is a control whose label fights the gesture, and that was reported by a player rather than noticed in review. So the dial says
speed (bigger and faster at the right), is stored as `FctOverlaySpeed`, and `FctScale.Time` remains exactly what `FctIngest` has always multiplied: how
long a number lives. The conversion is a division in one place (`TimeFromSpeed`) rather than a subtraction, because "50 % faster" is two thirds of the
time on screen, not 50 % less of it. `FctScale.Time` must therefore **not** be clamped with the dial's own bounds, which would silently cut an end off.

**The two dials are not symmetrical, and neither asymmetry is an accident.** Size runs ±50 % around 1.0, centred, because the type scale genuinely is
centred on the size everything was measured at — lane columns as fractions of width, the vertical reserve a line of text needs `FctLayout.TextReserve`,
the adaptive lifetime under load — and half again in either direction is where that stops describing the feature: past it, damage and healing columns
begin to occupy each other at ordinary window sizes. Values saved while the ceiling was ±30 % stay legal, which is why raising it needed no migration.

Speed neither centres on 1.0 nor reaches equally far, because playing with it said so. Half speed — twice as long on screen — is unusable in a fight, and
half again as fast was not enough; the tempo worth sitting at turned out to be about **15 % quicker than the one that shipped**. So this dial is measured
in percent *of the shipped tempo*, runs **−30 % to +90 %**, and parks at `SpeedDefault = 1.15`: numbers live 0.87× what they used to by default, 1.24× at
the slow end (a little past the old neutral pace rather than double it) and 0.46× at the fast end. A tempo is a preference about feel, so it moved as one
number on one dial — re-scaling choreography, layout budgets and the adaptive controller to make 1.15 the new zero would have been the same opinion with
forty constants in it, plus a re-measurement of everything measured at 1.0. The floors in `FctIngest` (900 ms of life, 200 ms of fade) are what keep the
fast end from becoming a flicker, and they now apply at every setting including the default, since there is no longer an unset case where this dial
should keep its hands off.

Both are applied **where a number is born**, never while it is on screen: size in `FctStyle.ApplyTo`, speed in `FctIngest.AssignLifetime`. That is not
tidiness — it is the reason dragging a dial cannot tug at text already in flight. Motion is a pure function of (hit, age), so a number whose size or
timing changed mid-flight would have to be re-measured, re-clamped and re-placed, which is the bug class layout exists to prevent. Two consequences
worth stating: changing size leaves everything currently flying at the old size until it fades, and speed moves `LifetimeMs`, `MotionMs` **and**
`FadeMs` together, because scaling only the lifetime leaves a number hanging in mid-air at the old animation speed, which reads as a stutter rather
than as a slower overlay. Floors hold underneath — never below 900 ms of life or 200 ms of fade — so the fast end compounded on top of what
`FctLifeController` does under raid load makes numbers quick rather than flickering.

**A short scripted loop plays while configure mode is up**, because hovering a slider in a game overlay would otherwise mean waiting for combat
to produce one of each type on demand:

```csharp
foreach (var hit in _demo.Hits)   // FctDemo: about twenty events over twelve seconds
```

Melee swings, a crit or two, a spell damage-over-time ticking the same amount three times so it folds into `412 ×3`, a proc, healing received
(including a crit heal), a hit landing on you, and the zero-damage words — the things a player has to be able to tell apart, arriving in roughly
the order a fight sends them. Still frames were built first and were wrong: five exhibits pinned to a board cannot show tempo at all, and the row
looked like a diagram of the overlay instead of the overlay.

It runs through a **private `FctIngest` into a private list**, not the overlay's. Same choreography — style, band, travel, folding, placement,
adaptive lifetime — and the same text building, which is why dragging either dial shows up in the numbers as they land. Separate rather than
shared because the real ingest owns the counters: folding a demo number into a live one, or counting a demo drop as lost data, would put the demo
inside the data. `ActiveCount` and every loss counter stay honest, and a real number that arrives while configuring behaves exactly as it always
has — drawn after the demo, so on top of it.

The script's vocabulary is EverQuest's own, and that is a checked claim rather than an impression: melee verbs are the parser's
(`StatsUtil.RegularMeleeTypes`, in the base form `FctManager.DisplaySource` prints — "Bite", not "Bites"), spell names appear in the shipped
`data/spells.txt`, proc names in `data/procs.txt`, and the words are `Labels` constants so they cannot drift from what the parser assigns.
Invented vocabulary — an early draft said "Backhand" — teaches a player to expect text that never appears. `FctDemoTest` asserts every name in the
script against those files.

Scheduling carries one promise: the last cue lands 7.15 s into a 12 s cycle, so every number has launched, travelled, held and faded before the
loop restarts. A cycle that cleared live text at the seam would look like the overlay truncates fades, which reads as a bug in the feature rather than
as a loop. The four quiet seconds at the end are also what makes it legible as a loop instead of as noise.

Changing any control **restarts the loop from its first cue** (`FctSkiaCanvas.RestartDemo`), on release rather than while a thumb is still being
dragged — restarting mid-drag would blank the very thing being watched, and waiting up to twelve seconds for a cycle to come round to the part where
the change is visible is not an effect anybody can see. Only demo numbers are cleared; a real number that lands while configuring behaves as it always
has, because configure mode never touches play.

And like the controls, the demo starts with configure mode and stops with it — unlike static exhibits, an un-stopped loop keeps asking for frames,
which a window you are fighting in should not spend.

The panel is neutral and translucent: `#3A000000` — about 23 % black — with a hairline brighter than its own fill (`#99FFFFFF`) so the frame stays
findable against snow or other bright ground, plus grey-white labels. It was blue steel (`#5C7A99` on `#0D131A`) over an 80 % black wall, then 45 %:
the app's palette has no blue in it, so framed in steel blue the overlay read as somebody else's addon pasted on top, and either opacity hid exactly
what the numbers are being positioned against. Configure mode is spent looking *through* this panel at the game, which is the reference for where a
column of numbers should sit; the fill exists only to lift the labels off a bright background, so it is now barely there. While locked nothing at all is
drawn behind the numbers. The numbers keep their colours; those are the vocabulary (§Colour answers "what", never "who") and only the furniture around
them changed.

Type on the configure row comes from the app's own font setting (`ThemeConfig.CurrentFontSize`, arriving in markup through `EQContentSize`) rather
than from a size this window chose for itself, which was 12 px throughout — small print sitting over a game whose interface the player had already set
to 13 pt or larger. Labels sit two steps above the base and the `-`/`+` marks seven, re-applied on each entry into configure mode so changing the font
size in Settings needs no restart. The numbers themselves are deliberately *not* sized from the theme: they have their own scale (`FctStyle`), because
combat text has to stay readable at a glance across a whole screen of HUD, and 13 pt of damage is illegible while 13 pt of menu text is correct.

### Under View, beside the Damage Meter, behaving like it

**The first time the feature is switched on, it opens on its controls.** Every default here is defensible and every one of them is this build's opinion,
and an overlay that appears with numbers already moving keeps a player from learning that size, speed and motion are theirs to set. The demo loop shows
all three within a couple of seconds, so enabling enters configure mode until somebody has pressed Save once — recorded as `FctOverlayConfigured`,
written by Save alone. Cancel deliberately does *not* set it: backing out means "not today", and the offer comes back next time rather than a choice
being forced on somebody who looked and decided.

`View → Floating Combat Text` offers **Enable Floating Combat Text** (reading **Disable FCT** once running — the menu is already spelled out above, and an
item that repeats it is a sentence), **Reset Position**, **Setup** — the same three shapes as `View → Damage Meter` two rows
above it, using this app's convention for menu state (a check icon plus an Enable/Disable header, not a checkable item) because an
overlay filed in a different menu with different mechanics is something players have to learn twice. Nothing FCT-related sits under
Tools any more; the render simulation that used to live there is a development tool and starts from the command line (`/fctsim`), so a released
build can still be measured where it misbehaves without shipping menu clutter. It took a backend argument while there were two renderers to compare.

**Reset Position** closes the overlay, forgets `FctOverlayLeft/Top/Width/Height`, and rebuilds it only if it was on screen. Rebuilding
rather than moving is deliberate: the shipped size and the centring live in one place (`RestoreSettings`), and a reset that merely
carried the current window elsewhere would leave behind the stored size that caused the problem. The order matters too — a closing
overlay writes where it was, so the forgetting has to happen after the close.

Which connects to the reason a stored position is now validated at all. A chromeless, click-through window that lands off-screen —
because the monitor it lived on was unplugged — is not an annoyance but a feature that silently stopped existing, with no title bar to
drag it back by. So restored geometry must keep a quarter of its area on the desktop, the same rule `App` applies to the main window,
measured in DIPs against `SystemParameters.VirtualScreen*` rather than `Screen.WorkingArea`, which is device pixels and drifts at any
scaling that is not 100%. A negative `Left` — a display sitting left of primary, which the old check rejected as "unset" — is
legitimate and passes. Resizing is bounded by the desktop for the same reason instead of by the primary monitor's work area.

### Drag an edge to resize, and it lands on a size that works

A transparent, chromeless window gets no resize frame from Windows, and the middle of the overlay belongs to the numbers — so the
grip is a 12 px band along each edge: corners size both axes, edges size one. The bands are collapsed while locked, because a
window whose clicks pass through to EverQuest must not offer anything to click, and the header's top inset was raised past them so
no control sits where a drag for size starts.

Moving it is not a treasure hunt either: while configuring, any press that no control and no resize band took moves the window. The
header used to be the only draggable band, which meant hunting for twelve pixels of chrome while a number floated past where you were
aiming. Controls keep their own clicks because a combo or button handles the press before it bubbles, and locked removes the whole
question by making the window click-through.

What it settles on is a **magnet, not a menu** (`FctResize`): drag freely and each axis is pulled onto an offered value when it
comes near — 980×640, 800×560, 760×520, 720×560 — and stays exactly where the hand stopped in between. Per-axis rather than whole
presets, so dragging one edge gets the same help as a corner, and a corner near 800×560 lands on it. The floor is 420×300, the
smallest size probed: every style still places every number inside the window and clear of the protected strip there, and below it
a band is shallower than a line of text.

The default moved from 980×640 to **800×560**, which is what most people need over a HUD. The honest cost, measured at each offered
size (worst-instant pair overlap): fountain 3-in-flight 7.5% → 6.7%, spray 8-in-flight 8.3% → 7.7% — free of charge — while **six
numbers held at once goes 15.5% → 20.7%**. Held text is the one thing that cannot trade space for motion, so it pays for the narrower
window; everything else does not.

**Numbers already flying move with the window.** Motion is a pure function of `(hit, age)` with no canvas argument: bands, side
bounds, travel and pulse cells are pixels baked at spawn, so a resize that touches nothing leaves them drawing where the old window
used to be. Probed at 980×640 → 620×400: **10 of 15** held numbers drawn outside the overlay, and pulse was worst (11 off-screen, 4
in the protected strip) because its grid is computed once. It healed itself as hits expired, which is a way of being wrong politely,
not a fix. So `FctResize.Rescale` maps them: free text by the ratio of each axis (a thrown number keeps its shape relative to the
window it is thrown in), pulse numbers by re-seating them — the cell *index* survives, where that index sits is the grid's business —
and bands plus side bounds are re-derived from the new size by the same function `Spawn` uses, never scaled approximately. Nowhere
above is off-screen or in the strip any more, at 620×400, 1400×900 and at the floor.

Fonts are deliberately not scaled: how big a number is drawn is a style decision, not a layout one. A smaller window therefore means
less room per number, which is absorbed by the lane cap and the life shortener — the mechanism that already decides how many numbers
fit — rather than by type nobody can read at arm's length.

### Colour answers "what", never "who"

`FctStyle` used to paint the successful-defence lane blue, which put direction on colour — and blue in particular
reads as mana, arcane damage or a friendly nameplate to anyone arriving from another MMO, so it was a wrong sign on
top of a redundant one. Nothing is blue now: yellow dealt / deep-orange crit / red taken / green heals, with crit's
hue pushed *deeper* than dealt damage rather than brighter (at 1.3× scale plus the pop, a light orange and the yellow
it must stand apart from converge). The zero-damage labels are hueless — pale slate for a defence that worked, one
step dimmer for my own whiff — with amber reserved for `Invulnerable`/`Absorb`, the two labels that mean "every cast
from here is wasted" and so deserve to beat the routine one next to them. The source line went neutral grey for the
same reason: it must not compete with a value colour for meaning.

Lane capacity is two-layered on purpose: `FctLifeController.Capacity` (5–7) is the *target* the adaptive lifetime
aims at, and `FctIngest`'s hard cap (12 per lane) is the backstop for burst windows. The backstop folds a repeat into a live
number before it drops anything, so overload compresses the display instead of eating damage. The age rules are about
readability now rather than correctness: a fold needs a target that is under 2.5 s old *and* has 40% of its life left, so a
count never lands on something already fading out — which matters more than it sounds, because a number that gains a "×3" as
its opacity drops reads as a glitch. Crits refuse to fold outright: each one is the event, and a crit number standing for
several hits is misleading, so crit overload is the case where `DroppedCount` actually moves. `FctIngestTest` pins both
halves — 40 identical hits into one lane leave every one of them counted on screen with zero drops, and none of the numbers
any bigger than a single hit — and 20 crits into a capped crit lane report 8 counted drops.

Folding used to follow NAG's median idea instead: `FctMedianTracker` kept a rolling window per lane and a direct hit under half
the lane's median was treated as routine noise and poured into whatever live number shared its lane. That is gone, and it went
with summation rather than alongside it — the median answered "when is it acceptable to add this amount to somebody else's
number", and once a fold can only collapse identical values there is nothing left to permit. It also refused the case players
actually ask about: two 2,040s sit *at* the median of a lane that deals 2,040s, so the repeat was shown as a second number
while five identical DoT ticks merged happily. (`periodic` DoT/HoT ticks still always fold; healing never does.) Reintroducing
a threshold means bringing the tracker back — see the counted-ignore-tier idea in `local/fct-implementation.md` §12 F4, which
is where a median-relative cut belongs, because it discards on purpose and has to say so.

### Text sizes, and the reserve they imply

The first pass used web-scale type, and every tier went up by at least two points once it was looked at how it is
actually read: across a game window, in peripheral vision, while moving (`FctStyle`: dealt 34 / taken 32 / healing 28 /
crit 40 / labels 24 / smallest numeric tier 23). The *ratios* were sound from the start, so treat an absolute size as a
presentation decision and a ratio as a design one.

Bigger type also made an existing bug impossible to miss: `FctHitState.Y0` is the **top** of the value text, and layouts
that reserved one em ran descenders and the entire source line off the bottom of the overlay — permanently, because
incoming hits travel downwards and spend their last seconds against the bottom edge. Every vertical bound now reserves
`FctLayout.TextReserve(hit)`: value height (`TextHeightFactor`), plus the source line's height when the hit carries one,
times `CritPeakScale` for a crit (the draw scales about a pivot partway down the value, so scaling the whole block
over-reserves slightly and never clips). It is a factor rather than measured glyph metrics for the same reason
`EstimateTextWidth` exists: bands are computed at spawn, before any backend has built text. `FctLayoutTest`
pins it in both region schemes, with a source line present and at crit scale.

### Procs are subordinate: a little smaller, and quicker

A proc is not the number anybody aimed at. Item and spell procs fire on their own schedule, several times a pull, and they
arrive on top of the swing or cast whose timing the player is reading - so at full lane size they compete with the hit
they accompany without being the reason for it. `FctStyle.ProcSizeFrac` (0.78) rides a proc's value below its lane, which
turns 34 into about 27: between dealt damage and a DoT tick, subordinate but not a footnote, with the source line shrinking
alongside it because a full-size ability name under a smaller number would undo the effect on its own.

Size alone does not separate two streams that land in the same place though, so procs also start in a different row of
their band: `FctLayout.ProcInsetFrac` (0.15) moves them further *out* from the protected strip — my procs higher up, procs
landing on me lower down — so the two occupy different rows of the same band and the eye can ignore one while reading the
other. Being a share of band depth it is clamped by the band ends, so a small overlay loses separation before it loses text;
a test stacks every combination on a 420×300 canvas to keep that true. Pulse mode does not need the inset: allocation gives
procs their own block of cells outright (`FctCellGrid`), which is a stronger version of the same rule.
`FctMotion.ProcTimeFrac` (0.7) then shortens the **whole** tempo rather than only its tail - travel, hold and fade
together - so a proc is gone shortly after the hit that provoked it instead of hanging there while that hit fades away.
A choreographed style scales as a unit for the same reason: shortening its life without its motion would run the parabola
in slow motion.

Neither reduction touches a crit proc, which is the one place where two independent "make it smaller" rules would have
stacked into a bug. A proc crit is the biggest single number in the log, and reducing both its size and its life would
have made the loudest event the quietest thing on screen - the opposite of what a pop is for. `FctStyle.ApplyTo` and
`FctIngest.ApplyProcTempo` each test `Blowout` for this, and a test asserts that a proc crit matches a plain crit in size,
life and motion, so the exemption cannot be lost by refactoring one of the two.

What makes this legitimate rather than a guess is that a proc is a **fact** about the record rather than an interpretation
of it: `DamageLineParser` assigns `Labels.Proc` by looking the spell up in `data/procs.txt` (EQ's own proc list, loaded in
`EQDataStore`), not from how a line happens to read. The flag travels on `FctHitCommand.Proc` because it cannot be
recovered downstream - by the time a canvas sees a hit it has a number, a lane and an ability name, none of which say why
the number exists. Same reason `Periodic` is carried as a flag instead of being guessed from a spell's name.

The simulation streams include a proc stream which reuses the ordinary spell names on purpose: the same word appearing at
two sizes side by side is what makes the treatment visible, rather than comparing a proc against some other ability.

### Smoothing, where animated text actually costs

- **Easing is smootherstep** (`6t⁵ − 15t⁴ + 10t³`) rather than ease-out-quad. Ease-out-quad leaves at full speed, which
  is what made a number look thrown onto the screen; smootherstep has zero velocity *and* zero acceleration at both
  ends, which is what "floated" means. The horizontal arc uses the same curve so the path cannot bend oddly mid-flight.
- **Skia's antialias flag defaults to off in SkiaSharp 3**, and every paint in `FctSkiaCanvas` draws glyphs or the
  blurred crit halo, so it is set explicitly: without it a 34 px number has staircase edges.
- **`SKFont.Hinting = None`, `SKFont.Subpixel = true`.** Hinting reshapes a glyph according to which pixel rows it lands
  on, so text that drifts a pixel per frame silently redraws its own outline every frame — the crawl people describe as
  jitter even though the position maths is continuous. Without subpixel positioning Skia snaps each run to a whole
  pixel, quantising exactly the motion `FctMotion` interpolated. Both settings are right for moving text and wrong for
  a static document, so they are commented rather than obvious: do not tidy them back to defaults.
- Pacing counts whole ticks instead of comparing elapsed time, for the reason given in "Raster at most 60 times a
  second": at exactly 60 Hz the old threshold skipped frames on ordinary jitter and produced an alternating cadence.
- **Both ends of the fade are eased.** A linear ramp changes slope abruptly at `fadeStart` — steady, then suddenly
  dimming, then gone — and a linear start is a pop. The trade is a slightly steeper mid-fade, which reads as the text
  holding up and then dissolving rather than draining away.
- **A counting-up total never moves anything else.** A folded DoT stack re-measures its glyphs each frame; letting the
  widening number also widen `ValueWidth` would widen the clamp band `ArcedX` reads, so the total drifted sideways as it
  climbed and looked unstable. `FctMotion.IsCountingUp` pins the backend to the widest measurement seen while counting.
- **The crit halo is shaped like the text it glows behind.** Its sprite is rendered with the same `Hinting = None` as
  the crisp pass; a hinted sprite under unhinted glyphs puts the bloom a fraction off the number.

### The one grammatical rule on the source line

`FctManager.DisplaySource` singularises the attack verb ("Crushes" → "Crush") for melee records **and** for the
zero-damage evade lines, because `DamageLineParser` fills `SubType` from `"X tries to crush Y, but Y dodges!"` even
though `Type` carries the label there. Miss that and the same swing reads "Crushes" beside DODGE and "Crush" under its
number — invisible in a log file, obvious in peripheral vision. Nothing else is ever conjugated: every other `SubType`
is a spell name, and proper nouns keep their letters (`Crown of Stars` is not `Crown of Star`).

### Click-through and persistence

In game the overlay must not eat clicks or take focus, so `FctOverlayWindow` runs layered plus
`WS_EX_TRANSPARENT`/`WS_EX_NOACTIVATE` while locked — the same recipe as the timer, text and toolbar overlays: read
the extended styles with `NativeMethods.GetWindowLongPtr`, set or clear the bits, write them back with
`NativeMethods.SetWindowLong` through `GetWindowLongFields.GwlExstyle`. Transparency itself is declared once in XAML
(`AllowsTransparency`) and WPF does not rewrite those bits afterwards, so they are applied on `SourceInitialized`
and on each lock toggle instead of from a window hook — re-writing them per mouse message costs a syscall pair for
every hover over the overlay and buys nothing observable. Geometry persists through `ConfigUtil`
(`FctOverlayLeft/Top/Width/Height/Enabled`) — written when a drag or resize is released, not by Save, because where you left the window
is never ambiguous — and the overlay reopens at startup, locked, if it was open on exit. Lock state itself is stored nowhere (see
*Two states*). The presentation switches — motion style (`FctOverlayMotion`) and the two dials (`FctOverlayTextScale`, `FctOverlaySpeed`; multipliers
rather than percentages because the file is somewhere a person may look, and speed rather than duration because that is what its dial measures) —
persist through `FctOverlaySettings` and are written **only by the Save button**, along with `FctOverlayConfigured`, the one-time mark that somebody has
actually chosen. The old `FctOverlayTimeScale` is still read once, inverted, when the speed key is absent; it is never written again. They are read by
the overlay and by the simulation window so a hold run and a spray
run differ in nothing but the thing being compared. Being presentation-only, these apply immediately and need neither a lock nor a re-parse —
which is the exception that proves the rule above: data-shaped settings have to be re-read by everything, and these are read once when a number
is built. `FctOverlayMotion` also reads the old
`FctOverlayFountain` boolean when its own key is absent: an upgrade should keep the choreography somebody had already
chosen instead of silently resetting them to hold, and because writes only ever use the new key the legacy entry fades
out on its own rather than needing a migration.

`MainWindow` mirrors the configure state so menu and window never disagree, entering configure mode calls `Activate()` so Esc and dragging are live
immediately, and asking to configure while the overlay is hidden shows it first rather than doing nothing silently. The menu item unticking ends
configure mode the same way Cancel does — settings back the way they were, overlay still open.

`NativeMethods` exposes exactly two style vocabulary sets — `ExtendedWindowStyles` and `GetWindowLongFields` — and
neither has a `WS_EX_APPWINDOW` member nor a plain `GWL_STYLE` accessor. Overlay windows stay toolwindows in both
lock states, which is also what keeps them out of Alt+Tab; anything wanting app-window behavior has to add the
constant deliberately rather than assume it is there.

### What is deliberately not here yet

- Party-wide and other-players' heals: fed only when group configuration exists to scope them.
- Resist/immune as distinct event classes: the parser reports partial resists as reduced totals (which display
  correctly) and full immunity as `Labels.Invulnerable`; there is no "resisted 75%" record to show, so nothing is invented.
- Per-character or per-lane configuration, palette customization, and a reduced-motion mode. Colour is deliberately
  not load-bearing for reading the overlay — direction comes from region plus travel, and the two lanes that could
  be confused (my whiff vs a defence that worked) are also separated by size and band — so a palette switch is a
  comfort feature rather than an accessibility blocker.
- Real GPU presentation via `D3DImage` (see above).
