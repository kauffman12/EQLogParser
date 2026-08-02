# Getting Started

This guide will walk you through installing EQLogParser, configuring your first character, and getting up and running with the core features.

## What is EQLogParser?

EQLogParser is a real-time combat analyzer and damage parsing application built specifically for the **EverQuest MMO**. It monitors and processes in-game log files to provide:

- **Damage dealt and received** breakdowns (per player, mob, or encounter)
- **Spell casting counts** and activity timelines
- **Audio triggers** that play sounds or TTS speech when log patterns match
- **Visual overlays** (damage meter, timers, text displays) that can show in OBS for streaming
- **Log search**, automated backups, and import/export of trigger packages

## Quick Start

### 1. Download & Install

1. Visit the [Download page](download.html) and grab the latest installer.
2. Run `EQLogParser-install-{version}.exe` as Administrator (required for log file access).
3. If `.NET 8.0 Desktop Runtime` is not already installed, the installer will prompt you to install it — follow the on-screen instructions.
4. After installation, launch EQLogParser from the Start menu or desktop shortcut.

### 2. Configure Your Character

1. In EQLogParser, go to **Options** → **Characters**.
2. Click **Add** and enter your character name exactly as it appears in-game.
3. Set the **Log File Path** — this is usually something like:
   `C:\\Users\\{username}\\Documents\\EverQuest\\Logs\\eqlog_{character}_project1999.ini`
4. Check **Active** to enable monitoring for that character.
5. Repeat for each character you want to track.

### 3. Enable Features

Once a log file is active, EQLogParser will automatically:

- Parse combat damage and display it in the **DPS Summary** (View → DPS/Healing/Tanking)
- Track spell casts in the **Spell Counts** window
- Show timers in the **Timeline** charts

For audio triggers and overlays:
1. Open **Trigger Manager** (View → Triggers)
2. Create a new trigger folder or use an existing one
3. Right-click → **New Trigger**, set a **Name**, enter a **Pattern** to match, and configure display/speak/timer options
4. Enable the trigger by checking the box next to it

### 4. Importing Triggers from GINA

If you're switching from GINA:
1. In Trigger Manager, right-click the **Triggers** folder
2. Select **Import** and choose your `.gtn` or `.xml` file
3. Imported triggers will be highlighted — review and adjust patterns as needed

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

