# Specification 1 — “One more inch of editor” (list concatenation, click checkbox, code highlighting)

- **Target version:** v2.2.2 → **v2.3.0 suggested** (Additional features, minor)
- **Created date:** 2026-07-06
- **Status:** Specifications before implementation (code unchanged)
- **Prior verification:** All assertions in this specification must be based on the actual source/installation package (`@codemirror/lang-markdown@6.5.0`,
  Reproduced and verified with lockfile pin). See §0.

---

## 0. Preliminary verification results (premise of the specification)

### 0-1. Hypothesis that “automatic list concatenation is dead” → **Dismissed. It already works.**

The suspect point is in `tools/mdeditor/editor.js:247`.
`Prec.high(keymap.of([indentWithTab, ...defaultKeymap, ...historyKeymap]))`
It was intercepting the Enter processing of `markdown({ addKeymap: true })`(`editor.js:240`).

Ground truth result (with the same extension array as the installed `@codemirror/lang-markdown@6.5.0` + editor.js)
Configure `EditorState` to check `state.facet(keymap)` dispatch order):

```text
Enter dispatch order:     ["insertNewlineContinueMarkup(markdown)", "insertNewlineAndIndent(default)"]
Backspace dispatch order: ["deleteMarkupBackward(markdown)", "deleteCharBackward"]
```

Reason: lang-markdown 6.5.0 is already registered internally as `Prec.high(keymap.of(markdownKeymap))` when `addKeymap`.
(`node_modules/@codemirror/lang-markdown/dist/index.js` L421-422). Within the same `Prec.high` bucket,
The order of appearance in the extended array determines priority, since `markdown()` (line 240) precedes the last keymap (line 247).
The markdown keymap is always executed first. Bundle drift verification of CI (`.github/workflows/ci.yml` editor-bundle job)
Since committed bundle = lockfile build is guaranteed, **Continuation is still alive in the deployment bundle.**

→ The work of feature 1 is reduced not to “implementation” but to (a) explicitly solidifying implicit ordering dependencies and (b) planting regression tests.

### 0-2. `insertNewlineContinueMarkup` actual semantics (based on UX definition)

| input(cursor = end of line) | Enter Result | Remarks |
|---|---|---|
| `-item` | `- item\n- ` | Continuing markers |
| `1. one` | `1. one\n2. ` | Auto increment number |
| `1. one▮\n2. two` (end of line 1) | `1. one\n2. \n3. two` | **Automatically renumber following items** |
| `- [x] done` | `- [x] done\n- [ ] ` | Continuing tasks with an empty checkbox |
| `-a\n-b` | `… \n - ` | Maintain nesting depth |
| `> q` | `> q\n> ` | Continue citation |
| `- ` (the only empty item) | `` (empty document) | Immediately Unlist |
| `- a\n- b\n- ` (3rd empty item) | `- a\n- b\n` | Immediately Unlist |
| `- item\n- ` (empty 2nd of 2 items) | 1st Enter: Insert blank line → 2nd Enter: Remove marker | CM6 intrinsic semantics (§1-2) |
| plain text line | Do not process → Fall through to defaultKeymap | summit |

### 0-3. GFM task syntax tree (target node for checkbox widget)

When parsing `- [ ] todo` with `markdownLanguage` (GFM), the node:
`BulletList > ListItem > ( ListMark, Task > ( TaskMarker, …Text ) )`.
The widget replacement target is **`TaskMarker`** (`[ ]` / `[x]`, 3 characters fixed), and the bullet hiding target is `ListMark`, which has Task as its sibling.

### 0-4. Highlight language pack bundle size actual measurement

Build editor.js full + probe the §3-3 language set below with esbuild(iife, minify, chrome110):

| | size |
|---|---|
| Currently `mdeditor.bundle.js` | 514,636 B (about 503 KB) |
| Probe with language pack | 771,603 B (about 754 KB) |
| **Increment** | **+257KB (+50%)** |

Since the assets are embedded in a single file exe, the deployment size also increases accordingly. Judged as acceptable based on desktop app.

