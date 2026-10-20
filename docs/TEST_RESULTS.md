# Test results (Foldex 1.0.0)

Machine: Windows 11 Home 10.0.26200, x64, .NET SDK 10.0.402. Filee 1.11.0 was installed and running the whole time.

## Automated tests

| Suite | Baseline (`49911af`, Filee 1.12.0) | Final (`feature/pdf-workspace`) |
|---|---|---|
| Filee.App.Tests (headless UI) | 339 passed, 2 skipped | 300 passed, 1 failed, 2 skipped (last full run; the failure, D-16, is fixed and its tests plus the 111 PDF / signature / pointer UI tests pass on the final code) |
| Filee.Core.Tests | 246 passed, 2 skipped | 258 passed, 2 skipped |
| Filee.Engines.Tests | 942 passed, **1 failed**, 43 skipped | 980 passed, 50 skipped |
| Filee.Cli.Tests | 22 passed, 1 skipped | 22 passed, 1 skipped |
| Filee.Platform.Tests | 18 passed, 3 skipped | 18 passed, 3 skipped |

**Final bug-fix pass (2026-10-19), on the final code: Engines 980, Core 258, CLI 22, Platform 18 passed, 0 failed; UI: the 111 PDF editor, signature, pointer, dialog, localization and page-render tests passed. The full UI suite (303 tests, ~45 min) last ran before the final fixes: 300 passed, 1 failed (D-16, since fixed).** The QA round, its defects and test cases are in `QA_TEST_PLAN.md`; a full UI run takes about 30 minutes. The App suite has fewer tests than at baseline because the ring toolbar and its
~70 geometry/animation tests were removed with it; the Quick panel that replaced it has its own tests (below).

Notes:

* Baseline failure: `PandocMarkupTests.More_markup_languages_and_slides` needs Pandoc. It ran (and failed) at baseline
  because the tests found engines in the installed Filee's `%LOCALAPPDATA%\Filee\engines`; Foldex uses its own engines
  folder, so engine-dependent tests (Pandoc, FFmpeg, LibreOffice, …) now skip unless those engines are downloaded for
  Foldex. Skips are engine availability, not failures.
* A first full run reported only 264 App tests: I had stopped the next suite's test host by mistake while the App run
  had finished. The clean rerun executed all 351 App tests.

### New tests (all passing)

| Area | Tests | What they check on real output |
|---|---|---|
| PDF engine (`PdfEditorTests`) | 19 | Reorder/delete/duplicate/rotate/insert (text order via PdfPig, /Rotate, page sizes), merge, split by ranges and every N, invalid ranges refused, AES-256 protect/open/unlock, metadata kept/removed (also absent from the bytes), annotations reopened by PDFsharp **and rendered by PDFium** (pixel colours at the marks), marks on rotated pages land where shown, page numbers/header/watermark (extracted text + pixels on a rotated page), picture stamps, save over a source, broken PDF → clear error and no files, cancellation → no file |
| Text, forms, signing (`PdfContentEditingTests`) | 4 | A form (text field + check box) is read, filled and read back from the saved file, the source unchanged; form values with rearranged pages or an unknown field are refused and nothing is written; a line of existing text is replaced (new words present, old gone, other line intact, same baseline and left edge); a PDF signed with a self-signed certificate has a /ByteRange whose PKCS#7 signature verifies (`SignedCms.CheckSignature`), and signing + password is refused |
| Editor: edit text + signature (`PdfWorkspaceTests`) | 1 | Clicking a line with the ✎ tool picks it up, the text is retyped, a signature picture is placed, Save writes a PDF where PdfPig reads the new words, not the old, and finds the picture |
| OCR (`OcrTests`) | 3 | Windows OCR reads a picture with word boxes; a scanned PDF becomes searchable with the words at their positions and **renders pixel-identical** to the scan; a PDF with text is left alone |
| Picture metadata (`ImageMetadataTests`) | 2 | EXIF camera name and GPS read; clean copy has no EXIF, the comment is gone from the bytes, size unchanged, original untouched |
| File utilities (`FileScannerTests`, `BatchRenameTests`) | 12 | Duplicates by SHA-256 (never deleting), compare, largest, content-vs-extension, rename preview/apply/swap/no overwrite/invalid names |
| Quick panel (`RenderTests`, `RadialControllerTests`) | 9 | Tile hit-testing, group sections, drop-preview slots in the editor, keyboard navigation + Enter, close button, progress hand-off closes the window |
| Workspace UI (`PdfWorkspaceTests`) | 10 | Pages render light/dark in en/ko/zh-CN; workspace pages keep state; editor opens a real PDF, renders thumbnails and preview, rotate/move/duplicate/undo/redo/delete, adds a mark and page numbers, Save writes the edits (checked with PDFsharp) and leaves the source byte-identical, second Save doesn't ask again; password prompt retries after a wrong password; marks turn with their page |
| Localization | existing | Every new key exists in all 4 languages with matching placeholders |

