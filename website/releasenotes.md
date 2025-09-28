# 2.3.21 | 09/28/25
**!!NOTICE!!** Text Overlays were changed to use **Normal** Font Weight in v2.3.0. You may need to change yours back to **Bold** manually.

1. Added **{timer-warn-time-value}** as a new Trigger variable. When specified it will be replaced with the configured timer **warning time** setting in any Display Text/Speak field where it is used.
2. Added **Private** setting to Triggers. Prevents accidental sharing via **Quick Share**.
3. Added multi-select **Classes** dropdown to **Player Benchmark**.
4. Added damage labels to **Player Benchmark**.
5. Performance improvements when using **End Early Timers**.
6. Updated to latest Syncfusion 31.1.17 libraries.

# 2.3.20 | 09/20/25
1. Updated **Classes** dropdowns on Summary Tables to allow multi-select.
2. Improved handling of audio devices being added/removed at runtime.
3. Fixed bug with TTS playback where audio could sometimes be corrupted.

# 2.3.19 | 09/14/25
1. Small update to loading voices during startup.

# 2.3.18 | 09/14/25
1. Added new option for **Timer Overlays** called **Hide Dupes**. This tries to hide duplicate timers when you have multiple characters configured to respond to the same exact thing. **Warning** This could potentially slow things down if used during a raid so use with caution. For any timer that has this problem have them go to a special overlay configured with this setting and try to limit the number of timers if possible.
2. Added a **Clear Logs** button to the Trigger Log.
3. EQLP now hides **Windows TTS Voices** that aren't working on your system.
4. Performance update to help when many characters are setup for **Triggers**.
5. Fixes for **Log Viewer** related to handling Regex.

# 2.3.17 | 09/02/25

1. More fixes for looted items view.

# 2.3.16 | 09/01/25

1. Added new **View Quick Share Stats** to the Quick Share window as well as the new Quick Share dialog. When clicked you'll be taken to a web page that displays the Quick Shares your parser currently knows about along with the number of times each key has been used or **Downloaded**. This may help during a raid to see if people have actually merged your new shares.
2. Changed **GINA** timer import to no longer select Fast Countdown.
3. Improvements to how looted items are recorded.
4. Fix for being unable to show a hidden windows task bar when maximized.
5. Fix for the top 3 and top 10 options in the **Healing Breakdown** not always showing the correct results.

# 2.3.15 | 08/07/25

1. Fixed some window sizing problems when maximized. The app should no longer go under the task bar and won't have weird padding around the window when maximized.
2. **Note** This may be the last update for a little while.

# 2.3.14 | 08/05/25

1. Added the ability to search the **Log Archive** from Log Search based on the selected player or current file and made some small UI improvements. Note that if you point the log archive configuration at a GINA archive folder it will search those existing files even if you don't use the EQLP archive feature.
2. Added better error handling for copying things to the clipboard. Hopefully, Quick shares should work now even if access is denied.
3. Added option to **Healing Breakdown** to limit players/spells shown.
4. Further reduced app start up time and improved splash screen updates.
5. Fixed bug where changing healing options didn't auto refresh the summary.
6. Fixed bug for **Not All Damage Opts Chosen** not showing for damage shields.
7. Updated the proc list with some missing spells.

# 2.3.13 | 08/01/25

1. **Renamed** windows under the **DPS/Healing/Tanking Menus** and added **Player Benchmark**. It is a new column chart for comparing players and is a work in-progress.
2. Updated layout serialization to try and avoid saving the layout if the app never leaves a minimized state. This should prevent the correct saved state from being lost.
3. Changed the default **Custom Active** and **Font** colors to white in the Trigger Manager character configuration. This is to help avoid accidentally hiding overlay text.
4. Implemented workaround to allow floating windows to be resized.
5. Updated option to disable the Damage Meter to also clear all data.
6. Fix for missing window border when starting maximized.
7. Fix for **Hit Frequency** chart range slider being partially hidden.
8. Moved software update check to its own thread to improve startup speed.
9. More code cleanup and small performance related improvements.

