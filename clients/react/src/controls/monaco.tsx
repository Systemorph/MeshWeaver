// Monaco-backed editors — the React mirror of the Blazor BlazorMonaco views:
//   - CodeEditorView  ← src/MeshWeaver.Blazor/Components/Monaco/CodeEditorView.razor
//       CodeEditorControl wire: { value, language, theme, readonly, height, lineNumbers, minimap,
//       wordWrap, placeholder } (defaults: plaintext / 300px / line numbers on — the Blazor bindings).
//   - MarkdownEditorView ← Monaco/MarkdownMonacoEditor.razor (MarkdownEditorControl: { value,
//       readonly, height }) — markdown language + word wrap, same value binding.
//   - DiffEditorView  ← Monaco/DiffEditorView.razor (DiffEditorControl: { originalContent,
//       modifiedContent, originalLabel, modifiedLabel, language="markdown", height="500px" }) —
//       read-only side-by-side diff.
//
// Monaco is CLIENT-ONLY and heavy, so it is loaded lazily via dynamic import: the library bundle
// stays lean (vite code-splits @monaco-editor/react; the editor itself streams from the loader's
// CDN at runtime). Until the module arrives — and in non-browser hosts where it never does — the
// SAME bound value renders through the textarea/pre fallback, so the control is functional
// everywhere and upgrade to Monaco is progressive.

import type { CSSProperties, ReactNode } from "react";
import { useEffect, useState } from "react";
import { Text, Textarea } from "@fluentui/react-components";
import type { Json, UiControl } from "../area/types.js";
import { useBindingPointer, useEmit, useResolve, useScope } from "../area/context.js";
import { useThemeMode } from "../theme/themeMode.js";
import { str } from "./common.js";

type MonacoModule = typeof import("@monaco-editor/react");

/** Lazily load @monaco-editor/react once per mount; null while loading / when unavailable. */
function useMonaco(): MonacoModule | null {
  const [mod, setMod] = useState<MonacoModule | null>(null);
  useEffect(() => {
    let live = true;
    import("@monaco-editor/react")
      .then((m) => {
        if (live) setMod(m);
      })
      .catch(() => undefined); // non-browser host — the fallback stays
    return () => {
      live = false;
    };
  }, []);
  return mod;
}

/** Bound editable value — CodeEditor/MarkdownEditor bind `value` (the Blazor ViewModel.Value), with
 *  `data` tolerated for hand-written trees. Writes go back through the standard update event. */
function useValueBinding(control: UiControl): { value: string; setValue: (v: string) => void; onBlur: () => void } {
  const bound = control.value ?? control.data;
  const value = str(useResolve(bound));
  const pointer = useBindingPointer(bound);
  const emit = useEmit();
  const { area } = useScope();
  return {
    value,
    setValue: (v: string) => {
      if (pointer) emit({ kind: "update", area, pointer, value: v });
    },
    onBlur: () => emit({ kind: "blur", area }),
  };
}

export interface MonacoSettings {
  language: string;
  height: string;
  theme: string;
  options: {
    readOnly: boolean;
    lineNumbers: "on" | "off";
    minimap: { enabled: boolean };
    wordWrap: "on" | "off";
    placeholder?: string;
    automaticLayout: boolean;
    scrollBeyondLastLine: boolean;
  };
}

/**
 * Map the (resolved) CodeEditorControl props onto Monaco construction options — the same defaults
 * the Blazor CodeEditorView binds ("plaintext", "300px", line numbers on). Pure; pinned by tests.
 */
export function monacoSettings(
  p: {
    language?: Json;
    theme?: Json;
    readonly?: Json;
    height?: Json;
    lineNumbers?: Json;
    minimap?: Json;
    wordWrap?: Json;
    placeholder?: Json;
  },
  dark: boolean,
): MonacoSettings {
  return {
    language: str(p.language) || "plaintext",
    height: str(p.height) || "300px",
    theme: str(p.theme) || (dark ? "vs-dark" : "light"),
    options: {
      readOnly: p.readonly === true,
      lineNumbers: p.lineNumbers === false ? "off" : "on",
      minimap: { enabled: p.minimap === true },
      wordWrap: p.wordWrap === true ? "on" : "off",
      placeholder: str(p.placeholder) || undefined,
      automaticLayout: true,
      scrollBeyondLastLine: false,
    },
  };
}

const monoFallbackStyle: CSSProperties = { fontFamily: "var(--fontFamilyMonospace)", minHeight: 180 };

