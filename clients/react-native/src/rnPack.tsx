// The React Native leaf pack — native <View>/<Text>/<TextInput> widgets wired to the Fluent-free
// renderer core (@meshweaver/react/core). This is the RN analog of MAUI's MauiViewPack and of the web
// Fluent pack: SAME UiControl tree, native leaves. Drop it into <RegistryProvider pack={rnPack}>.

import { createElement } from "react";
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
    return createElement("div", {
      style: { color: "#242424", fontSize: 14, lineHeight: 1.55 },
      dangerouslySetInnerHTML: { __html: html },
    });
  return <Text style={styles.body}>{html.replace(/<[^>]+>/g, " ").replace(/\s+/g, " ").trim()}</Text>;
};

// Markdown / collaborative-markdown editors (read-only for now). CollaborativeMarkdownControl carries
// its markdown in `value`; the plain Markdown control uses `data`. On web, convert markdown → HTML and
// inject; on native (no DOM) fall back to the raw text.
const Markdown: ControlComponent = ({ control }) => {
  const md = s(useResolve(control.value ?? control.data ?? control.content ?? control.markdown));
  if (Platform.OS === "web")
    return createElement("div", {
      style: { color: "#242424", fontSize: 14, lineHeight: 1.55 },
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
});