---

## 1. Goal / Non-goal

### Goal
1. **List automatic continuation (WYSIWYG):** Confirm that it already works by regression testing and specify keymap priority dependence.
2. **Clickable checkbox (WYSIWYG):** Show `- [ ]`/`- [x]` as a rendered checkbox and toggle it by clicking.
3. **Code block syntax highlighting:** ```` ```lang ```` fence code block
   Highlight in both (a) WYSIWYG editor (CM6) and (b) render preview/export (Markdig).

### Non-targets (except explicitly)
- **Click the checkbox in the render preview (Preview WebView2).** `IsScriptEnabled=false` is confirmed by security review.
  It is an invariant (`NoteWindow.xaml.cs:606`), and there is no click → Markdown source reflection path without a script.
  Toggles are **scoped to WYSIWYG editors**. (Markdig task checkbox in preview only displays `disabled` as now.)
- Custom override of empty item Enter semantics. CM6 basics (§0-2) are accepted as is — even renumbering and nesting
  Risks of rewriting a proven implementation by hand > "Enter twice in a 2-item list" Minor UX differences.
- No change in storage format. **Note content remains pure Markdown source** (maintains round-trip compatibility with Vault `.md`).
  Highlight and checkbox widgets are all view decorations and do not change the document. The moment the document changes
  There are only two cases where the user intended: Enter continuation, and toggle a checkbox click (`[ ]`↔`[x]` 3-character equal-length substitution).
- Dark theme highlight palette (single light palette since the note background is a pastel light type).

---

## 2. Define UX behavior

### 2-1. Automatic list continuation (WYSIWYG)

§0-2 Table is the specifications as is. Summary rules:
- Enter from a list item with content → automatically insert marker of the same depth and type (number is +1, automatic renumber of next item).
- Enter → always continue with `- [ ] ` (empty checkbox) in the task item.
- Enter → unlist (remove marker) on an empty item. However, only “the empty second item in a 2-item list” is in CM6 semantics.
  The first Enter inserts a blank line, and the second Enter removes it. **Document and accept.**
- Backspace: Delete a layer of markers from line head (`deleteMarkupBackward`) — Already active, just add test.
- Does not intervene in regular paragraphs (defaultKeymap fall-through).

### 2-2. Clickable checkbox (WYSIWYG)

- **Display:** on lines where there is no cursor (old philosophy in Live Preview = `activeLines`)
  `- [ ]`/`- Hide the `ListMark` ("-") and spaces in [x]` and replace `TaskMarker` with an actual `<input type="checkbox">` widget.
  The line where the cursor is located shows the original source (`- [ ]`) as it is now (same as the existing marker hiding rules).
- **Click:** When the checkbox is clicked
  - Replace the three corresponding `TaskMarker` characters in the document with `[ ]`↔`[x]` (`[X]` uppercase letters are also normalized to `[ ]`).
  - **Cursor/selection does not move.** Set `mousedown` to `preventDefault` to prevent clicks from moving the selection,
    The substitution is equi-length, so the existing cursor position is preserved (selection mapping invariant).
  - One dispatch = one undo step.
  - The toggle follows the existing `changeListener` (250ms debounce) → `postMessage` → C# storage path.
- **Edge Case:**
  - Nested tasks: Each item's TaskMarker is an independent widget — depth-independent behavior.
  - The `[ ]` string in the body after `[x]`: It is not a TaskMarker node, so it is not widgetized (based on the syntax tree).
  - Moving the cursor to that line immediately after the toggle makes the source visible again (active line rule) — this is intended behavior.
- **(optional, nice-to-have)** Strikethrough+blur style on the body of checked items. Implementation only when possible.

### 2-3. Code block syntax highlighting

- **WYSIWYG(CM6):** Finds the language using information strings such as ```` ```js ```` and colors code tokens (keywords/strings/comments/numbers, etc.).
  No language/information available → Monospace is colorless as is. Light background for code block lines (line decoration, optional).
