// Stickmark WYSIWYG markdown editor — CodeMirror 6, fully offline.
// Stores/returns plain Markdown source (round-trips cleanly with vault .md).
import { EditorView, keymap, drawSelection, highlightActiveLine, placeholder } from "@codemirror/view";
import { EditorState, Prec, RangeSetBuilder, EditorSelection } from "@codemirror/state";
import { history, defaultKeymap, historyKeymap, indentWithTab } from "@codemirror/commands";
import { markdown, markdownLanguage } from "@codemirror/lang-markdown";
import {
  syntaxTree, syntaxHighlighting, HighlightStyle, LanguageDescription, StreamLanguage, LanguageSupport,
} from "@codemirror/language";
import { tags as t } from "@lezer/highlight";
import { Decoration, ViewPlugin, WidgetType } from "@codemirror/view";
import { taskToggleChange } from "./core.mjs";

// ── Code block syntax highlighting: curated set of static imports (primary supported languages) ─────────────
import { javascript } from "@codemirror/lang-javascript";
import { python } from "@codemirror/lang-python";
import { html } from "@codemirror/lang-html";
import { css } from "@codemirror/lang-css";
import { json } from "@codemirror/lang-json";
import { sql } from "@codemirror/lang-sql";
import { xml } from "@codemirror/lang-xml";
import { cpp } from "@codemirror/lang-cpp";
import { java } from "@codemirror/lang-java";
import { csharp } from "@codemirror/legacy-modes/mode/clike";
import { shell } from "@codemirror/legacy-modes/mode/shell";
import { yaml } from "@codemirror/legacy-modes/mode/yaml";
import { powerShell } from "@codemirror/legacy-modes/mode/powershell";

const streamLang = (parser) => new LanguageSupport(StreamLanguage.define(parser));

const codeLanguages = [
  LanguageDescription.of({ name: "javascript", alias: ["js", "jsx", "node"],
    load: async () => javascript({ jsx: true }) }),
  LanguageDescription.of({ name: "typescript", alias: ["ts", "tsx"],
    load: async () => javascript({ typescript: true, jsx: true }) }),
  LanguageDescription.of({ name: "python", alias: ["py"], load: async () => python() }),
  LanguageDescription.of({ name: "html", alias: ["htm"], load: async () => html() }),
  LanguageDescription.of({ name: "css", load: async () => css() }),
  LanguageDescription.of({ name: "json", load: async () => json() }),
  LanguageDescription.of({ name: "sql", load: async () => sql() }),
  LanguageDescription.of({ name: "xml", load: async () => xml() }),
  LanguageDescription.of({ name: "cpp", alias: ["c", "c++", "cc", "h"], load: async () => cpp() }),
  LanguageDescription.of({ name: "java", load: async () => java() }),
  LanguageDescription.of({ name: "csharp", alias: ["cs", "c#"], load: async () => streamLang(csharp) }),
  LanguageDescription.of({ name: "shell", alias: ["bash", "sh", "zsh"], load: async () => streamLang(shell) }),
  LanguageDescription.of({ name: "yaml", alias: ["yml"], load: async () => streamLang(yaml) }),
  LanguageDescription.of({ name: "powershell", alias: ["ps1"], load: async () => streamLang(powerShell) }),
];

// ── Inline token color/thickness (makes Markdown source look like formatting) ───────────────────
const hl = HighlightStyle.define([
  { tag: t.heading1, fontSize: "1.7em", fontWeight: "700", lineHeight: "1.3" },
  { tag: t.heading2, fontSize: "1.42em", fontWeight: "700" },
  { tag: t.heading3, fontSize: "1.22em", fontWeight: "700" },
  { tag: [t.heading4, t.heading5, t.heading6], fontWeight: "700" },
  { tag: t.strong, fontWeight: "700" },
  { tag: t.emphasis, fontStyle: "italic" },
  { tag: t.strikethrough, textDecoration: "line-through" },
  { tag: t.link, color: "#2563eb", textDecoration: "underline" },
  { tag: t.url, color: "#2563eb" },
  { tag: t.monospace, fontFamily: "Consolas, 'Cascadia Mono', monospace", background: "rgba(0,0,0,.06)" },
  { tag: t.quote, color: "#6b7280", fontStyle: "italic" },
  { tag: [t.processingInstruction, t.contentSeparator], color: "#9aa0a6" },
  // ── Code block syntax highlighting token color (light palette single, §2-3) ───────────────────
  { tag: t.keyword, color: "#7c3aed" },
  { tag: [t.string, t.special(t.string)], color: "#15803d" },
  { tag: t.comment, color: "#9ca3af", fontStyle: "italic" },
  { tag: [t.number, t.bool, t.atom], color: "#b45309" },
  { tag: [t.typeName, t.className, t.namespace], color: "#0e7490" },
  { tag: [t.function(t.variableName), t.function(t.propertyName)], color: "#1d4ed8" },
  { tag: [t.operator, t.punctuation], color: "#6b7280" },
  { tag: [t.propertyName, t.attributeName], color: "#a21caf" },
  { tag: [t.tagName], color: "#be123c" },
]);

