// The React Native leaf pack — native <View>/<Text>/<TextInput> widgets wired to the Fluent-free
// renderer core (@meshweaver/react/core). This is the RN analog of MAUI's MauiViewPack and of the web
// Fluent pack: SAME UiControl tree, native leaves. Drop it into <RegistryProvider pack={rnPack}>.

import { createElement, useState } from "react";
import { View, Text, TextInput, Pressable, Switch, ScrollView, ActivityIndicator, StyleSheet, Platform } from "react-native";
import { marked } from "marked";
import {
  ControlRenderer,
  RenderArea,
  useBindingPointer,
  useChildAreas,
  useEmit,
  useResolve,
  useScope,
  type ControlComponent,
  type LeafPack,
  type SkinComponent,
} from "@meshweaver/react/core";

const s = (v: unknown): string => (v == null ? "" : String(v));

// ── shared binding helpers (the native twins of clients/react/src/controls/common.ts, which is NOT
//    exported from @meshweaver/react/core — the binding primitives it builds on ARE). Every editor reads
//    its value with useResolve and writes back through the /data pointer via emit({kind:"update"}). ──
interface Field {
  value: unknown;
  setValue: (v: unknown) => void;
  onBlur: () => void;
  disabled: boolean;
  label: string;
  placeholder: string;
}
function useField(control: any): Field {
  const bound = control.data ?? control.isChecked;
  const value = useResolve(bound);
  const pointer = useBindingPointer(bound);
  const disabled = !!useResolve(control.disabled);
  const label = s(useResolve(control.label));
  const placeholder = s(useResolve(control.placeholder));
  const emit = useEmit();
  const { area } = useScope();
  return {
    value,
    disabled,
    label,
    placeholder,
    setValue: (v) => { if (pointer) emit({ kind: "update", area, pointer, value: v }); },
    onBlur: () => emit({ kind: "blur", area }),
  };
}
function useOptions(control: any): { value: unknown; text: string }[] {
  const opts = useResolve(control.options);
  if (!Array.isArray(opts)) return [];
  return opts.map((o: any) => {
    if (o != null && typeof o === "object") {
      const value = "item" in o ? o.item : "value" in o ? o.value : o.text;
      return { value, text: s(o.text ?? value) };
    }
    return { value: o, text: s(o) };
  });
}
function Labeled({ label, children }: { label?: string; children: React.ReactNode }) {
  return (
    <View style={{ gap: 4 }}>
      {label ? <Text style={styles.label}>{label}</Text> : null}
      {children}
    </View>
  );
}

function Children({ control }: { control: any }) {
  return (
    <>
      {useChildAreas(control).map((c, i) => (
        <RenderArea key={i} areaKey={c.key} />
      ))}
    </>
  );
}

// ── skins (layout) ──────────────────────────────────────────────────────────
const stack: SkinComponent = ({ skin, control }) => (
  <View style={{ flexDirection: s(skin.orientation).toLowerCase() === "horizontal" ? "row" : "column", gap: (skin.verticalGap ?? skin.horizontalGap ?? 8) as number }}>
    <Children control={control} />
  </View>
);

const card: SkinComponent = ({ control }) => (
  <View style={styles.card}>
    <ControlRenderer control={control} />
  </View>
);

const passthrough: SkinComponent = ({ control }) => <ControlRenderer control={control} />;

// ── leaf controls ─────────────────────────────────────────────────────────────
const Label: ControlComponent = ({ control }) => {
  const big = /header|title|h1|h2/i.test(s(useResolve(control.typo)));
  return <Text style={big ? styles.header : styles.body}>{s(useResolve(control.data))}</Text>;
};

const Badge: ControlComponent = ({ control }) => (
  <View style={styles.badge}>
    <Text style={styles.badgeText}>{s(useResolve(control.data))}</Text>
  </View>
);

const Button: ControlComponent = ({ control }) => {
  const emit = useEmit();
  const { area } = useScope();
  return (
    <Pressable style={styles.button} onPress={() => control.isClickable && emit({ kind: "click", area })}>
      <Text style={styles.buttonText}>{s(useResolve(control.data)) || s(useResolve(control.label))}</Text>
    </Pressable>
  );
};