function MonacoValueEditor({ control, forceLanguage, wordWrapDefault }: { control: UiControl; forceLanguage?: string; wordWrapDefault?: boolean }): ReactNode {
  const monaco = useMonaco();
  const { resolved } = useThemeMode();
  const binding = useValueBinding(control);
  // Resolve every bindable prop unconditionally (hooks must not be short-circuited).
  const language = useResolve(control.language);
  const theme = useResolve(control.theme);
  const readonly = useResolve(control.readonly);
  const disabled = useResolve(control.disabled);
  const height = useResolve(control.height);
  const lineNumbers = useResolve(control.lineNumbers);
  const minimap = useResolve(control.minimap);
  const wordWrap = useResolve(control.wordWrap);
  const placeholder = useResolve(control.placeholder);
  const settings = monacoSettings(
    {
      language: forceLanguage ?? language,
      theme,
      readonly: readonly ?? disabled,
      height,
      lineNumbers,
      minimap,
      wordWrap: wordWrap ?? wordWrapDefault,
      placeholder,
    },
    resolved === "dark",
  );

  // The bound-textarea fallback: shown until Monaco arrives (and as Monaco's own `loading` node),
  // and permanently in hosts where the editor cannot load. Same binding — fully functional.
  const fallback = (
    <Textarea
      aria-label={settings.options.placeholder ?? "Code editor"}
      value={binding.value}
      disabled={settings.options.readOnly}
      placeholder={settings.options.placeholder}
      onChange={(_, d) => binding.setValue(d.value)}
      onBlur={binding.onBlur}
      textarea={{ style: { ...monoFallbackStyle, minHeight: settings.height } }}
      style={{ width: "100%" }}
    />
  );

  if (!monaco) return fallback;
  const Editor = monaco.default;
  return (
    <div style={{ width: "100%", height: settings.height }} onBlur={binding.onBlur}>
      <Editor
        height={settings.height}
        language={settings.language}
        theme={settings.theme}
        value={binding.value}
        options={settings.options}
        loading={fallback}
        onChange={(v) => {
          if (!settings.options.readOnly) binding.setValue(v ?? "");
        }}
      />
    </div>
  );
}

export function CodeEditorView({ control }: { control: UiControl }): ReactNode {
  return <MonacoValueEditor control={control} />;
}

export function MarkdownEditorView({ control }: { control: UiControl }): ReactNode {
  return <MonacoValueEditor control={control} forceLanguage="markdown" wordWrapDefault />;
}

export function DiffEditorView({ control }: { control: UiControl }): ReactNode {
  const monaco = useMonaco();
  const { resolved } = useThemeMode();
  const original = str(control.originalContent ?? control.original ?? control.originalData);
  const modified = str(control.modifiedContent ?? control.modified ?? (control.originalContent != null ? control.data : undefined));
  const originalLabel = str(control.originalLabel) || "Original";
  const modifiedLabel = str(control.modifiedLabel) || "Current";
  const language = str(control.language) || "markdown";
  const height = str(control.height) || "500px";

  const labels = (
    <div style={{ display: "flex", gap: 8 }}>
      <Text size={200} weight="semibold" style={{ flex: 1 }}>
        {originalLabel}
      </Text>
      <Text size={200} weight="semibold" style={{ flex: 1 }}>
        {modifiedLabel}
      </Text>
    </div>
  );

  if (!monaco) {
    return (
      <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
        {labels}
        <div style={{ display: "flex", gap: 8 }}>
          <pre style={diffPaneStyle("removed")}>{original}</pre>
          <pre style={diffPaneStyle("added")}>{modified}</pre>
        </div>
      </div>
    );
  }

  const Diff = monaco.DiffEditor;
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
      {labels}
      <div style={{ width: "100%", height }}>
        <Diff
          height={height}
          language={language}
          theme={resolved === "dark" ? "vs-dark" : "light"}
          original={original}
          modified={modified}
          options={{
            readOnly: true,
            originalEditable: false,
            renderSideBySide: true,
            enableSplitViewResizing: true,
            automaticLayout: true,
          }}
        />
      </div>
    </div>
  );
}

function diffPaneStyle(kind: "added" | "removed"): CSSProperties {
  return {
    flex: 1,
    margin: 0,
    padding: 8,
    overflow: "auto",
    fontFamily: "var(--fontFamilyMonospace)",
    fontSize: 12,
    background: kind === "added" ? "rgba(16,124,16,0.08)" : "rgba(216,59,1,0.08)",
    borderRadius: 4,
  };
}