// ── Live Preview: Hide markdown marker on lines where there is no cursor ───────────────
const HIDE_NODES = new Set([
  "HeaderMark", "EmphasisMark", "StrikethroughMark", "CodeMark", "QuoteMark", "LinkMark",
]);
const hiddenDeco = Decoration.replace({});

// ── Clickable task checkbox: TaskMarker of `- [ ]`/`- [x]` as actual checkbox ──
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
  // Flow the event to CM so that livePreview's mousedown handler handles the toggle.
  ignoreEvent() { return false; }
}

function activeLines(state) {
  const set = new Set();
  for (const r of state.selection.ranges) {
    const a = state.doc.lineAt(r.from).number, b = state.doc.lineAt(r.to).number;
    for (let n = a; n <= b; n++) set.add(n);
  }
  return set;
}

function buildDecorations(view) {
  const builder = new RangeSetBuilder();
  const act = activeLines(view.state);
  for (const { from, to } of view.visibleRanges) {
    syntaxTree(view.state).iterate({
      from, to,
      enter: (node) => {
        const line = view.state.doc.lineAt(node.from).number;

        // Task checkbox: Replace the TaskMarker ([ ]/[x], 3 characters) on the line without the cursor with a click widget
        if (node.name === "TaskMarker") {
          if (!act.has(line)) {
            const checked = /x/i.test(view.state.doc.sliceString(node.from, node.to));
            builder.add(node.from, node.to, Decoration.replace({ widget: new TaskCheckbox(checked) }));
          }
          return;
        }
        // Hide the "- " bullet in the task line (only the checkbox is visible). Limited to ListMark, which has Task as its sibling.
        if (node.name === "ListMark" && node.node.nextSibling && node.node.nextSibling.name === "Task") {
          if (!act.has(line)) {
            let end = node.to;
            if (view.state.doc.sliceString(end, end + 1) === " ") end += 1;
            if (end > node.from) builder.add(node.from, end, hiddenDeco);
          }
          return;
        }

        const isMark = HIDE_NODES.has(node.name);
        // Also hide the URL of the inline link `[text](url)` so that only the link text is visible
        const isLinkUrl = node.name === "URL" && node.node.parent && node.node.parent.name === "Link";
        if (!isMark && !isLinkUrl) return;
        if (act.has(line)) return;            // The line being edited shows the original marker as is.
        let end = node.to;
        if (node.name === "HeaderMark") {
          const ch = view.state.doc.sliceString(end, end + 1);
          if (ch === " ") end += 1;
        }
        if (end > node.from) builder.add(node.from, end, hiddenDeco);
      },
    });
  }
  return builder.finish();
}

const livePreview = ViewPlugin.fromClass(
  class {
    constructor(view) { this.decorations = buildDecorations(view); }
    update(u) {
      if (u.docChanged || u.selectionSet || u.viewportChanged) {
        this.decorations = buildDecorations(u.view);
      }
    }
  },
  {
    decorations: (v) => v.decorations,
    eventHandlers: {
      // Click the checkbox → Toggle the 3-character equal length of the document's TaskMarker. CM returns true
      // preventDefault, so the cursor/selection does not move, and the position is also preserved because it is an equal-length substitution.
      mousedown(e, view) {
        const t = e.target;
        if (t && t.nodeName === "INPUT" && t.classList.contains("cm-task-toggle")) {
          const spec = taskToggleChange(view.state, view.posAtDOM(t));
          if (spec) { view.dispatch({ changes: spec, userEvent: "input" }); return true; }
        }
        return false;
      },
    },
  }
);

const theme = EditorView.theme({
  "&": { fontSize: "14px", height: "100%", background: "transparent", color: "inherit" },
  ".cm-content": { fontFamily: "'Segoe UI','Malgun Gothic',sans-serif", padding: "10px 12px", caretColor: "currentColor" },
  ".cm-scroller": { overflow: "auto", lineHeight: "1.55" },
  "&.cm-focused": { outline: "none" },
  ".cm-line": { padding: "0" },
});