const TextField: ControlComponent = ({ control }) => {
  const value = s(useResolve(control.data));
  const pointer = useBindingPointer(control.data);
  const emit = useEmit();
  const { area } = useScope();
  const label = s(useResolve(control.label));
  return (
    <View style={{ gap: 4 }}>
      {label ? <Text style={styles.label}>{label}</Text> : null}
      <TextInput
        style={styles.input}
        value={value}
        onChangeText={(t) => pointer && emit({ kind: "update", area, pointer, value: t })}
        onBlur={() => emit({ kind: "blur", area })}
      />
    </View>
  );
};

const CheckBox: ControlComponent = ({ control }) => {
  const checked = !!useResolve(control.data ?? control.isChecked);
  const pointer = useBindingPointer(control.data ?? control.isChecked);
  const emit = useEmit();
  const { area } = useScope();
  return (
    <View style={{ flexDirection: "row", alignItems: "center", gap: 8 }}>
      <Switch value={checked} onValueChange={(v) => { if (pointer) emit({ kind: "update", area, pointer, value: v }); }} />
      <Text style={styles.body}>{s(useResolve(control.label))}</Text>
    </View>
  );
};

const Progress: ControlComponent = ({ control }) => <ActivityIndicator />;
const Spinner: ControlComponent = ({ control }) => <ActivityIndicator />;
const NavLink: ControlComponent = ({ control }) => {
  const emit = useEmit();
  const { area } = useScope();
  return (
    <Pressable onPress={() => control.isClickable && emit({ kind: "click", area })} style={{ paddingVertical: 6 }}>
      <Text style={[styles.body, { color: "#0f6cbd" }]}>{s(useResolve(control.title))}</Text>
    </Pressable>
  );
};

const DataGrid: ControlComponent = ({ control }) => {
  const rows = (useResolve(control.data) as any[]) ?? [];
  const cols = (control.columns as any[]) ?? [];
  return (
    <ScrollView horizontal>
      <View>
        <View style={[styles.row, styles.headerRow]}>
          {cols.map((c, i) => (
            <Text key={i} style={[styles.cell, styles.headerCell]}>{s(c.title ?? c.property)}</Text>
          ))}
        </View>
        {rows.map((r, ri) => (
          <View key={ri} style={styles.row}>
            {cols.map((c, ci) => (
              <Text key={ci} style={styles.cell}>{s(r?.[s(c.property)])}</Text>
            ))}
          </View>
        ))}
      </View>
    </ScrollView>
  );
};

// Server-prerendered HTML (doc bodies, rich text). On web (react-native-web) inject it into a real DOM
// node; on native there is no DOM, so strip tags to text — a full native HTML renderer is future work.
const Html: ControlComponent = ({ control }) => {
  const html = s(useResolve(control.data));
  if (Platform.OS === "web")
    return createElement("div", { className: "markdown-body", dangerouslySetInnerHTML: { __html: html } });
  return <Text style={styles.body}>{html.replace(/<[^>]+>/g, " ").replace(/\s+/g, " ").trim()}</Text>;
};

// Markdown / collaborative-markdown editors (read-only for now). CollaborativeMarkdownControl carries
// its markdown in `value`; the plain Markdown control uses `data`. On web, convert markdown → HTML and
// inject; on native (no DOM) fall back to the raw text.
const Markdown: ControlComponent = ({ control }) => {
  const md = s(useResolve(control.value ?? control.data ?? control.content ?? control.markdown));
  if (Platform.OS === "web")
    return createElement("div", {
      className: "markdown-body",
      dangerouslySetInnerHTML: { __html: marked.parse(md, { async: false }) as string },
    });
  return <Text style={styles.body}>{md}</Text>;
};

