// The RN chat composer — the thread input bar with the speech pipeline wired in:
//   mic tap        → dictate: record → centralized Whisper transcription → text lands in the composer
//   mic hold (PTT) → push-to-talk: record while held → release → transcribe → DIRECT submit to the
//                    active thread (mesh submitMessage), or start a new thread when none (startThread)
//   send           → submit the typed/dictated draft the same way
//
// Submission is the canonical client thread surface (@meshweaver/client-web Mesh.startThread /
// Mesh.submitMessage — the twins of hub.StartThread/hub.SubmitMessage); recognition is the
// centralized Whisper container behind the portal endpoint (src/speech/transcription.ts) — nothing
// on-device. The recording state is visibly rendered (red dot + label while recording, spinner
// while transcribing, error line on faults).

import { useMemo, useRef, useState } from "react";
import { ActivityIndicator, Pressable, StyleSheet, Text, TextInput, View } from "react-native";
import type { Recorder } from "./speech/recorder";
import { PushToTalkController, type SpeechFlowState, type ThreadSubmitter, type Transcriber } from "./speech/pushToTalk";

export interface ChatComposerProps {
  /** The mesh thread surface (Mesh.from(connection)); undefined = dictation-only (send disabled). */
  submitter?: ThreadSubmitter;
  /** Namespace new threads anchor under (e.g. the user's partition, "rbuergi"). */
  namespacePath: string;
  /** The speech pipeline; omit either to hide the mic (e.g. speech not configured on the portal). */
  recorder?: Recorder;
  transcriber?: Transcriber;
  /** Whisper language hint (default: the transcriber's configuration; "de" = Swiss German → Standard German). */
  language?: string;
}

export function ChatComposer({ submitter, namespacePath, recorder, transcriber, language }: ChatComposerProps) {
  const [draft, setDraft] = useState("");
  const [speechState, setSpeechState] = useState<SpeechFlowState>("idle");
  const [error, setError] = useState<string | null>(null);
  const [threadPath, setThreadPath] = useState<string | null>(null);
  const [sending, setSending] = useState(false);
  // Distinguishes hold-to-talk (submit on release) from tap-to-dictate (stop on second tap).
  const pttActive = useRef(false);

  const activeThreadRef = useRef<string | null>(null);
  activeThreadRef.current = threadPath;
  const draftRef = useRef("");
  draftRef.current = draft;

  const controller = useMemo(() => {
    if (!recorder || !transcriber) return null;
    return new PushToTalkController({
      recorder,
      transcriber,
      submitter,
      namespacePath,
      language,
      getActiveThreadPath: () => activeThreadRef.current,
      onThreadStarted: (path) => setThreadPath(path),
      onTranscript: (text) => setDraft((d) => (d.trim().length > 0 ? `${d} ${text}` : text)),
      onStateChange: (state, err) => {
        setSpeechState(state);
        setError(state === "error" ? (err ?? "Speech failed") : null);
      },
    });
    // The seams are stable app-level singletons; state flows through the refs above.
  }, [recorder, transcriber, submitter, namespacePath, language]);

  const swallow = () => {}; // faults already surfaced via onStateChange → the error line

  const onMicPress = () => {
    if (!controller) return;
    if (pttActive.current) return; // release of a long-press also fires onPress — the PTT path owns it
    if (controller.state === "recording") void controller.stopInto("composer").catch(swallow);
    else void controller.start().catch(swallow);
  };

  const onMicLongPress = () => {
    if (!controller || controller.state !== "idle") return;
    pttActive.current = true;
    void controller.start().catch(swallow);
  };

  const onMicPressOut = () => {
    if (!controller || !pttActive.current) return;
    pttActive.current = false;
    if (controller.state === "recording") void controller.stopInto("submit").catch(swallow);
  };

  const send = () => {
    const text = draftRef.current.trim();
    if (!submitter || text.length === 0 || sending) return;
    setSending(true);
    setError(null);
    const done = () => setSending(false);
    const failed = (e: unknown) => {
      setError(e instanceof Error ? e.message : String(e));
      setSending(false);
    };
    if (activeThreadRef.current) {
      Promise.resolve(submitter.submitMessage(activeThreadRef.current, text))
        .then(() => setDraft(""))
        .then(done, failed);
    } else {
      Promise.resolve(submitter.startThread(namespacePath, text))
        .then(({ path }) => {
          setThreadPath(path);
          setDraft("");
        })
        .then(done, failed);
    }
  };

  const recording = speechState === "recording";
  const transcribing = speechState === "transcribing";

  return (
    <View style={styles.bar}>
      {recording && (
        <View style={styles.stateRow}>
          <View style={styles.recordingDot} />
          <Text style={styles.recordingText}>Recording — tap ◼ to dictate, release to send</Text>
        </View>
      )}
      {transcribing && (
        <View style={styles.stateRow}>
          <ActivityIndicator size="small" color="#0f6cbd" />
          <Text style={styles.transcribingText}>Transcribing…</Text>
        </View>
      )}
      {error && <Text style={styles.errorText}>{error}</Text>}
      {threadPath && <Text style={styles.threadText}>Thread: {threadPath}</Text>}
      <View style={styles.inputRow}>
        <TextInput
          style={styles.input}
          value={draft}
          onChangeText={setDraft}
          placeholder={submitter ? "Message the mesh…" : "Dictate a note… (offline — no mesh connection)"}
          multiline
        />
        {controller && (
          <Pressable
            onPress={onMicPress}
            onLongPress={onMicLongPress}
            onPressOut={onMicPressOut}
            disabled={transcribing}
            style={[styles.roundBtn, recording ? styles.micRecording : styles.mic]}
            accessibilityLabel={recording ? "Stop recording" : "Record speech (hold to talk)"}
          >
            <Text style={styles.btnText}>{recording ? "◼" : "🎤"}</Text>
          </Pressable>
        )}
        <Pressable
          onPress={send}
          disabled={!submitter || sending || draft.trim().length === 0}
          style={[styles.roundBtn, styles.send, (!submitter || sending || draft.trim().length === 0) && styles.disabled]}
          accessibilityLabel="Send message"
        >
          <Text style={styles.btnText}>{sending ? "…" : "➤"}</Text>
        </Pressable>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  bar: { borderTopWidth: 1, borderTopColor: "#e1dfdd", padding: 8, gap: 6, backgroundColor: "#faf9f8" },
  stateRow: { flexDirection: "row", alignItems: "center", gap: 8 },
  recordingDot: { width: 10, height: 10, borderRadius: 5, backgroundColor: "#d13438" },
  recordingText: { color: "#d13438", fontWeight: "600" },
  transcribingText: { color: "#0f6cbd" },
  errorText: { color: "#d13438" },
  threadText: { color: "#605e5c", fontSize: 12 },
  inputRow: { flexDirection: "row", alignItems: "flex-end", gap: 8 },
  input: {
    flex: 1,
    minHeight: 40,
    maxHeight: 120,
    borderWidth: 1,
    borderColor: "#c8c6c4",
    borderRadius: 8,
    paddingHorizontal: 10,
    paddingVertical: 8,
    backgroundColor: "white",
  },
  roundBtn: { width: 40, height: 40, borderRadius: 20, alignItems: "center", justifyContent: "center" },
  mic: { backgroundColor: "#edebe9" },
  micRecording: { backgroundColor: "#d13438" },
  send: { backgroundColor: "#0f6cbd" },
  disabled: { opacity: 0.4 },
  btnText: { color: "white", fontSize: 16 },
});
