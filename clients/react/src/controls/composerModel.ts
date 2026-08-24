// The thread composer's @-MENTION MODEL — the platform-free half of the composer every shell
// shares (the same factoring meshSearchModel gave the search surface). The web ThreadChat leaf
// (Fluent Textarea) and the RN leaf (native TextInput) both consume THIS: token tracking against
// the caret, the debounced MeshOps.autocomplete fetch (generation-guarded, so a stale response
// never overwrites a newer token's suggestions), highlight movement, and splice-on-pick. The
// leaves keep only their platform's rendering and key/gesture wiring.
//
// Blazor parity: MeshNodeAutocomplete — an @token opens mesh suggestions; picking one splices the
// item's insertText (a UCR `@/path`) into the draft. Hosts without ops.autocomplete never open
// the dropdown, so the model is inert exactly where the capability is absent.

import { useEffect, useRef, useState } from "react";
import type { AutocompleteSuggestion, MeshOps } from "../live/meshOps.js";

export interface AtTokenState {
  /** The @token under the caret, including the `@`. */
  token: string;
  /** Draft offsets the token spans — what a pick replaces. */
  start: number;
  end: number;
}

export interface MentionModel {
  /** Call on every draft/caret change; decides whether an @token is active under the caret. */
  track(value: string, caret: number): void;
  /** Replace the active token with the suggestion's insert text; returns the new draft (null = no active token). */
  pick(draft: string, suggestion: AutocompleteSuggestion): string | null;
  /** Close the dropdown (blur / Escape / after send). */
  dismiss(): void;
  /** Move the keyboard highlight by ±1 (wraps). */
  move(delta: number): void;
  /** Set the highlight to an absolute index (hover). */
  highlightAt(index: number): void;
  suggestions: AutocompleteSuggestion[];
  highlight: number;
  /** True while suggestions are open — the leaf renders its dropdown iff this is set. */
  open: boolean;
}

const AT_TOKEN = /(^|\s)(@[\w\-./]*)$/;
const DEBOUNCE_MS = 250;
const MAX_SUGGESTIONS = 8;

/**
 * The shared mention model. `context` anchors the server-side autocomplete (the thread the
 * composer submits into, or its initial context path).
 */
export function useMentionModel(ops: MeshOps | null, context?: string): MentionModel {
  const [atState, setAtState] = useState<AtTokenState | null>(null);
  const [suggestions, setSuggestions] = useState<AutocompleteSuggestion[]>([]);
  const [highlight, setHighlight] = useState(0);
  const generation = useRef(0);

  useEffect(() => {
    if (!atState || !ops?.autocomplete) return;
    const gen = ++generation.current;
    const timer = setTimeout(() => {
      ops.autocomplete!(atState.token, context || undefined).then(
        (items) => {
          if (generation.current !== gen) return;
          setSuggestions(items.slice(0, MAX_SUGGESTIONS));
          setHighlight(0);
        },
        () => {
          if (generation.current === gen) setSuggestions([]);
        },
      );
    }, DEBOUNCE_MS);
    return () => clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [atState?.token]);

  const dismiss = () => {
    setAtState(null);
    setSuggestions([]);
  };

  return {
    track: (value, caret) => {
      const match = AT_TOKEN.exec(value.slice(0, caret));
      if (!match || !ops?.autocomplete) {
        dismiss();
        return;
      }
      setAtState({ token: match[2], start: caret - match[2].length, end: caret });
    },
    pick: (draft, suggestion) => {
      if (!atState) return null;
      const insert =
        String(suggestion.insertText ?? "") ||
        (suggestion.path ? `@/${String(suggestion.path)}` : String(suggestion.label ?? ""));
      const next = draft.slice(0, atState.start) + insert + " " + draft.slice(atState.end);
      dismiss();
      return next;
    },
    dismiss,
    move: (delta) =>
      setHighlight((h) => {
        const n = suggestions.length;
        return n === 0 ? 0 : (h + delta + n) % n;
      }),
    highlightAt: setHighlight,
    suggestions,
    highlight,
    open: atState != null && suggestions.length > 0,
  };
}