// An embedded layout area on ANOTHER address — a distinct subscription. Show a labelled placeholder;
// wiring a nested live GrpcAreaSource per embed is future work.
const LayoutAreaEmbed: ControlComponent = ({ control }) => {
  const ref = control.reference as any;
  return (
    <View style={styles.embed}>
      <Text style={styles.label}>▦ {s(ref?.area ?? ref?.Area) || "layout area"} @ {s(useResolve(control.address))}</Text>
    </View>
  );
};

// ── input / editor controls (native twins of clients/react/src/controls/inputs.tsx) ────────────────
const NumberField: ControlComponent = ({ control }) => {
  const f = useField(control);
  return (
    <Labeled label={f.label}>
      <TextInput
        style={styles.input}
        value={f.value == null || f.value === "" ? "" : String(f.value)}
        keyboardType="numeric"
        editable={!f.disabled}
        placeholder={f.placeholder}
        onChangeText={(t) => f.setValue(t === "" ? null : Number(t))}
        onBlur={f.onBlur}
      />
    </Labeled>
  );
};

const SearchBox: ControlComponent = ({ control }) => {
  const f = useField(control);
  return (
    <View style={styles.searchRow}>
      <Text style={styles.searchIcon}>🔍</Text>
      <TextInput
        style={styles.searchInput}
        value={s(f.value)}
        placeholder={f.placeholder || "Search"}
        autoCapitalize="none"
        onChangeText={f.setValue}
        onBlur={f.onBlur}
      />
    </View>
  );
};

// Select / Listbox — a tap-to-choose option list (no native <Picker> dependency). Selected row is ticked.
const OptionList: ControlComponent = ({ control }) => {
  const f = useField(control);
  const options = useOptions(control);
  return (
    <Labeled label={f.label}>
      <View style={styles.optionList}>
        {options.map((o, i) => {
          const selected = s(o.value) === s(f.value);
          return (
            <Pressable
              key={i}
              style={[styles.optionRow, selected && styles.optionRowSelected]}
              onPress={() => !f.disabled && f.setValue(o.value)}
            >
              <Text style={styles.optionCheck}>{selected ? "✓" : ""}</Text>
              <Text style={styles.body}>{o.text}</Text>
            </Pressable>
          );
        })}
      </View>
    </Labeled>
  );
};

const RadioGroup: ControlComponent = ({ control }) => {
  const f = useField(control);
  const options = useOptions(control);
  return (
    <Labeled label={f.label}>
      <View style={{ gap: 6 }}>
        {options.map((o, i) => {
          const selected = s(o.value) === s(f.value);
          return (
            <Pressable key={i} style={styles.radioRow} onPress={() => !f.disabled && f.setValue(o.value)}>
              <View style={[styles.radioOuter, selected && styles.radioOuterOn]}>
                {selected ? <View style={styles.radioInner} /> : null}
              </View>
              <Text style={styles.body}>{o.text}</Text>
            </Pressable>
          );
        })}
      </View>
    </Labeled>
  );
};

// Combobox — freeform TextInput that also filters + picks from the options (Fluent Combobox twin).
const Combobox: ControlComponent = ({ control }) => {
  const f = useField(control);
  const options = useOptions(control);
  const [open, setOpen] = useState(false);
  const text = s(f.value);
  const filtered = options.filter((o) => o.text.toLowerCase().includes(text.toLowerCase()));
  return (
    <Labeled label={f.label}>
      <TextInput
        style={styles.input}
        value={text}
        editable={!f.disabled}
        placeholder={f.placeholder}
        autoCapitalize="none"
        onChangeText={(t) => { f.setValue(t); setOpen(true); }}
        onFocus={() => setOpen(true)}
        onBlur={() => { setOpen(false); f.onBlur(); }}
      />
      {open && filtered.length > 0 ? (
        <View style={styles.optionList}>
          {filtered.map((o, i) => (
            <Pressable key={i} style={styles.optionRow} onPress={() => { f.setValue(o.value); setOpen(false); }}>
              <Text style={styles.body}>{o.text}</Text>
            </Pressable>
          ))}
        </View>
      ) : null}
    </Labeled>
  );
};