- **Render preview + HTML/PDF export (Markdig):** Same fence block on the server (C#) side
  **Bake in inline style** Highlight. Script line 0 — CSP·Keep `IsScriptEnabled=false` invariant.
- **Supported languages (primary):** JavaScript/TypeScript/JSX, Python, HTML, CSS, JSON, SQL, XML, C/C++, Java,
  C#, Shell (bash), YAML, PowerShell. (CM6 side aliases: `js, jsx, ts, tsx, py, htm, cs, sh, bash, zsh, yml, ps1`, etc.)
- The palettes in the editor and preview are "similar enough" (not forced to be identical). Both sides are legible on a light pastel background.

---

## 3. Technical design

### 3-1. `tools/mdeditor/editor.js` — Specify keymap priority (feature 1)

Currently, `Prec.high(...)` on line 247 is in the same bucket as the markdown keymap, so it relies on the implicit rule of "collocation order".
**Demotes the defaultKeymap bundle to default priority** so that structurally the markdown keymap always wins:

```js
// BEFORE (editor.js:246-247)
Prec.high(formatKeymap),
Prec.high(keymap.of([indentWithTab, ...defaultKeymap, ...historyKeymap])),

// AFTER — Invariant: Enter/Backspace(insertNewlineContinueMarkup / in lang-markdown
// deleteMarkupBackward) is registered as Prec.high (6.5.0 dist L421), so it is always seen before this keymap.
// If you raise this to Prec.high again, list concatenation may be disabled depending on the array order — do not raise it.
Prec.high(formatKeymap),
keymap.of([indentWithTab, ...defaultKeymap, ...historyKeymap]),
```

Impact Analysis: This file contains other keymap sources such as formatKeymap (format-only key, no Enter/Backspace)
Since it is only a markdown keymap, there are no other bindings whose order will change due to demotion. Movement change 0, intention clarification is all.

### 3-2. `tools/mdeditor/editor.js` — Checkbox widget (feature 2)

Use the CM6 official decoration example (boolean toggle) pattern as is. Integrate into existing `buildDecorations` (line 42)
(RangeSetBuilder is a natural fulfillment since add from ascending order is inside the same iterate loop):

```js
// TaskMarker([ ] / [x]) as a clickable checkbox — only on lines without cursor (maintaining the live preview philosophy)
class TaskCheckbox extends WidgetType {
  constructor(checked) { super(); this.checked = checked; }
  eq(other) { return other.checked === this.checked; }
  toDOM() {
    const box = document.createElement("input");
    box.type = "checkbox";
    box.checked = this.checked;
    box.className = "cm-task-toggle";
    return box;
  }
  ignoreEvent(e) { return e.type !== "mousedown"; } // mousedown is handled by the handler below
}

// Add to buildDecorations' enter callback:
if (node.name === "TaskMarker") {
  const line = view.state.doc.lineAt(node.from).number;
  if (!act.has(line)) {
    const checked = /x/i.test(view.state.doc.sliceString(node.from, node.to));
    builder.add(node.from, node.to, Decoration.replace({ widget: new TaskCheckbox(checked) }));
  }
  return;
}
// Hide ListMark("- ") in the Task line: node.name === "ListMark"
// If node.node.nextSibling?.name === "Task", joins HIDE target (including 1 space following).

// Click → Toggle document (cursor not moving). Wire to the eventHandlers of the livePreview plugin:
function toggleTaskAt(view, pos) {
  // pos is the result of posAtDOM of the widget DOM — find the TaskMarker at the corresponding position in the tree and replace it with equal length
  let done = false;
  syntaxTree(view.state).iterate({ from: pos, to: pos + 3, enter: (n) => {
    if (n.name !== "TaskMarker" || done) return;
    const cur = view.state.doc.sliceString(n.from, n.to);
    view.dispatch({ changes: { from: n.from, to: n.to, insert: /x/i.test(cur) ? "[ ]" : "[x]" } });
    done = true;
  }});
  return done;
}
// ViewPlugin.fromClass(..., { decorations: ..., eventHandlers: {
//   mousedown(e, view) {
//     if (e.target.classList?.contains("cm-task-toggle"))
//       return toggleTaskAt(view, view.posAtDOM(e.target)) && e.preventDefault(), true;
//   }}})
```

- `toggleTaskAt` separates view-independent logic as much as possible** (input: state+pos → output: change spec)
  Write verifiable without DOM in Node self-test (§5-1).
- livePreview's `update()` conditions (docChanged/selectionSet/viewportChanged) are sufficient as is —
  The toggle is rebuilt with docChanged.

### 3-3. `tools/mdeditor/editor.js` + `package.json` — CM6 code highlighting (feature 3a)

Use **static import curation set** instead of `@codemirror/language-data` (all languages, several MB) (+257KB ground truth, §0-4):

```js
// Add package.json dependencies (all codemirror official scope, lockfile pin):
// @codemirror/lang-javascript, lang-python, lang-html, lang-css, lang-json,
// lang-sql, lang-xml, lang-cpp, lang-java, legacy-modes
import { LanguageDescription, StreamLanguage } from "@codemirror/language";
import { javascript } from "@codemirror/lang-javascript";
// … (python, html, css, json, sql, xml, cpp, java)
import { csharp } from "@codemirror/legacy-modes/mode/clike";
import { shell } from "@codemirror/legacy-modes/mode/shell";
import { yaml } from "@codemirror/legacy-modes/mode/yaml";
import { powerShell } from "@codemirror/legacy-modes/mode/powershell";  // Existence confirmed completed

const codeLanguages = [
  LanguageDescription.of({ name: "javascript", alias: ["js", "jsx", "node"],
    load: async () => javascript({ jsx: true }) }),
  LanguageDescription.of({ name: "typescript", alias: ["ts", "tsx"],
    load: async () => javascript({ typescript: true, jsx: true }) }),
  // … python/html/css/json/sql/xml/cpp (with c alias)/java …
  LanguageDescription.of({ name: "csharp", alias: ["cs", "c#"],
    load: async () => new LanguageSupport(StreamLanguage.define(csharp)) }),
  // … shell(bash/sh/zsh), yaml(yml), powershell(ps1) …
];

// Change line 240:
markdown({ base: markdownLanguage, addKeymap: true, codeLanguages }),
```

Added code token coloring to `hl` (HighlightStyle, line 12) — currently only Markdown tags, so the code tokens are colorless:

```js
{ tag: t.keyword, color: "#7c3aed" },
{ tag: [t.string, t.special(t.string)], color: "#15803d" },
{ tag: t.comment, color: "#9ca3af", fontStyle: "italic" },
{ tag: [t.number, t.bool, t.atom], color: "#b45309" },
{ tag: [t.typeName, t.className, t.namespace], color: "#0e7490" },
{ tag: [t.function(t.variableName), t.function(t.propertyName)], color: "#1d4ed8" },
{ tag: [t.operator, t.punctuation], color: "#6b7280" },
{ tag: [t.propertyName, t.attributeName], color: "#a21caf" },
{ tag: [t.tagName], color: "#be123c" },
```

Note: The `t.monospace` background (line 22) is for inline code — the fence block background is for separate line decoration (on the FencedCode line).
Treated as `rgba(0,0,0,.05)`) or omitted (optional).