# 2.3.12 | 07/26/25

1. Improved caching of audio files when played by triggers.
2. **Performance** updates for log search.
3. Updated log search to allow searching of configured characters from Trigger Manager if advanced mode is enabled.
4. Log search will now use **Regex** automatically based on what is typed into the search box.
5. Updated the installer to cleanup unused/old files and to reference the most recent version of the .Net 8 Desktop run-time.

# 2.3.11 | 07/24/25

1. Additional **Performance** improvements to lower overall CPU usage when changing tabs and moving around the parser windows.
2. Added **Enable Chat Archive** to the Options menu. Uncheck this if you really don't want to use the archived chat feature.
3. Modernized the chat archive process with updated timers and async processing to avoid potential performance problems.
4. Updated Trigger and Overlay folder tree right-click menu to keep multiple items selected, closer to how it works in Windows, and prevented **Rename** from being enabled when mutliple items are selected.
5. Added **Import Warning** as orange highlighting on Triggers in the folder tree when they come from GINA with missing wav files.
6. Fix for Trigger Search failing in some cases.
7. Updated to latest Syncfusion library version 30.1.41.

# 2.3.10 | 07/22/25

1. **Performance** made improvements during app startup, most noticeble when auto monitoring log files and loading large verified player lists.
2. **Performance** updates to retune UI thread activity to try and lower CPU usage and replaced busy indicators on Trigger Manager with simple animated gifs as they used way more CPU than a busy indicator should.
2. Fix for **Spell Cast Order** not displaying seconds correctly.
3. More **UI updates** to windows that have Save/Close options for consistency and hopefully make things less confusing.

# 2.3.9 | 07/19/25

1. Added **Export CSV with Formatted Values** to the Options menu. It is on by default and causes CSV exports from the summary windows to display numbers using your local format. For example, 4,334,220.30. If this option is turned off it will export without formatting: 4334220.30
2. Fix for the **Create Backup** dialog not displaying properly.
3. Fix for **Clear Chat Archive** not displaying players/servers if the server name was too short. Should only have been impacting some EMU servers.
3. Made one last set of tweaks for restoring window position.

# 2.3.8 | 07/18/25

1. Added a **Repeated Threshold** to the trigger **Timer End Early Settings**. If you use `{repeated}` or `{counter}` to count the number of times a timer has been triggered, you can now end the timer early when the count reaches the specified value. When this happens, the count will also reset to zero even if the **Repeated Reset Time** hasn’t been reached.
2. Added **Remove All** option for overlays under the **Update Selected Triggers** menu.
3. Fixed a bug when using the **Update Selected Triggers** menu on a folder. It would often require two attempts to work and printed errors in the log file.
4. Fix that should help with saving EQLP in a maximized state on a second monitor.
5. Updated the description for each trigger property/field.

# 2.3.7 | 07/16/25

1. Added right-click menu to the **EQLogParser icon** in the system tray for Exit, Minimize/Restore, and About.
2. **Removed** the Trigger Variable section from the main Help menu and moved it to a button above where you define triggers so it's harder to miss.
3. Added some basic Regex info to the online docs.
4. Fixed bug where the parser would start very small instead of the last saved size.
5. Fixed some tooltips in Trigger Manager.

# 2.3.6 | 07/15/25

1. Updated to save active tab and try to switch to it at startup.
2. Updated to save window position when the window is moved and not just resized.
3. Added **Raid Time** to Parse Preview and cleaned up the options and labels.
4. Added a stop button to Trigger Manager that works the same as sending {EQLP:STOP}.
5. Fix for **{EQLP:STOP}** to fully remove active timers from all overlays.
6. Renamed **Close Overlays** to **Hide Overlays** as it's only intended to hide them until new data comes in. It does not remove text or timers from your Overlays.
7. Fix for the **Auto Enable** checkbox on the receive quick share dialog. If it is not checked then triggers will import as disabled.
8. Updated the search icon on the **Log Search** window to have pointer on hover to make it more obvious that it is clickable and to have color that's not usually used for a label or title.

