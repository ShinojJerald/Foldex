# QA test plan and results (Foldex 1.0.0, QA round 2026-10-19)

Branch `feature/pdf-workspace`, checkpoint tag before this round: `checkpoint/before-qa-audit-2026-10-19`.
Machine: Windows 11 Home 10.0.26200, .NET SDK 10.0.402, Microsoft Edge installed (used to print real-world test PDFs).

**Execution methods**: *Auto-E* = engine test (xUnit, real files written and read back with PDFsharp / PdfPig / PDFium
rendering); *Auto-UI* = headless Avalonia test driving the real view models and windows; *Live* = the installed app on
this PC, driven with screen control; *Inspect* = code / build output inspection. No test uses mocks for file I/O, PDF
writing or rendering; UI tests stub only the native file and question dialogs.

## Defects found and fixed in this round

| ID | Severity | Defect | Root cause | Fix | Regression test |
|---|---|---|---|---|---|
| D-01 | P0 | Editing "stopped working" after saving over the opened file: later saves were refused ("can't be changed safely") | After a save the editor kept its pending edits and applied them again on top of the already-edited file | The saved file becomes the document after every save (pending edits cleared, stamp/password options reset once written) | `Edit_save_over_the_original_and_edit_again_for_several_rounds` (3 rounds) |
| D-02 | P0 | Text editing never worked on PDFs printed by Chrome / Edge ("Save as PDF") — very common real-world files | Chromium pages start with a scaling transform that is never restored; after a text edit rewrote the page content, the new text was drawn under it (tiny, misplaced) and the read-back check refused the save | The page's own content is wrapped in `q … Q` once before Foldex draws on it | `RealWorldPdfTests.Edits_a_line_of_a_browser_printed_pdf` (4 lines: heading, bold, body, italic serif) |
| D-03 | P0 | No way to close a document; no Save / Don't save / Cancel choice | Not implemented (only "discard?" when opening another file or closing the window) | Close button, Ctrl+W / Ctrl+F4; one Save / Don't save / Cancel dialog for Close, opening another PDF and closing the window | `Close_asks_save_dont_save_or_cancel_and_keeps_edits_when_saving_does_not_happen`, `Save_changes_dialog_answers_by_keyboard` |
| D-04 | P0 | After choosing "Discard" once, a later window close with new edits could skip the question (silent data loss) | A `_discardConfirmed` flag was never reset | Flag replaced by a one-shot `_unsavedResolved` cleared after use | Code review (`MainWindow.OnClosing`); live check L-03 |
| D-05 | P1 | A new text box / note / text edit was not left selected, so its text couldn't be typed (found live) | Switching back to the pointer tool cleared the selection before it was read | Selection taken before the tool switch | Live check L-02; `Edits_existing_text_and_places_a_signature` |
| D-06 | P2 | Saved signatures ignored the configured data folder (`FILEE_DATA_DIR`) | Used the default AppData path directly | Uses the app's data store folder | `Five_typed_styles_…` (asserts the folder) |
| D-07 | P3 | Signature style previews invisible in the dark theme (dark ink on dark) | Previews drawn on the window background | Each style previewed on white paper | Screenshot `signature-studio-type.png` |
| D-08 | P3 | "Drag the mark to move it" shown for text edits, which can't move | Generic hint | Hidden for text edits | Inspect |
| D-09 | P1 | Typed signatures were cut off: "Jerald" came out as "Jerala" (found live, twice) | Script swashes reach outside the text's line box, and a TextBlock rendered to a bitmap clips its glyphs to its own bounds. A first fix (padding around the TextBlock) didn't help: the live recheck still showed "Jerala" | The padding is inside the TextBlock, so its bounds include the swashes; the picture is then trimmed | `Typed_signatures_are_not_clipped`: no ink at the edges, and a script "d" renders much taller than "a" in every style (fails with both earlier versions) |
| D-10 | P2 | A long file name pushed Save and Close under the title, out of reach (found live) | The title sat in a horizontal StackPanel, which measures with unlimited width, so it never trimmed | Title in a star-sized column, trimmed with an ellipsis | `A_long_file_name_keeps_save_and_close_in_reach_and_recent_files_update` (fails without the fix) |
| D-11 | P3 | "Recent PDFs" in the empty editor didn't show files opened or saved since the app started (found live) | The list was computed once in the constructor | Computed on demand, refreshed when a document opens or closes | same test |
| D-12 | P3 | After Close, Ctrl+O and other editor shortcuts didn't work until something was clicked (found live) | Focus was on the removed document's controls | The editor takes focus again when a document opens or closes | Live L-03 |
| D-13 | P3 | Replacement text longer than the old line was clipped in the preview, and edges of the old glyphs showed (found live) | The preview cover was the old line's exact size | Cover grows with the new text and reaches past the old line | Screenshot `pdf-editor-edit-text.png` |
| D-14 | P0 | Drawing in Signature Studio left no ink: "The signature has no visible ink", Insert disabled (bug report) | Avalonia's `Polyline` builds its shape when `Points` is set and ignores points added to the same list, so each stroke stayed a single point | Each pointer move assigns a new point list | `EditorPointerTests.A_signature_drawn_with_the_mouse_has_ink_and_can_be_inserted` (real pointer events; fails without the fix) |
| D-15 | P0 | "There's no editable text there" when clicking visible text (bug report, a 7–8.5 pt study guide) | Lines of small text are about 7 pt tall with ~10 pt gaps; a click had to land within 2 pt of the glyphs, so 39% of clicks on a line found nothing (measured read-only on the reported file) | The nearest line within 8 pt is picked (100% of 4,920 test clicks on the reported pages found the right line, none a neighbour); separate messages for turned pages, pages without text (OCR) and near-misses | `Clicks_near_small_text_pick_the_nearest_line_and_misses_say_why` (fails with the 2 pt margin), `Every_line_of_a_two_page_shaded_document_can_be_picked`, `Clicking_text_on_a_letter_page_picks_that_line` |
| D-16 | P1 | Saved pages got an empty `/CropBox [0 0 0 0]`, so a text change saved over a file could not be picked again (found by the D-15 tests) | PDFsharp's `CropBox` getter adds an empty box to a page that has none | The crop box is read only when present; empty ones are removed on save and treated as the media box when reading | `Saving_never_writes_an_empty_crop_box_and_repairs_one` (fails on the previous code) |
| D-17 | P1 | Lines came out diagonal (bug report) | The geometry was correct (lines follow the drag at every zoom and on turned pages), but the Line tool previewed a rectangle while dragging, so lines drawn by following the box came out as its diagonal | The Line tool previews the line itself | `The_line_tool_previews_a_line_not_a_box` (fails without the fix), `Lines_follow_the_drag_at_any_zoom_and_on_a_turned_page` |
| D-18 | P3 | PDF tools said OCR is something "Foldex doesn't include", next to its own OCR card | Text left from before OCR was added | Points to Recognise text (OCR) | Inspect |
| T-01 | (test harness) | The new Save-changes dialog test failed in the full parallel UI run (passed alone); full runs also seemed to stall | The headless UI thread is shared, so a modal-dialog test interleaved with other classes' windows; its keys were also never released (headless `KeyPress` is key-down only). The "stall" was partly a misreading: I watched the idle `testhost` bridge instead of the busy `Filee.App.Tests.exe` | Dialog test moved to a non-parallel collection, closes its dialogs in `finally`, releases every key | Full suite run (found with per-class runs and xUnit `-longRunning`) |