### 3-4. `Stickmark/Utils/HtmlRenderer.cs` — Preview/Export Highlights (Feature 3b)

**Comparison of options:**

| plan | Advantages | Disadvantages | Judgment |
|---|---|---|---|
| **(A) ColorCode-Universal** (`ColorCode.HtmlFormatter` NuGet) | MS CommunityToolkit Organization·MIT·Offline·`HtmlFormatter` outputs **inline style** → Complies directly with CSP `style-src 'unsafe-inline'`, script 0 | Added 2 new NuGets (Core+HtmlFormatter) supply chain; Language coverage is narrower than CM6 (but covers the entire §2-3 list: C#/C++/CSS/HTML/Java/JS/TS/PS/Python/SQL/XML, etc.) | **Adoption** |
| (B) Markdig.SyntaxHighlighting | Simple wiring | Third party individual maintainer, old version ColorCode wrapper, update identity | Dismissed |
| (C) CM6 only highlights (preview not emphasized) | Supply chain increase 0 | Because the preview is the default screen (split view·`.md` opens as a preview), the feeling is half-hearted | Dismissed |

**Implementation:** Custom renderer in the Markdig pipeline — replace `HtmlObjectRenderer<CodeBlock>` with:

```csharp
// New file: Stickmark/Utils/CodeBlockHighlightRenderer.cs
/// Map FencedCodeBlock's info (```csharp, etc.) to ColorCode language and render it as inline style HTML.
/// For unsupported languages and vice-fence blocks, fall back to <pre><code>, escaped as before.
internal sealed class CodeBlockHighlightRenderer : HtmlObjectRenderer<CodeBlock>
{
    private static readonly HtmlFormatter Formatter = new(StyleDictionary.DefaultLight);

