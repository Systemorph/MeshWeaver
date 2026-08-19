// Client screens + client-menu definitions — the RN/web twin of the MAUI app's IClientMenuProvider
// destinations (memex/Memex.Client/Services/ClientMenuProviders.cs). These render in-app (not from the
// mesh): Voice (speech), Connect (remote instances), Profile, Settings.
import { useEffect, useRef, useState, type ReactNode } from "react";
import { View, Text, TextInput, Pressable, ScrollView, StyleSheet } from "react-native";
import { loadInstances, saveInstance, removeInstance, setCurrentInstance, currentInstance, defaultPortalUrl, instanceIdentity, discoverInstances, mergeDiscovered, getConnectStatus, onConnectStatus, type MeshInstance } from "./connection";
import { refreshOAuth, signInWithOAuth } from "./oauth";
import { useStyles, useTheme, type Palette } from "./theme";

const useSheet = () => useStyles(makeStyles);

export type ClientDestination = "profile" | "voice" | "instances" | "settings";

export interface ClientMenuItem {
  label: string;
  destination: ClientDestination;
}

/** Client menu contexts (User / Settings) — provider-shaped, but sourced in-app like MAUI's client providers. */
export const CLIENT_MENUS: { context: string; glyph: string; items: ClientMenuItem[] }[] = [
  {
    context: "You",
    glyph: "👤",
    items: [
      { label: "My profile", destination: "profile" },
      { label: "Voice", destination: "voice" },
      { label: "Connect to mesh", destination: "instances" },
      { label: "Zoom & display", destination: "settings" },
    ],
  },
];

export function ClientScreen({
  destination,
  onConnected,
}: {
  destination: ClientDestination;
  onConnected: () => void;
}): ReactNode {
  switch (destination) {
    case "voice":
      return <VoiceScreen />;
    case "instances":
      return <ConnectScreen onConnected={onConnected} />;
    case "profile":
      return <ProfileScreen />;
    case "settings":
      return <SettingsScreen />;
  }
}

// ── Voice (speech) ────────────────────────────────────────────────────────────
// The browser twin of the MAUI Voice/ stack (on-device Whisper). Here we use the Web Speech API —
// the browser's built-in recogniser — with a de-CH (Swiss German) / de / en / auto language choice.
function VoiceScreen(): ReactNode {
  const s = useSheet();
  const [supported, setSupported] = useState(true);
  const [listening, setListening] = useState(false);
  const [lang, setLang] = useState("de-CH");
  const [text, setText] = useState("");
  const recRef = useRef<any>(null);

  useEffect(() => {
    const SR = typeof window !== "undefined" ? (window as any).SpeechRecognition || (window as any).webkitSpeechRecognition : null;
    if (!SR) {
      setSupported(false);
      return;
    }
    const rec = new SR();
    rec.continuous = true;
    rec.interimResults = true;
    rec.onresult = (e: any) => {
      let full = "";
      for (let i = 0; i < e.results.length; i++) full += e.results[i][0].transcript;
      setText(full);
    };
    rec.onend = () => setListening(false);
    recRef.current = rec;
    return () => { try { rec.stop(); } catch { /* already stopped */ } };
  }, []);

  const toggle = () => {
    const rec = recRef.current;
    if (!rec) return;
    if (listening) {
      rec.stop();
      setListening(false);
    } else {
      rec.lang = lang;
      setText("");
      try {
        rec.start();
        setListening(true);
      } catch { /* start() throws if already running */ }
    }
  };

  return (
    <ScreenScroll title="🎙️  Voice" subtitle="Dictate with the browser speech recogniser. On MAUI this runs on-device Whisper (incl. a Swiss-German model); on the web it uses the built-in recogniser.">
      {!supported ? (
        <Text style={s.note}>This browser has no Speech Recognition API. Chrome / Edge support it.</Text>
      ) : (
        <>
          <View style={s.row}>
            {["de-CH", "de-DE", "en-US", "fr-CH"].map((l) => (
              <Pressable key={l} onPress={() => setLang(l)} style={[s.chip, lang === l && s.chipActive]}>
                <Text style={[s.chipText, lang === l && s.chipTextActive]}>{l}</Text>
              </Pressable>
            ))}
          </View>
          <Pressable onPress={toggle} style={[s.recordBtn, listening && s.recordBtnActive]}>
            <Text style={s.recordText}>{listening ? "■  Stop" : "●  Record"}</Text>
          </Pressable>
          <Text style={s.transcript}>{text || (listening ? "Listening…" : "Tap Record and speak.")}</Text>
        </>
      )}
    </ScreenScroll>
  );
}