const Slider: ControlComponent = ({ control }) => {
  const f = useField(control);
  const min = Number(useResolve(control.min) ?? 0);
  const max = Number(useResolve(control.max) ?? 100);
  const step = Number(useResolve(control.step) ?? 1);
  const val = Number(f.value ?? min);
  const clamp = (n: number) => Math.max(min, Math.min(max, n));
  const pct = max > min ? Math.round(((clamp(val) - min) / (max - min)) * 100) : 0;
  return (
    <Labeled label={f.label}>
      <View style={styles.sliderRow}>
        <Pressable style={styles.stepBtn} onPress={() => !f.disabled && f.setValue(clamp(val - step))}>
          <Text style={styles.stepTxt}>−</Text>
        </Pressable>
        <View style={styles.sliderTrack}><View style={[styles.sliderFill, { width: `${pct}%` }]} /></View>
        <Pressable style={styles.stepBtn} onPress={() => !f.disabled && f.setValue(clamp(val + step))}>
          <Text style={styles.stepTxt}>＋</Text>
        </Pressable>
        <Text style={styles.sliderVal}>{Number.isFinite(val) ? val : min}</Text>
      </View>
    </Labeled>
  );
};

// Date / DateTime — the web renders an <input type=date> bound to the ISO string sliced to 10/16 chars.
// Native mirrors that with a text field (a native wheel picker is future polish); binding is identical.
const DateInput: ControlComponent = ({ control }) => {
  const f = useField(control);
  return (
    <Labeled label={f.label}>
      <TextInput style={styles.input} value={f.value ? String(f.value).slice(0, 10) : ""} editable={!f.disabled}
        placeholder="YYYY-MM-DD" autoCapitalize="none" onChangeText={f.setValue} onBlur={f.onBlur} />
    </Labeled>
  );
};
const DateTimeInput: ControlComponent = ({ control }) => {
  const f = useField(control);
  return (
    <Labeled label={f.label}>
      <TextInput style={styles.input} value={f.value ? String(f.value).slice(0, 16) : ""} editable={!f.disabled}
        placeholder="YYYY-MM-DDTHH:MM" autoCapitalize="none" onChangeText={f.setValue} onBlur={f.onBlur} />
    </Labeled>
  );
};

const MenuItem: ControlComponent = ({ control }) => {
  const emit = useEmit();
  const { area } = useScope();
  return (
    <Pressable style={styles.menuItem} onPress={() => control.isClickable && emit({ kind: "click", area })}>
      <Text style={styles.body}>{s(useResolve(control.title))}</Text>
    </Pressable>
  );
};

// ── code / markdown editors (native twins of controls/editors.tsx). Monaco is DOM-only; the web pack
//    itself falls back to a plain <textarea> when Monaco isn't loaded — native uses the same shape: a
//    monospace multiline TextInput, fully bound. ──────────────────────────────────────────────────
const CodeEditor: ControlComponent = ({ control }) => {
  const f = useField(control);
  return (
    <Labeled label={f.label}>
      <TextInput
        style={styles.codeInput}
        value={s(f.value)}
        editable={!f.disabled}
        multiline
        autoCapitalize="none"
        autoCorrect={false}
        spellCheck={false}
        onChangeText={f.setValue}
        onBlur={f.onBlur}
      />
    </Labeled>
  );
};

const DiffEditor: ControlComponent = ({ control }) => {
  const original = s(useResolve(control.original ?? control.originalValue));
  const modified = s(useResolve(control.data ?? control.modified ?? control.modifiedValue));
  return (
    <View style={{ gap: 6 }}>
      <Text style={styles.label}>− original</Text>
      <ScrollView horizontal style={styles.codeBlock}><Text style={[styles.code, styles.diffOld]}>{original}</Text></ScrollView>
      <Text style={styles.label}>＋ modified</Text>
      <ScrollView horizontal style={styles.codeBlock}><Text style={[styles.code, styles.diffNew]}>{modified}</Text></ScrollView>
    </View>
  );
};

// Auto-generated form container (EditForm) — renders its child field areas in a column.
const EditForm: ControlComponent = ({ control }) => (
  <View style={{ gap: 10 }}>
    <Children control={control} />
  </View>
);