    protected override void Write(HtmlRenderer renderer, CodeBlock block)
    {
        var lang = (block as FencedCodeBlock)?.Info is { Length: > 0 } info
            ? Languages.FindById(MapAlias(info)) : null;
        var code = ExtractSource(block);            // block.Lines → original text
        if (lang is null) { WritePlainEscaped(renderer, code); return; } // maintain current path
        renderer.Write(Formatter.GetHtmlString(code, lang));               // ColorCode performs escaping
    }
    // MapAlias: "js" → "javascript", "cs" → "csharp", "ps1" → "powershell", "yml" → (not supported → null) …
}

// HtmlRenderer.cs — Keep the pipeline as is, just add a renderer replacement point:
// Replace Markdown.ToHtml(source, Pipeline) with MarkdownPipeline + custom HtmlRenderer instance path
//   renderer.ObjectRenderers.ReplaceOrAdd<CodeBlockRenderer>(new CodeBlockHighlightRenderer());
// Render()/RenderDocument() both use the same helper (ToHtmlHighlighted) to consistently apply preview, export, and print.
```

**Security Requirements (Fixed by Test, §5-2):**
- ColorCode output will never include `<script>`·event handler properties —
  ```` ```html<script>… ```` Asserts that input comes only as escaped text.
- Unsupported language fallback path outputs the same escape byte as the existing one.
- Does not change CSP (inline styles are already allowed, scripts are still blocked).
- The stricter CSP (only `img-src data:`) of `RenderDocument` (export) is also unchanged — only the style attribute is highlighted.

**Supply Chain Specification:** `ColorCode.Core` + `ColorCode.HtmlFormatter`(github.com/CommunityToolkit/ColorCode-Universal,
MIT, Microsoft CommunityToolkit organization). **pin exact version** to csproj (latest stable at time of implementation, no scoping allowed);
New dependency notice in CHANGELOG release notes. NuGet signature/package source mapping remains the same as the existing policy.

### 3-5. Asset Pipeline (no changes confirmed)

- The embedded (EmbeddedResource) structure of `Stickmark.csproj:25-30` is the same — no modification is necessary because the bundle file name is immutable.
- `MarkdownWysiwyg.EnsureAssetsExtracted`(`Stickmark/Utils/MarkdownWysiwyg.cs:109`) is used for the first time in each process.
  `File.Create` **overwrites** so no out-of-date extract cache issues — no code changes (just check size in smoke).
- Rebuild procedure: `npm install` in `tools/mdeditor` (add deps → update lockfile) → `npm run build` →
  Copy `dist/mdeditor.bundle.js` to `Stickmark/Assets/mdeditor/`. CI editor-bundle job catches drift.
- `editor.html` has no changes other than 1 line of checkbox cursor style (`.cm-task-toggle { cursor: pointer; }`) — CSP immutable.

---

## 4. Work decomposition + risk classification table

