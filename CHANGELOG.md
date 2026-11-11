# Changes in this fork

Hoff is a fork of [Charlie22911/FleetHacker](https://github.com/Charlie22911/FleetHacker), based on its 0.3.23-preview source. This file lists what the fork has changed. The downloads on the [latest release](https://github.com/bobathefetts/Hoff/releases/latest) are built from this source.

## 2026-11-11

Fixes from an external code review. Each finding was first checked against the code; these are the ones fixed today.

### Scanning

- **Only links are skipped.** A scan used to skip every file or folder that Windows marks as a reparse point. That includes deduplicated, tiered and archived files on file servers, so those files went unreviewed. Now only links are skipped: junctions, symbolic links and mount points, which can lead outside the folder being scanned, plus entries whose type cannot be read and two kinds that cannot be opened as files (app execution aliases and Unix sockets). Other reparse points are scanned like ordinary files. Reading a tiered or archived file makes the server fetch it, which can be slow.
- **Skipped links are reported, not counted as done.** A skipped entry was counted as a processed file, so the progress bar could show a scan as complete while files had been left out. Links now have their own count. The activity line and the finished scan say how many were not followed, the first 20 are named in the diagnostics log, and the scan summary gives the total. Windows' own hidden compatibility junctions in each profile (such as `My Documents` and `Documents\My Music`) point to folders the scan reaches anyway, so they are skipped without being counted.
- **Files stored elsewhere are not fetched just to check their type.** A cloud placeholder or a tiered or archived file with an unknown extension is no longer read to look for renamed media, because reading it downloads or recalls it. Files with a selected media extension are still scanned.
- **Files read and found not to be media always count as processed**, also when renamed-media checking is turned off, so the progress bar can reach 100%.
- Scans started before this change keep their old results when resumed. Start a fresh scan to pick up files an earlier scan skipped.

### Safety

- **Videos open only when you press Play.** Selecting a video in Quick Review (after a third of a second) or opening it in Inspect used to load it at once through Windows Media Foundation, inside the main app, on a file from the scanned drive. Now only the saved poster, the controls and a ▶ badge appear until you press Play.
- **A DLL placed beside the exe is never loaded.** Windows used to look in the exe's own folder first for some system DLLs, such as `bcrypt.dll`, so a file with that name in the same folder (for example in Downloads) ran inside the app. The exe now loads its imported DLLs, and any DLL loaded by name later, from System32 only.
- **HTML reports leave thumbnails out unless asked.** The "embed thumbnails" box in **Export review report** now starts unticked, and its label says that anyone who opens the report sees the pictures.
- **New originals passwords need at least 12 characters** in the Windows app, both when the vault is created and when the password is changed. The browser edition is unchanged. The password window now says why nothing happened (empty, too short, or the two entries differ) instead of staying open silently. Existing shorter passwords still unlock.

### Messages and documentation

- Error messages name this app (`Media Compliance Reviewer.exe`, or whatever the exe is called) instead of `FleetHacker.exe` or a `MediaReviewer.Decoder.exe` that does not exist, and the AI-worker message says it may have crashed, run out of memory or been blocked by security software.
- The README explains how to make the Marqo model file, with the exact Python and package versions and the size and SHA-256 to check.
- The README, the browser README and the browser edition's start screen advise opening the browser edition in a browser profile kept only for reviewing, because other HTML files opened from disk in the same profile can read its saved data.
- Two unused browser files (`media-database-bridge.js`, `media-database-host.js`) were removed. They were never part of the built HTML.

### Tests

- The self-test checks the link rule against real junctions (including a Windows-style compatibility junction) and the reparse types used for deduplicated, tiered, compressed, cloud and DFS entries, the new-password rule, and that HTML reports embed thumbnails only on request.
- The first-scan window test checks that a video selected in Quick Review or opened in Inspect is not opened before Play.
- `tests\Smoke.ps1` runs the window test from a folder holding copies of System32 DLLs and fails if any of them is loaded from there, and fails if a message names the wrong program.

## 2026-11-10

### Desktop app: everyday use

- **No startup prompt.** The "Welcome to Media Review" window is gone. The first time you press **Start scan**, the app creates a default catalog at `%LOCALAPPDATA%\Media Compliance Reviewer\Media Review Catalog.sqlite`, keeps the folder and options you chose, and starts the scan. Later launches reopen that catalog. **Open or create catalog** in the sidebar still opens any other catalog.
- **The top item is highlighted for you.** When the list fills, its first item is selected. After **Looks good** or **Flag item**, the next item is selected, so a run of items can be reviewed without clicking each one.
- **The highlighted row is readable in every theme.** The list draws its own highlight instead of the Windows default.
- **The page box keeps what you type.** While a scan was running, the list refresh put the current page number back into the box about once a second, so a new number could not be entered. A refresh now leaves the box alone while you type, including between the last key and the click on **Jump**. Clicking into the box selects the old number so the new one replaces it, Enter works like **Jump**, and a page that does not exist stays in the box, selected, with the reason shown.

### Desktop app: fixes from code review

- **Reconnect all sources** no longer binds sampled-hash "quick candidate" matches on its own. They are bound only when **Also accept quick candidate matches** is ticked, or confirmed when resuming. A candidate already bound for the same unchanged file is kept, and a check applied by hand in Sources is never rewritten.
- **ZIP scanning checks each member on its own.** A member that is encrypted, compresses more than 100:1, exceeds 512 MiB or is a nested archive is recorded as a failed file with its reason, and the other members are catalogued. At most five such members and one summary line are logged per archive.
- Renamed media inside a ZIP is detected from its header.
- **Undo:** a step that can no longer apply because the item was edited afterwards is dropped instead of blocking every later Ctrl+Z.
- Command-line mode works from a UTF-8 console (code page 65001).
- Errors on the UI thread are logged and the session continues. Background operations survive a failure while publishing a result.
- Thumbnails are requested again after the view changes, and R no longer rotates a video in Quick Review.
- Password strings and byte arrays are cleared in the unlock dialogs.
- Every SQLite connection sets `trusted_schema=OFF`.
- The video decoder initialises its rotation output.

### Browser build (the offline HTML file)

- Cancelling an Evidence capture no longer deletes the content record.
- Catalog root paths are loaded automatically only from a drive letter, never from a UNC file URL.
- **Quick Review:** dragging a zoomed picture pans it and never records a decision, a second finger cancels the gesture, and the notes box is cleared after each decision.
- **Choose folder** goes through the scan dialog.
- The main catalog opens with `trusted_schema=OFF` and `cell_size_check=ON`.

### Build and tests

- `build.ps1` takes `-Distribution` for a WSL distribution with another name, and the project file pins C# 5.
- The built-in self-test covers the ZIP, reconnect and undo changes, and checks that reviewed items stay reviewed after the catalog is reopened and the folder is scanned again.
- A new window test (`--first-scan-smoke`, run by `tests\Smoke.ps1`) presses **Start scan** with no catalog open, reviews three items in a row using only the automatic highlight, and types into the page box while the list refreshes.
- The window tests never create or open the real default catalog, and wait for held modifier keys to be released.
- In `browser`, `npm test` runs the build, the packaging check, and the Chrome and Firefox tests. The Firefox test covers the Quick Review drag.
- `tests\VideoPlayback.ps1` passes the constructor's third argument.
- `.gitignore` excludes model files and IDE build output.
