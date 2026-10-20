# Known limitations

What Foldex 1.0.0 doesn't do yet, or does with a catch. If something here blocks you, please
[open an issue](https://github.com/ShinojJerald/Foldex/issues).

## Editing PDFs

- **Changing existing text works one line at a time.** The new text starts where the old line started; a longer line
  runs further right instead of re-flowing the paragraph. It is written in the closest font installed on your PC, which
  can differ slightly from the PDF's own font.
- **Some text can't be changed:** text on pages stored rotated inside the PDF (portrait pages turned to landscape by
  the file itself), text in scanned pages (they're pictures — run OCR in PDF tools first), and text drawn in unusual ways.
  Foldex checks every change after saving; if a page can't be changed safely, nothing is saved and you're told so. Use
  a text box instead in that case.
- **Pictures inside the page** can't be replaced or edited.
- **A box or shape drawn over text doesn't remove the text.** It stays in the file underneath, so this isn't redaction.
- **Annotations that were already in the PDF** are kept and shown, but can't be selected, moved or deleted.
- **Text boxes and sticky notes** show Latin characters only in their on-page appearance; Korean, Chinese and other
  scripts appear as "?" there (the note's own text is stored correctly).
- **Forms:** standard PDF forms (AcroForm) only, not XFA forms. Fill the form before rearranging pages.
- **One PDF open at a time**, no printing from the editor (open the saved file in your PDF viewer to print), and no
  autosave — save your work before closing.
- **Undo** starts again after each save.

## Signatures

- A typed, drawn or uploaded signature is a **picture** of a signature, like signing a printout. It is not a
  cryptographic signature.
- **Sign with a certificate** adds a real digital signature, but it's invisible on the page, can't be combined with a
  password, and has no trusted timestamp. Editing and saving a signed PDF again breaks that signature (as in any editor).
- Four ink colours; signatures always keep their proportions when resized.

## PDF tools

- **Compress** makes the pictures in a PDF smaller. PDFs that are mostly text don't get much smaller.
- **OCR** uses the text recognition built into Windows, so it reads the languages Windows has OCR packs for (English
  is on most PCs). Handwriting and blurry scans are recognised poorly.
- Very large PDFs (hundreds of MB) use about that much memory while open.

## Converting files

- Some formats need an optional engine (for example FFmpeg for video, LibreOffice for some office formats). Foldex
  offers to download it when you first need it.

## Installing

- **The installer isn't code-signed yet**, so Windows may show "Windows protected your PC". Choose **More info → Run
  anyway** if you downloaded it from this project's Releases page, and compare the file with the published SHA-256
  checksum.
- **Windows 10 (1809) or 11, 64-bit only.** macOS and Linux versions aren't available yet.
- **Updates:** Foldex doesn't check for new versions by itself yet. Watch the
  [Releases page](https://github.com/ShinojJerald/Foldex/releases) (GitHub's *Watch → Custom → Releases*).
- On Windows 11, *Convert with Foldex* is under **Show more options** in the right-click menu (and in *Send to*).
- **If you also use Filee:** both apps can react to the same drag shortcut. Change it in one of them (Settings →
  Shortcuts). Settings and downloaded engines aren't shared between the two.
- The Korean, Polish and Chinese translations would benefit from a review by native speakers.
