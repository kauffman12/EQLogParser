# redist

The four Microsoft Visual C++ runtime DLLs that `onnxruntime.dll` imports, installed **app-local** — beside
`EQLogParser.exe` — so a Windows machine that never installed the Visual C++ 2022 redistributable can still speak.

| File | What it is | SHA-256 |
| --- | --- | --- |
| `msvcp140.dll` | C++ standard library (the bulk of it) | `0f885b509a685d2bbfa652fed26b5fb31d88fbdab0a978c641d1c7b8aa460aa9` |
| `msvcp140_1.dll` | C++ library, additional portion (`_1`) | `bfad5aef46c63a669e3c140655cdfdf395b6c979b400a447bd5dcb65ed8826c3d` |
| `vcruntime140.dll` | CRT: `memcpy`, exceptions, SEH | `d5e4d9a3e835fa679450145d6a7d94e36573a509317111904d9b3712c30d9066` |
| `vcruntime140_1.dll` | CRT, additional portion (`_1`, exception handling) | `1f2d41c4aa5db0bc33ebf7b66d72943a817d7ce6cbe880502a9403823633093f` |

All four are **x64, PE32+, Microsoft-signed**, file version `14.44.35211.0` (Visual Studio 2022 17.14 toolset — the
VC143 toolset still ships the v14 `140` DLL names, that is normal). Every other ONNX Runtime import
(`api-ms-win-*`, `dxcore.dll`, `dxgi.dll`, `dbghelp.dll`, `KERNEL32`) is a Windows component and must not be bundled.

## Why these four, and why here

ONNX Runtime is 12 MB of C++ and links the MSVC runtime dynamically. Piper used to work on a bare Wine prefix while
Kokoro did not, which is what exposed that EQLP had been assuming the redistributable was there; it is not something a
user should have to install for one feature of one app.

The `[Files]` section installs them into `{app}` and `TtsPackManager.ClaimVisualCppRuntimes()` claims those module names
**before** `onnxruntime.dll` is mapped. The claim is the load-bearing part: an ONNX Runtime that lives in
`%LOCALAPPDATA%\EQLogParser\kokoro\native` resolves its imports from that folder and then from the system, never from
the program folder, so four DLLs sitting next to `EQLogParser.exe` would do nothing on their own.

## App-local means this process uses these copies

The Windows DLL search order puts the **program folder ahead of System32**, so once these four sit next to
`EQLogParser.exe` they are what this process maps — whether or not the machine has a redistributable, and whether or not
the machine's copy is newer. That is Microsoft's documented *local deployment* of the CRT, and it comes with one duty:
**the checked-in copies must stay on a current toolset build.** The CRT is backward compatible, so serving older
components a newer runtime is fine, while serving them an older one is how a process ends up unable to find an export.
Refresh these when they lag (below), the same way a NuGet bump is a deliberate act.

`TtsPackManager.ClaimVisualCppRuntimes()` logs which file answered each of the four names at Debug level, because "which
CRT is this process on" is exactly the question a `0xc000007b` or a missing-export failure asks.

## What this is not

Not a pack staging folder: `sign.cmd` leaves these alone (they carry Microsoft's signature, and re-signing would replace
the vendor attribution for nothing — see its TTS section for the same rule), and nothing under `{app}\redist` is read at
run time. Only `{app}` itself is.

## Refreshing them

Take them from a Visual Studio 2022 or Build Tools redistributable directory, never from `C:\Windows\System32`:

```powershell
Get-ChildItem "C:\Program Files\Microsoft Visual Studio" -Recurse `
  -Include msvcp140.dll,msvcp140_1.dll,vcruntime140.dll,vcruntime140_1.dll -ErrorAction SilentlyContinue |
  Where-Object { $_.FullName -match '\\VC\\Redist\\MSVC\\[^\\]+\\x64\\Microsoft\.VC143\.CRT\\' } |
  Select-Object FullName
```

Copy all four together — they are one package, and mixing versions of `msvcp140.dll` and `vcruntime140.dll` is how a
process ends up with two CRT heaps. When these change, update the digests and the file version quoted above; the installer
lists all four by name in `EQLogParserInstall\EQLogParserInstall.iss` and so does `sign.cmd`, so a fifth DLL needs adding
in both.

The folder reaches the build output through the `None Include="redist\**"` item in `EQLogParser.csproj`, which is what
lets `.iss` see `{#MyReleaseDir}\redist\*.dll`.
