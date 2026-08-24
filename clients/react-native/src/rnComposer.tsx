// The RN thread composer bar — the native skin over the SHARED composer model
// (@meshweaver/react/core useMentionModel): @-mention autocomplete exactly like the web leaf and
// Blazor's MeshNodeAutocomplete, plus the speech pipeline (tap-dictate / hold-PTT) that used to
// live on the app-level "Message the mesh…" bar. The app bar is GONE: the mesh renders the
// composer wherever a thread surface appears (ThreadChatControl — the user home's Composer band,
// thread views, side panels), so there is ONE composer, declared by the server, same as Blazor.
import { useMemo, useRef, useState } from "react";
import { ActivityIndicator, Pressable, StyleSheet, Text, TextInput, View } from "react-native";
import { useLocalize, useMentionModel, type MeshOps } from "@meshweaver/react/core";
import { currentInstance } from "./connection";
import { ExpoAudioRecorder } from "./speech/expoRecorder";
import { SpeechTranscriptionClient } from "./speech/transcription";
import { PushToTalkController, type SpeechFlowState } from "./speech/pushToTalk";

const str = (v: unknown): string => (v == null ? "" : String(v));

export interface ComposerBarProps {
  ops: MeshOps | null;
  /** The active thread (submits go here); null starts a new one under `namespacePath`. */
  threadPath: string | null;
  /** Namespace a NEW thread anchors under (ThreadChatControl.namespacePath). */
  namespacePath?: string;
  /** Context path carried on submissions and anchoring the @-mention autocomplete. */
  contextPath?: string;
  /** Fired when a submit created the thread — the leaf pins later sends to it. */
  onThreadStarted: (path: string) => void;
}

