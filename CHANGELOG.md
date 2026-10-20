# Changelog

What changed in each version of Foldex.

Each version's section starts with an `<!-- update -->` block (hidden on GitHub). The app's update notice shows its
level and its one-line summaries in the user's language (`en`, `ko`, `zh`, `pl`; a language without lines shows the
English ones): `required` (wrong or lost results, security, Foldex not starting; shown every start), `recommended` (bug
fixes; shown once per version) or `optional` (new features; sidebar button only).

## 1.0.0

<!-- update
level: optional
en: Foldex: a PDF editor with page thumbnails, annotations, undo and save
en: PDF tools: merge, split, compress with sizes, passwords and OCR
en: File utilities: duplicates, wrong extensions, picture metadata and batch rename
ko: Foldex: 페이지 미리 보기, 주석, 실행 취소, 저장이 있는 PDF 편집기
ko: PDF 도구: 병합, 분할, 압축, 암호, OCR
ko: 파일 도구: 중복 파일, 잘못된 확장자, 그림 메타데이터, 일괄 이름 변경
-->

The first Foldex release. Foldex is built on [Filee](https://github.com/KnifeLemon/Filee) 1.12.0 (MIT) and installs next to it (its own folder, settings and Explorer entry).

- **PDF editor** (new, Workspace → PDF editor): open a PDF and see its pages as thumbnails with a zoomable preview.
  Reorder pages by dragging (or Alt+↑/↓), rotate, duplicate, delete, insert pages from another PDF and export the
  selected pages, with undo and redo. Add highlights, underlines and strike-outs (they follow the words under them),
  sticky notes, text boxes, rectangles, ellipses, lines and pictures. When saving, add page numbers, a header, a footer,
  a watermark, a password (AES-256) or remove the document information. The original file is never changed; the first
  save asks where to save, and you're asked before unsaved changes are lost.
- **Edit existing text, fill forms, sign**: click a line of a PDF's own text and retype it (checked after saving;
  nothing is saved if the page can't be changed safely), also in PDFs printed by Chrome / Edge; fill AcroForm fields in
  the side panel; sign the saved PDF with a certificate (.pfx, PKCS#7 SHA-256). Saving a document that keeps its pages
  in order now keeps its form, bookmarks and links.
- **Signature Studio**: type your name in five signature styles (bundled fonts, works offline), draw it, or upload a
  picture (white background removed); choose ink colour and size with a live preview, insert it where you clicked, then
  move, resize (aspect ratio kept) or nudge it.
- **Fixed (final pass)**: drawing in Signature Studio left no ink; clicking small text often said "no editable text"
  (clicks now pick the nearest line); saved pages got an empty crop box that blocked editing them again; the Line tool
  previewed a box, so lines came out diagonal (it now previews the line).
- **Close** for PDFs (Ctrl+W) with **Save / Don't save / Cancel**, also when opening another PDF or closing the window;
  a cancelled or failed save never closes the document. After saving, the saved file becomes the document, so edits are
  never applied twice (this fixes editing that stopped working after saving over the original).
- **PDF tools** (new): merge PDFs in a chosen order, split by page ranges or every N pages, compress with three presets
  showing the size before and after, protect with a password or save an unlocked copy (with the password), extract text,
  and make scanned PDFs searchable with the OCR built into Windows (nothing is uploaded or downloaded).
- **File utilities** (new): find exact duplicates (SHA-256) and the largest files in a folder, find files whose content
  doesn't match their extension, compare two files, view a picture's EXIF and GPS data and save a copy without it
  (checked afterwards), and rename many files with a preview. Scans never delete or move files.
- **New look ("Nebula")**: a night-sky dark theme (default) and a soft lavender light theme, the logo's cyan → violet
  gradient kept for the brand, flat accent buttons and navigation, a top bar with search (Ctrl+K) and a light/dark
  switch, and a sidebar grouped into Workspace and Settings. Primary buttons meet WCAG AA contrast.
- **Home**: a hero banner, a drop zone with Browse files / Browse folder, tool tiles (including Fill & sign), a live
  Task queue (progress, cancel, open folder), Recent files with type filters, and the conversion history.
- No About page: the version and a **Licenses** link sit at the bottom of the sidebar; LICENSE.txt and
  THIRD-PARTY-NOTICES.txt ship next to Foldex.exe.
- **Quick panel** replaces the ring-shaped toolbar: hold the shortcut while dragging files (or use Browse / the drop
  zone) and a card of action tiles opens at the cursor; drop the files on a tile or click it. Groups show as sections.
  After a drop the card shows the progress and ends with ✓ or ✕. The settings page edits the same panel (drag to reorder,
  ✎ to edit).
- GitHub and support links go to github.com/ShinojJerald and buymeacoffee.com/tunekadal.
- Automatic update checks are off in Foldex (Filee's releases are a different app).
