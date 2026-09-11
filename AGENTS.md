# Project Rules & Guidelines

You are an expert AI assistant tasked with maintaining this C#/WPF/.net 8.0 project.

## Core Principles
- **Follow Coding Standards** read and follow the standards under docs/CodingStandards.md
- **File structure**: Prefer small files and atomic commits.
- **Searching**: All files are under the current directoy. 
- **Do not** add heavy dependencies without explicit user approval.

## Testing Guidelines
- **Always** run `dotnet build` after completing work
- **FCT vocabulary is closed**: two region schemes (`Bands` = the panel's *fountain*, `ByType` = *split*) and four motion styles
  (`Freeze`, `Fountain`, `Spray`, `Arc`) plus `Straight`, which is arc's rail with the bend taken out. Halves, pulse, the cell grid and the
  stream were deleted on measurement, not parked: do not resurrect one because a comment mentions it. A new scheme or style arrives with a
  settings word, a panel entry, and tests — the shape names saved in `settings.ini` are exactly the words the dropdown shows (`arc`, `line`,
  `spray`, `freeze`; `Straight` excepted, saved as `line`).
- **FCT engine tests**: the dials are process globals (`FctScale.Text/Crit/Time`, `FctLayout.LabelSide`). Test classes in
  `EQLogParser.Test/src/control/fct` reset them through `FctAmbient.Reset()` via `[TestInitialize]`, and both test assemblies declare
  `[assembly: DoNotParallelize]`. Anything new that touches that engine must keep both, or state from one test leaks into another
  (a leaked speed dial was measured moving a row's spine 74 px — a real assertion failing on a machine that ran the same code).
- **Releases**: when touching `sign.cmd` or `EQLogParserInstall/*.iss`, read `docs/ReleaseChecklist.md`

## Post-Implementation Checklist
After completing work, verify:
- **Method visibility ordering**: public → internal → private (per CodingStandards.md)
- **Pattern matching**: use `is not T` / `is T var` instead of `as T` + null check (per CodingStandards.md)
- **Unused usings**: no leftover or unnecessary `using` statements

## Agent Behavior
- **Ask for Clarification**: If a feature requirement is ambiguous, ask before implementing.
- **Read the Syncfusion PDF files**: If there's a question about Syncfusion APIs look under syncfusion-wpf for info.
- **Never Use Claude/Anthropic Models**: Never use Claude or any other Anthropic model/provider for anything — no Claude Code, no Anthropic LLMs, no Anthropic VLMs.
- **Vision / Image Inspection**: Handle vision requests with `read` (native vision) and local tools only. Never call external or remote VLM services (e.g., `inspect_image`).

## Important: Never Touch
- `BackupUtil`
- `MaterialDarkCustom`
