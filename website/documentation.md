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
- Check out the [Linux Support](documentation.html#linux-support) guide if you're on Linux/Wine
- Browse the [F.A.Q.](documentation.html#f-a-q) for troubleshooting tips

---

# Regex 101

## 🔹 **Basics**

- **`.`** — Matches **any single character** (except new line)  
  _Example: `a.c` matches "abc", "axc", "a-c", etc._
- **`*`** — Means "**zero or more**" of the thing before it  
  _Example: `a*` matches "", "a", "aa", "aaa", etc._
- **`+`** — Means "**one or more**" of the thing before it  
  _Example: `a+` matches "a", "aa", "aaa", but not "" (empty)_
- **`?`** — Makes the thing before it **optional** (zero or one)  
  _Example: `colou?r` matches "color" or "colour"_
- **`[...]`** — Matches **one character** from inside the brackets  
  _Example: `[abc]` matches "a", "b", or "c" (just one of them)_
- **`[^...]`** — Matches **one character NOT** in the brackets  
  _Example: `[^0-9]` matches any character that's NOT a number_

## 🔹 **Anchors (Position Matchers)**

- **`^`** — The **start** of the line/text  
  _Example: `^Hello` matches only if "Hello" is at the very start_
- **`$`** — The **end** of the line/text  
  _Example: `bye$` matches only if "bye" is at the very end_

## 🔹 **Shortcuts (Character Types)**

- **`\d`** — Any **digit** (0 to 9)
- **`\w`** — Any **word character** (letter, digit, or underscore)
- **`\s`** — Any **whitespace** (space, tab, etc.)
- **`\b`** — The **edge of a word** (word boundary)

## 🔹 **Grouping and OR**

- **`( ... )`** — Groups things together  
  _Example: `(cat|dog)` matches "cat" or "dog"_
- **`|`** — Means "**or**"  
  _Example: `yes|no` matches "yes" or "no"_

## 🔹 **Specials**

- **`\`** — **Escapes** a special character so it’s treated as normal  
  _Example: `\.` matches a real dot, not "any character"_
- **Special Character List:** .   ^   $   *   +   ?   (   )   [   ]   {   }   \   |   /

## 🔹 **Counts (Repetition)**

- **`{n}`** — **Exactly** n times  
  _Example: `a{3}` matches "aaa" only_
- **`{n,}`** — **At least** n times  
  _Example: `a{2,}` matches "aa", "aaa", "aaaa", etc._
- **`{n,m}`** — **Between** n and m times  
  _Example: `a{2,4}` matches "aa", "aaa", or "aaaa"_

## ⚠️ **.NET Regex Syntax**

- **No slashes needed:** Just type your pattern (don’t use `/like this/`)
- **Case-sensitive by default:** "cat" doesn’t match "Cat"
- **Spaces and punctuation:** They must match **exactly** as typed
- **Named groups are supported:** You can name parts with `(?<name>...)`
- **Don’t use `$1`, `$2`, etc.:** These are not used as Group references

## 🚀 **Performance Tips**

- **Keep patterns simple and specific** for fastest results.
- **Avoid `.*` in the middle** of patterns; use only if necessary.
- **Don’t nest lots of parentheses** or use complex patterns.
- **Start with a specific word or phrase** (e.g., `^You take`) instead of wildcards.
- **Don’t match entire blocks of text** or paragraphs.
- **Avoid long runs of wildcards and optionals** (like `(.*a.*)*`).


# Trigger Variables

These are special variables or codes that can be used in trigger `Pattern` fields to capture values so that they can be displayed or spoken.

None of these trigger variables are case-insensitive. Whether you use `{c}` or `{C}` they will do the same thing. Also, if you define one variable as `{S}` and reference it later as `{s}` it will still work. The x value in `{Sx}` or `{Nx}` is any number from `0` to `9` so you can use more than one of these in the same trigger.

In addition, modifiers may be used with these variables for display purposes. They do not function in any of the `Pattern` fields but will work in the display fields. These modifiers include `.number`, `.capitalize`, `.lower`, `.upper`, `.padleft`, `.padright`, and `.center`. Number will format number values based on region. For example, in the U.S., they will be formatted with commas. The other options are self-explanatory.

Example Usage when using Modifiers: `{S1.capitalize}`  `{n.number}`

### Padding and Centering Modifiers

The `.padleft`, `.padright`, and `.center` modifiers allow you to format text with padding for aligned display. These require a width argument specified after a colon.

- **`.padleft:width`** - Pads the value with spaces on the left to reach the specified total width
- **`.padright:width`** - Pads the value with spaces on the right to reach the specified total width
- **`.center:width`** - Centers the value within the specified width by adding spaces on both sides

Example Usage:
- `{S1.padleft:20}` - Left-pads the captured value to 20 characters total
- `{S1.padright:20}` - Right-pads the captured value to 20 characters total
- `{S1.center:20}` - Centers the captured value within 20 characters

These are useful for creating aligned columns in display text or when formatting output for overlays.

## `{C}`

Replaced by your character name. Use it in any `Pattern` field as well as all other fields that display, speak, or share information including timer warnings. Everything.

## `{S}`, `{Sx}`

Acts as a wildcard to capture values in `Pattern` fields. It will capture anything, including multiple words, and allow the value to be used later in any fields that displays, speaks, or shares information. Requires `Regex` to be enabled; internally, the parser replaces `{s1}` with `(?<s1>.+)`.

## `{N}`, `{Nx}`

Like `{S}` but captures numbers (no spaces or multiple numbers are allowed). Also, allows the value to be used later in any fields that displays, speaks, or shares information.

## `{N>y}`, `{y<N<z}`

Works like `{N}` but allows additional checks on the range of numbers that will match the trigger. Use operators like `>`, `<`, `>=`, `<=`, or `==`. Use `|` to combine. Example: `{100<=N<200}` to match numbers between `100` and `200`.

## `{L}`

Replaced by the line that triggered the event minus the date/time segment. Useful for testing and seeing the full line that matched. Available only in the `Text to Display`, `Text to Speak`, and `Alternate Timer Name` fields.

## `{LOGTIME}`

Replaced by the time from the line that triggered the event in the `hh:mm:ss` format. Useful if you want to know the time the trigger was fired. Available only in the `Text to Display`, `Text to Speak`, `Text to Send`, and `Alternate Timer Name` fields.

## `{REPEATED}`

This variable is replaced with the number of times the trigger has been repeated and has captured the same values that have been used to display or speak information. Available only in `Text to Display`, `Text to Speak`, and `Alternate Timer Name`. Example Text to Display: `{s1} {repeated}`. This will print the count of how many times the trigger is fired with the same value captured by `{s1}`. The count resets after `750ms` and this reset time can be configured by setting the `Repeated Reset Time` of the trigger.

## `{COUNTER}`

Similar to `{REPEATED}` but it counts the number of times the trigger has fired regardless of the variables captured and used in the display or speak information fields. This variable also uses the `Repeated Reset Time` field to specify the delay used to restart the count.

## `{TS}`

Like `{S}` but used to capture a timestamp in the format `hh:mm:ss` or any number which will be counted as seconds. Requires `Regex` to be enabled. Used only to dynamically set the `Timer Duration`.

## `{NULL}`

Used in any field that displays, speaks, or shares information to suppress the message entirely. Useful when overriding `Timer End Early` behavior. If `{NULL}` is set then nothing will be displayed or spoken.

## `{TIMER-WARN-TIME-VALUE}`

Replaced by the `Timer` setting for `Warn With Time Remaining`. This allows your Display/Speak messages to reference this `Trigger` configuration value if needed. More variables like this could be added in the future where configuration settings are made available when triggers run.

## `{EQLP:STOP}`

Not a trigger variable. You send this text as a say, to the group, raid, another player, or custom channel if you want your triggers to reload, overlays to close, and audio to stop. The chat you send needs to start with this code and it's limited to ensure that it came from you.

## `{EQLP:CLEAR}`

Not a trigger variable. You send this text as a say, to the group, raid, another player, or custom channel if you want to clear all custom variables, counters, and their expiry timers across all active trigger processors. Unlike `{EQLP:STOP}`, this does **not** stop audio or close overlays — it only resets variable state. Use this after zoning, changing zones, or when you want a clean slate for spell/zone tracking without interrupting active triggers.

---

## Creating Custom Variables (Trigger Variables Tab)

In addition to the built-in variables above, you can create **custom variables** that persist across trigger firings. This lets one trigger capture a value (like a caster name or spell being cast) and other triggers reference it later. To access this feature, open the **Trigger Manager**, select a trigger, and click the **Trigger Variables** tab. Each variable has the following settings:

### Action Type

**Set Value** stores a value in the variable. **Clear Value** removes it.

### Variable Name

A unique name for the variable, such as `gCaster` or `gSpellName`. It can be used in conditions and display text. Names beginning with `g` are conventional but not required.

### Data Type

- **Fixed** stores a specific text or numeric value.
- **Counter** stores a number that changes each time the trigger fires.

### Value

The value to store. This can be:

- A capture group, such as `{s1}`
- Another variable, such as `{otherVar}`
- A literal value, such as `Red Dragon`

### Initial Value

*Counter only.* The starting value when the counter is first created. The default is `0`.

### Step

*Counter only.* The amount added each time the trigger fires. The default is `1`. Use a negative value to decrement.

### Time To Live

*Optional.* The number of seconds before the variable expires and is automatically cleared. Set this to `0` for no expiration.

### Adding and Removing Variables

- Click the **+ Add Variable** button at the bottom of the tab to add a new variable card.
- Click the **Remove** button on any card to delete it.
- If all cards are deleted, a blank starter card is automatically added back.

### Examples

| Value | What Gets Stored |
|---|---|
| `{otherVar}` | The current value of another custom variable |
| `Red Dragon` | The literal text "Red Dragon" |
| `{s1} the Brave` | The captured value with appended text |

### Clearing Variables

Use **Clear Value** actions to remove a variable when a condition ends. For example:

- Trigger A matches "Player begins casting" → **Set** `gCasting = true`
- Trigger B matches "Player stops casting" → **Clear** `gCasting`

Other triggers can check `{gCasting}` in their Match Variables field to only fire while the player is actively casting.

### End Clear Variables (Timer Tab)

The **End Clear Variables** field (found under *Basic Timer Options* on the Timer tab) lets you specify a list of custom variables to clear automatically when a Timer ends — whether it expires normally or ends early via an End Early Pattern. This is useful for cleaning up temporary variables that were only needed while the timer was active.

Enter variable names separated by commas, spaces, or semicolons. The following formats are all accepted and resolve to the same variable name:

- `gCaster` — plain name
- `{gCaster}` — braces (same as display text syntax)
- `${gCaster}` — dollar-brace
- `gCaster, gSpellName; gZone` — multiple names with mixed separators

Variables are cleared **after** the End Text to Display and End Sound/Text to Speak are processed, so end display text can still reference the variable values. The cleanup happens once all timer-end side effects (display, speak, log) are complete.

**Example — Caster name that dies with the buff timer:**

1. Trigger matches "Player begins casting Spirit of Vesagran" → Set `gEpicCaster = {s1}`
2. Enable Timer, Duration: 3:00, End Text to Display: `BRD Epic ({gEpicCaster})`
3. End Clear Variables: `gEpicCaster`

When the 3-minute buff timer ends, the display shows the caster name and the variable is cleared.

---

## Match Variables Field

The **Match Variables** field (found under *Basic Trigger Options*) lets you add an extra gate on top of the Pattern match. Even if the Pattern matches, the trigger will **only fire** if the condition expression evaluates to `true`.

This is useful for situations like:
- Only fire when a captured HP value is above 50
- Only fire when a custom variable set by another trigger has a specific value
- Combine multiple checks with `and` / `or` logic

### Syntax Overview

A condition expression consists of **variables**, **literals**, **comparison operators**, and **boolean operators**:

```
{s} = hello
{hp} > 50
{name} contains dragon
({hp} > 50 and {mana} > 10) || {godmode} = true
```

### Variables

Variables are referenced using curly braces and resolve to their current string value. Both `{name}` and `${name}` syntaxes are supported:

| Syntax | Resolves To |
|---|---|
| `{s}` / `{s1}` / `{n2}` | Capture group values from the current Pattern match |
| `${s}` / `${s1}` | Same as above (dollar-sign prefix is optional) |
| `{hp}` | A custom variable named `hp` (set via the Trigger Variables tab) |
| `${hp}` | Same as above (dollar-sign prefix is optional) |
| `{target}` | A named regex capture group or custom variable |

If a variable is **not set** (never assigned a value), it resolves to `null`. In comparisons, `null` behaves as described in the operator table below.

### Comparison Operators

All comparison operators are **case-insensitive**.

| Operator | Aliases | Description | Example |
|---|---|---|---|
| `=` | `==`, `eq` | Equal to | `{name} = test` |
| `!=` | `<>`, `neq` | Not equal to | `{class} != druid` |
| `>` | `gt` | Greater than (numeric) | `{hp} > 50` |
| `>=` | `ge`, `gte` | Greater than or equal | `{level} >= 20` |
| `<` | `lt` | Less than (numeric) | `{mana} < 100` |
| `<=` | `le`, `lte` | Less than or equal | `{damage} <= 999` |
| `contains` | — | Contains substring (case-insensitive) | `{name} contains dragon` |

**Important notes:**
- Numeric comparisons (`>`, `<`, `>=`, `<=`) attempt to parse both sides as numbers. If either side cannot be parsed as a number, the comparison returns `false`.
- `contains` is case-insensitive. If the right-hand side resolves to `null` (unset variable), the result is `false`.
- `=` and `==` treat `null` values correctly: `{unsetVar} = null` is `true`, `{setVar} = null` is `false`.

### Boolean Operators

Combine multiple conditions using boolean logic:

| Operator | Aliases | Description |
|---|---|---|
| `and` | `&&` | Both sides must be true |
| `or` | `||` | At least one side must be true |
| `not` | `!` | Negates the following expression |
| `(` ... `)` | — | Groups expressions for precedence |

**Operator precedence** (highest to lowest): `not` → `and` → `or`. Use parentheses to override.

### Literals

You can compare variables against literal values:

| Type | Syntax | Example |
|---|---|---|
| **String (bareword)** | `hello` | `{name} = test` |
| **String (quoted)** | `"hello world"` or `'hello world'` | `{name} = "red dragon"` |
| **Number** | `42`, `-10`, `3.14` | `{hp} > 50` |
| **Boolean** | `true`, `false` | `{enabled} = true` |
| **Null** | `null` | `{target} != null` |
| **Empty string** | `""` or `''` | `{name} = ""` |

Quoted strings preserve spaces. Barewords cannot contain spaces.

### Standalone Variables

A variable by itself (no comparison) is treated as a boolean check:
- `{enabled}` → `true` if the variable is set and non-empty, `false` otherwise
- `not {disabled}` → `true` if `disabled` is unset or empty

### Error Handling

If the Match Variables field contains a syntax error (unclosed braces, unknown operators, mismatched parentheses), the condition is treated as **false** — meaning the trigger will **not** fire even if the Pattern matches. A warning is logged to the EQLogParser error log so you can identify and fix the issue.


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
7. Do no extract the **.tgf.gz** files onto your computer.

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