// ── Formatting commands (buttons/shortcuts insert Markdown syntax instead) ──────────────────
function toggleInline(marker) {
  return (view) => {
    view.dispatch(view.state.changeByRange((range) => {
      const { from, to } = range;
      const before = view.state.sliceDoc(Math.max(0, from - marker.length), from);
      const after = view.state.sliceDoc(to, Math.min(view.state.doc.length, to + marker.length));
      if (before === marker && after === marker) {
        // unwrap if already wrapped
        return {
          changes: [{ from: from - marker.length, to: from }, { from: to, to: to + marker.length }],
          range: EditorSelection.range(from - marker.length, to - marker.length),
        };
      }
      return {
        changes: [{ from, insert: marker }, { from: to, insert: marker }],
        range: EditorSelection.range(from + marker.length, to + marker.length),
      };
    }));
    view.focus();
    return true;
  };
}

function setHeading(level) {
  return (view) => {
    view.dispatch(view.state.changeByRange((range) => {
      const line = view.state.doc.lineAt(range.head);
      const m = line.text.match(/^(#{1,6}\s+)/);
      const cur = m ? m[1] : "";
      const curLevel = m ? m[1].trim().length : 0;
      const want = curLevel === level ? "" : "#".repeat(level) + " ";  // Release if at the same level
      const shift = want.length - cur.length;
      return {
        changes: { from: line.from, to: line.from + cur.length, insert: want },
        range: EditorSelection.range(range.anchor + shift, range.head + shift),
      };
    }));
    view.focus();
    return true;
  };
}

const LINE_MARKERS = [/^>\s+/, /^[-*+]\s\[[ xX]\]\s+/, /^[-*+]\s+/, /^\d+\.\s+/];
function setLinePrefix(prefix) {
  return (view) => {
    view.dispatch(view.state.changeByRange((range) => {
      const line = view.state.doc.lineAt(range.head);
      let cur = "";
      for (const re of LINE_MARKERS) { const mm = line.text.match(re); if (mm) { cur = mm[0]; break; } }
      const want = cur === prefix ? "" : prefix;   // If it's the same marker, release it, otherwise replace it
      const shift = want.length - cur.length;
      return {
        changes: { from: line.from, to: line.from + cur.length, insert: want },
        range: EditorSelection.range(range.anchor + shift, range.head + shift),
      };
    }));
    view.focus();
    return true;
  };
}

function insertLink(view) {
  view.dispatch(view.state.changeByRange((range) => {
    const text = view.state.sliceDoc(range.from, range.to) || "link";
    const insert = `[${text}](url)`;
    const urlStart = range.from + 1 + text.length + 2;   // "[" + text + "]("
    return {
      changes: { from: range.from, to: range.to, insert },
      range: EditorSelection.range(urlStart, urlStart + 3), // Select "url"
    };
  }));
  view.focus();
  return true;
}

const COMMANDS = {
  bold: toggleInline("**"),
  italic: toggleInline("*"),
  strike: toggleInline("~~"),
  code: toggleInline("`"),
  h1: setHeading(1),
  h2: setHeading(2),
  h3: setHeading(3),
  bullet: setLinePrefix("- "),
  numbered: setLinePrefix("1. "),
  task: setLinePrefix("- [ ] "),
  quote: setLinePrefix("> "),
  link: insertLink,
};

const formatKeymap = keymap.of([
  { key: "Mod-b", run: COMMANDS.bold, preventDefault: true },
  { key: "Mod-i", run: COMMANDS.italic, preventDefault: true },
  { key: "Mod-Shift-x", run: COMMANDS.strike, preventDefault: true },
  { key: "Mod-`", run: COMMANDS.code, preventDefault: true },
  { key: "Mod-k", run: COMMANDS.link, preventDefault: true },
  { key: "Mod-Alt-1", run: COMMANDS.h1, preventDefault: true },
  { key: "Mod-Alt-2", run: COMMANDS.h2, preventDefault: true },
  { key: "Mod-Alt-3", run: COMMANDS.h3, preventDefault: true },
  { key: "Mod-Shift-8", run: COMMANDS.bullet, preventDefault: true },
  { key: "Mod-Shift-7", run: COMMANDS.numbered, preventDefault: true },
  { key: "Mod-Shift-9", run: COMMANDS.task, preventDefault: true },
  { key: "Mod-Shift-.", run: COMMANDS.quote, preventDefault: true },
]);

// ── English UI text for the editor toolbar
const L = {
  placeholder: "Type here…  (Ctrl+B bold, Ctrl+I italic)",
  bold: "Bold (Ctrl+B)", italic: "Italic (Ctrl+I)", strike: "Strikethrough (Ctrl+Shift+X)",
  code: "Inline code (Ctrl+`)", h1: "Heading 1 (Ctrl+Alt+1)", h2: "Heading 2 (Ctrl+Alt+2)",
  bullet: "Bulleted list", numbered: "Numbered list", task: "Checklist", quote: "Quote", link: "Link (Ctrl+K)",
};

// ── Host (C#) Bridge ─────────────────────────────────────────────────────
let view = null;
let suppress = false;
let debounce = null;

function postChange() {
  if (suppress) return;
  const md = view.state.doc.toString();
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.postMessage(JSON.stringify({ type: "change", md }));
  }
}

const changeListener = EditorView.updateListener.of((u) => {
  if (!u.docChanged) return;
  clearTimeout(debounce);
  debounce = setTimeout(postChange, 250);
});

function createEditor(initial) {
  view = new EditorView({
    parent: document.getElementById("editor"),
    state: EditorState.create({
      doc: initial || "",
      extensions: [
        history(),
        drawSelection(),
        highlightActiveLine(),
        EditorView.lineWrapping,
        markdown({ base: markdownLanguage, addKeymap: true, codeLanguages }),
        syntaxHighlighting(hl),
        livePreview,
        theme,
        changeListener,
        placeholder(L.placeholder),
        Prec.high(formatKeymap),
        // Invariant: lang-markdown's Enter/Backspace(insertNewlineContinueMarkup /
        // deleteMarkupBackward) is registered as Prec.high (6.5.0 dist L421), so it is always seen before this keymap.
        // If you raise this to Prec.high again, list concatenation may be disabled depending on the array order — do not raise it.
        keymap.of([indentWithTab, ...defaultKeymap, ...historyKeymap]),
      ],
    }),
  });
  view.focus();
}

// Toolbar button wiring — Prevent mousedown so that editor selection remains when clicked.
function wireToolbar() {
  document.querySelectorAll("#toolbar [data-cmd]").forEach((btn) => {
    const key = btn.getAttribute("data-cmd");
    if (L[key]) btn.title = L[key];   // Tooltip in app language
    btn.addEventListener("mousedown", (e) => e.preventDefault());
    btn.addEventListener("click", () => {
      const cmd = COMMANDS[key];
      if (cmd) cmd(view);
    });
  });
}

// C# → JS: Note content injection (string data only, not HTML injection)
// Esc exits WYSIWYG editing and returns to the rendered preview.
document.addEventListener("keydown", (e) => {
  if (e.key !== "Escape") return;
  if (window.chrome && window.chrome.webview) {
    e.preventDefault();
    e.stopPropagation();
    window.chrome.webview.postMessage(JSON.stringify({ type: "escape" }));
  }
}, true);
window.SPEditor = {
  setMarkdown(md) {
    suppress = true;
    view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: md ?? "" } });
    suppress = false;
  },
  getMarkdown() { return view.state.doc.toString(); },
  exec(name) { const c = COMMANDS[name]; if (c) c(view); },
  focus() { view.focus(); },
};

