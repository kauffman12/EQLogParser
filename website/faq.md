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

## What is the difference between Sec and Busy?

Both columns describe time for that row in the fights you selected, but they answer different questions:

| Column | What it measures | Used to calculate DPS or HPS? |
|---|---|---|
| **Sec** | Recorded combat duration for this row, including pauses | Yes |
| **Busy** | Estimated active time within Sec, excluding pauses over 10 seconds | No |

**Sec is not necessarily the whole fight's duration.** Each row has its own recorded time, and selecting several fights combines its covered periods rather than counting all the time between fights.

**Busy looks at individual activity timestamps.** Timestamps up to 10 seconds apart count as continuous activity. Longer pauses are left out, and no time is added before the first activity or after the last. It only counts inside the periods already covered by Sec, so **Busy never exceeds Sec**.

For example, within one recorded period, activity at seconds **0, 5 and 20** gives **Sec = 21** and **Busy = 7**: seconds 0–5 count as six seconds, plus one second at 20. Each endpoint counts as a second.

Each summary measures its own activity: damage dealt, healing done or incoming attacks. Misses and avoided attacks can count too. An **X +Pets** row combines the owner's and pets' activity without counting the same second twice. Busy estimates logged activity, not button presses or time spent casting.

## What do DPS, RDPS and SDPS mean?

These rates use different amounts of time:

| Column | Calculation | What it tells you |
|---|---|---|
| **DPS** | The row's damage divided by its **Sec** | Average damage per second over that row's recorded combat time |
| **RDPS** | Damage in the latest six logged seconds of the meter's combat, divided by six | Recent damage output; available on the damage meter |
| **SDPS** | The row's damage divided by the raid's recorded combat time | Damage per second using the same duration for everyone |

For example, 10,000 damage over your own 10 seconds gives **1,000 DPS**. If the raid's recorded duration is 20 seconds, the same damage gives **500 SDPS**.

In the Healing Summary, **HPS** uses the healer's Sec and **SHPS** uses the raid's recorded combat duration. On the meter's Tank panel, the damage rates describe **damage taken**, not damage dealt.

**Busy is not a denominator for any of these rates.** RDPS advances with logged combat, not a wall-clock countdown: if no new combat arrives, the number holds until activity changes or the meter closes. It divides by six even when the encounter has fewer than six logged seconds.

Use **View → Damage Meter → Setup → Columns** to choose which numeric columns the meter displays. Hiding a column changes the display, not the calculations.

## Why don't the Damage Meter values match what I see when selecting fights in the DPS/Tanking Summary?

The **summary tables show the fights you selected**. The **damage meter chooses combat automatically** according to its reset mode. Their totals can differ when they cover different fights or different portions of a fight.

To compare them:

1. Compare the same combat and check the meter's [reset mode](#how-do-the-damage-meter-s-reset-modes-work). A manually cleared meter may cover only part of a selected fight.
2. Compare the same direction:
    - **DPS** on the meter shows damage dealt; compare it with the **DPS Summary**
    - **Tank** shows damage taken; compare it with the **Tanking Summary**, including players, pets and mercenaries
3. Compare the same rate: **DPS**, **RDPS** and **SDPS** use different time periods, as explained above.
4. Check class filters and how pets are grouped so you are comparing the same rows.

The **DPS** and **Tank** buttons appear together only when **Setup → Meter panels** is **Both**. Choosing **Damage** or **Tanking** shows that panel alone, without tab buttons.

## How do the Damage Meter's reset modes work?

Choose a reset mode under **View → Damage Meter → Setup**:

- **On Kill:** the meter keeps each ongoing encounter's damage. When one opponent dies, its encounter leaves the ongoing total while surviving opponents keep theirs. When no ongoing encounter remains, the meter briefly shows the final result, with a deadline about eight seconds after the last accepted attack. Without a death, a gap of more than 30 logged seconds separates encounters; about 30 seconds without accepted attacks also closes the display.
- **Timed modes:** the meter keeps an accumulated result until there has been no accepted attack for the configured number of seconds. Kills do not clear it. After that quiet period, the display closes and the next attack starts a new accumulation.

A timed mode is **not a rolling last-N-seconds total**. With a ten-second reset, a fight can accumulate for several minutes as long as there is no pause longer than ten seconds. Accepted misses and avoided attacks can also keep an encounter going.

Use the toolbar's **Clear Parse** button to start counting from that point, even during a fight. **Closing with X does not clear the accumulated result**; its reset rules still apply when you reopen it.

