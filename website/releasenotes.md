# 2.3.0

*	**Hardware Acceleration** is now always disabled under Linux/WINE as it makes WPF apps unstable. The WINELOADER environment variable is used to detect WINE.
*	Changed **{counter}** to work like the GINA version. Any use of `{repeated}` remains the same as it has been. Also, fixed `{repeated}` when used within an `Alternate Timer Name`. It was incorrectly counting if other variables were in the name.
*	Added **{logtime}** variable that can be specified in `Text to Display`, `Text to Speak`, `Text to Send (Discord)`, and `Alt Timer Name`. It will display the time from the log line that causes the trigger to fire in the `hh:mm:ss` format.
*	Moved docs to the web so they can be updated without downloading a new installer.
*	**{EQLP:STOP}** is no longer case sensitive.
*	Fix parsing bug related to npcs names that contain hit. `(again)`

# 2.2.x

* **Summary and Timelines.** Added a way to manually assign classes. In addition, if a persona change is seen in the log it will attempt to assign the class to the new persona. Timelines now support drag and drop, zooming with Control+ and mouse wheel. Export formats are also now consistent pretty much everywhere.

* **Trigger Manager.** Overlays were re-worked to improve rendering performance and fix some bugs related to timers getting stuck. Many new trigger settings were added including timer icons, font color and active color overrides per trigger or per character, custom volume, matching against previous lines, and discord web hooks are now supported. Continued to improve support for importing from GINA. Implemented **Quick Share** for Overlays and **Trusted Player** list so that things can be auto-merged.

       * Advanced mode now has a status to show if a log is active or missing. Implemented a search function so that you can find triggers based on the trigger name or pattern. Included support for **mp3** files and older voices using the windows **SAPI** implementation may now be used.

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