// WebView2 may send initial content as a string message
if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener("message", (e) => {
    try {
      const m = typeof e.data === "string" ? JSON.parse(e.data) : e.data;
      if (m && m.type === "load") window.SPEditor.setMarkdown(m.md);
    } catch { /* ignore */ }
  });
}

// start
const params = new URLSearchParams(location.search);
const demo = params.has("demo")
  ? "# Stickmark WYSIWYG\n\n**Bold**, *Italics*, ~~strikethrough~~, `inline code`.\n\n## Things to do\n- [x] Deploy v2.0.1\n- [ ] Wiki cleanup\n- [ ] Screenshot\n\n> Quote like this.\n\n[Link](https://github.com/dameng324/stickmark) #Tag.\n"
  : "";
createEditor(demo);
wireToolbar();
view.contentDOM.addEventListener("blur", () => { clearTimeout(debounce); postChange(); });

// Self-test (?selftest=1): To check whether the command/checkbox widget actually works.
if (params.has("selftest")) {
  const results = {};

  // 1) bold command: Surround the selection area with **
  window.SPEditor.setMarkdown("bold me");
  view.dispatch({ selection: EditorSelection.single(0, view.state.doc.length) });
  window.SPEditor.exec("bold");
  results.bold = window.SPEditor.getMarkdown();

  // 2) Checkbox: A widget appears on the line without the cursor, and the toggle is reflected in the source.
  window.SPEditor.setMarkdown("- [ ] todo\n\nplain");
  view.dispatch({ selection: EditorSelection.single(view.state.doc.length) });  // Move cursor to last line
  const spec = taskToggleChange(view.state, view.state.doc.line(1).from + 2);   // After "- " = TaskMarker
  if (spec) view.dispatch({ changes: spec });
  results.toggled = window.SPEditor.getMarkdown();

  const el = document.getElementById("selftest");
  const finish = () => {
    results.widgets = document.querySelectorAll(".cm-task-toggle").length;      // aggregate after render
    if (el) el.textContent = JSON.stringify(results);
  };
  if (el) el.textContent = JSON.stringify(results);
  setTimeout(finish, 80);
}

//Notify the host that it is ready
if (window.chrome && window.chrome.webview) {
  window.chrome.webview.postMessage(JSON.stringify({ type: "ready" }));
}
