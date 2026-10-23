# Getting Started

This guide will walk you through installing EQLogParser, running your first parse, and setting up audio triggers and overlays.

## What is EQLogParser?

EQLogParser is a real-time combat analyzer and damage parsing application built specifically for the **EverQuest MMO**. It monitors and processes in-game log files to provide:

- **Damage, healing and tanking** breakdowns by player and encounter
- **Spell casting counts** and activity timelines
- **Audio triggers** that play sounds or TTS speech when log patterns match
- **Visual overlays** (damage meter, timers, text displays) that can show in OBS for streaming
- **Log search**, automated backups, import/export of trigger packages, and one-click migration from NAG databases

Open a character's log file and select fights to view their statistics. You can also enable the **damage meter** to follow combat automatically. The optional **trigger engine** provides sounds, spoken alerts, timers and text overlays when a message matches a pattern you choose.

## 1. Download & Install

1. Visit the [Download page](download.html) and grab the latest installer.
2. Run `EQLogParser-install-{version}.exe` and approve the Windows permission prompt if one appears.
3. If `.NET 10.0 Desktop Runtime` is not already installed, the installer will prompt you to install it — follow the on-screen instructions.
4. After installation, launch EQLogParser from the Start menu or desktop shortcut.

## 2. Open Your First Log

You do not need triggers to use the combat summaries:

1. In EverQuest, enter `/log on` to enable logging for your character.
2. Find `eqlog_{Character}_{Server}.txt` in your EverQuest **Logs** folder.
3. In EQLogParser, choose **File → Open and Monitor Log File** and select that file. Recently opened files also appear in this menu.
4. When the **Fight List** fills, select a fight to see its results. Use Ctrl-click to select individual fights or Shift-click to select a range.
5. Open the tables you want under **View → DPS / Healing / Tanking → Summary Table**. Their results cover the fights you selected.
6. Right-click a player in the DPS Summary for more detail, including **Spell Counts**, **Spell Cast Order** and **ADPS Timeline**.

The **Sec** column shows recorded combat duration, including pauses. **Busy** estimates active time within that duration, excluding pauses over 10 seconds. Busy does not change DPS or HPS; see [Sec and Busy explained](faq.html#what-is-the-difference-between-sec-and-busy).

For a display that follows combat automatically, enable **View → Damage Meter → Enable Meter**. Use **Setup** in the same menu to choose its appearance, panels and reset mode. See [how the meter compares with the summaries](faq.html#why-don-t-the-damage-meter-values-match-what-i-see-when-selecting-fights-in-the-dps-tanking-summary).

Tip: enable **Options → Auto Monitor Last Log** and EQLogParser will reopen that log and start monitoring automatically every time it launches.

## 3. Audio Triggers and Overlays

Want your triggers to play sounds, speak text out loud, or show on-screen overlays? Those come from the trigger engine:

1. Open the **Trigger Manager** (View → Triggers → Trigger Manager).
2. Right-click the **Triggers** folder → **New Trigger**. Set a **Name**, enter a **Pattern**, and choose what it should display, speak or time. Enable **Regex** if your pattern uses regular expressions or capture codes such as `{S1}`; see [Triggers & Regex](documentation.html).
3. Click **Save** and check the trigger's box in the tree to enable it.
4. Turn on the master checkbox at the top of the window to activate triggers for your monitored log.

Create **timer and text overlays** in the Trigger Manager's **Overlays** folder, then assign them to your triggers. The **damage meter** is separate: configure it under **View → Damage Meter → Setup**. For streaming, see [getting overlays into OBS](faq.html#how-do-i-get-overlays-to-show-in-obs).

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
- Your NAG **overlays** are imported alongside the triggers. NAG's Fight Combat Tracker (FCT) overlays are skipped; the dialog tells you how many
- A summary dialog and an **HTML report** ("Open Report" button) listing triggers with settings that could not be carried over exactly, such as class-level restrictions or some timer/counter actions tied to specific phrases. Check it before enabling triggers
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
- **Incorrect identities**: Open **View → Windows → Identity Table** to correct a name's type, class or pet owner. Changes update the results without reopening the file; see [identity corrections](faq.html#a-monster-or-boss-has-a-wrong-name-or-my-pet-shows-up-as-an-npc).

## Next Steps

- Read [Sec and Busy explained](faq.html#what-is-the-difference-between-sec-and-busy) and [DPS, RDPS and SDPS explained](faq.html#what-do-dps-rdps-and-sdps-mean)
- Read the full [Trigger Variables](documentation.html#trigger-variables) reference for pattern matching syntax
- If your triggers speak text out loud and you're wondering which TTS engine to use, see [the TTS Engine options in the FAQ](faq.html#what-are-the-tts-engine-options-and-how-do-i-change-them)
- On Linux or Wine? The standard installer works as-is, but the prefix needs the .NET 10 Desktop Runtime and the Visual C++ 2015-2022 redistributable (`vcredist2022`) first — the latter is required for any speech — see the [Linux notes in the FAQ](faq.html#linux-support)
- Browse the [F.A.Q.](faq.html#f-a-q) for troubleshooting tips

---

