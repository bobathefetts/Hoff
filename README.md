# FleetHacker Media Compliance Reviewer

Media Compliance Reviewer for Windows is an offline C#/.NET desktop tool for finding and reviewing images and videos in large folder collections. The light and dark WPF workspace has List, Thumbnails and Quick Review views, a review sidebar, and a preview pane.

## Downloads

- [Download for Windows (.exe)](https://github.com/bobathefetts/Hoff/releases/latest/download/Media.Compliance.Reviewer.exe)
- [Download the offline browser preview (.html)](https://github.com/bobathefetts/Hoff/releases/latest/download/Media.Compliance.Reviewer.WebAssembly.html)

These links always fetch the newest build of this fork (Hoff). The [release page](https://github.com/bobathefetts/Hoff/releases/latest) includes both source archives, and [CHANGELOG.md](CHANGELOG.md) lists what the fork has changed since the original [FleetHacker 0.3.23-preview](https://github.com/Charlie22911/FleetHacker/releases/tag/v0.3.23-preview). See the [FleetHacker overview presentation](docs/FleetHacker-Overview.pptx) for the review workflow and an example of how verified matches reduce repeat work.

Made by IT1 Charlie McPherson, IT1 Joshua Maulden, and IT2 Alexander Harris. **Made at FleetHacker 2026**.

Thanks to Jeremy Gerdes and the Phoenix LAI contributors for their work on local Gemma and Vulkan on Windows. [Phoenix LAI](https://github.com/fleethacker-data-crucible/phoenix-lai), [MIT license](https://github.com/fleethacker-data-crucible/phoenix-lai/blob/ca1b88a99bc5825c74d9ad8018422b1bc06df968/LICENSE).

Open `Media Compliance Reviewer.exe` directly on x64 Windows with .NET Framework 4.7.2 or later. Copy the executable to use the catalog and manual review tools. SQLite, FFmpeg, dav1d, OpenCV and llama.cpp are statically linked. The application uses Windows-supplied WPF and Windows Imaging Component. Review databases and their SQLite sidecars live at the selected local destination. Optional ZIP previews can use a temporary media file.

Open the downloaded HTML directly in desktop Chrome, Edge, or Firefox. It provides offline scanning and manual review in List, Gallery, and Quick Review. The browser uses its own catalog format and opens Windows portable backups read-only. Use the Windows build for AI draft review and access to Evidence stored in Windows catalogs. [Browser usage and build instructions](browser/README.md)

## Download Gemma for advanced AI review

**Advanced AI review with Gemma 4 E2B requires both files below.** Download them separately and place them in the same folder as the FleetHacker executable on a local drive. Keep the original filenames. The pair is about 3.4 GB and FleetHacker checks both files when it loads the model.

- [Download `gemma-4-E2B-it-Q4_0.gguf` (language model, 2.84 GB)](https://huggingface.co/ggml-org/gemma-4-E2B-it-GGUF/resolve/6938c58e698502037401a5f19f6905f2fb2f7dee/gemma-4-E2B-it-Q4_0.gguf?download=true)
- [Download `mmproj-gemma-4-E2B-it-Q8_0.gguf` (vision projector, 557 MB)](https://huggingface.co/ggml-org/gemma-4-E2B-it-GGUF/resolve/6938c58e698502037401a5f19f6905f2fb2f7dee/mmproj-gemma-4-E2B-it-Q8_0.gguf?download=true)

## Open a catalog and scan

1. There is no startup prompt. The first time you press **Start scan**, the app creates a default catalog at `%LOCALAPPDATA%\Media Compliance Reviewer\Media Review Catalog.sqlite` and reopens it on later launches. To use a different catalog, choose **Open or create catalog** in the sidebar: select an existing SQLite catalog to open it, or enter a new filename on a local drive to create one. When opening a final HTML database, choose a new local filename for the Windows copy.
2. Enter a local folder, mapped drive or UNC media share under **Source root**. In **Maintenance**, use **Use current source path as default** to save it in the database. You can temporarily enter a different path on another machine. Use **Reconnect all sources** to verify a replacement root for previously recorded locations.
3. Start or resume a scan, then choose a review queue from the sidebar or compact **View** dropdown. New catalogs scan root-level media and check headers on ordinary files with unknown extensions by default. Change either setting or enable full SHA-256 under **Options**. Saved catalog preferences and resumed scan settings are preserved.
4. Click a List column to sort the current queue or choose **Thumbnails** for an adjustable grid. Double-click an item to open **Inspect / video** without changing the gallery view. Choose **Quick Review** in the sidebar to review one item at a time. Swipe or drag left for the previous item, right for the next item, up for Compliant, or down for Non-compliant. The arrow keys do the same when the image is focused. A cue appears during a drag when the gesture is ready. Quick Review advances when the decision is saved. Press **C** or **N** outside an editor for the corresponding decision in any view.
5. Use **Details / notes** or **Inspect** to open an item while scanning. Scroll over a photo to zoom and drag to pan when zoomed. In **Inspect / video** and **Quick Review**, move the pointer over the media to reveal playback controls. You can play, pause, stop, seek, skip ten seconds, change volume, mute, or cycle speed from 1× to 2×. Click the video or press **Space** with the preview focused to play or pause. Playback starts on demand. Windows-supported videos prepare in a paused state after selection. Inspect and Quick Review keep the saved poster visible until playback starts and show a message when there is no poster. Stop playback or leave the item to release the source file. Use the vault to retain originals and capture Evidence.

The compact command area keeps scan controls, search and classification visible. Expand **Scan settings** for file types, workers, age exclusions and other scan options. Expand **Filters and display** for folder/type filtering, sorting and Fit/Fill. **Reviewer and notes** holds the label and notes saved with decisions; its heading indicates when notes have been entered. **More review actions** groups preview retries and identity checks separately from retained originals and Evidence tools. Collapsing a section preserves its values and the current selection. Press Enter in Search or User folder, or choose Apply, to apply the filters. Expanded controls scroll within a limited area on shorter windows.

FLV and WebM scan as videos and get saved posters. Animated GIFs stay in the image categories and play on a loop. Press **Play** to prepare an offline playback copy for these formats. The source and playback copy are each limited to 256 MiB. Stop playback or leave the item to release the temporary copy.

Playback preparation runs separately from exports and other long catalog tasks. Inspect also has its own decoder so it can load images while scan previews are being created. Inspect shows both source and preview dimensions when they differ. Wide or tall previews can use up to 8,192 pixels on the longest edge within the decoded pixel and encoded size limits.

When ZIP scanning is on, each archive member is checked on its own. A member that is encrypted, compresses more than 100:1, exceeds 512 MiB, or is itself an archive is listed as a failed file with the reason, and the other members are still catalogued. Renamed media inside a ZIP is detected from its header when header checks are on. ZIP previews and playback copies use a private temporary directory. The app checks for abandoned copies in the background when it starts. An open instance or media helper keeps its directory leased until it has finished using it.

**Details / notes** lists each recorded path with its source root and refreshes the list after a scan. An **Exact match** row shows the same full SHA-256 on another review card. A **Candidate** row shows the same sampled fingerprint on another card and still needs full verification. A Quick scan can verify a new copy against a full fingerprint already in the catalog.

In Inspect, press **Left** or **Right** to browse items on the current page, **Up** for Compliant, or **Down** for Non-compliant. Inspect advances to the next item on the page after a successful decision. Text fields and video controls keep their own keyboard behavior. Choose **Full screen** or press **F** or **F11** in Inspect or Quick Review. You can keep browsing and marking items in full screen. Press **Esc** to restore the window.

Use **Undo** or **Ctrl+Z** to reverse a recent supported review from the current session. A batch is reversed together. If any selected item's saved review changed after that decision (for example its notes were edited), the whole Undo is rejected, the newer edits are preserved, and that step is dropped so earlier reviews can still be undone.

Quick Review keeps the same item in focus when a scan reorders the current page. If the item leaves the page, Quick Review displays the item at the previous position or the last item on the page. The gallery updates its cards and counts together and reuses saved previews that have not changed.

Open **Details / notes** on an Evidence item to unlock and read its authenticated capture details. Those details are read-only. The originals password protects the retained capture, including its capture metadata. The catalog can still contain plaintext review history and source information.

The left sidebar holds the review queues, view modes, and catalog tools. Click the integrated border arrow at the middle of the sidebar’s right edge, the preview’s left edge, or the top controls’ bottom edge to collapse or reopen that section. The **View** dropdown remains available when the sidebar is closed. In List or Thumbnails, drag the thin preview divider immediately to the left of its arrow to resize the pane. You can also focus the divider with Tab and use the arrow keys. The app remembers the width when it closes.

Use **Maintenance > Sources / reconnect** to check and apply a replacement folder for one root. **Reconnect all sources** handles bulk checks. It binds files whose full SHA-256 matches. A quick candidate (a larger file that was matched on sampled parts instead of being read in full) is not full-file proof, so it is bound only when you tick **Also accept quick candidate matches**; otherwise it stays unverified until you rescan the new location or run Reconnect all again with that option. A candidate that is already bound for the same unchanged file at the same location stays bound when you repeat Reconnect all. **Preview tasks** shows the outcome for each item and lets you resume or discard unfinished work. **Export review report** offers CSV and offline HTML, with optional plaintext Evidence details after vault unlock. Select a Non-compliant item for its **Case sheet**, which keeps reviewer and Windows file owner distinct.

If a reconnect points to a different folder, start a fresh scan there. **Resume** requires the original source for its saved manifest. When retrying a preview or retaining an original from a full SHA-256 card, the app can try another recorded copy if the preferred source is unavailable. Retention verifies the chosen copy's full SHA-256 before saving it.

This is an unsigned development preview. Use a copy of an existing catalog for evaluation. SVG files can be cataloged and fingerprinted. Windows playback depends on the encoding and installed Windows media components.

## AI draft check

1. For advanced review, [download both Gemma files](#download-gemma-for-advanced-ai-review). Gemma requires a CPU with AVX2, FMA and F16C and uses more time and memory than Marqo. For Marqo, place `Marqo-NSFW-384.onnx` beside the executable on a local fixed drive.
2. Finish or pause your scan, then choose **AI draft check**. Select a model and threshold and choose **Save and run**. **Save settings** updates the queue from scores already saved. Each model has its own scores and threshold. The catalog remembers your selection, with Marqo as the default when the Gemma pair is unavailable.
3. Open **AI suggestions** to inspect flagged items, with the highest scores first by default. Use the usual Compliant or Non-compliant controls to make a decision. A saved decision clears the suggestion. The regular **To be reviewed** queue includes all items awaiting a human decision.

Marqo defaults to 7% for catalogs without a saved threshold. Raise it to narrow the review queue. Gemma assigns a workplace review priority. Its 55% default includes ordinary swimwear. Raise it to 80% to focus on visible or implied nudity, including artistic nudity, sexualized lingerie, and uncertain cases. Gemma priorities are policy tiers. Treat the suggestions as leads for human review.

Choose **Edit Gemma search criteria** under **AI draft check** to define what Gemma should look for. Keep categories A through E and edit their descriptions. Their fixed priority scores are A 97%, B 90%, C 55%, D 8%, and E 82%. Choose **Apply** and then **Save and run** to score the saved previews using the new criteria. The rubric is saved per catalog. **Reset to default** restores the original workplace rubric and reuses any matching default scores. Human decisions and Marqo scores are preserved.

Gemma loads in the background after you open a catalog on a supported device with both model files. Check the model status in **AI draft check**. A draft uses the loaded worker with the catalog’s saved rubric and device setting. Gemma defaults to **Auto**. With a Vulkan 1.2-capable graphics driver and at least 4 GiB of available graphics memory, Auto tries GPU acceleration for image encoding and language inference. It prefers an eligible dedicated GPU over integrated graphics. If the GPU worker fails, the current image is retried on the CPU and the rest of the run stays on the CPU. The progress message shows the active device. Select **CPU only** under **Gemma processing device** to skip GPU probing. The catalog remembers this choice and reuses saved scores across both modes. GPU mode uses the existing Windows graphics driver and remains offline.

The draft check runs locally in a private worker from the same executable. Still images use the saved catalog preview and can be scored with the source disconnected. The AI model makes no network requests.

### Video and GIF draft review

In **0.3.23-preview** and later, both Marqo and Gemma can also review cataloged videos and GIFs with a usable saved preview and an accessible original source. Connect the media source before running the draft. The check samples up to five frames from a video and three from one GIF pass, spaced across the known duration. Identical encoded frames are scored once. The highest frame score determines the item's priority, and you can review it in **AI suggestions**.

The run summary counts motion items it could not read. Reconnect the source and run the draft again to retry them. Frame sampling can miss brief events between samples, so use the score to prioritize manual review.

## Command-line use

Run the executable with `--help` for the options. Use `--database PATH` to open a catalog, `--create-database PATH` to create one, and `--import-html SOURCE DESTINATION` to convert a final HTML catalog. Use `--open-evidence-package PATH` to open a protected package. Use `--verify-dependencies` to check startup, the isolated image decoder, and Windows cryptography.

For unattended scans, pass `--database PATH --scan-root ROOT` with any desired scan options. Use `--database PATH --resume-scan latest` to resume using the stored scan options. The scan exit codes are `0` for clean completion, `1` for failure, `2` for cancellation, and `3` for completion with file errors. Wait for the process to exit before checking the result.

## Developer build

Extract the source ZIP and open `MediaComplianceReviewer.csproj` to edit the project. The distributable is a native x64 host containing the managed output, so the managed build by itself is an intermediate file. The build uses Windows .NET Framework C# tooling and a developer WSL environment with Bash, OpenSSL, Python 3, curl, tar, make, Ninja, `glslc` and the pinned LLVM-MinGW toolchain. Set `MCR_GLSLC` if the shader compiler is outside the build host's `PATH`. The dependency recipes verify the archives before using them.

Build the codecs once or after changing their configuration. Then link the application from Windows. The commands assume the Ubuntu-24.04 WSL distribution and the project as the current directory. For a distribution with another name, change `-d` in the four script commands and pass `-Distribution <name>` to `build.ps1`.

```powershell
wsl.exe -d Ubuntu-24.04 -- bash native/build-codecs.sh
wsl.exe -d Ubuntu-24.04 -- bash native/build-av1.sh
wsl.exe -d Ubuntu-24.04 -- bash native/build-ai.sh
wsl.exe -d Ubuntu-24.04 -- bash native/build-gemma.sh
.\build.ps1
.\tests\Smoke.ps1 -Executable '.\dist\Media Compliance Reviewer.exe'
```

The smoke test copies and renames the executable, checks the catalog and review lifecycle with generated media, opens the WPF gallery, and checks the application and redirected temp directories for extra files. Pass generated media via `-PreviewFixtures` to test additional formats. Pass `-PlaybackFixtures` with a directory containing a generated `legacy.flv` with audio and a short looping `animated.gif` to check offline playback. Include a generated `audio.webm` with Opus audio to verify WebM playback. Add 6,000 × 1,000 `wide.webp`, 1,000 × 6,000 `tall.webp`, and 4,000 × 4,000 `square.webp` fixtures with simple content to exercise the linked decoder's size bounds.

The window step sends keys to the app, which ignores them while Shift, Ctrl or Alt is held; the test waits for a held key to be released. `tests\VideoPlayback.ps1` checks the WPF player against `.build\Application.exe` and a generated 320 × 180 video of at least five seconds. In `browser`, `npm test` builds the offline page and runs the packaging check, the Chrome test and the Firefox test; the Firefox test needs `geckodriver` on `PATH` or its path in `MEDIA_REVIEWER_GECKODRIVER`.

Use `-DraftModel` with the path to the approved `Marqo-NSFW-384.onnx` to include native AI inference in the smoke test. To reproduce the model export, use `native/export-marqo.py` with Marqo's original `model.safetensors` and the output filename. The export uses PyTorch 2.6.0 CPU, torchvision 0.21.0, timm 1.0.15, ONNX 1.17.0 and ONNX Runtime 1.21.1. The exporter checks the original checkpoint and verifies the conversion on generated inputs. [Marqo's original checkpoint](https://huggingface.co/Marqo/nsfw-image-detection-384/blob/main/model.safetensors)

## Third-party source and licenses

- SQLite 3.54.1 is public domain. The native build pins the official amalgamation. [SQLite source](https://www.sqlite.org/2026/sqlite-amalgamation-3540100.zip)
- FFmpeg 9.0.2 is built as a static LGPL 2.1-or-later decoder subset. About includes the license. For relinking, use the source, native build recipes and C#/WPF source in this project. The build recipe accepts an explicitly selected local FFmpeg source tree through `MCR_FFMPEG_SOURCE` so that you can rebuild with your changes. [FFmpeg corresponding source](https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz)
- dav1d 1.5.4 supplies the software AV1 decoder under the BSD 2-Clause license. [dav1d source](https://downloads.videolan.org/pub/videolan/dav1d/1.5.4/)
- OpenCV 5.0.0 supplies the linked CPU inference engine under Apache 2.0. About includes the corresponding third-party notices. The optional Marqo weights use Apache 2.0 and are converted to ONNX. [OpenCV source](https://github.com/opencv/opencv/tree/5.0.0), [Marqo model card](https://huggingface.co/Marqo/nsfw-image-detection-384)
- llama.cpp b12141 supplies the linked Gemma CPU and Vulkan inference engines under MIT. The optional Google Gemma 4 E2B weights use Apache 2.0. [llama.cpp source](https://github.com/ggml-org/llama.cpp/tree/b12141), [Google Gemma model card](https://ai.google.dev/gemma/docs/core/model_card_4)