## Why did my Damage Meter disappear, or why does it look empty?

1. Check the [reset mode](#how-do-the-damage-meter-s-reset-modes-work). A quiet period can close the meter even when the last opponent did not die. It returns automatically when there is eligible combat to display, provided **Enable Meter** remains on.
2. Check **Setup → Meter panels**. With **Damage** selected, incoming attacks alone cannot open or keep the meter up. With **Tanking**, outgoing attacks alone cannot do so. With **Both**, either direction can keep it up.
3. Check the **class filter**. It hides rows without changing the recorded totals. If it hides everyone, the meter can remain open with no visible player rows; select **All Classes** to show them again.
4. With **Both** enabled, the tab you selected may be empty while the other has results. The other button briefly turns green every few seconds to draw your attention. The meter never switches tabs for you.

## When using Trigger Log, I would like a quick way to edit the Trigger for the log entry.
1. Open **Trigger Manager**, then select an event in **Trigger Log**. The matching trigger is selected in the manager, ready to edit.
2. You can also use **Trigger Search** above the folder tree. It searches trigger names and pattern fields.
3. Drag the **Trigger Log** and **Trigger Manager** tabs into separate panes to see both at once, as shown below:

<a style="margin-left: 30px;" href="img/trigger-selection.gif" target="_blank">
  <img src="img/trigger-selection.gif" alt="Select Trigger from Trigger Log" height="300" loading="lazy">
</a> 

## My triggers work in the tester but not in-game (or nothing fires at all)

1. Make sure EverQuest logging is on (`/log on`) and EQLogParser is monitoring the correct character's file.
2. Check both enable settings: the trigger's checkbox in the tree, and the master checkbox in basic mode or the character's checkbox in advanced mode.
3. Check **Regex** if the pattern uses regex syntax or capture codes such as `{S1}` or `{N}`. Save any changes.
4. Use **Log Search**, or open the original log file, to find the exact message and copy it into the tester. **Trigger Log** records trigger events, so it may not contain a message that never fired a trigger.
5. Compare spaces, words and punctuation. EQ sometimes uses a backtick (&#96;) where you might expect an apostrophe ('). Also check the trigger's **Previous Line** and **Match Variables** conditions if you use them.
6. Check EverQuest's **Options → Chat Settings** for filters affecting the messages your trigger needs.

## Why are my Overlays not showing or they use the wrong colors?
For timer and text overlays:

1. Assign an overlay in the trigger's settings, or mark an overlay as **default** to use it when the trigger has no assignment.
2. Preview the overlay and **Save** your changes.
3. Check the trigger's **Custom Active Color** and **Custom Font Color**. These can override the overlay's colors.
4. In advanced mode, also check **Overlay Active Color** and **Overlay Font Color** in the character's settings. A **Transparent** color can make a timer appear to be missing.
5. To test a color override, choose a visible color and save. Clear the override afterward if you want to use the overlay's own colors.

For the damage meter, use **View → Damage Meter → Setup** instead.

<a style="margin-left: 30px;" href="img/trigger-colors.png" target="_blank">
  <img src="img/trigger-colors.png" alt="Custom Colors in Trigger Settings" height="200" loading="lazy">
</a>
<a style="margin-left: 30px;" href="img/character-colors.png" target="_blank">
  <img src="img/character-colors.png" alt="Custom Colors in Character Settings" height="200" loading="lazy">
</a>

## How do I get overlays to show in OBS?
1. Enable **Streamer Mode** for the window you want to capture
    - For timer or text overlays, use that overlay's settings in **Trigger Manager**
    - For the damage meter, use **View → Damage Meter → Setup**
2. In OBS, add a **Window Capture** source (not Game Capture)
    - Set the **Capture Method** to **Windows 10 or newer** by right-clicking the source → Properties
3. Select the correct window from the dropdown in OBS
    - Choose the overlay window itself (e.g. "Damage Meter") and **not** the EverQuest window
4. Make sure the window is visible before selecting it in OBS. For a timer or text overlay, preview it or fire its trigger. For the damage meter, open its **Setup** window to display the preview

## When using one of the right-click Copy options or sending a Quick Share. Nothing is copied.

1. Try copying again. Another application may be holding the clipboard open.
2. Open **Tools → View Error Log**. An `OpenClipboard Failed` or `CLIPBRD_E_CANT_OPEN` error means EQLogParser could not access the clipboard; it does not identify the cause. If the problem persists, check clipboard utilities, remote-desktop software and security software. ESET users have reported needing an exception in its HIPS settings.
3. If only **Send Parse to EQ** fails, check the character count in **Preview Parse**. EverQuest limits how much text you can paste at once.

## Why does my Charm Pet or Merc not show up correctly in the Summary table?

EQLogParser uses evidence such as ownership messages, charm events and known mercenary names to identify combatants. A pet and an NPC sharing a name can still make identification difficult.

First check the summary's pet options. Damage can be grouped under an **X +Pets** row; the Tanking Summary has a **Show Pets** checkbox. If the name's identity or owner is wrong, use the **Identity Table** as described below.

## A monster or boss has a wrong name, or my pet shows up as an NPC

Open **View → Windows → Identity Table** and find the name:

1. Check **Type** and **Why** to see how it was identified. Hover over Why for more detail.
2. Use the pencil in **Type** to choose Player, Pet, Mercenary or NPC. Player rows also have a class pencil; Pet rows have an owner pencil.
3. To discard saved assignments and reconsider the name using the current log, click the **history-arrow button** beside its name.

Changes update the results without reopening the log. You can also right-click a name in a summary or the Fight List and use **Set … as** to change its type. Assigning a pet owner is a separate choice from identifying it as a pet.

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

## What are the TTS Engine options and how do I change them?
1. If your triggers speak text out loud, the speaking is done by one of three text-to-speech engines:
    - **Windows** — uses installed Windows voices, with no extra download. Voice quality depends on what is installed. Not available under Linux/Wine
    - **Piper** — fast and lightweight, making it a good starting point for older PCs or frequent spoken alerts. Its voices can sound less natural. Downloads about 682 MB, including 12 voices
    - **Kokoro** — more natural-sounding voices, but requires more CPU and memory. Downloads about 228 MB
2. Change the engine from the Trigger Manager window: click the **Tools** dropdown button in the toolbar at the top and select **TTS Engine**
    - Selecting an engine in the dropdown only updates the description below it — nothing changes until you press a button
    - If that engine's runtime is not on this machine yet, the button reads **Download Piper (682 MB)** or similar. When the download finishes, EQLogParser switches to the new engine automatically
    - If the runtime is already installed, the button says **Use** followed by the engine name. The switch is live: the next thing your triggers speak already uses the new engine, no restart needed
3. Why you might pick one over the other:
    - **Windows** when you want zero setup and are happy with the built-in Windows voices (not an option under Linux/Wine)
    - **Piper** for frequent spoken alerts or a less powerful PC
    - **Kokoro** for more natural speech, if your PC handles it comfortably
4. Voices belong to the engine that provides them:
    - Pick one from the **voice** dropdown in the Trigger Manager toolbar; in advanced mode each character keeps its own voice
    - Switching engines temporarily shows that engine's default voice until you pick one. A voice saved for one engine (e.g. Piper) comes back when you switch to it again
5. The Piper and Kokoro runtimes are downloaded by the app into `%LOCALAPPDATA%\EQLogParser` — they are not part of the installer
    - To reclaim the space, select the engine in the TTS Engine window and press **Remove Files** (you cannot remove the engine that is currently speaking; switch to another one first)
6. If you followed older instructions: you no longer need to download Piper from Google Drive or unzip anything into the EQLogParser program folder. The TTS Engine window downloads, verifies and installs everything itself, and the installer cleans up old copies when you upgrade

# Linux Support

Use the standard [Windows installer](download.html) under **64-bit Wine**. This setup has been tested on Ubuntu/Debian-based systems.

Install these dependencies inside the same Wine environment as EQLogParser:

| Dependency | Bottles name | winetricks name |
|---|---|---|
| .NET 10 Desktop Runtime | `dotnetdesktop10` | `dotnetdesktop10` |
| Microsoft Visual C++ 2015-2022 redistributable, needed for Piper and Kokoro speech | `vcredist2022` | `vcrun2022` |

If you are upgrading from a version that used .NET 8, add the .NET 10 Desktop Runtime. Both runtimes can remain installed.

Choose [Bottles](#installing-with-bottles) for a graphical setup, or [plain Wine](#installing-with-plain-wine) to manage the environment yourself.

## Installing with Bottles

Bottles manages separate Wine environments, called **bottles**. Install it through Flatpak with these terminal commands:

```bash
# Install Flatpak
sudo apt install flatpak

# Add the Flathub repository (where Bottles is hosted)
flatpak remote-add --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo

# Install Bottles
flatpak install flathub com.usebottles.bottles
```

Use the project recipe below to install the dependencies automatically, or follow the manual steps. Both routes use a **Custom**, **64-bit** bottle with a **Caffe** runner (called **Wine** on older Bottles versions), rather than Proton. Choose **Custom** to expose the runner selection.

### The easy way: `PERSONAL_INSTALLERS`

Launch Bottles with the project's installer recipe enabled:

```bash
PERSONAL_INSTALLERS=https://raw.githubusercontent.com/kauffman12/EQLogParser/refs/heads/master/bottles flatpak run com.usebottles.bottles
```

Then, once Bottles is open:

1. Click **Create a New Bottle**. Choose type **Custom**, architecture **64-bit**, and runner **Caffe** (**Wine** on older versions). The recipe requires a 64-bit bottle.
2. Open the bottle's **Install App** page (called **Installers** in newer versions) and select **EQLogParser**.
3. Press its download button. The recipe installs **vcredist2022**, **dotnetdesktop10** and **allfonts**, then downloads and runs the EQLogParser installer.
4. Launch EQLogParser from the bottle's program list with **Run App**. The Flatpak build does not add a desktop menu entry.

Run the recipe again to install the version it currently names; Bottles does not notify you when the recipe changes.

> **Tip:** save that command as a shell script or a desktop shortcut. The variable has to be set on the Bottles process itself, so starting Bottles from your usual app menu will not list EQLogParser.

### The manual way: build the bottle yourself

Download the EQLogParser installer, open Bottles normally, and:

1. Click **Create a New Bottle**. When prompted for the type, select **Custom**, make sure the architecture is **64-bit**, and pick **Caffe** as the **Runner** (**Wine** on older Bottles — not Proton), then click **Create** and wait for Bottles to finish setting up the environment.
2. Open the bottle's **Dependencies** page and install these:
    - **vcredist2022** — required for text to speech, without it Piper and Kokoro download their files and then stay silent (item 3 under Known Issues below)
    - **dotnetdesktop10** — the .NET 10 Desktop Runtime EQLogParser runs on
    - **allfonts** — optional, the font set the published recipe installs
3. Press **Install App → Run executable** and choose `EQLogParser-install-{version}.exe`, then launch `EQLogParser.exe` from `Program Files\EQLogParser`.

Either way, on first start nothing speaks until one engine is downloaded: Trigger Manager → **Tools** → **TTS Engine** → **Download** (Piper, about 682 MB, or Kokoro, about 228 MB). See [What are the TTS Engine options?](#what-are-the-tts-engine-options-and-how-do-i-change-them)

## Installing with plain Wine

If you prefer to manage a Wine prefix directly, use `wine` and `winetricks`:

1. Create a 64-bit prefix and put both prerequisites in it:
    - `WINEPREFIX=~/eqlogparser WINEARCH=win64 wineboot`
    - `WINEPREFIX=~/eqlogparser winetricks vcrun2022 dotnetdesktop10`
2. Run the installer in that prefix: `WINEPREFIX=~/eqlogparser wine EQLogParser-install-{version}.exe`
3. Launch it and download a speech engine the same way as above
4. **vcrun2022** covers the Visual C++ 2015-2022 dependency; you do not need to add the older redistributables separately

## Known Issues with Linux
1. WPF applications are unstable with WINE so hardware acceleration is disabled 
    - Note the WINELOADER environment variable is used to detect WINE
    - Make sure that variable exists if you notice problems
    - The EQLogParser log file should show Software as the RenderMode
    - Log file location: ~/.wine/drive_c/users/username/AppData/Roaming/EQLogParser/logs
2. WINE x64 does not work with any windows text-to-speech engine
    - Use **Piper** or **Kokoro** instead — both work under Wine, and the TTS Engine window in the Trigger Manager downloads one for you. See [What are the TTS Engine options?](#what-are-the-tts-engine-options-and-how-do-i-change-them)
3. Speech stays silent after a download that reported success, and the error log says `Unable to load DLL 'onnxruntime' … Module not found`
    - Check that the prefix has the Visual C++ 2015-2022 redistributable: add **vcredist2022** in Bottles or run `WINEPREFIX=~/eqlogparser winetricks vcrun2022` for the prefix created above, then restart EQLogParser and press **Use** again. No re-download is needed for a missing dependency
    - A successful download verifies the engine files, but does not install this Windows dependency
4. Each speech engine downloads several hundred MB into the prefix, at `~/.wine/drive_c/users/username/AppData/Local/EQLogParser` (under Bottles that is inside the bottle's own `drive_c`)
    - **Remove Files** in the TTS Engine window gives the space back; you have to switch to another engine first

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
