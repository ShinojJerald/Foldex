<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="branding/foldex-logo-dark.png">
    <img src="branding/foldex-logo-light.png" alt="Foldex" width="320">
  </picture>
</p>

<h3 align="center">Edit the text in any PDF — free, offline, nothing uploaded.</h3>

<p align="center">
  Click a line in a PDF and retype it. Fill forms, sign, merge, compress and OCR.<br>
  Plus a converter for 244 file formats and tools to tidy your folders. For Windows 10 and 11.
</p>

<p align="center">
  <a href="https://github.com/ShinojJerald/Foldex/releases/latest"><img src="https://img.shields.io/badge/Download%20for%20Windows-Foldex%201.0.0-4F46E5?style=for-the-badge&logo=windows&logoColor=white" alt="Download Foldex for Windows"></a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-2563EB" alt="Windows 10 | 11">
  <img src="https://img.shields.io/badge/price-free-1D9E75" alt="Free">
  <img src="https://img.shields.io/badge/works-offline-0F766E" alt="Works offline">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-6A62C9" alt="MIT license"></a>
</p>

<p align="center">
  <img src="docs/media/foldex-edit-text.gif" alt="Clicking the heading of a PDF, retyping it, and saving" width="800">
</p>

## Why Foldex

Most free PDF editors only let you put a text box **over** old text. Foldex changes the page's own text: the old
words are removed and the new ones are written in their place, in the same size and colour. Then it reads the saved
file back to make sure nothing else on the page changed.

Everything happens on your PC. Foldex works offline and never uploads your files.

| | Foldex | Adobe Acrobat | iLovePDF (web) | PDF24 Creator |
|---|---|---|---|---|
| Price | Free | Paid subscription | Free with limits, paid plans | Free |
| Works offline, files stay on your PC | ✅ | ✅ | ❌ files are uploaded to its servers | ✅ |
| Change existing text in a PDF | ✅ one line at a time | ✅ full editing | — | — |
| Fill forms, sign, merge, split, compress, OCR | ✅ | ✅ | ✅ | ✅ |
| Converter for images, documents, audio, video, e-books… | ✅ 244 formats | PDF-centred | PDF-centred | PDF-centred |
| Folder tools (duplicates, metadata, batch rename) | ✅ | — | — | — |
| Open source | ✅ MIT | — | — | — |