// ── Connect to a remote mesh ────────────────────────────────────────────────────
function ConnectScreen({ onConnected }: { onConnected: () => void }): ReactNode {
  const s = useSheet();
  const [instances, setInstances] = useState<MeshInstance[]>(loadInstances());
  const [current, setCurrent] = useState(currentInstance().name);
  // Prefill with a URL that is actually reachable from THIS device: the local monolith only
  // exists on a dev machine (localhost on a phone is the phone), so native builds prefill the
  // public portal. The placeholder-looks-filled trap is also why `add` must never be silent.
  const nativeDefault = defaultPortalUrl().includes("localhost")
    ? "https://memex.meshweaver.cloud" : defaultPortalUrl();
  const [name, setName] = useState("");
  const [url, setUrl] = useState(nativeDefault);
  const [token, setToken] = useState("");
  const [formError, setFormError] = useState("");
  // ONBOARDING, not a form: first-run shows a welcome and the preinstalled meshes as the
  // primary choice; the raw add-a-portal form is advanced and folded away. Signing in is a
  // per-mesh act (tap a mesh → paste a token), not a wall in front of the app.
  const firstRun = !instances.some((i) => !i.local && i.token);
  // The live connect's status, rendered HERE — a Release build has no console, and a
  // failed connect must be readable where the user is looking.
  const [connectStatus, setConnectStatus_] = useState(getConnectStatus());
  useEffect(() => onConnectStatus(() => setConnectStatus_(getConnectStatus())), []);
  const [showForm, setShowForm] = useState(false);
  const [tokenFor, setTokenFor] = useState<string | null>(null);
  const [rowToken, setRowToken] = useState("");
  const [busyFor, setBusyFor] = useState<string | null>(null);
  const [rowError, setRowError] = useState<string | null>(null);
  // Browser sign-in: the portal's own login (SSO included) via its OAuth server — the
  // primary path. Pasting an mw_ token stays as the fallback for headless setups.
  const oauthSignIn = (inst: MeshInstance) => {
    setBusyFor(inst.name); setRowError(null);
    signInWithOAuth(inst.url)
      .then((r) => {
        saveInstance({ ...inst, token: r.accessToken, refreshToken: r.refreshToken,
                       clientId: r.clientId, tokenExpiresAt: r.expiresAt });
        refresh();
        discover({ ...inst, token: r.accessToken });
        onConnected();
      })
      .catch((e) => setRowError(`${inst.name}: ${e?.message ?? "sign-in failed"}`))
      .finally(() => setBusyFor(null));
  };
  const saveRowToken = (inst: MeshInstance) => {
    saveInstance({ ...inst, token: rowToken.trim() });
    setTokenFor(null); setRowToken("");
    refresh();
    discover({ ...inst, token: rowToken.trim() });
    onConnected();
  };

  const refresh = () => {
    setInstances(loadInstances());
    setCurrent(currentInstance().name);
  };
  // The fleet is data ON the mesh (Hosting/Deployment nodes): after pointing at a portal,
  // quietly pull its deployment directory into the switcher. Best-effort, never blocking.
  const discover = (inst: MeshInstance) => {
    discoverInstances(inst).then((d) => { if (d.length) { mergeDiscovered(d); refresh(); } }).catch(() => {});
  };
  const select = (n: string) => {
    setCurrentInstance(n); setCurrent(n);
    const inst = loadInstances().find((i) => i.name === n);
    if (inst && !inst.local) {
      // An expired OAuth token refreshes silently; a dead refresh token falls back to the
      // visible "Sign in" affordance rather than failing areas one by one.
      if (inst.refreshToken && inst.clientId && inst.tokenExpiresAt && inst.tokenExpiresAt < Date.now()) {
        refreshOAuth(inst.url, inst.clientId, inst.refreshToken).then((r) => {
          if (r) {
            saveInstance({ ...inst, token: r.accessToken, refreshToken: r.refreshToken, tokenExpiresAt: r.expiresAt });
            refresh(); onConnected();
          } else {
            setRowError(`${inst.name}: session expired — sign in again.`);
          }
        }).catch(() => {});
      }
      discover(inst);
    }
    onConnected();
  };
  const add = () => {
    const nm = name.trim() || url.trim().replace(/^https?:\/\//, "");
    if (!url.trim()) {
      setFormError("Enter the portal URL — the gray text is only a placeholder.");
      return;
    }
    setFormError("");
    const inst: MeshInstance = { name: nm, url: url.trim().replace(/\/+$/, ""), token: token.trim(), local: false };
    saveInstance(inst);
    setName(""); setUrl(""); setToken("");
    refresh();
    discover(inst);
    onConnected();
  };

  return (
    <ScreenScroll
      title={firstRun ? "Welcome to Memex" : "Connect to a mesh"}
      subtitle={firstRun
        ? "Choose a mesh to connect to. You can browse public content right away — to sign in, tap a mesh and paste an API token (portal ▸ Settings ▸ Security ▸ API Tokens)."
        : "Your meshes. Tap to connect; discovered instances of a connected mesh appear here automatically."}>
      {connectStatus ? (
        <Text style={{ color: connectStatus.includes("failed") ? "#d13438" : "#4c8dff", marginBottom: 10 }}>
          {connectStatus}
        </Text>
      ) : null}
      {instances.map((i) => {
        const id = instanceIdentity(i);
        const loud = id.tone === "prod" || id.tone === "client";
        return (
          <View key={i.name} style={s.instRow}>
            <Pressable onPress={() => select(i.name)} style={[s.inst, { borderLeftColor: id.color, borderLeftWidth: 4 }, current === i.name && s.instActive]}>
              <View style={s.instHead}>
                <Text style={s.instIcon}>{id.icon}</Text>
                <Text style={[s.instName, { color: id.color }, loud && s.instNameLoud, current === i.name && s.instNameActive]}>{i.name}{current === i.name ? "  ✓" : ""}</Text>
                <Text style={[s.instBadge, { color: id.color, borderColor: id.color }]}>{id.kind}</Text>
              </View>
              <Text style={s.instUrl}>{i.local ? "same origin (anonymous)" : i.url}{i.token ? "  ·  🔑 token" : "  ·  anonymous"}</Text>
            </Pressable>
            {!i.local && (
              <Pressable onPress={() => { removeInstance(i.name); refresh(); onConnected(); }} style={s.instDel}>
                <Text style={s.instDelText}>Remove</Text>
              </Pressable>
            )}
            {!i.local && tokenFor !== i.name && (
              <View style={{ flexDirection: "row", gap: 16 }}>
                <Pressable onPress={() => oauthSignIn(i)} style={{ paddingVertical: 6 }} disabled={busyFor === i.name}>
                  <Text style={{ color: "#4c8dff", fontSize: 13, fontWeight: "600" }}>
                    {busyFor === i.name ? "Signing in…" : i.token ? "Sign in again" : "Sign in"}
                  </Text>
                </Pressable>
                <Pressable onPress={() => { setTokenFor(i.name); setRowToken(i.token); }} style={{ paddingVertical: 6 }}>
                  <Text style={{ color: "#4c8dff", fontSize: 13 }}>paste a token</Text>
                </Pressable>
              </View>
            )}
            {tokenFor === i.name && (
              <View style={{ marginTop: 6 }}>
                <Field label={`API token for ${i.name}`} value={rowToken} onChange={setRowToken} placeholder="mw_…" />
                <Pressable onPress={() => saveRowToken(i)} style={s.primaryBtn}><Text style={s.primaryBtnText}>Save & connect</Text></Pressable>
              </View>
            )}
          </View>
        );
      })}
      {rowError ? <Text style={{ color: "#d13438", marginTop: 8 }}>{rowError}</Text> : null}
      {!showForm ? (
        <Pressable onPress={() => setShowForm(true)} style={{ marginTop: 18, paddingVertical: 8 }}>
          <Text style={{ color: "#4c8dff" }}>＋ Add a custom portal by URL</Text>
        </Pressable>
      ) : (
        <>
          <Text style={[s.sectionLabel, { marginTop: 18 }]}>Add a portal</Text>
          <Field label="Name" value={name} onChange={setName} placeholder="e.g. Memex" />
          <Field label="URL" value={url} onChange={setUrl} placeholder="https://memex.meshweaver.cloud" />
          <Field label="API token (optional)" value={token} onChange={setToken} placeholder="mw_…" />
          {formError ? <Text style={{ color: "#d13438", marginBottom: 8 }}>{formError}</Text> : null}
          <Pressable onPress={add} style={s.primaryBtn}><Text style={s.primaryBtnText}>Connect</Text></Pressable>
        </>
      )}
    </ScreenScroll>
  );
}

function ProfileScreen(): ReactNode {
  const s = useSheet();
  const inst = currentInstance();
  return (
    <ScreenScroll title="My profile" subtitle="You are connected to this mesh as an anonymous participant (empty token). Connect to a portal with a token to sign in.">
      <Text style={s.kv}>Mesh: <Text style={s.kvVal}>{inst.name}</Text></Text>
      <Text style={s.kv}>Endpoint: <Text style={s.kvVal}>{inst.url || "same origin"}</Text></Text>
      <Text style={s.kv}>Identity: <Text style={s.kvVal}>{inst.token ? "API token" : "Anonymous"}</Text></Text>
    </ScreenScroll>
  );
}

function SettingsScreen(): ReactNode {
  const s = useSheet();
  return (
    <ScreenScroll title="Zoom & display" subtitle="Display preferences.">
      <Text style={s.note}>Use your browser zoom (⌘ +/−) to scale the app. Toggle light/dark with the ☾/☀ button in the top bar.</Text>
    </ScreenScroll>
  );
}

// ── shared bits ─────────────────────────────────────────────────────────────────
function ScreenScroll({ title, subtitle, children }: { title: string; subtitle?: string; children: ReactNode }): ReactNode {
  const s = useSheet();
  return (
    <ScrollView contentContainerStyle={s.screen}>
      <Text style={s.title}>{title}</Text>
      {subtitle ? <Text style={s.subtitle}>{subtitle}</Text> : null}
      <View style={{ height: 12 }} />
      {children}
    </ScrollView>
  );
}

function Field({ label, value, onChange, placeholder }: { label: string; value: string; onChange: (v: string) => void; placeholder?: string }): ReactNode {
  const s = useSheet();
  const { palette } = useTheme();
  return (
    <View style={{ marginBottom: 12 }}>
      <Text style={s.fieldLabel}>{label}</Text>
      <TextInput style={s.input} value={value} onChangeText={onChange} placeholder={placeholder} placeholderTextColor={palette.textMuted} autoCapitalize="none" autoCorrect={false} />
    </View>
  );
}

const makeStyles = (p: Palette) => StyleSheet.create({
  screen: { maxWidth: 640, width: "100%", alignSelf: "center", paddingHorizontal: 40, paddingVertical: 28 },
  title: { fontSize: 26, fontWeight: "700", color: p.text },
  subtitle: { fontSize: 14, color: p.textSubtle, marginTop: 8, lineHeight: 20 },
  sectionLabel: { fontSize: 11, fontWeight: "700", color: p.textMuted, letterSpacing: 0.6, textTransform: "uppercase", marginBottom: 8 },
  note: { fontSize: 14, color: p.textSubtle, lineHeight: 20 },
  row: { flexDirection: "row", gap: 8, marginBottom: 16, flexWrap: "wrap" },
  chip: { paddingHorizontal: 12, paddingVertical: 6, borderRadius: 16, borderWidth: 1, borderColor: p.border, backgroundColor: p.surface },
  chipActive: { backgroundColor: p.accent, borderColor: p.accent },
  chipText: { fontSize: 13, color: p.text },
  chipTextActive: { color: p.onAccent, fontWeight: "600" },
  recordBtn: { alignSelf: "flex-start", backgroundColor: p.accent, paddingHorizontal: 24, paddingVertical: 12, borderRadius: 8, marginBottom: 18 },
  recordBtnActive: { backgroundColor: "#c50f1f" },
  recordText: { color: p.onAccent, fontWeight: "600", fontSize: 15 },
  transcript: { fontSize: 16, color: p.text, lineHeight: 24, minHeight: 60, backgroundColor: p.sidebarBg, borderRadius: 8, padding: 14, borderWidth: 1, borderColor: p.border },
  instRow: { flexDirection: "row", alignItems: "stretch", gap: 8, marginBottom: 8 },
  inst: { flex: 1, padding: 12, borderRadius: 8, borderWidth: 1, borderColor: p.border, backgroundColor: p.surface },
  instActive: { backgroundColor: p.navActiveBg },
  instHead: { flexDirection: "row", alignItems: "center", gap: 8 },
  instIcon: { fontSize: 16 },
  instName: { fontSize: 14, fontWeight: "600", color: p.text },
  instNameLoud: { fontWeight: "800", textTransform: "uppercase", letterSpacing: 0.6, fontSize: 13 },
  instNameActive: {},
  instBadge: { fontSize: 10, fontWeight: "700", letterSpacing: 0.4, textTransform: "uppercase", borderWidth: 1, borderRadius: 10, paddingHorizontal: 7, paddingVertical: 1, marginLeft: "auto" },
  instUrl: { fontSize: 12, color: p.textMuted, marginTop: 4 },
  instDel: { justifyContent: "center", paddingHorizontal: 12, borderRadius: 8, borderWidth: 1, borderColor: p.border },
  instDelText: { color: "#e06c6c", fontSize: 12 },
  fieldLabel: { fontSize: 12, color: p.textSubtle, marginBottom: 4 },
  input: { borderWidth: 1, borderColor: p.border, borderRadius: 6, padding: 9, fontSize: 14, backgroundColor: p.inputBg, color: p.text },
  primaryBtn: { alignSelf: "flex-start", backgroundColor: p.accent, paddingHorizontal: 22, paddingVertical: 11, borderRadius: 7, marginTop: 4 },
  primaryBtnText: { color: p.onAccent, fontWeight: "600", fontSize: 14 },
  kv: { fontSize: 14, color: p.textSubtle, marginBottom: 8 },
  kvVal: { color: p.text, fontWeight: "600" },
});