| # | work | file | Risk | Evidence |
|---|---|---|---|---|
| 1 | keymap priority demoted + invariant annotation | `tools/mdeditor/editor.js` | **Low Risk (Sonnet)** | Verified 1-line change + comments, behavior change 0 |
| 2 | Node self-test new (`test.mjs`): Dispatch order, 9 cases of continuation, toggle logic | `tools/mdeditor/test.mjs` (new), `package.json` scripts | **Low Risk (Sonnet)** | Formalization of verification code already written and executed in §0 |
| 3 | Add self-test step to CI | `.github/workflows/ci.yml` | **Low Risk (Sonnet)** | `npm test` 1 step to existing editor-bundle job |
| 4 | Checkbox Widget + Toggle + ListMark Hidden Integration | `tools/mdeditor/editor.js` | **High Risk (Opus Direct)** | Subtle view logic such as decoration alignment, event/cursor interaction, active line switching, etc. |
| 5 | Add CM6 codeLanguages ​​+ HighlightStyle code token + deps | `editor.js`, `package.json`(+lock) | **Low Risk (Sonnet)** | Official API formal pattern, build success with probe, size measurement completed |
| 6 | Rebuild bundle + copy assets + check for drift | `Stickmark/Assets/mdeditor/mdeditor.bundle.js` | **Low Risk (Sonnet)** | Procedure mechanical, CI verified |
| 7 | ColorCode integration (custom Markdig renderer, preview+export) | `Stickmark/Utils/CodeBlockHighlightRenderer.cs` (new), `HtmlRenderer.cs`, `Stickmark.csproj` | **High Risk (Opus Direct)** | Security perimeter (escape, CSP), new supply chain, render pipeline replacement |
| 8 | C# unit test (highlight, escape, fallback) | `Stickmark.Tests/Tests.cs` | **Low Risk (Sonnet)** | Following the existing HtmlRendererTests pattern (Assertion list is based on #7 output, reviewed by Opus) |
| 9 | Single file publish smoke (§5-4) | (verification only) | **Low Risk (Sonnet)** | Checklist execution type - However, for actual use, DB isolation precautions must be followed |
| 10 | Document·CHANGELOG·Version 2.3.0 Upgrade | `CHANGELOG.md`, `README.md`, `Stickmark.csproj` | **Low Risk (Sonnet)** | Document/Version Operations |

Recommended order: 1→2→3 (complete function 1) → 5→4→6 (editor side) → 7→8 (render side) → 9→10.
4 and 7 are independent of each other, so they can be parallel.

---

## 5. Test/verification plan

### 5-1. Node self-test (new `tools/mdeditor/test.mjs`, CI incorporated)

Verification using only `EditorState` + command function without DOM (method demonstrated in §0). Using the `node --test` runner:

- **Keymap regression:** In `state.facet(keymap)`, Enter 1st priority is `insertNewlineContinueMarkup`,
  Backspace 1st priority is `deleteMarkupBackward` (consisting of the same extended array as editor.js).
- **Nine cases of concatenation:** §0-2 Entire table as input/output assertion.
- **Toggle logic:** State level core of `toggleTaskAt` — `[ ]`→`[x]`, `[x]`→`[ ]`, `[X]`→`[ ]`,
  Fake `[ ]` in the text does not respond, cursor position remains unchanged after toggling.
- **Highlight wiring:** for each supported alias (js/ts/py/cs/sh/yml/ps1…)
  `LanguageDescription.matchLanguageName(codeLanguages, alias)` is non-null.
- CI: Add `node --test test.mjs` step after `npm ci` in editor-bundle job (same job as bundle drift verification).

### 5-2. C# unit test (`Stickmark.Tests/Tests.cs`, added to existing HtmlRendererTests)

