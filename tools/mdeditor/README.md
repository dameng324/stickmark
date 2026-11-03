# WYSIWYG Markdown Editor Bundle (CodeMirror 6)

This is the dev source for creating Stickmark's markdown note WYSIWYG editor (offline).
Build artifacts are copied to `Stickmark/Assets/mdeditor/` and included in the app.

## Configuration
- `editor.js` — CodeMirror 6 settings + live preview (marker hidden) + WebView2 bridge
- `build.mjs` — Create a single IIFE bundle with esbuild
- `package.json` — Dependencies (CodeMirror 6 modules, esbuild)

## Rebuild
```bash
cd tools/mdeditor
npm install
npm run build          # → dist/mdeditor.bundle.js
# Copy the output as an app asset
cp dist/mdeditor.bundle.js ../../Stickmark/Assets/mdeditor/
cp dist/editor.html ../../Stickmark/Assets/mdeditor/ # Keep host HTML in this folder
```

> `editor.html` (a strict CSP host page) is managed directly in `Stickmark/Assets/mdeditor/`.
> `node_modules/`, `dist/` do not commit (`.gitignore`).

## Security notes
- Scripts and web messages are enabled in the dedicated WYSIWYG WebView2. The rendered preview keeps a strict CSP and only runs a host-injected double-click listener.
- Full network blocking: page CSP `default-src 'none'`(+`connect-src 'none'`), virtual host `DenyCors`,
  Off-page navigation and new windows are blocked.
- The note contents are only exchanged as string data (setMarkdown/getMarkdown) rather than HTML injection, so there is no code execution path.