Screenshots from the UI tests: `tests/Filee.App.Tests/bin/Release/net10.0/screenshots/` (e.g. `pdf-editor-document.png`,
`page-pdftools-ko-dark.png`, `page-home-en-dark.png`).

## Contrast (Replica's WCAG checker, `replica-design/contrast.py`)

| Pair | Light | Dark |
|---|---|---|
| Text on page / surface / alt surface | 15.6 / 17.0 / 14.7 (AAA) | 15.9 / 14.2 / 12.3 (AAA) |
| Secondary text on page / surface / alt surface | 5.7 / 6.2 / 5.4 (AA) | 7.9 / 7.1 / 6.1 |
| Danger / success / warning on surface | 5.1 / 5.3 / 5.4 (AA) | 7.1 / 7.9 / 7.3 |
| White on Filee's accent `#7F77DD` | **3.76 – fails AA** | same |
| White on Foldex's accent `#6A61D8` | 4.87 (AA) | same |

## Built artifacts (REA and PowerShell)

* `Releases\Foldex-1.0.0-win-Setup.exe` (99.9 MB) and `Foldex-1.0.0-win-Portable.zip` (140 MB), with
  `Foldex-1.0.0-SHA256SUMS.txt`.
* Setup version info: ProductName *Foldex*, ProductVersion 1.0.0. **Authenticode: NotSigned.**
* REA `inspect_pe_resources` on the published app exe: icon group with 6 sizes (16–256 px), version resource,
  manifest. REA `inspect_managed_artifact` on `Filee.dll`: assembly 1.0.0.0, .NET 10, ReadyToRun, x64, no strong name.
* Publish folder: `Foldex.exe` present, no `Filee.exe`, `cli\foldex.cmd` present.

## Install / uninstall on this machine

Not a clean machine: Filee 1.11.0 was installed and running. Each step needed the user's UAC approval.

| Step | Result |
|---|---|
| Record Filee before | `Filee.exe` SHA-256 `F4A5B5E9…CB6BD835`, 323 files, uninstall entry 1.11.0 |
| Install the pre-rename build (same installer code, named "Filee Studio") silently with the Explorer-verb task | Exit 0; uninstall entry 1.0.0 with its own AppId; 321 files; Start-menu shortcut; per-user Explorer verb; settings created in its own folder; the app started and ran **alongside** Filee (both processes responding) |
| Uninstall it silently | Exit 0; install folder, uninstall entry, Explorer verb and Start-menu shortcut removed; settings kept (by design) |
| Check Filee after | Same SHA-256, same 323 files, same version, still running |
| Install `Foldex-1.0.0-win-Setup.exe` silently | Exit 0; uninstall entry "Foldex 1.0.0", publisher "Foldex (built on Filee)", `C:\Program Files\Foldex\`; Start-menu shortcut; `Foldex.exe` running next to `Filee.exe` |
| Launch and click through (computer use, real installed app) | Main window with the Foldex icon and name, grouped navigation, Home (Browse files/folder, quick actions), PDF editor (empty state), PDF tools; the first-run engine chooser opened as in Filee |
| Upgrade to the redesigned final build (same AppId) | Exit 0; `Foldex.exe` reports FileDescription/ProductName "Foldex", 1.0.0; Filee's exe hash unchanged |
| Live Quick panel conversion (installed app) | Opened the panel for `sample-photo.jpg` (Explorer-menu path `--convert`), clicked **WEBP**: progress → ✓ → panel closed; `sample-photo.webp` written and read back as WebP 640×400 |
| Live main window | New theme, sidebar, hero, Quick tools, Task queue render in the installed app |
| Foldex uninstall | Not run again after the final upgrade (left installed for you); the uninstaller is the same script verified above |

Not checked: a clean machine or VM; SmartScreen behaviour on download; the installer's interactive wizard pages (only
silent mode was run); drag-and-drop gestures with a real mouse in the installed app; OCR languages other than en-US.
