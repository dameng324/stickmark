// Node self-test — Regressively fix the ground truth of Spec-1 (§0/§5-1) with only EditorState + command function without DOM.
// editor.js depends on the browser global (document, window.chrome, etc.), so it is not imported here.
import { test } from "node:test";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

import { EditorState, EditorSelection } from "@codemirror/state";
import { keymap } from "@codemirror/view";
import { defaultKeymap, historyKeymap, indentWithTab } from "@codemirror/commands";
import {
  markdown, markdownLanguage, insertNewlineContinueMarkup, deleteMarkupBackward,
} from "@codemirror/lang-markdown";
import { ensureSyntaxTree } from "@codemirror/language";
import { taskToggleChange } from "./core.mjs";

const require = createRequire(import.meta.url);
const __dirname = path.dirname(fileURLToPath(import.meta.url));

// Same extended array as editor.js (only the view-independent part) — markdown() precedes keymap.of(...)
// Reproduce the order and verify the dispatch priority measured in §0-1.
function markdownState(doc, cursor = doc.length) {
  const state = EditorState.create({
    doc,
    selection: EditorSelection.single(cursor),
    extensions: [
      markdown({ base: markdownLanguage, addKeymap: true }),
      keymap.of([indentWithTab, ...defaultKeymap, ...historyKeymap]),
    ],
  });
  ensureSyntaxTree(state, state.doc.length, 5000);
  return state;
}

// ── §0-1: keymap priority regression — markdown keymap is always looked at before defaultKeymap ──────
test("Enter 1st priority is lang-markdown's insertNewlineContinueMarkup", () => {
  const state = markdownState("- item");
  const binding = state.facet(keymap).flat().find((b) => b.key === "Enter");
  assert.equal(binding.run, insertNewlineContinueMarkup);
});

test("Backspace number 1 is lang-markdown's deleteMarkupBackward", () => {
  const state = markdownState("- item");
  const binding = state.facet(keymap).flat().find((b) => b.key === "Backspace");
  assert.equal(binding.run, deleteMarkupBackward);
});

// ── §0-2: Automatic splicing of lists/citations — Entire table (9 cases + general text fall-through) ────────────
function runEnter(doc, cursor) {
  const state = markdownState(doc, cursor);
  let result = null;
  const handled = insertNewlineContinueMarkup({ state, dispatch: (tr) => { result = tr.state; } });
  return { handled, doc: result ? result.doc.toString() : null, cursor: result ? result.selection.main.head : null };
}

test("Bullet list: Continuing markers", () => {
  const r = runEnter("- item", 6);
  assert.equal(r.handled, true);
  assert.equal(r.doc, "- item\n- ");
});

test("Number list: Number automatically increases", () => {
  const r = runEnter("1. one", 6);
  assert.equal(r.handled, true);
  assert.equal(r.doc, "1. one\n2. ");
});

test("Number list: automatic renumber of following items", () => {
  const r = runEnter("1. one\n2. two", 6);
  assert.equal(r.handled, true);
  assert.equal(r.doc, "1. one\n2. \n3. two");
});

test("Task list: Always continue with an empty checkbox", () => {
  const r = runEnter("- [x] done", 10);
  assert.equal(r.handled, true);
  assert.equal(r.doc, "- [x] done\n- [ ] ");
});

test("Nested list: maintain depth", () => {
  const r = runEnter("- a\n  - b", 9);
  assert.equal(r.handled, true);
  assert.equal(r.doc, "- a\n  - b\n  - ");
});

test("Quote: Continuing marker", () => {
  const r = runEnter("> q", 3);
  assert.equal(r.handled, true);
  assert.equal(r.doc, "> q\n> ");
});

test("The only empty item: unlist immediately", () => {
  const r = runEnter("- ", 2);
  assert.equal(r.handled, true);
  assert.equal(r.doc, "");
});

test("3rd empty item: unlist immediately", () => {
  const r = runEnter("- a\n- b\n- ", 10);
  assert.equal(r.handled, true);
  assert.equal(r.doc, "- a\n- b\n");
});

test("Empty 2nd of 2 items: 1st Enter inserts a blank line, 2nd Enter removes marker (CM6 unique semantics)", () => {
  const first = runEnter("- item\n- ", 9);
  assert.equal(first.handled, true);
  assert.equal(first.doc, "- item\n\n- ");

  const second = runEnter(first.doc, first.cursor);
  assert.equal(second.handled, true);
  assert.equal(second.doc, "- item\n\n");
});

