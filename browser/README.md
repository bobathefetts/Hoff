# Media Compliance Reviewer WebAssembly

Download the `.WebAssembly.html` asset from [the latest Hoff release](https://github.com/bobathefetts/Hoff/releases/latest) and open it in desktop Chrome, Firefox, or Microsoft Edge. The file includes SQLite and media preview decoders compiled to WebAssembly. It runs directly from disk and processes selected files in the browser.

**Keep a separate browser profile for reviewing.** The browser keeps its working copy of the catalog, including saved previews, notes and file paths, in local storage that every HTML file opened from disk in the same browser profile can read. This was confirmed in Chrome and Firefox. Open the reviewer in a profile used only for reviewing, and do not open other downloaded HTML or SVG files in that profile.

## Start a browser review

Choose **Create database** or **Open existing database**, then choose a folder to scan. The browser asks for access to each source folder and database. Use **List**, **Gallery**, or **Quick Review** in the sidebar. In Quick Review, use the left and right arrow keys to move between items, and the up and down arrow keys to mark them Compliant or Non-compliant. The same gestures work with a mouse drag or touch swipe. Use the edge buttons to collapse the sidebar, scan controls, and preview. Drag the preview divider to resize it.

Choose **Save database** from the gear menu to keep a portable copy. Inspect shows a saved preview if an original is unavailable. When the browser supports the original video's codec, Inspect and Quick Review offer playback, seeking, volume, and playback speed. In Quick Review, choose **Open video** to switch from the saved poster to the player. Choose **Full screen** to expand either view.

Chrome and Edge can save back to a database file when you grant write access. Firefox works in browser storage and **Save database** downloads a SQLite copy. Select the folder again if Firefox needs access to originals after reopening. Features that write directly to an output folder, such as bulk Evidence export, require a browser with writable folder access.

Browser catalogs use the browser reviewer's SQLite format. The Windows reviewer can import catalogs from the final HTML schema. Use a separate database file for each edition.

## View a Windows catalog

1. In the Windows reviewer, choose **Maintenance → Portable backup**.
2. Open the browser edition and choose **View Windows backup**.
3. Select the backup. Choose a bucket to see saved previews, reviewer notes, and recorded locations.

The viewer reads Windows catalog schema 13 backups directly and opens them read-only. It displays the first 100 recorded locations per item. Windows Evidence, retained originals, Windows scan resume, and local AI review are available in the desktop edition.

## Build from source

Install Node.js 24. Run the following commands from `browser/` in the FleetHacker checkout or from the extracted browser source archive. The build uses the bundled browser sources and pinned WebAssembly runtimes. The base sources come from the [Media Compliance Reviewer source snapshot](https://github.com/Charlie22911/Media-Compliance-Reviewer/tree/4a3cb7d6735e10c1746836b06e2e553c434b8878).

```sh
npm install
npm run build
npm run check
```

The output is written to `../dist/Media Compliance Reviewer WebAssembly.html`. The build limits embedded script, library, and decoder payload lines to 1,024 characters and keeps ordinary HTML formatting. It reconstructs packaged scripts locally when the page opens. Third-party notices are in `base/NOTICE.md` and the application's About dialog.

The browser source includes a snapshot of the Windows theme palettes, catalog identifiers, and draft-review configuration. In the full project checkout, run `npm run sync:desktop` in the browser source folder to refresh it after changing those definitions. The build checks the snapshot against the Windows sources when they are present and requires an explicit backup adapter update for a new Windows catalog schema.

Run `node test-browser.cjs` for offline startup, fullscreen, and read-only backup browsing in Chrome. Pass a Windows fixture catalog and a generated MP4 as arguments to also check playback in Inspect and Quick Review. Set `MEDIA_REVIEWER_TEST_BROWSER` to the Edge executable to run the same checks in Edge. Run `node test-firefox.cjs` with Mozilla’s geckodriver on PATH to verify Firefox’s create, scan, review, download, and reopen workflow. Set `MEDIA_REVIEWER_GECKODRIVER` to use an explicit driver path.
