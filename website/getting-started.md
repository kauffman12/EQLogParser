# Getting Started

This guide will walk you through installing EQLogParser, running your first parse, and setting up audio triggers and overlays.

## What is EQLogParser?

EQLogParser is a real-time combat analyzer and damage parsing application built specifically for the **EverQuest MMO**. It monitors and processes in-game log files to provide:

- **Damage dealt and received** breakdowns (per player, mob, or encounter)
- **Spell casting counts** and activity timelines
- **Audio triggers** that play sounds or TTS speech when log patterns match
- **Visual overlays** (damage meter, timers, text displays) that can show in OBS for streaming
- **Log search**, automated backups, import/export of trigger packages, and one-click migration from NAG databases

At its core EQLogParser is a **parser** — open a character's log file and the stats take care of themselves. On top of that sits an optional **trigger engine**: audio callouts and visual overlays that react to anything in the log, which you only need if you want alerts or streaming graphics.

## 1. Download & Install

1. Visit the [Download page](download.html) and grab the latest installer.
2. Run `EQLogParser-install-{version}.exe` as Administrator (required for log file access).
3. If `.NET 8.0 Desktop Runtime` is not already installed, the installer will prompt you to install it — follow the on-screen instructions.
4. After installation, launch EQLogParser from the Start menu or desktop shortcut.

## 2. Open Your First Log

EQLogParser is a parser first — no triggers or configuration required to get value out of it:

1. Find your character's EverQuest log file — named like `eqlog_{Character}_{Server}.txt`, usually in your EverQuest folder.
2. Choose **File → Open and Monitor Log File** and pick it (recently opened logs appear at the top of that menu).
3. EQLogParser parses the file and keeps monitoring it in real time while you play:
    - Damage dealt and received shows up in the **DPS/Healing/Tanking Summary** (View menu)
    - Spell casts are tracked in **Spell Counts**
    - Timers show up in the **Timeline** charts

Tip: enable **Options → Auto Monitor Last Log** and EQLogParser will reopen that log and start monitoring automatically every time it launches.

## 3. Audio Triggers and Overlays

Want sounds, TTS callouts, or on-screen overlays? Those come from the trigger engine:

1. Open the **Trigger Manager** (View → Triggers → Trigger Manager).
2. Right-click in the tree → **New Trigger**, set a **Name**, enter a **Pattern** to match (see [Triggers & Regex](documentation.html) for syntax), and configure its display/speak/timer options.
3. Tick the checkbox next to **Check to Activate Triggers** at the top of the window — that's the master switch for your monitored log.

Overlays (damage meter, timers, text) are set up the same way in the **Overlays** folder; see [getting overlays into OBS](faq.html#how-do-i-get-overlays-to-show-in-obs) if you're streaming.

## 4. Have More Than One Character?

The Trigger Manager starts in **basic mode**, where that one master switch covers whichever log you have open. To track several characters at once — each with its own log file, voice, and enable state:

1. In the Trigger Manager, click **Switch to Advanced** at the top right of the window.
2. A **Manage Characters** pane appears on the left. Pick **New → New Character**, enter the character's name exactly as it appears in-game, and use **Select Log** to point at that character's `eqlog_{Character}_{Server}.txt` file.
3. Check the box next to each character to enable its triggers — the window header shows how many characters are active.

The choice is remembered, and you can flip back with **Switch to Basic** at any time.

## 5. Importing Triggers from GINA

If you're switching from GINA:

1. In Trigger Manager, right-click the **Triggers** folder
2. Select **Import** and choose your `.gtp` GINA package file
3. Imported triggers will be highlighted — review and adjust patterns as needed

## 6. Migrating from NAG

If you're coming from NAG, EQLogParser can import your entire NAG database in one step:

1. In the main menu, select **Tools → Migrate NAG Database**
2. Choose your NAG database folder — the one that contains `trigger-database.json`
3. Wait for the summary dialog (large databases take a moment)

What you get:

- A new **`NAG Ingest - {date time}`** folder under **Triggers** with your NAG folders replicated. Triggers whose NAG folder was deleted are placed in an **Orphaned Triggers** sub-folder so nothing is lost
- Your NAG **overlays** are imported alongside the triggers (except FCT overlays, which have no EQLogParser equivalent — the dialog tells you how many were skipped)
- A summary dialog and an **HTML report** ("Open Report" button) listing every trigger with any features that have no EQLogParser equivalent (e.g. class level filtering, per-phrase action scoping). Check it before enabling triggers
- **Audio files are not copied** — NAG stores them separately. Triggers referencing missing audio are listed in the report; copy the `.wav`/`.mp3` files into EQLogParser's Sounds folder as needed (Tools → Open Sounds Folder)

Important:

- **All imported triggers start disabled**, and per-character enable states are not carried over from NAG — enable what you want for your characters in the Trigger Manager
- Running the migration again creates a new timestamped folder; it never updates an earlier import, so delete old ones once you've organized your triggers

## Moving to a New PC

EQLogParser keeps all of its configuration — characters, triggers, custom variables, overlays, and app settings — in `%AppData%\EQLogParser`. The built-in backup tool packages that entire folder into a single zip file:

1. On your old PC, select **Tools → Create Backup File**
2. Save the `.zip` somewhere you can move it (USB drive, cloud storage, etc.) — the default filename includes the version and date/time
3. Install EQLogParser on the new PC and launch it once
4. Select **Tools → Restore From Backup** and choose your backup file
5. Click OK on the confirmation dialog — EQLogParser replaces its configuration with the one from the backup and restarts automatically

If a restore fails partway through, your existing configuration is rolled back automatically. Creating a fresh backup before big changes is also a good habit — just keep the `.zip` somewhere safe.

## Common Gotchas

- **Game log filters**: Make sure EQ chat filters for DoT, spell, and combat messages are turned off (in-game: Options → Chat Settings). Otherwise, EQLogParser won't see the messages.
- **Windowed mode**: Overlays work best when EverQuest is in windowed or borderless-windowed mode.
- **Overlay Taskbar setting**: Set `Overlay Windows Taskbar` to **off** in EQ options for overlays to display correctly.
- **Character names**: The parser uses naming conventions to distinguish players, pets, and NPCs. Use the **Verified Players** and **Verified Pets** lists (View → Windows) to correct misidentifications.

## Next Steps

- Read the full [Trigger Variables](documentation.html#trigger-variables) reference for pattern matching syntax
- Check out the [Linux Support](faq.html#linux-support) guide if you're on Linux/Wine
- Browse the [F.A.Q.](faq.html#f-a-q) for troubleshooting tips

---