export function ComposerBar({ ops, threadPath, namespacePath, contextPath, onThreadStarted }: ComposerBarProps) {
  const t = useLocalize();
  const [text, setText] = useState("");
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [speechState, setSpeechState] = useState<SpeechFlowState>("idle");

  // ── the SHARED mention model (core) — this leaf only renders the dropdown natively ──────────
  const mention = useMentionModel(ops, contextPath || threadPath || undefined);
  const textRef = useRef("");
  textRef.current = text;
  const onChange = (value: string) => {
    setText(value);
    // RN reports the caret via onSelectionChange; for typing at the end (the overwhelming case)
    // the end-of-text caret is correct, and a mid-text selection change re-tracks right after.
    mention.track(value, value.length);
  };
  const onSelectionChange = (e: { nativeEvent: { selection: { end: number } } }) =>
    mention.track(textRef.current, e.nativeEvent.selection.end);
  const pick = (sIdx: number) => {
    const next = mention.pick(textRef.current, mention.suggestions[sIdx]);
    if (next != null) setText(next);
  };

  // ── send (the leaf's canonical submit surface — Mesh.startThread / Mesh.submitMessage) ──────
  const send = () => {
    const body = text.trim();
    if (!body || !ops || sending) return;
    setSending(true);
    setError(null);
    mention.dismiss();
    const done = () => {
      setSending(false);
      setText("");
    };
    const fail = (e: unknown) => {
      setSending(false);
      setError(e instanceof Error ? e.message : String(e));
    };
    if (threadPath) {
      ops.submitMessage(threadPath, body, { contextPath: contextPath || undefined }).then(done, fail);
    } else if (namespacePath) {
      ops.startThread(namespacePath, body, { contextPath: contextPath || undefined }).then((r) => {
        onThreadStarted(r.path);
        done();
      }, fail);
    } else {
      setSending(false);
      setError(t("chat.noNamespace"));
    }
  };

  // ── speech (moved off the deleted app bar): mic tap = dictate → draft; hold = PTT → submit ──
  const threadRef = useRef<string | null>(null);
  threadRef.current = threadPath;
  const controller = useMemo(() => {
    const inst = currentInstance();
    if (!inst.url || !ops) return null;
    return new PushToTalkController({
      recorder: new ExpoAudioRecorder(),
      transcriber: new SpeechTranscriptionClient({ url: inst.url, token: inst.token || undefined }),
      submitter: ops,
      namespacePath: namespacePath ?? "",
      getActiveThreadPath: () => threadRef.current,
      onThreadStarted,
      onTranscript: (transcript) =>
        setText((d) => (d.trim().length > 0 ? `${d} ${transcript}` : transcript)),
      onStateChange: (state, err) => {
        setSpeechState(state);
        setError(err ?? null);
      },
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ops, namespacePath]);
  const pttActive = useRef(false);

  return (
    <View>
      {mention.open ? (
        <View style={styles.mentionBox} accessibilityRole="menu">
          {mention.suggestions.map((sugg, i) => (
            <Pressable
              key={`${str(sugg.insertText) || str(sugg.path)}-${i}`}
              accessibilityRole="menuitem"
              onPress={() => pick(i)}
              style={[styles.mentionRow, i === mention.highlight && styles.mentionRowActive]}
            >
              <Text style={styles.mentionLabel}>{str(sugg.label) || str(sugg.path)}</Text>
              {sugg.path || sugg.description ? (
                <Text style={styles.mentionSub} numberOfLines={1}>
                  {str(sugg.path) || str(sugg.description)}
                </Text>
              ) : null}
            </Pressable>
          ))}
        </View>
      ) : null}
      {error ? <Text style={styles.error}>{error}</Text> : null}
      {speechState !== "idle" ? (
        <View style={styles.speechRow}>
          {speechState === "recording" ? <View style={styles.recDot} /> : <ActivityIndicator size="small" />}
          <Text style={styles.speechText}>
            {speechState === "recording" ? t("chat.recording") : t("chat.transcribing")}
          </Text>
        </View>
      ) : null}
      <View style={styles.row}>
        <TextInput
          style={styles.input}
          value={text}
          onChangeText={onChange}
          onSelectionChange={onSelectionChange}
          placeholder={t("chat.composerPlaceholder")}
          multiline
          editable={!!ops}
          onSubmitEditing={send}
        />
        {controller ? (
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("chat.dictate")}
            style={styles.micButton}
            onPress={() => {
              // Tap: start dictation / stop-and-transcribe INTO the draft. A long-press release
              // also fires onPress — the PTT path owns that gesture (chat-bar semantics, kept).
              if (pttActive.current) return;
              if (controller.state === "recording") void controller.stopInto("composer").catch(() => {});
              else void controller.start().catch(() => {});
            }}
            onLongPress={() => {
              // Hold-to-talk: record while held, release transcribes and SUBMITS directly.
              if (controller.state !== "idle") return;
              pttActive.current = true;
              void controller.start().catch(() => {});
            }}
            onPressOut={() => {
              if (!pttActive.current) return;
              pttActive.current = false;
              if (controller.state === "recording") void controller.stopInto("submit").catch(() => {});
            }}
          >
            <Text style={styles.micText}>{speechState === "recording" ? "■" : "🎤"}</Text>
          </Pressable>
        ) : null}
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("common.send")}
          style={[styles.sendButton, (!text.trim() || sending) && styles.sendButtonDisabled]}
          onPress={send}
        >
          <Text style={styles.sendButtonText}>{sending ? "…" : "➤"}</Text>
        </Pressable>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  row: { flexDirection: "row", alignItems: "flex-end", gap: 8 },
  input: { flex: 1, borderWidth: 1, borderColor: "#d1d1d1", borderRadius: 8, paddingHorizontal: 10, paddingVertical: 8, minHeight: 40, maxHeight: 120, fontSize: 15 },
  micButton: { width: 40, height: 40, borderRadius: 20, backgroundColor: "#f0f0f0", alignItems: "center", justifyContent: "center" },
  micText: { fontSize: 18 },
  sendButton: { backgroundColor: "#0f6cbd", width: 40, height: 40, borderRadius: 20, alignItems: "center", justifyContent: "center" },
  sendButtonDisabled: { opacity: 0.5 },
  sendButtonText: { color: "#ffffff", fontSize: 15, fontWeight: "600" },
  mentionBox: { borderWidth: 1, borderColor: "#e1e1e1", borderRadius: 8, marginBottom: 6, backgroundColor: "#ffffff", overflow: "hidden" },
  mentionRow: { paddingVertical: 6, paddingHorizontal: 10 },
  mentionRowActive: { backgroundColor: "#e1ebf7" },
  mentionLabel: { fontSize: 13, fontWeight: "600", color: "#242424" },
  mentionSub: { fontSize: 11, color: "#8a8a8a" },
  error: { color: "#d13438", fontSize: 12, marginBottom: 4 },
  speechRow: { flexDirection: "row", alignItems: "center", gap: 6, marginBottom: 4 },
  recDot: { width: 10, height: 10, borderRadius: 5, backgroundColor: "#d13438" },
  speechText: { fontSize: 12, color: "#616161" },
});