# 2.3.5 | 07/13/25

1. Fix for **DPS Summary** loading incorrect times for some players on initial selection.
2. Updated List option formatting for sending parses to work better with Discord.

# 2.3.4 | 07/13/25

1. Added **Inline and List** options to Parse Preview. If you select **List** the selected parse will display with one player per line instead of one long string. This may be useful for pasting parses in Discord or other areas as it's easier to read. **Inline** is what it's always done.
2. Added **Time Interval** to Tanking Summary and Healing Summary similar to DPS Summary. Needed a good amount of re-work so **look out of any issues** when using this.
3. Included **Specials** with Healing and Tanking parses if enabled.
4. Updated **Non-Melee** Tanking Summary option to not count absorbed as non-melee hits.
5. Fix for shared triggers not updating.
6. Added note to character trigger config related to active and font color overrides.
7. Cleaned up some UI issues with the splash screen.

# 2.3.3 | 07/09/25

1. Enabled renaming of **Tabs** so you can organize multiple breakdowns easier. Restarting the app will reset the names. Double click on the tab to rename.
2. Fix for receiving **Quick Shares** not working in some cases.

# 2.3.2 | 07/06/25

1. Added **Custom Volume** to character configuration when using **Advanced Mode** in Trigger Manager. This value will override the global setting so each character can have their own volume. If triggers are modifying the volume as well the adjustment will be applied to the character volume.
2. Fixed bug when turning on/off triggers while they're still being processed could give errors. (may still happen in rare cases)

# 2.3.1 | 07/06/25

1. Perfomance improvements that should help with high usage of TTS.
2. Added **% Acc** and **% Hit** to the Damage Summary.
3. Added **Show Damage Percent** to the Damage Meter. It's just the percent of damage the player has done compared to the total. Where it displays may change.
4. Testing new build/release process.

# 2.3.0 | 07/06/25

1. Started 2.3.x and updated branding. New official site: **http://eqlogparser.kizant.net**
2. Releaste Notes and Trigger Variable docs are now hosted on the new site.
3. Hardware Acceleration is disabled under **Linux** as it unstable with WPF apps. Note that the WINELOADER environment variable is used to detect WINE.
4. Changed **{counter}** to work like GINA version. **{repeated}** remains unchanged. 
5. Fixed **{repeated}** when used with Alternate Timer Name. It was incorrectly counting when variables were used.
6. Added **{logtime}** that can be specified in Text to Display, Text to Speak, Text to Send, and Alt Timer Name fields. It will display the time from the log that caused the trigger to fire in the hh:mm:ss format.
7. Added **Font Weight** for Overlays to specify bold, thin, etc.
8. Added **Horizontal Alignment** to Text Overlays. Display text on the Left, Center, or Right.
9. Added **Close Pattern** to Text Overlays to allow a pattern from the log to close and clear the window. It is very simple and does not support any special variables like trigger patterns.
10. Changed **Fade Delay** to Text Overlays to allow values up to 99999 seconds.
11. Updated **Close Overlays** button to also clear Text Overlays.
12. {EQLP:STOP} is no longer case sensitive.
13. Added **Start with Window Minimized** to the Options menu.
14. Fixed parsing bug related to npcs names that contain the word hit. (again)

# 2.2.x

* **Summary and Timelines.** Added a way to manually assign classes. In addition, if a persona change is seen in the log it will attempt to assign the class to the new persona. Timelines now support drag and drop, zooming with Control+ and mouse wheel. Export formats are also now consistent pretty much everywhere.