test("Plain text line: fall through to defaultKeymap without processing", () => {
  const r = runEnter("hello", 5);
  assert.equal(r.handled, false);
});

// ── Click checkbox toggle core (core.mjs, DOM independent) ────────────────────────────────────
function taskState(doc) {
  const state = EditorState.create({ doc, extensions: [markdown({ base: markdownLanguage, addKeymap: true })] });
  ensureSyntaxTree(state, state.doc.length, 5000);
  return state;
}

test("Checkbox toggle: [ ] -> [x]", () => {
  const spec = taskToggleChange(taskState("- [ ] todo"), 2);
  assert.deepEqual(spec, { from: 2, to: 5, insert: "[x]" });
});

test("Checkbox toggle: [x] -> [ ]", () => {
  const spec = taskToggleChange(taskState("- [x] done"), 2);
  assert.deepEqual(spec, { from: 2, to: 5, insert: "[ ]" });
});

test("Checkbox toggle: [X] (capital letter) -> Normalize to [ ]", () => {
  const spec = taskToggleChange(taskState("- [X] DONE"), 2);
  assert.deepEqual(spec, { from: 2, to: 5, insert: "[ ]" });
});

test("Fake [ ] text in the body does not respond (not in the TaskMarker node)", () => {
  const doc = "this is not [ ] a task";
  const spec = taskToggleChange(taskState(doc), doc.indexOf("[ ]"));
  assert.equal(spec, null);
});

test("Nested task: operates regardless of depth", () => {
  const doc = "- a\n  - [ ] nested";
  const pos = doc.indexOf("[ ]");
  const spec = taskToggleChange(taskState(doc), pos);
  assert.deepEqual(spec, { from: pos, to: pos + 3, insert: "[x]" });
});

test("Toggle substitution is always equal length (3 characters)", () => {
  for (const doc of ["- [ ] a", "- [x] b", "- [X] c"]) {
    const spec = taskToggleChange(taskState(doc), 2);
    assert.equal(spec.to - spec.from, 3);
    assert.equal(spec.insert.length, 3);
  }
});

// ── §3-3: Code highlight language pack wiring ───────────────────────────────────────────────
// editor.js is browser-wide dependency, so it is not imported. Instead, (a) the presence of the alias in the source text
// Check with the string and (b) check whether the actual package is interpreted as a lockfile.
// Execution loading (whether esbuild can actually bundle the module) is handled by CI's bundle build step.
const editorSrc = readFileSync(path.join(__dirname, "editor.js"), "utf8");
const codeLanguagesBlock = editorSrc.match(/const codeLanguages = \[[\s\S]*?\n\];/)?.[0];

test("The codeLanguages array is defined in editor.js", () => {
  assert.ok(codeLanguagesBlock, "codeLanguages block not found");
});

test("codeLanguages contains all aliases of the first level supported languages in the specification", () => {
  const aliases = [
    "js", "jsx", "ts", "tsx", "py", "htm", "cs", '"c#"',
    "bash", "sh", "zsh", "yml", "ps1", "c", "cpp",
  ];
  for (const alias of aliases) {
    const needle = alias.startsWith('"') ? alias : `"${alias}"`;
    assert.ok(codeLanguagesBlock.includes(needle), `Missing alias: ${alias}`);
  }
});

test("10 CodeMirror language packs for code highlighting are interpreted as lockfile", () => {
  const direct = [
    "@codemirror/lang-javascript", "@codemirror/lang-python", "@codemirror/lang-html",
    "@codemirror/lang-css", "@codemirror/lang-json", "@codemirror/lang-sql",
    "@codemirror/lang-xml", "@codemirror/lang-cpp", "@codemirror/lang-java",
  ];
  for (const pkg of direct) assert.doesNotThrow(() => require.resolve(pkg), pkg);

  // legacy-modes does not have a root entry point in the exports field (submodule only), so editor.js is actually
  // Check the submodule path being imported.
  const legacyModes = [
    "@codemirror/legacy-modes/mode/clike", "@codemirror/legacy-modes/mode/shell",
    "@codemirror/legacy-modes/mode/yaml", "@codemirror/legacy-modes/mode/powershell",
  ];
  for (const mod of legacyModes) assert.doesNotThrow(() => require.resolve(mod), mod);
});