// ── read-only display twins (controls/display.tsx) ─────────────────────────────────────────────────
const CodeSample: ControlComponent = ({ control }) => (
  <ScrollView horizontal style={styles.codeBlock}>
    <Text style={styles.code}>{s(useResolve(control.data ?? control.code ?? control.value))}</Text>
  </ScrollView>
);

const Exception: ControlComponent = ({ control }) => (
  <View style={styles.exception}>
    <Text style={styles.exceptionText}>⚠ {s(useResolve(control.message ?? control.data ?? control.title))}</Text>
  </View>
);

const Spacer: ControlComponent = () => <View style={{ flex: 1 }} />;

// Icon — emoji / short glyphs render directly; a Fluent icon NAME (no DOM SVG on native) shows a chip.
const Icon: ControlComponent = ({ control }) => {
  const v = useResolve(control.icon ?? control.data);
  const name = typeof v === "string" ? v : s((v as any)?.id ?? (v as any)?.Id ?? (v as any)?.provider);
  return <Text style={styles.body}>{name && [...name].length <= 2 ? name : "▨"}</Text>;
};

// ── mesh display controls (native twins of controls/mesh.tsx) ──────────────────────────────────────
const MeshNodePicker: ControlComponent = ({ control }) => {
  const f = useField(control);
  return (
    <Labeled label={f.label}>
      <TextInput style={styles.input} value={s(f.value)} editable={!f.disabled}
        placeholder="Pick a node (path)…" autoCapitalize="none" onChangeText={f.setValue} onBlur={f.onBlur} />
    </Labeled>
  );
};

const UserProfile: ControlComponent = ({ control }) => {
  const name = s(useResolve(control.name)) || s(useResolve(control.data));
  const initial = (name.trim()[0] ?? "?").toUpperCase();
  return (
    <View style={styles.userRow}>
      <View style={styles.avatar}><Text style={styles.avatarText}>{initial}</Text></View>
      <Text style={styles.body}>{name}</Text>
    </View>
  );
};

const ThreadMessageBubble: ControlComponent = ({ control }) => {
  const role = s(useResolve(control.role)) || "user";
  const mine = /user/i.test(role);
  const text = s(useResolve(control.message)) || s(useResolve(control.data));
  return (
    <View style={{ flexDirection: "row", justifyContent: mine ? "flex-end" : "flex-start" }}>
      <View style={[styles.bubble, mine ? styles.bubbleMine : styles.bubbleTheirs]}>
        <Text style={styles.body}>{text}</Text>
      </View>
    </View>
  );
};

// A mesh node as a card: emoji/initial + title + description; taps fire a server click when clickable.
const MeshNodeCard: ControlComponent = ({ control }) => {
  const emit = useEmit();
  const { area } = useScope();
  const nodePath = s(useResolve(control.nodePath));
  const title = s(useResolve(control.title)) || nodePath;
  const description = s(useResolve(control.description));
  const imageUrl = s(useResolve(control.imageUrl));
  const isEmoji = !!imageUrl && !imageUrl.includes("/") && !imageUrl.startsWith("data:") && !imageUrl.trimStart().toLowerCase().startsWith("<svg");
  const initial = (title.trim()[0] ?? "?").toUpperCase();
  const clickable = !!control.isClickable || (!control.disableNavigation && !!nodePath);
  const inner = (
    <View style={styles.nodeCard}>
      <View style={styles.nodeCardIcon}><Text style={styles.nodeCardIconText}>{isEmoji ? imageUrl : initial}</Text></View>
      <View style={{ flex: 1 }}>
        <Text style={styles.nodeCardTitle} numberOfLines={1}>{title}</Text>
        {description ? <Text style={styles.label} numberOfLines={2}>{description}</Text> : null}
      </View>
    </View>
  );
  return clickable ? (
    <Pressable onPress={() => control.isClickable && emit({ kind: "click", area })}>{inner}</Pressable>
  ) : inner;
};

