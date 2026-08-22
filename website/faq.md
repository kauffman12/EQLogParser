# F.A.Q

## Why do spells like Gracious Gift of Mana not show up in the spell counts table?
1. Some spells do not have messages when they land players and do not appear in the log
2. For **Gracious Gift of Mana**, it has a spell message but only you can see it in your log. These spells are hidden by default as the main purpose of the spell count table is to compare your spell counts with other players
3. To view hidden spells, use the dropdown at the top as shown below:

<div style="margin-left: 30px;">
  <img src="img/show-spells.png" alt="Show All Spells" loading="lazy">
</div>

## What is "Use EMU Server Parsing" and when should I enable it?
1. The **Use EMU Server Parsing** option tells the parser which log format to expect
    - Turn it **ON** if you are playing on an emulator server (P99, Project Quarm, etc.)
    - Turn it **OFF** if you are playing on live servers (EverQuest, EverQuest Legends)
2. Having this set incorrectly can cause a variety of issues:
    - Spell damage not showing up in the DPS Summary
    - DoT damage missing or under-reported
    - Classes not being detected correctly
    - Mob names showing incorrectly or pets confused with NPCs

## Why does unknown or spell names show in the DPS Summary?
1. If a **DoT** is on an **NPC** and the player dies or zones it may stop reporting the player and say unknown instead
    - Check the damage breakdown for the **Unknown** player to get a better idea of the cause
    - Unknown is also included to make sure all damage is counted for the group or raid
2. If a name of a spell is listed in the **DPS Summary** it may be for a similar reason
    - Sometimes if the spell is a proc or other effect related to a **DoT** where the player has left the zone it may now create an older style entry in the log file where the spell name is in the position of where the player name usually is and the player name is absent. This case should be fairly rare.