Acrobat is the most complete editor if you can pay for it. PDF24 is a great free, offline toolbox, but it adds new text
rather than changing what's there. Foldex brings real text editing, a broad converter and file utilities together, for
free, as open source. (Comparison as of 2026; check each product's site for current details.)

## Screenshots

<p align="center">
  <img src="docs/media/foldex-pdf-editor.png" alt="PDF editor with an edited heading and a signature on the signature line" width="100%">
</p>

<table>
  <tr>
    <td width="50%" valign="top"><img src="docs/media/foldex-signature-studio.png" alt="Signature Studio with five signature styles"><br>
      <b>Signature Studio</b> — type your name in five styles, draw it with a mouse, pen or finger, or upload a photo of
      it. Place it, move it and resize it on the page.</td>
    <td width="50%" valign="top"><img src="docs/media/foldex-pdf-tools.png" alt="PDF tools after compressing two PDFs"><br>
      <b>PDF tools</b> — merge, split, compress (with before and after sizes), passwords, extract text and OCR.</td>
  </tr>
  <tr>
    <td width="50%" valign="top"><img src="docs/media/foldex-file-utilities.png" alt="File utilities listing the largest files in a folder"><br>
      <b>File utilities</b> — duplicates, largest files, wrong extensions, photo metadata, compare and batch rename.</td>
    <td width="50%" valign="top"><img src="docs/media/foldex-home-light.png" alt="Foldex home screen in the light theme"><br>
      <b>Light and dark themes</b>, search with <kbd>Ctrl</kbd>+<kbd>K</kbd>, and English, 한국어, Polski and 简体中文.</td>
  </tr>
</table>

## Features

### PDF editor

- **Edit existing text** — click a line and retype it. If a page can't be changed safely, nothing is saved and Foldex
  tells you why.
- **Fill forms** — text fields, check boxes, lists and radio buttons, saved into the PDF's own form.
- **Sign** — a typed, drawn or uploaded signature placed on the page, or a real digital signature with your
  certificate (.pfx).
- **Annotate** — highlight, underline and strike-out that follow the words, notes, text boxes, shapes, lines and pictures.
- **Pages** — drag to reorder, rotate, duplicate, delete, insert from another PDF, export a selection.
- **When saving** — page numbers, header and footer, watermark, password (AES-256), remove document info.
- **Safe** — undo and redo, *Save / Don't save / Cancel* when closing, and the original file is never changed unless
  you save over it.

### PDF tools

Merge · split by ranges or every N pages · compress · add or remove a password · extract text · **OCR** to make
scanned PDFs searchable, using the text recognition built into Windows.

### Converter

Convert images (HEIC, WebP, AVIF, RAW, SVG…), PDF, Word, Excel, PowerPoint, HWP, e-books, audio, video, archives,
fonts, CAD and 3D models — 244 formats. Hold <kbd>Ctrl</kbd> while dragging files in Explorer and the **Quick panel**
opens at your cursor; drop the files on a format. You can also right-click files (*Convert with Foldex*), use the drop
zone on Home, set up **Watch folders** that convert new files automatically, or use the command line.

<p align="center"><img src="docs/media/foldex-quick-panel.png" alt="Quick panel with conversion targets" width="300"></p>

### File utilities

Find duplicate files and the largest files in a folder · find files whose content doesn't match their extension ·
compare two files · view and remove photo metadata (EXIF, GPS) · rename many files at once with a preview.
Scans only read your files; nothing is deleted or moved.

## Install

1. Download **Foldex-1.0.0-win-Setup.exe** from the [latest release](https://github.com/ShinojJerald/Foldex/releases/latest).
2. Run it. Because the installer isn't code-signed yet, Windows may say *"Windows protected your PC"* — choose
   **More info → Run anyway**. You can check the file against `Foldex-1.0.0-SHA256SUMS.txt` on the release page.
3. Start **Foldex** from the Start menu.

A portable ZIP is on the release page too: unzip it and run `Foldex.exe`.
Uninstall from **Settings → Apps → Foldex**. Your settings stay in `%APPDATA%\Foldex` unless you delete that folder.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| <kbd>Ctrl</kbd>+<kbd>K</kbd> | Search tools and pages |
| Hold <kbd>Ctrl</kbd> while dragging files in Explorer | Open the Quick panel at the cursor |
| <kbd>Ctrl</kbd>+<kbd>O</kbd> / <kbd>Ctrl</kbd>+<kbd>S</kbd> / <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>S</kbd> | Open / Save / Save as |
| <kbd>Ctrl</kbd>+<kbd>W</kbd> | Close the PDF |
| <kbd>Ctrl</kbd>+<kbd>Z</kbd> / <kbd>Ctrl</kbd>+<kbd>Y</kbd> | Undo / Redo |
| <kbd>Ctrl</kbd>+<kbd>R</kbd> / <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>R</kbd> | Rotate pages right / left |
| <kbd>Ctrl</kbd>+<kbd>D</kbd>, <kbd>Delete</kbd> | Duplicate / delete selected pages (or the selected mark) |
| <kbd>Ctrl</kbd>+<kbd>+</kbd> / <kbd>Ctrl</kbd>+<kbd>-</kbd> / <kbd>Ctrl</kbd>+<kbd>0</kbd> | Zoom in / out / fit page |
| Arrow keys (<kbd>Shift</kbd> for bigger steps) | Nudge the selected mark or signature |

## Command line

Tick *Add the "foldex" command to PATH* in the installer, then:

```powershell
foldex convert photo.heic --to jpg
foldex convert *.png --to webp --quality 80 -o converted
foldex watch D:\Inbox --to pdf --move-originals
foldex formats
```

## Privacy

Your files never leave your PC. Foldex doesn't upload anything, has no account and no tracking. The only time it goes
online is to download an optional conversion engine when you ask for it. Saved signatures stay in
`%APPDATA%\Foldex\signatures`.

## Known limitations

Text editing works line by line and doesn't re-flow paragraphs; scanned pages need OCR first; one PDF is open at a
time; the installer isn't code-signed yet; Windows only for now. The full list:
[docs/KNOWN_LIMITATIONS.md](docs/KNOWN_LIMITATIONS.md).

## Build from source

You need Windows 10/11 x64 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
git clone https://github.com/ShinojJerald/Foldex.git
cd Foldex
dotnet build Filee.slnx -c Release

# Run without touching an installed Foldex's settings
$env:FILEE_DATA_DIR = "$env:TEMP\foldex-dev"; dotnet run --project src/Filee.App -c Release

# Tests
dotnet test tests/Filee.Engines.Tests -c Release
dotnet test tests/Filee.App.Tests -c Release

# Installer and portable ZIP → Releases\
.\build\build-installer.ps1 -Portable
```

Project names still start with *Filee* (the codebase Foldex grew from); the app, executable and installer are Foldex.
More: [docs/BUILD_AND_INSTALL.md](docs/BUILD_AND_INSTALL.md) · [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) ·
[docs/PDF_FEATURES.md](docs/PDF_FEATURES.md).

Foldex has about 1,580 automated tests, including tests that drive the app with simulated mouse and keyboard input and
render saved PDFs to check them; see [docs/TEST_RESULTS.md](docs/TEST_RESULTS.md).

## Contributing

Bug reports, ideas and format requests are welcome in [Issues](https://github.com/ShinojJerald/Foldex/issues). Please
don't attach private documents; a small sample file that shows the problem helps most.

## Support

If Foldex saves you time, you can [buy me a coffee](https://buymeacoffee.com/tunekadal) or follow my work on
[GitHub](https://github.com/ShinojJerald).

## License and credits

Foldex is released under the [MIT License](LICENSE).

- Built on [Filee](https://github.com/KnifeLemon/Filee) by Myeonggu Kim (KnifeLemon) and contributors, MIT License.
- Signature fonts (Great Vibes, Dancing Script, Pinyon Script, Kaushan Script, Sacramento) are under the SIL Open Font
  License 1.1; their licence texts are in
  [src/Filee.App/Assets/Fonts/Signatures](src/Filee.App/Assets/Fonts/Signatures) and are installed with the app.
- Other libraries and engines: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
