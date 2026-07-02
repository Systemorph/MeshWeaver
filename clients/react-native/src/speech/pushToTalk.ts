// The speech → chat-thread flow: microphone (Recorder seam) → centralized Whisper transcription
// (SpeechTranscriptionClient) → either the thread COMPOSER (dictation) or a DIRECT SUBMIT
// (push-to-talk) through the mesh thread surface — submitMessage to the active thread, or
// startThread under the namespace when none (the client twin of hub.SubmitMessage/hub.StartThread,
// see @meshweaver/client-web `Mesh`).
//
// Pure TS — no react-native imports — so the whole flow unit-tests with a fake recorder,
// transcriber, and submitter. The UI (chat.tsx) renders `state` for the visible recording
// indicator and calls start/stopInto/cancel.

import type { Recorder } from "./recorder";
import type { AudioInput, SpeechTranscript } from "./transcription";

/** The visible lifecycle the mic UI renders. Errors surface here — never swallowed. */
export type SpeechFlowState = "idle" | "recording" | "transcribing" | "error";

/** Where a finished recording goes: into the composer draft, or straight into the thread. */
export type SpeechSink = "composer" | "submit";

/** The mesh thread-submission seam — satisfied by @meshweaver/client-web's Mesh. */
export interface ThreadSubmitter {
  /** Queue a user message on an existing thread (hub.SubmitMessage twin). */
  submitMessage(threadPath: string, text: string): Promise<string | null>;
  /** Create a thread with the first message queued (hub.StartThread twin). */
  startThread(namespacePath: string, text: string): Promise<{ path: string }>;
}

export interface Transcriber {
  transcribe(audio: AudioInput, opts?: { language?: string }): Promise<SpeechTranscript>;
}

export interface PushToTalkOptions {
  recorder: Recorder;
  transcriber: Transcriber;
  /** Required for the "submit" sink; the "composer" sink works without a mesh connection. */
  submitter?: ThreadSubmitter;
  /** Namespace startThread anchors under when no thread is active (e.g. the user's partition). */
  namespacePath?: string;
  /** The active thread, when the chat has one open — direct submits go here. */
  getActiveThreadPath?: () => string | null | undefined;
  /** Fired when a direct submit had no active thread and started one — adopt it as active. */
  onThreadStarted?: (threadPath: string) => void;
  /** The composer sink — receives the recognized text (dictation mode). */
  onTranscript?: (text: string) => void;
  /** Observe state transitions (render the recording indicator); `error` set on the error state. */
  onStateChange?: (state: SpeechFlowState, error?: string) => void;
  /** Whisper language hint forwarded per call (default: the transcriber's own configuration). */
  language?: string;
}

/** The outcome of one push-to-talk round, for callers/tests that await stopInto. */
export interface SpeechFlowResult {
  text: string;
  /** "submitted" = queued on a thread; "started" = new thread created; "composer" = draft only; "empty" = nothing recognized. */
  outcome: "composer" | "submitted" | "started" | "empty";
  threadPath?: string;
}

export class PushToTalkController {
  private readonly opts: PushToTalkOptions;
  private currentState: SpeechFlowState = "idle";

  constructor(opts: PushToTalkOptions) {
    this.opts = opts;
  }

  get state(): SpeechFlowState {
    return this.currentState;
  }

  /** Begin recording. No-op unless idle/error (a round in flight keeps its state machine). */
  async start(): Promise<void> {
    if (this.currentState === "recording" || this.currentState === "transcribing") return;
    try {
      await this.opts.recorder.start();
      this.setState("recording");
    } catch (error) {
      this.fail(error);
      throw error;
    }
  }

  /**
   * Stop recording, transcribe on the centralized endpoint, and route the text to `sink`:
   * "composer" → onTranscript; "submit" → submitMessage on the active thread, or startThread
   * under namespacePath when none. Whitespace-only transcripts submit nothing (mirrors the
   * .NET whitespace no-op — never enqueue an empty round).
   */
  async stopInto(sink: SpeechSink): Promise<SpeechFlowResult> {
    if (this.currentState !== "recording") throw new Error("Not recording.");
    let audio: AudioInput;
    try {
      audio = await this.opts.recorder.stop();
    } catch (error) {
      this.fail(error);
      throw error;
    }

    this.setState("transcribing");
    let text: string;
    try {
      const transcript = await this.opts.transcriber.transcribe(
        audio,
        this.opts.language ? { language: this.opts.language } : undefined,
      );
      text = transcript.text.trim();
    } catch (error) {
      this.fail(error);
      throw error;
    }

    if (text.length === 0) {
      this.setState("idle");
      return { text, outcome: "empty" };
    }

    if (sink === "composer") {
      this.opts.onTranscript?.(text);
      this.setState("idle");
      return { text, outcome: "composer" };
    }

    try {
      const result = await this.submit(text);
      this.setState("idle");
      return result;
    } catch (error) {
      this.fail(error);
      throw error;
    }
  }

  /** Abandon the current recording (no transcription, no submit). */
  async cancel(): Promise<void> {
    if (this.currentState === "recording") await this.opts.recorder.cancel();
    this.setState("idle");
  }

  private async submit(text: string): Promise<SpeechFlowResult> {
    const submitter = this.opts.submitter;
    if (!submitter) throw new Error("Direct submit requires a mesh connection (submitter).");
    const active = this.opts.getActiveThreadPath?.();
    if (active) {
      await submitter.submitMessage(active, text);
      return { text, outcome: "submitted", threadPath: active };
    }
    const namespacePath = this.opts.namespacePath;
    if (!namespacePath) throw new Error("No active thread and no namespacePath to start one under.");
    const { path } = await submitter.startThread(namespacePath, text);
    this.opts.onThreadStarted?.(path);
    return { text, outcome: "started", threadPath: path };
  }

  private setState(state: SpeechFlowState): void {
    this.currentState = state;
    this.opts.onStateChange?.(state);
  }

  private fail(error: unknown): void {
    this.currentState = "error";
    this.opts.onStateChange?.("error", error instanceof Error ? error.message : String(error));
    // Best-effort: never leave the platform recorder holding the mic after a fault.
    void this.opts.recorder.cancel().catch(() => {});
  }
}