## Test cases

Status: ✅ pass, ❌ fail, ⏭ skipped (reason given), — not applicable.

### PDF document lifecycle (P0)

| ID | Scenario | Test data | Steps | Expected | Method | Status |
|---|---|---|---|---|---|---|
| LC-01 | Open, edit, undo/redo, save, save again | 3-page PDF, mixed sizes | Rotate, move, duplicate, delete, undo, redo, rectangle, page numbers, Save, Save | Edits in the file, source byte-identical, Save As asked only once | Auto-UI `Editor_opens_edits_undoes_and_saves_a_pdf` | ✅ |
| LC-02 | Save over the original, edit, save — 3 rounds | 1-page letter (Arial) | Edit text + note, Save (over source), repeat | Each round's text in the file, the previous text gone, notes accumulate 1→2→3, nothing pending after save | Auto-UI | ✅ |
| LC-03 | Close without changes | letter | Open, Close | Closes, no question | Auto-UI | ✅ |
| LC-04 | Close → Cancel | letter + rectangle | Close, Cancel | Stays open, edit kept, still unsaved | Auto-UI | ✅ |
| LC-05 | Close → Save → Save As cancelled | same | Close, Save, cancel the file dialog | Not a save: stays open with the edit | Auto-UI | ✅ |
| LC-06 | Close → Save → write fails | target is a folder | Close, Save | Error shown, stays open, edit kept | Auto-UI | ✅ |
| LC-07 | Close → Save → succeeds | same | Close, Save to a new file | Written (annotation present), closed | Auto-UI | ✅ |
| LC-08 | Close → Don't save | same | Close, Don't save | Closed, nothing written, source unchanged | Auto-UI | ✅ |
| LC-09 | Open another PDF over unsaved edits | 2 letters | Open other → Cancel; then → Don't save | Cancel keeps the first with its edit; Don't save opens the second | Auto-UI | ✅ |
| LC-10 | Repeated open → edit → navigate away and back → close, 5 cycles | letter | Edit text, text box, highlight, undo, redo, Home, back, Close | Every cycle: tools work, undo/redo work, edits survive navigation, close empties the editor | Auto-UI `Tools_keep_working_through_repeated_open_edit_close_cycles` | ✅ |
| LC-11 | Save changes dialog by keyboard | — | Enter / Esc / Alt+N / close box, then 3 more times | Save / Cancel / Don't save / Cancel, repeatable | Auto-UI | ✅ |
| LC-12 | Password-protected PDF | AES-256 PDF | Open, wrong password, right password | Asked again after a wrong one; opens with the right one | Auto-UI `A_password_protected_pdf_asks_for_its_password` | ✅ |
| LC-13 | Broken PDF / cancelled save | not-a-PDF, cancelled token | Save | Clear error, no file, no temp | Auto-E `A_broken_file_…`, `Cancelling_writes_nothing` | ✅ |
| LC-14 | Read-only target | browser-printed PDF copied read-only | Save over it | Error, target bytes unchanged, no `.tmp` left | Auto-E | ✅ |
| LC-15 | Unicode name with spaces | `Résumé 文件 (copy).pdf` | Save with a note | Written and readable, source unchanged | Auto-E | ✅ |
| LC-16 | Large document | 400 pages | Reverse order, highlight, note, page numbers | Saved in < 60 s, 400 pages, numbering "1 / 400" | Auto-E | ✅ |
| LC-17 | Close the window with unsaved edits | live app | Edit, close window | Save / Don't save / Cancel | Live L-03 | see live checks |