1. ```` ````csharp ```` block → Presence of inline `style=` highlight span in output, preserving original code text.
2. `<script>alert(1)</script>` in ```` ```html ```` block → Absence of **executable** `<script>` tag in output (check escape).
3. Unsupported language (````` ```zig `````)·No information Fence → Same `<pre><code>` escape fallback as before.
4. Apply highlighting to `RenderDocument` (export) as well + CSP meta invariant.
5. No highlighting applied to inline code (`` ` ``) (maintain existing style).

### 5-3. Headless Chromium Self-Test (Editor DOM Interaction)

Extend `editor.html?selftest=1`(`editor.js:299`) to cover the widget level:
- Add scenario: Document injection → Place cursor on another line → Check existence of `TaskMarker` widget DOM (`.cm-task-toggle`) →
  Programmatically call `toggleTaskAt` → `getMarkdown()` reflects `[x]` → write the resulting JSON to `#selftest`.
- Execution: After serving `Stickmark/Assets/mdeditor/` to a local static server (`npx http-server`, etc.)
  Asserting the content of `#selftest` in `chrome --headless=new --dump-dom http://127.0.0.1:<port>/editor.html?selftest=1`.
  (Make sure to serve http as CSP `script-src 'self'` is unstable on file:// origins.)
- CI inclusion is optional (if Runner Chrome is available) — If not possible, maintain a manual checklist before release and 5-1 covers the logic.

### 5-4. Single file publish smoke (prevents relapses in the past — required)

> ⚠️ **Isolation of Real-World Data:** Smoke should not terminate a running Stickmark, and must be stored under a separate user account or
> Proceed after backing up `%LOCALAPPDATA%\Stickmark` (principle of notifying users before touching live DB/settings).

1. `dotnet publish Stickmark/Stickmark.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish`
   (Same flag as in release.yml — disable alternative verification with Debug run).
2. Run `publish/Stickmark.exe` alone → Create markdown note → WYSIWYG toggle ON.
3. Check: (a) Load the editor normally (past regression point: extract embedded assets), (b) enter `- [ ]` and move the cursor to another line →
   Show checkbox, toggle click, reflect `[x]` in source, (c) Continue Enter in list/increase number,
   (d) ```` ```js ```` block color, (e) code block highlighting in preview (split), (f) external browser when clicking link (proof of keeping script OFF).
4. The size of `%LOCALAPPDATA%\Stickmark\mdeditor\mdeditor.bundle.js` matches the new bundle (approximately 754KB±) — ensure that it is not a remnant of the old asset.
5. Vault round trip: Export note as `.md` → Open file → Toggle/Continue. Check if the result is saved as pure Markdown.

### 5-5. Check for regression

- All 107 existing tests are green (`dotnet test -c Release`).
- CI editor-bundle drift green (matches rebuild including lockfile update).

---

## 6. Expected size and release

### Scale of change

| file | change | scale (joules) |
|---|---|---|
| `tools/mdeditor/editor.js` | Edit | +150~200 (80 widgets, 50 language packs, 12 highlight tags, 30 annotation/self-test extensions) |
| `tools/mdeditor/test.mjs` | New | ~150 |
| `tools/mdeditor/package.json` + lock | Added 10 deps | small amount + lock update |
| `Stickmark/Assets/mdeditor/mdeditor.bundle.js` | Rebuild | 514KB → approximately 754KB (+257KB actual measurement) |
| `Stickmark/Assets/mdeditor/editor.html` | Edit | +1 (cursor style) |
| `Stickmark/Utils/CodeBlockHighlightRenderer.cs` | New | ~80 |
| `Stickmark/Utils/HtmlRenderer.cs` | Edit | ±20 |
| `Stickmark/Stickmark.csproj` | 2 NuGets + versions | +3 |
| `Stickmark.Tests/Tests.cs` | Add ~6 tests | +60 |
| `.github/workflows/ci.yml` | Self-test step | +5 |
| `CHANGELOG.md`, `README.md` | document | +30 |

Total: approximately 11 source files, net increase of approximately 500-600 lines (excluding bundles). New supply chain: NuGet 2 (ColorCode), npm 10 (all codemirror official scope/lockfile pins).

### Release Proposal
- **v2.3.0** (minor — 3 user-visible features, no destructive changes, unchanged storage format).
- CHANGELOG (Keep a Changelog) Added: List automatic continuation regression fixation/click checkbox/code highlight (editor + preview),
  Changed: Editor bundle size +257KB, Dependencies: ColorCode-Universal Additional notice.
- Follow the screen-capture skill procedure when updating README screenshots before tagging.