3. See the [What is Use EMU Server Parsing?](#what-is-use-emu-server-parsing-and-when-should-i-enable-it) FAQ entry below for more information.

## Why don't the Damage Meter values match what I see when selecting fights in the DPS/Tanking Summary?
1. The **Damage Meter** has a toolbar with two buttons: **DPS** and **Tank**
    - **DPS** shows damage dealt by players (compare this to the **DPS Summary**)
    - **Tank** shows damage absorbed by players while tanking (compare this to the **Tanking Summary**)
2. Make sure the correct button is selected based on which summary you are comparing against
    - The active button will be highlighted in orange, while the inactive one remains white
3. If you're comparing to the **DPS Summary**, click the **DPS** button on the Damage Meter toolbar to ensure it is showing damage dealt and not tanking stats

## When using Trigger Log, I would like a quick way to edit the Trigger for the log entry.
1. When you select a row in the **Trigger Log** it will select the trigger in the **Trigger Manager** as long as you have it open. If so, just switch back to that tab and check
2. The second way to quickly find a trigger is to use the **Trigger Search** box above the folder tree where you create triggers. It searches by name and by the pattern fields
3. You may also find it useful to **drag-and-drop** the **Trigger Log** or **Trigger Manager** tabs around so that you can see both at the same time as shown below:

<a style="margin-left: 30px;" href="img/trigger-selection.gif" target="_blank">
  <img src="img/trigger-selection.gif" alt="Select Trigger from Trigger Log" height="300" loading="lazy">
</a> 

## My triggers work in the tester but not in-game (or nothing fires at all)
1. Check that **EQ Chat Filters** are turned off in EverQuest itself
    - In EQ go to **Options** → **Chat Settings** and look for filters related to DoT, spell, and combat messages
    - If these filters are enabled, EQ will silently block those messages from being written to the log file before the parser ever sees them
2. Make sure your trigger pattern exactly matches the actual in-game log line
    - A common issue is that EQ uses a backtick character **`** instead of an apostrophe **'** in some messages
    - Another common issue is that the test string is missing words from the actual log line
    - Open the **Trigger Log** tab after playing and copy the exact log line to verify your pattern matches

## Why are my Overlays not showing or they use the wrong colors?
1. Specify the Overlay in the Trigger settings or verify that **default** is checked in the Overlay.
    - The **default** Overlay is the fallback when no other Overlay is specified.
2. Preview the Overlay and make sure it is displaying as you're expecting. Remember to **save** changes.
3. Check the **Custom Active Color** and **Custom Font Color** in the trigger you are testing with.
    - If these colors are specified they will override what the overlay is configured with.
    - Even if they say **Click to Select Custom Color** try clicking and saving with a color as a test.
    - Example images will be shown below.
4. If in advanced mode, check **Overlay Active Color** and **Overlay Font Color** when you modify your character settings.
    - These options are another way to choose custom colors. Sometimes they get set by accident.
    - One common issue is that **Transparent** gets selected which makes it look like the timer never displays.
5. In both cases above, it may help to set a color and see if it does anything. If so go back and reset/clear the value.

<a style="margin-left: 30px;" href="img/trigger-colors.png" target="_blank">
  <img src="img/trigger-colors.png" alt="Custom Colors in Trigger Settings" height="200" loading="lazy">
</a>
<a style="margin-left: 30px;" href="img/character-colors.png" target="_blank">
  <img src="img/character-colors.png" alt="Custom Colors in Character Settings" height="200" loading="lazy">
</a>

## How do I get overlays to show in OBS?
1. Enable **Stream Mode** in the overlay settings within EQLogParser
    - Open the overlay configuration and check the **Stream Mode** checkbox
2. In OBS, add a **Window Capture** source (not Game Capture)
    - Set the **Capture Method** to **Windows 10 or newer** by right-clicking the source → Properties
3. Select the correct window from the dropdown in OBS
    - Choose the overlay window itself (e.g. "Damage Meter") and **not** the EverQuest window
4. If the overlay still does not appear, make sure the trigger has actually fired to create the overlay window first

## When using one of the right-click Copy options or sending a Quick Share. Nothing is copied.
1. Check the error log for the message below. If you see it then your anti-virus software is blocking access. You'll need to figure out how to add an exception for EQLogParser.exe. This seems to be common with ESET and you may want to look for the HIPS settings and see if you can add the exception there.
    - **ERROR EQLogParser.UiUtil - Failed to set Clipboard Text: OpenClipboard Failed (0x800401D0 (CLIPBRD_E_CANT_OPEN))**
2. If you do not see an error and it is only happening with Send Parse to EQ. Keep in mind that Everquest has a limit on how many characters you can paste. If you open the Preview Parse window you'll see a count and warning if you copy too much.

## Why does my Charm Pet or Merc not show up correctly in the Summary table?
1. The main reason for this is naming. The parser is not good at handling names that do not look like player names
2. Charm pets are extra difficult as there's no way to distinguish the pet from npc if you fight an npc with the same name
3. The name problem can be improved upon but it is complicated and something will be worked on in the future

## A monster or boss has a wrong name, or my pet shows up as an NPC
1. EverQuest does not tell the parser which names are players, pets, or NPCs so it has to guess based on context
    - This can cause confusion when a pet shares a name with an NPC, or when multiple instances of a mob are present
2. Check and clean the **Verified Players** and **Verified Pets** lists
    - Open these windows from the **View** menu → **Windows**
    - Remove any entries that don't make sense (e.g. boss names in the players list, or your pet in the wrong owner)
3. After cleaning up the lists, reload the log file so the parser can re-evaluate
4. If a specific mob is misidentified during a fight, right-click it and select **Set As Pet** or **Set As NPC** to correct it
5. For damage shields, DoTs, and environmental effects showing as monsters in the DPS Summary, try unchecking the **Tanking** checkbox
    - This hides objects that damage players but are not actively attacking back

## How do I import a trigger package (.tgf.gz or .gtp)?
1. Open the **Trigger Manager** window
2. Right-click on the **Triggers** folder (or any sub-folder you want to import into)
3. Select **Import** from the context menu
4. In the file dialog, select the **.tgf.gz** or **.gtp** file and click **Open**
    - The file dialog filters for supported formats, so make sure you have the correct file type selected in the dropdown
5. Imported triggers will be highlighted to show they were recently added
    - You can clear the highlighting by right-clicking and selecting **Clear Highlighting**
6. Overlays are imported the same way but use the **Overlays** folder instead
    - Overlay packages use the **.ogf.gz** extension
7. Do not extract the **.tgf.gz** files onto your computer.

## Coming from NAG?
1. See **Migrating from NAG** in [Getting Started](getting-started.html#6-migrating-from-nag) — one menu command imports your entire trigger and overlay database
2. In short: **Tools → Migrate NAG Database**, pick the folder that contains `trigger-database.json`, and everything lands in a new `NAG Ingest - {date time}` folder under Triggers
3. Imported triggers start **disabled** so you can enable what you want — check the HTML report from the summary dialog for any NAG features that have no EQLogParser equivalent

# Linux Support

EQLogParser has been officially supporting Linux since version 2.2.66 with only minor issues. Note that the 64bit version of WINE is required.

## Simplest Install: Flatpak + Bottles

The easiest way to get EQLogParser running on Linux is using **Flatpak** and **Bottles**. Bottles is a Wine management app that handles all the setup for you, and Flatpak is the recommended way to install it.

### Step 1 — Install Flatpak and Bottles

Open a terminal and run the following commands:

```bash
# Install Flatpak
sudo apt install flatpak

# Add the Flathub repository (where Bottles is hosted)
flatpak remote-add --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo

# Install Bottles
flatpak install flathub com.usebottles.bottles
```

### Step 2 — Launch Bottles

Start Bottles using the following command. The `PERSONAL_INSTALLERS` variable tells it where to find the EQLogParser installer:

```bash
PERSONAL_INSTALLERS=https://raw.githubusercontent.com/kauffman12/EQLogParser/refs/heads/master/bottles flatpak run com.usebottles.bottles
```

> **Tip:** You can save this as a shell script or desktop shortcut so you don't have to type it every time.

### Step 3 — Create a New Bottle

Once Bottles is open:

1. Click **Create a New Bottle**
2. When prompted for the bottle type, select **Custom**
3. Make sure the architecture is set to **64-bit**
4. Change the **Runner** to **Wine**
5. Click **Create** and wait for Bottles to finish setting up the environment

### Step 4 — Install EQLogParser

After the bottle is created:

1. Click the **Install App** button inside your new bottle
2. Find and select **EQLogParser** from the list of available apps
3. Follow any on-screen prompts to complete the installation

### Step 5 — Run EQLogParser

Once installation is finished, simply click the **Run App** button to launch EQLogParser. That's it — no manual Wine configuration required!

---

## Manually installing without Flatpak/Bottles

### Download **EQLogParser** and the **.Net 8.0 Desktop Runtime x64** found [here](index.html).
Use the following steps to install under Linx (tested on Ubuntu/Debian-based systems):
```
sudo apt install wine  # (version 10)
sudo apt install winetricks  # (version 20250102-1)
winetricks allfonts
winetricks renderer=gdi
wine windowsdesktop-runtime-8.0.25-win-x64.exe  # (or latest)
wine EQLogParser-install-2.3.49.exe  # (or latest)
```

## Known Issues with Linux
1. WPF applications are unstable with WINE so hardware acceleration is disabled 
    - Note the WINELOADER environment variable is used to detect WINE
    - Make sure that variable exists if you notice problems
    - The EQLogParser log file should show Software as the RenderMode
    - Log file location: ~/.wine/drive_c/users/username/AppData/Roaming/EQLogParser/logs
2. WINE x64 does not work with any windows text-to-speech engine
    - Piper TTS is provided as an alternative but requires manual Installation
    - The bottles install comes with 1 voice pre-loaded
    - Follow steps below for all voices
    
## Piper TTS
Piper TTS is an Open Source **text-to-speech engine** and a custom build is provided for EQLogParser. It is hosted on google drive and may be subject to a limited number of downloads per day/month.

1. Download the <a href="https://drive.google.com/file/d/1G2Ecg9sfOMxifRzrKwqySGwHoVV3tHUJ/view?usp=sharing" target="_blank">PiperTTS</a> zip file
2. Unzip into ~/.wine/drive_c/Program Files/EQLogParser/piper-tts
3. Verify it was unzipped properly
    - The piper-tts folder contains dlls and a voices folder
    - The piper-tts folder should be directly under the EQLogParser folder
4. Restart EQLogParser. Note that the log file should tell you that it's using piper-tts
5. Test changing voices in the Trigger Manager window

# Feedback

Please use the **Discussion** and **Issues** links at the top right of this page for submitting feedback. They will take you to the Github project for EQLogParser where everything is kept.

## Guidelines
1. Create account on <a href="http://github.com" target="_blank">Github</a> if you do not have one already
2. Login and post a message in either the Issues or the Discussion section
    - Reading through the existing topics to see if your question has been answered before
3. Bugs or feature requests should be created as an Issue
4. General questions should be created in the Discussion section
5. When reporting a bug be as detailed as possible
    - Are you on Live servers or P99?
    - What steps were taken to produce the bug?
    - What is the result you were expecting?
    - What have you already tried to resolve the issue?
    - Are you using the latest EQLogParser?
    - Have you checked the error log from the Tools menu?
    - When checking the error log look for any **exceptions** or **ERROR** statements, etc
6. If a bug is related to data not being parsed or a trigger not matching
    - Include the line from your log file that has the data 
    - If trigger related then include the Regex or screenshot of your Trigger settings