### Editing existing text, forms, certificate signing

| ID | Scenario | Steps | Expected | Method | Status |
|---|---|---|---|---|---|
| TX-01 | Replace a line (PDFsharp / PdfPig PDFs) | Click line, retype, save | New words in place (same baseline/left), old gone, other line intact | Auto-E, Auto-UI | ✅ |
| TX-02 | Replace a line in a browser-printed PDF (heading, bold, body, italic serif) | 4 cases | As TX-01 | Auto-E (Edge) | ✅ |
| TX-03 | Line that can't be changed safely | (read-back fails) | Nothing saved, message suggests a text box | Auto-E (verified in D-02 before the fix) | ✅ |
| FM-01 | Fill text field + check box, keep form | AcroForm PDF | Values read back from the saved file; source unchanged | Auto-E | ✅ |
| FM-02 | Form values with rearranged pages / unknown field | | Refused, nothing written | Auto-E | ✅ |
| SG-01 | Certificate signing | self-signed RSA-2048 | `/ByteRange` signature verifies (`SignedCms.CheckSignature`); signing + password refused | Auto-E | ✅ |

### Signature Studio (P1)

| ID | Scenario | Expected | Method | Status |
|---|---|---|---|---|
| SS-01 | Typed name in five styles | Five different PNGs (hashes differ), different shapes, all transparent, all show the typed name | Auto-UI | ✅ |
| SS-02 | Colour and size | Navy ink measured in the PNG; width 120 pt → choice 120 pt, height by aspect ratio | Auto-UI | ✅ |
| SS-03 | Draw: thickness, undo stroke, clear | Thickness applied to all strokes; undo changes the picture; nothing drawn → no Insert | Auto-UI | ✅ |
| SS-04 | Upload with white background | Background transparent, trimmed | Auto-UI, Auto-E | ✅ |
| SS-05 | Broken / blank / missing / oversized upload | Refused with a message, Insert disabled, nothing written | Auto-UI, Auto-E | ✅ |
| SS-06 | Place on page 2 of a turned page, cancel first | Cancel places nothing; placed at the click at the chosen size | Auto-UI | ✅ |
| SS-07 | Resize keeps aspect ratio; zoom | Width 240 → height × same ratio; points unchanged by zoom, pixels follow zoom | Auto-UI | ✅ |
| SS-08 | Saved position, 0° / 90° / 270° pages | Rendered by PDFium: ink where placed, empty part white (no opaque box), other page untouched; same after reopening and saving again | Auto-E | ✅ |
| SS-09 | Signature on a browser-printed PDF | Ink where placed, not larger | Auto-E | ✅ |
| SS-10 | Live: Studio opens, styles, insert, move, resize | | Live L-04 | see live checks |

