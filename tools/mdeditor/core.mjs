// Pure logic core — shared by editor.js (esbuild bundle) and test.mjs (Node self-test).
// DOM independent: Receives only EditorState and location and returns change spec.
import { syntaxTree } from "@codemirror/language";

/// A change spec that toggles the 3 characters of pos (near the TaskMarker start position) `[ ]`/`[x]`/`[X]` to equal length.
/// If it is not a TaskMarker, it is null — Does not respond to fake "[ ]" text in the body (based on the syntax tree).
export function taskToggleChange(state, pos) {
  let spec = null;
  syntaxTree(state).iterate({
    from: pos,
    to: Math.min(pos + 3, state.doc.length),
    enter: (n) => {
      if (spec || n.name !== "TaskMarker") return;
      const cur = state.doc.sliceString(n.from, n.to);
      spec = { from: n.from, to: n.to, insert: /x/i.test(cur) ? "[ ]" : "[x]" };
    },
  });
  return spec;
}
