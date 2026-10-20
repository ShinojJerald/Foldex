# PDF features

Foldex adds a PDF workspace (**PDF editor**), whole-file **PDF tools** and OCR to Filee. Everything runs on the
computer; no file is uploaded. The original file is never changed unless you pick it yourself in a Save As dialog, which
asks before replacing it.

Engine code: `src/Filee.Engines/Pdf/PdfEditor.cs` (page operations, annotations, stamps, security),
`PdfContentEditing.cs` (existing text, forms, signing), `PdfPageRenderer.cs` (thumbnails / preview), `PdfOcr.cs` +
`src/Filee.Engines/Ocr/` (OCR).
Libraries: PDFsharp 6.2 (MIT) to write, PDFium via PDFtoImage (BSD-3 / MIT) to render, PdfPig (Apache-2.0) to read
text, all already used by Filee.

## PDF editor

Open a PDF with **Open** (Ctrl+O), by dropping it on the page, or from *Recent PDFs* / Home → *Recent files*.

| Action | How | Shortcut |
|---|---|---|
| Select pages | Click a thumbnail; Ctrl/Shift+click for several | Ctrl+A all |
| Reorder | Drag thumbnails (a line shows where they go), or move the selection | Alt+↑ / Alt+↓ |
| Rotate selected pages | Toolbar | Ctrl+R right, Ctrl+Shift+R left |
| Duplicate / delete selected pages | Toolbar | Ctrl+D / Delete |
| Insert pages from another PDF | Toolbar `+` (inserted after the selection) | |
| Export selected pages as a new PDF | Toolbar ⬆ | |
| Undo / redo | Toolbar | Ctrl+Z / Ctrl+Y (Ctrl+Shift+Z) |
| Zoom | − / + / Fit width / Fit page | Ctrl++ / Ctrl+- / Ctrl+0 |
| Previous / next page | Status bar arrows | PageUp / PageDown |
| Save / Save as | Toolbar | Ctrl+S / Ctrl+Shift+S |

* An **Unsaved changes** label sits next to the title while there are unsaved changes; **Save** and **Close** are next
  to it.