const LayoutAreaDefinitionCard: ControlComponent = ({ control }) => {
  const def = (control.definition ?? {}) as any;
  const title = s(def.title ?? def.area);
  const description = s(def.description);
  return (
    <View style={styles.nodeCard}>
      <View style={{ flex: 1 }}>
        <Text style={styles.nodeCardTitle}>{title}</Text>
        {description ? <Text style={styles.label}>{description}</Text> : null}
      </View>
    </View>
  );
};

// Live-ops controls (chat/search/collection) need a connected mesh (useMeshOps) — labeled placeholders
// until the live-portal wiring lands, so a streamed area never falls through to "Unsupported".
const livePlaceholder = (label: string): ControlComponent =>
  function LivePlaceholder() {
    return <View style={styles.embed}><Text style={styles.label}>▦ {label}</Text></View>;
  };

const fallback: ControlComponent = ({ control }) => <Text style={{ color: "#d83b01" }}>Unsupported: {control.$type}</Text>;

export const rnPack: LeafPack = {
  defaultContainer: stack,
  skins: { LayoutStack: stack, Layout: stack, LayoutGrid: stack, Card: card, NavMenu: stack, NavGroup: stack, Toolbar: stack, __default: passthrough },
  controls: {
    Label,
    Markdown,
    CollaborativeMarkdown: Markdown,
    Html,
    LayoutArea: LayoutAreaEmbed,
    Badge,
    Button,
    TextField,
    TextArea: TextField,
    CheckBox,
    Switch: CheckBox,
    Progress,
    Spinner,
    NavLink,
    DataGrid,
    Catalog: DataGrid,
    // inputs / editors
    NumberField,
    SearchBox,
    Select: OptionList,
    Listbox: OptionList,
    RadioGroup,
    Combobox,
    Slider,
    Date: DateInput,
    DateTime: DateTimeInput,
    MenuItem,
    MarkdownEditor: CodeEditor,
    CodeEditor,
    Editor: CodeEditor,
    DiffEditor,
    EditForm,
    // read-only display
    CodeSample,
    Highlight: CodeSample,
    Exception,
    Spacer,
    Icon,
    // mesh display controls
    MeshNodePicker,
    UserProfile,
    ThreadMessageBubble,
    MeshNodeCard,
    LayoutAreaDefinition: LayoutAreaDefinitionCard,
    ThreadChat: livePlaceholder("Thread chat"),
    MeshSearch: livePlaceholder("Search results"),
    MeshNodeCollection: livePlaceholder("Node collection"),
    Appearance: livePlaceholder("Appearance"),
  },
  fallback,
};

