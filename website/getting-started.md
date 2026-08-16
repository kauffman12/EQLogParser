# Getting Started

This guide will walk you through installing EQLogParser, configuring your first character, and getting up and running with the core features.

## What is EQLogParser?

EQLogParser is a real-time combat analyzer and damage parsing application built specifically for the **EverQuest MMO**. It monitors and processes in-game log files to provide:

- **Damage dealt and received** breakdowns (per player, mob, or encounter)
- **Spell casting counts** and activity timelines
- **Audio triggers** that play sounds or TTS speech when log patterns match
- **Visual overlays** (damage meter, timers, text displays) that can show in OBS for streaming
- **Log search**, automated backups, import/export of trigger packages, and one-click migration from NAG databases

## Quick Start

### 1. Download & Install

1. Visit the [Download page](download.html) and grab the latest installer.
2. Run `EQLogParser-install-{version}.exe` as Administrator (required for log file access).
3. If `.NET 8.0 Desktop Runtime` is not already installed, the installer will prompt you to install it — follow the on-screen instructions.
4. After installation, launch EQLogParser from the Start menu or desktop shortcut.

### 2. Configure Your Character

1. Open the **Trigger Manager** (View → Triggers → Trigger Manager).
2. In the **Manage Characters** pane on the left, click **Add** and enter your character name exactly as it appears in-game.
3. Click **Select Log** and choose that character's EverQuest log file — named like `eqlog_{Character}_{Server}.txt` (usually in your EverQuest folder).
4. Click **Save**, then check the box next to the character in the list to enable monitoring.
5. Repeat for each character you want to track.

### 3. Enable Features

Once a log file is active, EQLogParser will automatically:

- Parse combat damage and display it in the **DPS Summary** (View → DPS/Healing/Tanking)
- Track spell casts in the **Spell Counts** window
- Show timers in the **Timeline** charts

For audio triggers and overlays:
1. Open the **Trigger Manager** (View → Triggers → Trigger Manager)
2. Create a new trigger folder or use an existing one
3. Right-click → **New Trigger**, set a **Name**, enter a **Pattern** to match, and configure display/speak/timer options
4. Enable the trigger by checking the box next to it

### 4. Importing Triggers from GINA

If you're switching from GINA:
1. In Trigger Manager, right-click the **Triggers** folder
2. Select **Import** and choose your `.gtp` GINA package file
3. Imported triggers will be highlighted — review and adjust patterns as needed

### 5. Migrating from NAG

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