* **Trigger Manager.** Overlays were re-worked to improve rendering performance and fix some bugs related to timers getting stuck. Many new trigger settings were added including timer icons, font color and active color overrides per trigger or per character, custom volume, matching against previous lines, and discord web hooks are now supported. Continued to improve support for importing from GINA. Implemented **Quick Share** for Overlays and **Trusted Player** list so that things can be auto-merged. Advanced mode now has a status to show if a log is active or missing. Implemented a search function so that you can find triggers based on the trigger name or pattern. Included support for mp3 files and older voices using the windows SAPI implementation may now be used.

* **New Features.** Added Create/Restore Backup so that settings may be saved and restored at a later time. This also includes a **BackupUtil.exe** in the EQLogParser directory so that a backup can be created from the command line. Impemented a **Wav Creator** so that you can generate text to speech using any of the configured voices and save the results to a file. Added a **Splash Screen** to show loading status as well as errors if they happen and a link to open the error log. Implemented better parsing support for **EMU** servers.

* **Linux Support.** EQLogParser now works completely with linux using WINE and I've included an optional text to speech engine that can be used as WINE 64bit does not support windows TTS.

* **.NET 8.0 Desktop Runtime.** Version 8.0.11+ of .NET is now required to run EQLogParser. This includes performance improvements and many features to help with development. It is supported until 2026 but expect a move to .NET 10 toward the end of 2025.

* **Improved Install Process.** Switched to using Inno Setup and an .exe based installer. Desktop icons no longer get moved/replaced and there are no longer problems with some install files not being updated. No longer need to 'repair' the install every so often when it stops working.

# 2.1.x

* **Log Management features.** It is available under Tools -> Log Management. Check the box to enable archiving, choose a folder for the archived files, select options from the two dropdowns. It will attempt to archive files that meet those criteria whenever the parser is started, whenever you add/enable a character's triggers or when you open a file to display a parse. Note that it will not archive randomly during a raid and it will wait for the parse to finish loading before attempting to archive.

* **Restore Open Views.** The parser will remember and re-open many of the application's windows/tabs upon restart. This appliies to everything under the View menu except for log search.

* **Look and Feel Menu Item under Options.** It has settings for Font Size, Font Family, and the previous Themes. Only a limited number of options are available at this time and a number of changes were made throughou the UI for things to adjust sizes better. Hopefully all the current combinations of Font Size/Family work well.

* **Audio Triggers and Text/Timer Overlays are fully supported.**
    * Look under View -> Triggers. 
    * Trigger Manager is for creating triggers and overlays. You can also import from GINA trigger files. See the triggerVariables document for differences.
    * Triggers can be shared with others via Quick Share where a key can be sent to someone else in game and it will download and install the triggers.
    * Added Phonetic Dictionary option to Trigger Manager. It's very simple as the TTS engine doesn't support anything too useful. However, you can specify a word to replace with another word or phrase by adding rows to the dictionary table. Adding too many entries could potentially slow processing so look out for that.
    * Quick Share Log is a table of all received quick shares as well as any that it sees in the log file that you load.
    * Trigger Log is a table that gets updated when triggers are fired. 
    * Trigger Tester allows you to paste log lines into a list and Run them as if you were playing. Then your triggers and overlays should fire and display. The real-time option takes timestamps into account and attempts to delay each line by the appropriate amount.
    * The Tools menu has an option for Opening the Trigger Sounds folder. You can add new .wav files there and they should show up as options in Trigger Manager.
    * If you created triggers in previos versions it's probably best to start over.

* **New Damage Meter.**
    * It has it's only menu under View -> Damage Meter.
    * The UI has been updated with a modern look and feel.
    * A Tank tab is available to view similar damage information for Tanks. It does not need to be configured. You can click back and forther between DPS and Tank while it's running.
    * There's a new optoin for smaller damage percent bars to allow the Overlay to use a little less space.
    * Added a Streamer Mode checkbox to the Damage Meter. If this is checked the meter will show up as a window when you alt-tab and it should also be visible/selectable in OBS

* **Added Help Menu.**
    * One option allows you to view these release notes.
    * Reporting an Issue takes you to the Github page for reporting bugs.
    * The About option takes you to the README on Github.