const styles = StyleSheet.create({
  body: { fontSize: 14, color: "#242424" },
  header: { fontSize: 22, fontWeight: "700", color: "#242424" },
  label: { fontSize: 12, color: "#616161" },
  card: { padding: 12, borderRadius: 8, borderWidth: 1, borderColor: "#e1e1e1", gap: 8, backgroundColor: "white" },
  badge: { alignSelf: "flex-start", backgroundColor: "#0f6cbd", borderRadius: 10, paddingHorizontal: 8, paddingVertical: 2 },
  badgeText: { color: "white", fontSize: 12 },
  button: { backgroundColor: "#0f6cbd", paddingVertical: 10, paddingHorizontal: 14, borderRadius: 6, alignItems: "center" },
  buttonText: { color: "white", fontWeight: "600" },
  input: { borderWidth: 1, borderColor: "#ccc", borderRadius: 4, padding: 8, fontSize: 14 },
  row: { flexDirection: "row" },
  headerRow: { borderBottomWidth: 1, borderColor: "#ddd" },
  cell: { minWidth: 110, padding: 8, fontSize: 13 },
  headerCell: { fontWeight: "700" },
  embed: { padding: 12, borderRadius: 8, borderWidth: 1, borderStyle: "dashed", borderColor: "#c7c7c7", backgroundColor: "#fafafa" },
  // search
  searchRow: { flexDirection: "row", alignItems: "center", gap: 6, borderWidth: 1, borderColor: "#ccc", borderRadius: 6, paddingHorizontal: 8, backgroundColor: "white" },
  searchIcon: { fontSize: 14 },
  searchInput: { flex: 1, paddingVertical: 8, fontSize: 14 },
  // option lists (Select / Listbox / Combobox)
  optionList: { borderWidth: 1, borderColor: "#e1e1e1", borderRadius: 6, overflow: "hidden", backgroundColor: "white" },
  optionRow: { flexDirection: "row", alignItems: "center", gap: 8, paddingVertical: 10, paddingHorizontal: 10, borderBottomWidth: StyleSheet.hairlineWidth, borderColor: "#eee" },
  optionRowSelected: { backgroundColor: "#eef4fb" },
  optionCheck: { width: 16, color: "#0f6cbd", fontWeight: "700" },
  // radio
  radioRow: { flexDirection: "row", alignItems: "center", gap: 8 },
  radioOuter: { width: 20, height: 20, borderRadius: 10, borderWidth: 2, borderColor: "#8a8886", alignItems: "center", justifyContent: "center" },
  radioOuterOn: { borderColor: "#0f6cbd" },
  radioInner: { width: 10, height: 10, borderRadius: 5, backgroundColor: "#0f6cbd" },
  // slider
  sliderRow: { flexDirection: "row", alignItems: "center", gap: 8 },
  sliderTrack: { flex: 1, height: 6, borderRadius: 3, backgroundColor: "#e1e1e1", overflow: "hidden" },
  sliderFill: { height: 6, backgroundColor: "#0f6cbd" },
  sliderVal: { minWidth: 32, textAlign: "right", fontSize: 13, color: "#242424" },
  stepBtn: { width: 28, height: 28, borderRadius: 14, backgroundColor: "#edebe9", alignItems: "center", justifyContent: "center" },
  stepTxt: { fontSize: 16, color: "#242424" },
  // menu item
  menuItem: { paddingVertical: 10, paddingHorizontal: 8 },
  // code
  code: { fontFamily: Platform.OS === "ios" ? "Menlo" : "monospace", fontSize: 12.5, color: "#242424" },
  codeInput: {
    fontFamily: Platform.OS === "ios" ? "Menlo" : "monospace",
    fontSize: 12.5,
    minHeight: 120,
    borderWidth: 1,
    borderColor: "#ccc",
    borderRadius: 6,
    padding: 10,
    backgroundColor: "#1e1e1e00",
    color: "#242424",
    textAlignVertical: "top",
  },
  codeBlock: { backgroundColor: "#f5f5f5", borderRadius: 6, padding: 10 },
  diffOld: { color: "#b00020" },
  diffNew: { color: "#0a7d2c" },
  // exception
  exception: { backgroundColor: "#fdeaea", borderColor: "#f3b3b3", borderWidth: 1, borderRadius: 6, padding: 10 },
  exceptionText: { color: "#a4262c", fontSize: 13 },
  // mesh controls
  userRow: { flexDirection: "row", alignItems: "center", gap: 8 },
  avatar: { width: 28, height: 28, borderRadius: 14, backgroundColor: "#0f6cbd", alignItems: "center", justifyContent: "center" },
  avatarText: { color: "white", fontSize: 12, fontWeight: "700" },
  bubble: { maxWidth: "78%", paddingVertical: 8, paddingHorizontal: 12, borderRadius: 12 },
  bubbleMine: { backgroundColor: "#cfe4fa" },
  bubbleTheirs: { backgroundColor: "#f0f0f0" },
  nodeCard: { flexDirection: "row", alignItems: "center", gap: 12, padding: 12, borderRadius: 8, borderWidth: 1, borderColor: "#e1e1e1", backgroundColor: "white" },
  nodeCardIcon: { width: 48, height: 48, borderRadius: 6, backgroundColor: "#cfe4fa", alignItems: "center", justifyContent: "center" },
  nodeCardIconText: { fontSize: 22, fontWeight: "600", color: "#0f6cbd" },
  nodeCardTitle: { fontSize: 15, fontWeight: "600", color: "#242424" },
});