* **Close** (Ctrl+W or Ctrl+F4), opening another PDF and closing the window ask **Save / Don't save / Cancel** when
  there are unsaved changes (Enter saves, Alt+N doesn't save, Esc cancels). Save closes only when the file was really
  written: a cancelled Save As or a failed save keeps the document open with every edit. Without unsaved changes it
  closes at once.
* The first **Save** asks where to save (suggested: "name (edited).pdf" next to the original); later saves go there.
* After a save, the saved file **becomes the document**: what was saved (text changes, marks, stamps, form values) is
  part of it, so the next save never applies it twice. Undo history starts again, and the "When saving" options (page
  numbers, header/footer, watermark, password, certificate) reset because they're now in the file.
* Thumbnails are rendered small, one page at a time in the background; the preview renders only the current page at the
  current zoom (capped at ~40 megapixels), so large PDFs open quickly and don't fill memory.
* Pages keep their size and orientation; rotation is stored as the page's /Rotate, so nothing is re-rendered.

### Marks (annotations)

Pick a tool, then drag on the page (click for a sticky note). The new mark is selected; edit its text, line width or
font size in the side panel, or click a colour swatch. With the pointer tool, click a mark to select it, drag to move it,
Delete to remove it.

| Tool | PDF annotation | Notes |
|---|---|---|
| Highlight, Underline, Strike-out | `/Highlight`, `/Underline`, `/StrikeOut` | On saving, the mark snaps to the words under it (one quad per line, from PdfPig). On pages with their own rotation or an offset page box it keeps the drawn rectangle. |
| Sticky note | `/Text` | Text shows as a pop-up comment in other viewers |
| Text box | `/FreeText` | Helvetica (WinAnsi): characters outside Latin-1 are shown as "?" in the appearance — see limitations |
| Rectangle, Ellipse, Line | `/Square`, `/Circle`, `/Line` | Colour and line width. While dragging, the Line tool previews the line itself (start at the press, end under the pointer). |
| Picture / stamp | drawn into the page content | Any picture ImageMagick reads; re-encoded as PNG, so its EXIF data isn't copied |

Every annotation carries its own appearance stream, so it looks the same in viewers that don't draw annotations
themselves. Tests reopen the saved files with PDFsharp and render them with PDFium (Chrome's PDF engine) to check that
the marks are there and in the right place, also on rotated pages.

Annotations already in a PDF are kept when you save, and shown in the preview, but they can't be edited in Foldex.

### Options applied when saving (side panel)

* **Page numbers** with a pattern (`{page} / {total}`) at one of six positions.
* **Header** and **footer** text (may use `{page}` and `{total}`).
* **Watermark**: diagonal text with adjustable opacity.
* **Password to open**: encrypts the saved file with AES-256.
* **Remove title, author and other document info**: checked on the written bytes in tests.

These are drawn into the page content (in the page's shown orientation), so every viewer and printer shows them.

## PDF tools

All results are new files next to the originals ("name (2).pdf" when a name is taken).

| Tool | What it does |
|---|---|
| Merge PDFs | Files in the listed order (move up/down) into one; Save As picks the name |
| Split a PDF | Every N pages, or comma-separated ranges (`1-3, 4, 5-`), one file per part. Invalid ranges are refused with a message instead of silently producing the whole document. |
| Compress PDFs | Filee's own compressor (pictures re-encoded; text and vectors untouched) with three presets: High quality (300 dpi), Balanced (150 dpi), Small file (72 dpi, with a readability warning). Shows before → after and % saved; a file that is already optimised is reported as such and kept as it was. Optionally strips picture metadata. Runs through the normal conversion queue, so it appears in the toast and in Home → Recent conversions. |
| Passwords | Protect (AES-256) or save an unlocked copy **with the current password**. Foldex never tries to open a PDF without its password. |
| Extract text | The PDF's own text layer to .txt. A result with no text is flagged: "may be a scanned PDF, which needs OCR". |
| Recognise text (OCR) | See below |

## OCR

Uses the OCR engine built into Windows 10/11 (`Windows.Media.Ocr`) through Windows PowerShell, so nothing is downloaded
and the base app stays small. The languages offered are the ones Windows has installed (Settings → Time & language →
Language & region → language options → *Optical character recognition*).

* **Scanned PDFs → "name (searchable).pdf"**: each page without a text layer is rendered at 300 dpi, recognised, and the
  words are written over the page as fully transparent text at their positions. The page itself is copied unchanged, so
  the file looks the same (tested pixel by pixel) but can be searched, selected and copied. Pages that already have text
  are left alone.
* **Pictures → "name.txt"**.
* Progress per file, Cancel, and per-file errors.

OCR differs from **Extract text**: Extract reads text the PDF already contains; OCR reads text from pictures of pages.

## Editing existing text, forms and signatures

Engine code: `src/Filee.Engines/Pdf/PdfContentEditing.cs`, run as part of `PdfEditor.Save`.

When the saved document keeps all the pages of one file in their original order, Foldex changes that file as a whole
(so its form, bookmarks and links stay); otherwise it builds a new PDF from the pages, as before.

### Edit existing text (✎ tool)

Click a line of text: it becomes a text change, shown in the preview, with its text in the side panel. Type the new
text and save. A click on a line, or within 8 pt of it (above, below or just past its end), picks the nearest line; small text is only a few points tall. When nothing is picked, the message says why: no text on the page (scanned: run OCR in PDF tools), a turned page, or no line near the click.

* The page's content stream is parsed and every text operator that **starts on that line** is removed; the new text is
  drawn at the same baseline, size and colour, in the closest installed font (same family when Windows has it, bold /
  italic kept, Arial otherwise). The old text is really gone, not painted over.
* After writing, the file is **read back**: the new text must be in the line's place and every other word of the page
  must still be there. If not (text inside form XObjects, unusual encodings, a line that shares an operator with other
  text), **nothing is saved** and the message suggests a text box instead.
* One line at a time; the line keeps its starting point, so longer text runs further right (no reflow of the paragraph).
* Not on turned pages (turn back, edit, turn again), pages with an offset page box, or scanned pages (no text; run OCR
  first or use a text box).

### Fill forms

PDFs with AcroForm fields show a **Form** card in the side panel: text fields, check boxes, lists and radio groups, by
their label. Read-only and signature fields are shown but can't be changed. Values are written into the form itself
(`/V`, and `/NeedAppearances` so viewers redraw the fields). Filling needs the pages in their original order; reorder
after saving the form. XFA-only forms aren't supported.

### Signatures

* **Signature Studio** (fingerprint icon in the toolbar, then click where to sign):
  * **Type** your name and pick one of five styles, each previewed on paper with your name: *Classic Script* (Great
    Vibes), *Modern Signature* (Dancing Script), *Formal Cursive* (Pinyon Script), *Bold Signature* (Kaushan Script),
    *Minimal Signature* (Sacramento). The fonts are bundled (SIL Open Font License), so this works offline and looks the
    same on every PC.
  * **Draw** with the mouse, a pen or a finger: stroke thickness, Undo stroke, Clear.
  * **Upload** a photo or scan (up to 25 MB / 100 megapixels): turned upright, white background made transparent
    (optional), trimmed to the ink, scaled down to 2000 px. Broken or blank pictures are refused with a message.
  * Ink colour (black, navy, blue, burgundy) for typed and drawn signatures; **width on the page** with the height
    following the aspect ratio; a live preview with its size in points. **Insert signature** places it at the click;
    **Use last signature** reuses the newest saved one.
  * On the page it's a picture mark: drag to move, drag the corner handle to resize (aspect ratio kept), arrow keys
    nudge (Shift: 10 pt), Delete removes. It's written into the page when saving, at the same place and size on turned
    pages too, with no white box around it.
  * Signatures are kept as transparent PNGs in `%APPDATA%\Foldex\signatures`. A typed, drawn or uploaded signature is
    a picture of a signature, **not** a cryptographic signature.
* **Sign with a certificate** (When saving): choose a `.pfx` / `.p12` file and its password. The saved PDF gets an
  invisible PKCS#7 signature (SHA-256, PDFsharp's signer) over the whole file; viewers show whether it was changed
  afterwards. Self-signed certificates show as "unknown signer" until trusted. Can't be combined with a password.

### Not possible

Reflowing paragraphs, changing pictures inside the page, or editing text in scanned pages. A white rectangle over text
is **not** redaction — the text stays in the file.

## Security and reliability

* PDFs are untrusted input: open errors (broken files, wrong passwords) become clear messages; nothing half-written is
  left behind (writes go to a temporary file next to the target, then replace it in one step).
* Encrypted PDFs need their password; Foldex never bypasses encryption. PDFsharp opens the standard security
  handlers (RC4/AES 128/256); other handlers fail with a message.
* Passwords are not logged or stored; the source password is only kept in memory while the document is open.
* OCR passes file paths to PowerShell as separate arguments (and a list file), never inside a command string; the
  script is written to a new temporary file for each run and deleted afterwards.