### Other features (existing suites)

| Area | Coverage | Method | Status |
|---|---|---|---|
| PDF tools: merge, split, compress, protect / unlock, extract text, OCR | `PdfEditorTests`, `PdfCompressionTests`, `OcrTests`, `PdfReaderTests` (outputs read back; OCR text positions and pixel-identical render) | Auto-E | see run totals |
| Conversions (images, documents, e-books, audio/video, archives, CAD, fonts, …) | Upstream Filee suite of ~1,000 engine tests: outputs are opened and their content checked (pages, text, pixels, durations), not only that a file exists. Tests needing optional engines (Pandoc, FFmpeg, LibreOffice, …) skip when those engines aren't downloaded for Foldex | Auto-E | see run totals |
| File utilities: duplicates, largest, wrong extension, compare, metadata, batch rename | `FileScannerTests`, `BatchRenameTests`, `ImageMetadataTests` | Auto-E / Core | see run totals |
| Pages render light/dark in 4 languages, search, Quick panel | `RenderTests`, `ListSearchTests`, Quick panel tests | Auto-UI | see run totals |
| Installer, coexistence with Filee | `TEST_RESULTS.md` (install / upgrade / uninstall, Filee hash unchanged) | Live | ✅ (earlier round; re-checked L-01) |

## Not available in Foldex (not tested, recorded)

* **Printing** from the PDF editor: there is no print command (open the saved PDF in a viewer to print).
* **Tabs / several open PDFs at once**: the editor holds one document; opening another asks about unsaved changes first.
* **Selecting and copying page text** in the editor preview (use PDF tools → Extract text).
* **Crash recovery / autosave** of unsaved edits: none. Saves are atomic (temporary file, then replace), so a crash
  never leaves a half-written file, but unsaved edits are lost.
* **Non-proportional signature resizing**: deliberately not offered (signatures keep their aspect ratio).

## Live checks on the installed app

Installed build upgraded in place (`Foldex-1.0.0-win-Setup.exe`, silent, UAC approved by you), test file printed by
Edge into `Documents\Foldex QA\qa-sample.pdf` (no personal files used; one of your recent PDFs was opened view-only by
a mis-aimed click and closed unchanged).

| ID | Check | Result |
|---|---|---|
| L-01 | Upgrade: exit 0, installed `Foldex.exe` identical to the build, 5 font licences in `licensesonts`, Filee's exe hash unchanged | ✅ |
| L-02 | ✎ on the heading of the Edge-printed PDF → the line is selected with its text ready to type; "Unsaved changes" shows; after saving and reopening, the page itself reads "Consulting Agreement" in the original blue bold | ✅ (preview clipping → D-13, fixed after this check) |
| L-03 | Close with changes → Save changes dialog; Esc keeps it open with the edit; Save → Save As cancelled keeps it open; Save → Save As → written and closed; Close of an unchanged PDF (button and Ctrl+W) doesn't ask | ✅ (found D-10, D-11, D-12) |
| L-04 | Signature Studio: five styles with the typed name, navy ink, Insert places it at the click, corner-drag resizes at the same aspect ratio, Ctrl+S saves and the reloaded page shows the signature baked in with no box | ✅ (found D-09) |

The fixes for D-09 to D-13 were made after these checks; they are covered by the automated tests above and the build
was installed again afterwards (see the report).

### Live checks after the final bug-fix pass (installed build, test file `Documents\Foldex QA\qa-sample.pdf`)

| ID | Check | Result |
|---|---|---|
| L-05 | Install: exit 0; installed `Foldex.exe` and `Filee.Engines.dll` identical to the build; Filee unchanged | ✅ |
| L-06 | Signature Studio → Draw: a mouse stroke shows on the pad and in the preview, no "no visible ink" error, Insert enabled; the drawing is placed on the page | ✅ |
| L-07 | Line tool: the drag shows a line preview (no box) and a horizontal drag makes a horizontal line | ✅ |
| L-08 | Edit text: a click just below a small body line selects it; the changed text, the drawn signature and the line are all in the saved file after it reloads | ✅ |

Not checked live: pen and touch input (they arrive through the same Avalonia pointer events as the mouse), and the
scanned-page message (automated test only).
