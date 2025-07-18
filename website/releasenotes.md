# 2.3.8

**!!NOTICE!!** Text Overlays were changed to default to **Normal** Font Weight in version 2.3.0. You may want to switch yours to **Bold** if you prefer how it used to work.

1. Added a **Repeated Threshold** to the trigger **Timer End Early Settings**. If you use `{repeated}` or `{counter}` to count the number of times a timer has been triggered, you can now end the timer early when the count reaches the specified value. When this happens, the count will also reset to zero even if the **Repeated Reset Time** hasn’t been reached.
2. Added **Remove All** option for overlays under the **Update Selected Triggers** menu.
3. Fixed a bug when using the **Update Selected Triggers** menu on a folder. It would often require two attempts to work and printed errors in the log file.
4. Fix that should help with saving EQLP in a maximized state on a second monitor.
5. Updated the description for each trigger property/field.

# 2.3.7

1. Added right-click menu to the **EQLogParser icon** in the system tray for Exit, Minimize/Restore, and About.
2. **Removed** the Trigger Variable section from the main Help menu and moved it to a button above where you define triggers so it's harder to miss.
3. Added some basic Regex info to the online docs.
4. Fixed bug where the parser would start very small instead of the last saved size.
5. Fixed some tooltips in Trigger Manager.

# 2.3.6

1. Updated to save active tab and try to switch to it at startup.
2. Updated to save window position when the window is moved and not just resized.
3. Added **Raid Time** to Parse Preview and cleaned up the options and labels.
4. Added a stop button to Trigger Manager that works the same as sending {EQLP:STOP}.
5. Fix for **{EQLP:STOP}** to fully remove active timers from all overlays.
6. Renamed **Close Overlays** to **Hide Overlays** as it's only intended to hide them until new data comes in. It does not remove text or timers from your Overlays.
7. Fix for the **Auto Enable** checkbox on the receive quick share dialog. If it is not checked then triggers will import as disabled.
8. Updated the search icon on the **Log Search** window to have pointer on hover to make it more obvious that it is clickable and to have color that's not usually used for a label or title.

# 2.3.5

1. Fix for **DPS Summary** loading incorrect times for some players on initial selection.
2. Updated List option formatting for sending parses to work better with Discord.

# 2.3.4

1. Added **Inline and List** options to Parse Preview. If you select **List** the selected parse will display with one player per line instead of one long string. This may be useful for pasting parses in Discord or other areas as it's easier to read. **Inline** is what it's always done.
2. Added **Time Interval** to Tanking Summary and Healing Summary similar to DPS Summary. Needed a good amount of re-work so **look out of any issues** when using this.
3. Included **Specials** with Healing and Tanking parses if enabled.
4. Updated **Non-Melee** Tanking Summary option to not count absorbed as non-melee hits.
5. Fix for shared triggers not updating.
6. Added note to character trigger config related to active and font color overrides.
7. Cleaned up some UI issues with the splash screen.

# 2.3.3

1. Enabled renaming of **Tabs** so you can organize multiple breakdowns easier. Restarting the app will reset the names. Double click on the tab to rename.
2. Fix for receiving **Quick Shares** not working in some cases.

# 2.3.2

1. Added **Custom Volume** to character configuration when using **Advanced Mode** in Trigger Manager. This value will override the global setting so each character can have their own volume. If triggers are modifying the volume as well the adjustment will be applied to the character volume.
2. Fixed bug when turning on/off triggers while they're still being processed could give errors. (may still happen in rare cases)

# 2.3.1

1. Perfomance improvements that should help with high usage of TTS.
2. Added **% Acc** and **% Hit** to the Damage Summary.
3. Added **Show Damage Percent** to the Damage Meter. It's just the percent of damage the player has done compared to the total. Where it displays may change.
4. Testing new build/release process.

# 2.3.0

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