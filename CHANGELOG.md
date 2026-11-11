# Changes in this fork

Hoff is a fork of [Charlie22911/FleetHacker](https://github.com/Charlie22911/FleetHacker), based on its 0.3.23-preview source. This file lists what the fork has changed. The downloads on the [latest release](https://github.com/bobathefetts/Hoff/releases/latest) are built from this source.

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
