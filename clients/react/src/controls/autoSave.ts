// AUTO-SAVING EDITORS — the client half of `MarkdownEditorControl.AutoSaveAddress` /
// `CodeEditorControl.AutoSaveAddress`.
//
// An auto-save editor is NOT bound to a layout-area `/data` pointer: the control carries a literal
// value and the node path it edits, and the renderer is expected to write the debounced text back to
// that node itself. Blazor does exactly this — `MarkdownEditorView` / `CodeEditorView` push the
// debounced text through the process-wide `IMeshNodeStreamCache`.
//
// 🚨 The JS packs did not (issue #1476). They emitted the standard area-pointer `update` event, which
// for an auto-save editor has no pointer to write to — so the event went nowhere, the edit was never
// persisted, and NOTHING failed visibly: the text stayed on screen until the view was reloaded. This
// module is that missing write path, shared by the web (Monaco) and React Native packs so the two
// cannot implement it differently — the control-parity ratchets only assert `$type` registration and
// are blind to a per-control contract like this one.

import { useCallback, useEffect, useRef } from "react";
import type { UiControl } from "../area/types.js";
import { useMeshOps, type MeshOps } from "../live/meshOps.js";
import { str } from "./common.js";

/** Debounce window before an edit is persisted — the Blazor AutoSaveHandler's 500 ms throttle. */
export const AUTO_SAVE_DEBOUNCE_MS = 500;

/** Which content shape the edited node carries. */
export type AutoSaveKind = "markdown" | "code";

/**
 * The RFC 7396 merge patch that persists `text` into the node's content.
 *
 * `MarkdownContent.Content` / `CodeConfiguration.Code` are the fields Blazor writes. A merge patch
 * touches only those, so every other content field — and the polymorphic `$type` — survives, which
 * is stricter than the Blazor markdown path (it replaces the whole content object and drops the
 * metadata with it).
 *
 * The two DERIVED markdown fields are cleared deliberately: `prerenderedHtml` and `codeSubmissions`
 * are caches of the previous text, and a reader prefers `prerenderedHtml` when present
 * (`ContentLayoutArea`), so leaving them would render the text the user just replaced. Blazor clears
 * them too, by virtue of constructing a fresh `MarkdownContent`.
 */
export function autoSaveContentPatch(kind: AutoSaveKind, text: string): Record<string, unknown> {
  return kind === "code"
    ? { content: { code: text } }
    : { content: { content: text, prerenderedHtml: null, codeSubmissions: null } };
}

/** The node path an auto-saving editor writes to, or "" when the control did not opt in. */
export function autoSaveAddressOf(control: UiControl): string {
  return str(control.autoSaveAddress);
}

/**
 * The debounced writer for an auto-saving editor: feed it every keystroke; it patches the node at
 * `control.autoSaveAddress` once the typing pauses. Returns null when the control did not opt in (or
 * the host has no mesh ops), so a caller can keep its ordinary pointer binding.
 *
 * A pending edit is FLUSHED on unmount rather than dropped — navigating away mid-debounce is exactly
 * when "my last sentence did not save" happens, and a merge patch is idempotent so an extra write is
 * harmless.
 */
export function useAutoSave(control: UiControl, kind: AutoSaveKind): ((value: string) => void) | null {
  const ops = useMeshOps();
  const address = autoSaveAddressOf(control);

  // Refs so the flush-on-unmount effect never re-runs (and never cancels a live debounce) when the
  // parent re-renders with a new ops object.
  const opsRef = useRef<MeshOps | null>(ops);
  opsRef.current = ops;
  const addressRef = useRef(address);
  addressRef.current = address;
  const kindRef = useRef(kind);
  kindRef.current = kind;

  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const pending = useRef<string | null>(null);

  const flush = useCallback(() => {
    if (timer.current !== null) {
      clearTimeout(timer.current);
      timer.current = null;
    }
    const text = pending.current;
    pending.current = null;
    if (text === null || !addressRef.current || !opsRef.current) return;
    opsRef.current.patch(addressRef.current, autoSaveContentPatch(kindRef.current, text));
  }, []);

  useEffect(() => flush, [flush]); // flush whatever is pending when the editor goes away

  const save = useCallback(
    (value: string) => {
      pending.current = value;
      if (timer.current !== null) clearTimeout(timer.current);
      timer.current = setTimeout(flush, AUTO_SAVE_DEBOUNCE_MS);
    },
    [flush],
  );

  return address && ops ? save : null;
}
