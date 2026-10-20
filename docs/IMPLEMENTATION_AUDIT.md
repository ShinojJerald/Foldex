# Implementation audit: Filee → Foldex

Written while adding the PDF workspace (branch `feature/pdf-workspace`). It records what the repository had at the
start, what the tools available in this session could and couldn't do, and the state of every feature asked for.

## Baseline

| | |
|---|---|
| Repository | https://github.com/KnifeLemon/Filee, cloned to `C:\dev\Filee` |
| Baseline commit | `49911afab6eef81f144543710f1aa8021c617ff1` (`main`, "Merge pull request #129 … release/1.12.0") |
| Baseline version | 1.12.0 (`Directory.Build.props`) |
| Installed on this machine | Filee 1.11.0 in `C:\Program Files\Filee` (running in the tray during the work; left untouched) |
| Working tree | Clean clone, no local changes. All work is on the new branch `feature/pdf-workspace`. |
| License | MIT (Filee contributors). Every library added or used here is MIT/BSD/Apache compatible (see below). |
| Framework | .NET 10 (`net10.0`), C# latest, Avalonia 12.1.3 with Fluent theme, CommunityToolkit.Mvvm, central package management |
| Projects | `Filee.Core` (jobs, formats, presets, settings, watch folders), `Filee.Engines` (converters), `Filee.Platform.*` (OS integration), `Filee.App` (Avalonia UI), `Filee.Cli`, 5 test projects (xUnit v3, Avalonia.Headless) |
| Packaging | `build/build-installer.ps1` → Inno Setup 7.1 (downloaded portable, SHA-256 pinned) compiling `installer/Filee.iss`; self-contained ReadyToRun publish (Filee's GitHub release workflows are not included in Foldex) |
| Signing | None. The upstream project has no code-signing certificate; installers are unsigned. |

### Baseline build and tests

The machine only had the .NET Core 3.1 SDK. The .NET 10 SDK (10.0.402) was installed per user with Microsoft's
`dotnet-install.ps1` into `%LOCALAPPDATA%\Microsoft\dotnet` (no administrator rights, system `dotnet` untouched).

* `dotnet build Filee.slnx -c Release`: 0 errors, 10 warnings (all pre-existing).
* `dotnet test`: **1,567 passed, 1 failed, 51 skipped.** The failure, `PandocMarkupTests.More_markup_languages_and_slides`,
  needs Pandoc, which isn't installed (CI downloads it). Skips are engine-dependent tests (FFmpeg, LibreOffice, …).
* The first clone location (the app's scratch folder) had paths too long for the test host (`Win32Exception 206`), so
  the repository was moved to `C:\dev\Filee`.

## Tools: what REA and Replica actually offer here

**REA** (MCP server `rea`, ~230 tools) is a reverse-engineering toolkit: native/managed binary analysis (Ghidra, Rizin,
objdump, PE resources, signatures), Electron/browser runtime capture, Android/ADB, Frida, packet captures. Its own
skill says to *skip REA for ordinary source-repository architecture analysis*. It has no tool that reads a C# source tree,
evaluates libraries or reviews diffs, so source auditing, library choice and code review were done directly from the
source. REA was used where it fits: inspecting the **built artifacts** (the published `Filee.exe` and the installer) for
version resources, icons and signature state — see `docs/TEST_RESULTS.md`.

**Replica** is a set of skills for cloning someone else's app (recon of a target, design tokens, build, test, brand,
launch). Most of it doesn't apply to extending this codebase. The part that did: its WCAG contrast checker
(`replica-design/contrast.py`). Run on the new palette it found that white text on Filee's default accent `#7F77DD`
is 3.76:1, failing WCAG AA for the accent buttons; the default accent is now `#6A61D8` (4.87:1). Other text pairs pass
AA in both themes (results in `docs/TEST_RESULTS.md`). Its online-review research skill (`replica-entrepreneur`) was not
run: it needs a long web research session and would not change what was built here.

## Source audit (where things live)

| Area | Where | Notes |
|---|---|---|
| Entry point, lifetime | `Filee.App/Program.cs`, `App.axaml.cs`, `Services/SingleInstance.cs` | Tray app; one instance per user (mutex + named pipe); main window is closed (not hidden) when sent to the tray to free memory |
| Navigation, pages | `ViewModels/MainWindowViewModel.cs`, `ViewLocator.cs` (convention `XxxPageViewModel` → `Views.Pages.XxxPage`) | Pages were recreated on every visit |
| Styling, theming | `Styles/Soft.axaml` (light/dark dictionaries), `Services/ThemeService.cs` (accent, radii, font at runtime) | |
| File selection, drop | `Views/Pages/HomePage.axaml.cs` (drop → donut), donut via global gesture (`Services/Triggers`) | No Browse button before |
| Conversion engine | `Filee.Core/Conversion` (`JobQueue`, `RoutePlanner` – Dijkstra over converter edges), `Filee.Engines/*` | Output naming `OutputPathResolver`; sources never overwritten |
| PDF | `Engines/Pdf`: `PdfSharpConverter` (image→PDF, split/extract by range, merge), `PdfCompressor` (re-encodes pictures), `PdfiumConverter` (render), `PdfDocumentReader` (PdfPig, PDF→DOCX/HWPX), `PdfTextConverter`, `PdfSlidesConverter` | No page editing, annotations, stamps or encryption |
| Jobs, progress, cancel | `JobQueue` (per-engine concurrency, cancellation token per job), toast window | |
| Settings | `Filee.Core/Settings/UserDataStore.cs` (JSON, atomic writes, migrations) in `%APPDATA%\Filee` | |
| Explorer integration | `Filee.Platform.Windows` (classic verb, Send To, Win11 sparse package `Filee.ExplorerMenu`) | |
| Watch folders, CLI | `Filee.Core/Watching/FolderWatcher.cs`, `Services/WatchFolderService.cs`, `Filee.Cli` | |
| Tests | 5 projects, ~1,000 test methods (many theories) | Headless UI render tests produce screenshots |

## Feature matrix

Legend: **Verified** = implemented and covered by automated tests that check the result files (or UI state);
**Insufficiently tested** = implemented, works in manual/headless checks but has thin or no dedicated tests;
**Partial**; **Missing**; **Blocked**.

### Existing Filee features (kept)

| Feature | Status | Evidence |
|---|---|---|
| Format conversions (images, documents, e-books, media, archives, CAD, 3D, fonts, subtitles, data, e-mail) | Verified (upstream tests) | 942+ engine tests pass; FFmpeg/LibreOffice/Pandoc-dependent ones skip without those engines |
| Donut toolbar, gestures, presets, profiles | Verified (upstream tests) | App tests pass |
| Watch folders (enable/disable/remove, include filters, keep/move originals) | Verified (upstream tests) | `FolderWatcherTests`, `WatchRuleOptionsTests` |
| Command line (`filee-cli`) | Verified (upstream tests) | `CliTests` |
| Explorer verb + Send To | Verified (upstream tests) | Now registered under Foldex's own key |
| Windows 11 top-level Explorer menu | Not shipped in Foldex | Its package identity is Filee's; shipping it would take over Filee's entry (see `KNOWN_LIMITATIONS.md`) |
| Conversion history, retry failed files | Verified (upstream tests) | |

### PDF workspace (new)

| Feature | Status | Evidence |
|---|---|---|
| Open PDF (picker, drop, recent), thumbnails, preview, page navigation | Verified | `PdfWorkspaceTests.Editor_opens_edits_undoes_and_saves_a_pdf` |
| Zoom in/out, fit width (follows the window), fit page | Insufficiently tested | Fit width checked in screenshots |
| Rotate, delete, duplicate, reorder (drag + Alt+↑/↓), insert from another PDF, export selected pages | Verified (engine + VM) | `PdfEditorTests.Reorders_deletes_duplicates_rotates_and_inserts_pages`, workspace test |
| Thumbnail drag-to-reorder gesture | Insufficiently tested | Implemented in `PdfEditorPage.axaml.cs`; the reorder itself (`MovePages`) is tested, the pointer gesture is not |
| Multi-page selection | Insufficiently tested | |
| Undo / redo (page operations, adding/moving/deleting marks) | Verified | workspace test |
| Save (asks once where), Save As (native dialog confirms replacing), unsaved indicator, confirm before losing changes | Verified | workspace test; close-window guard in `MainWindow.axaml.cs` |
| Merge, split (ranges or every N pages), extract | Verified | `PdfEditorTests.Merges_files_in_order`, `Splits_by_ranges_and_by_size`, `Rejects_ranges_outside_the_document` |
| Page numbers, header, footer, watermark (correct on rotated pages) | Verified | `Adds_page_numbers_header_footer_and_watermark` (text extracted, rendered pixels checked) |
| Annotations: highlight, underline, strike-out (follow the words under them), sticky note, text box, rectangle, ellipse, line; colour and line width | Verified | `Writes_annotations_that_other_viewers_show` (PDFsharp reopens them; PDFium renders them), `Marks_land_where_they_are_shown_on_turned_pages` |
| Pictures / stamps on pages | Verified | `Places_a_picture_on_a_page` |
| Password protect (AES-256), open protected PDFs, save unlocked copy with the password | Verified | `Protects_opens_and_removes_a_password`, `A_password_protected_pdf_asks_for_its_password` |
| Remove document information (verified on the bytes) | Verified | `Keeps_or_removes_document_information` |
| Compression presets with before/after size and % | Partial | Uses Filee's existing tested compressor (`PdfCompressionTests`); the new tools card's size report is not separately tested |
| OCR: scanned PDF → searchable PDF, picture → text, language choice, cancel | Verified on Windows with an OCR language | `OcrTests` (skip without one) |
| Text extraction (text layer, not OCR) | Verified (upstream `PdfTextConverter`) | The tools card reports "no text, may need OCR" |
| Editing existing text (one line at a time, read back after saving) | Verified | `Replaces_a_line_of_existing_text`, `Edits_existing_text_and_places_a_signature`; pictures inside the page: missing |
| Filling form fields (AcroForm) | Verified | `Fills_a_form_and_keeps_it`, `Refuses_form_values_when_pages_are_rearranged_or_unknown` |
| Signatures: drawn / typed picture; certificate (.pfx, PKCS#7 SHA-256) | Verified | `Signs_with_a_certificate` (signature checked with `SignedCms`), `Edits_existing_text_and_places_a_signature`; the draw dialog itself is not driven by tests |
| Malformed PDFs fail cleanly; no partial output; temp files removed | Verified | `A_broken_file_fails_clearly_and_writes_nothing`, `Cancelling_writes_nothing`, `Can_save_over_a_source_and_leaves_no_temporary_files` |

### File utilities (new)

| Feature | Status | Evidence |
|---|---|---|
| Exact duplicates (size, then SHA-256), never deletes | Verified | `FileScannerTests.Finds_exact_duplicates_only` |
| Largest files, compare two files | Verified | `Compares_and_lists_the_largest_files` |
| Extension ≠ content (magic numbers) | Verified | `Detects_content_that_does_not_match_the_extension`, `Recognises_common_signatures` |
| Picture metadata (EXIF, GPS, IPTC, XMP) and a verified clean copy | Verified | `ImageMetadataTests` |
| Batch rename with preview, no overwrite, swaps | Verified | `BatchRenameTests` |
| Document metadata view | Partial | PDF info is read by `PdfEditor.ReadInfo`; no UI for Office documents |

### Interface (new)

| Feature | Status | Evidence |
|---|---|---|
| Grouped navigation (Workspace / Settings), workspace pages full width and kept while the window is open | Verified | `Workspace_pages_keep_their_state_while_the_window_is_open` |
| Nebula theme (dark default, light), top bar search + light/dark switch | Insufficiently tested | Render tests in both themes; the switch and search checked in the installed app |
| Home: hero, tool tiles, live Task queue, Recent files with filters | Insufficiently tested | Rendered in tests; the queue binds to the real conversion jobs |
| Quick panel replacing the ring toolbar (gesture, drop, click, keyboard, progress, editor) | Verified | `Quick_panel_renders_for_images_and_runs_from_the_keyboard`, `A_group_lists_its_presets_as_tiles`, `The_rotate_and_flip_groups_show_as_sections`, `Dragging_a_preset_shows_where_it_goes_in_the_quick_panel`, `RadialControllerTests` |
| Home: Browse files, Browse folder, quick actions, recent files | Insufficiently tested | Rendered in screenshots; buttons open the existing donut |
| Light / dark, 4 languages for all new text | Verified | Render tests (light/dark, en/ko/zh-CN), `LocalizationTests` (same keys in every language, placeholders match) |
| Contrast (WCAG AA) | Verified with Replica's checker | `TEST_RESULTS.md` |
| Keyboard shortcuts in the editor | Insufficiently tested | KeyBindings in `PdfEditorPage.axaml` |
| Screen-reader names on icon buttons | Insufficiently tested | `AutomationProperties.Name` on every icon-only control |

### Asked for, not built

| Feature | Status | Why |
|---|---|---|
| Natural-language / AI assistant | Missing | Prioritised a stable PDF editor; no local model was added (none needed for the deterministic tools) |
| Separate "Image tools", "Audio & video", "Automation" sections | Not added as pages | Those features already exist as presets in the donut and as watch folders; the nav doesn't show empty sections |
| File associations | Not added | Only on explicit request (as asked) |
| Clean-machine install test | Blocked | No VM or Windows Sandbox was used; see `TEST_RESULTS.md` for what was checked on this machine |
| Code signing | Blocked | No certificate |
